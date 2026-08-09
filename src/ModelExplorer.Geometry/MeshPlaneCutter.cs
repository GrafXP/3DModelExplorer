using System.Numerics;
using LibTessDotNet;

namespace ModelExplorer.Geometry;

/// <summary>
/// The result of clipping a mesh to the positive side of a plane and closing
/// every valid intersection contour.
/// </summary>
public sealed record MeshPlaneCutResult(
    MeshData Mesh,
    int ClosedContourCount,
    int OpenContourCount,
    int CapTriangleCount);

/// <summary>
/// Clips triangle meshes against one plane and creates real cap geometry over
/// the newly exposed surface.
/// </summary>
/// <remarks>
/// The input and output are deliberately unwelded, matching <see cref="MeshData"/>.
/// Intersection endpoints are welded only in a temporary contour graph. That is
/// necessary for STL data, where neighbouring triangles do not share vertex
/// indices even when their positions are identical.
/// </remarks>
public static class MeshPlaneCutter
{
    /// <summary>
    /// Keeps the half-space for which <c>dot(plane.Normal, point) + plane.D</c>
    /// is non-negative and caps its closed cut contours.
    /// </summary>
    public static MeshPlaneCutResult CutAndCap(
        MeshData source,
        Plane plane,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Indices.Length % 3 != 0)
        {
            throw new ArgumentException("The mesh index count must be divisible by three.", nameof(source));
        }

        var normalLength = plane.Normal.Length();
        if (!float.IsFinite(normalLength) || normalLength <= float.Epsilon)
        {
            throw new ArgumentException("The cutting plane must have a finite, non-zero normal.", nameof(plane));
        }

        var normal = plane.Normal / normalLength;
        var normalizedPlane = new Plane(normal, plane.D / normalLength);
        var scale = MathF.Max(source.Bounds.MaxExtent, 1f);
        var epsilon = MathF.Max(scale * 1e-6f, 1e-6f);

        var positions = new List<Vector3>(source.Positions.Length);
        var normals = new List<Vector3>(source.Normals.Length);
        var indices = new List<int>(source.Indices.Length);
        var contourSegments = new List<Segment>();

        Span<Vector3> triangle = stackalloc Vector3[3];
        Span<float> distances = stackalloc float[3];
        var clipped = new List<Vector3>(4);

        for (var triangleIndex = 0; triangleIndex < source.Indices.Length; triangleIndex += 3)
        {
            if ((triangleIndex & 0x1fff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            for (var corner = 0; corner < 3; corner++)
            {
                var sourceIndex = source.Indices[triangleIndex + corner];
                if ((uint)sourceIndex >= (uint)source.Positions.Length)
                {
                    throw new ArgumentException("The mesh contains an index outside its position array.", nameof(source));
                }

                triangle[corner] = source.Positions[sourceIndex];
                distances[corner] = SignedDistance(normalizedPlane, triangle[corner]);
            }

            CollectContourSegment(triangle, distances, epsilon, contourSegments);
            ClipTriangle(triangle, distances, epsilon, clipped);

            if (clipped.Count < 3)
            {
                continue;
            }

            for (var i = 1; i + 1 < clipped.Count; i++)
            {
                AddTriangle(positions, normals, indices, clipped[0], clipped[i], clipped[i + 1]);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var contours = BuildContours(contourSegments, epsilon * 4f, out var openContourCount);
        var capTriangleCount = AddCaps(
            positions,
            normals,
            indices,
            contours,
            normalizedPlane,
            epsilon,
            cancellationToken);

        var mesh = positions.Count == 0
            ? MeshData.Empty
            : new MeshData
            {
                Positions = [.. positions],
                Normals = [.. normals],
                Indices = [.. indices],
                Bounds = CalculateBounds(positions),
            };

        return new MeshPlaneCutResult(mesh, contours.Count, openContourCount, capTriangleCount);
    }

    private static float SignedDistance(in Plane plane, in Vector3 point) =>
        Vector3.Dot(plane.Normal, point) + plane.D;

    private static void ClipTriangle(
        ReadOnlySpan<Vector3> triangle,
        ReadOnlySpan<float> distances,
        float epsilon,
        List<Vector3> output)
    {
        output.Clear();

        var previous = triangle[^1];
        var previousDistance = distances[^1];
        var previousInside = previousDistance >= -epsilon;

        for (var i = 0; i < triangle.Length; i++)
        {
            var current = triangle[i];
            var currentDistance = distances[i];
            var currentInside = currentDistance >= -epsilon;

            if (currentInside != previousInside)
            {
                output.Add(Intersect(previous, current, previousDistance, currentDistance));
            }

            if (currentInside)
            {
                output.Add(current);
            }

            previous = current;
            previousDistance = currentDistance;
            previousInside = currentInside;
        }

        RemoveAdjacentDuplicates(output, epsilon);
    }

    private static void CollectContourSegment(
        ReadOnlySpan<Vector3> triangle,
        ReadOnlySpan<float> distances,
        float epsilon,
        List<Segment> segments)
    {
        var positive = 0;
        var negative = 0;
        var onPlane = 0;

        for (var i = 0; i < 3; i++)
        {
            if (distances[i] > epsilon)
            {
                positive++;
            }
            else if (distances[i] < -epsilon)
            {
                negative++;
            }
            else
            {
                onPlane++;
            }
        }

        Span<Vector3> intersections = stackalloc Vector3[3];
        var intersectionCount = 0;

        if (positive > 0 && negative > 0)
        {
            for (var i = 0; i < 3; i++)
            {
                var next = (i + 1) % 3;
                var aDistance = distances[i];
                var bDistance = distances[next];

                if (MathF.Abs(aDistance) <= epsilon)
                {
                    AddUnique(intersections, ref intersectionCount, triangle[i], epsilon);
                }

                if ((aDistance > epsilon && bDistance < -epsilon) ||
                    (aDistance < -epsilon && bDistance > epsilon))
                {
                    AddUnique(
                        intersections,
                        ref intersectionCount,
                        Intersect(triangle[i], triangle[next], aDistance, bDistance),
                        epsilon);
                }
            }
        }
        else if (onPlane == 2 && negative == 1)
        {
            // The plane follows an existing mesh edge. Only the discarded-side
            // triangle contributes it; a kept-side neighbour must not duplicate it.
            for (var i = 0; i < 3; i++)
            {
                if (MathF.Abs(distances[i]) <= epsilon)
                {
                    intersections[intersectionCount++] = triangle[i];
                }
            }
        }

        if (intersectionCount == 2 &&
            Vector3.DistanceSquared(intersections[0], intersections[1]) > epsilon * epsilon)
        {
            segments.Add(new Segment(intersections[0], intersections[1]));
        }
    }

    private static Vector3 Intersect(in Vector3 a, in Vector3 b, float aDistance, float bDistance)
    {
        var denominator = aDistance - bDistance;
        if (MathF.Abs(denominator) <= float.Epsilon)
        {
            return a;
        }

        var t = Math.Clamp(aDistance / denominator, 0f, 1f);
        return Vector3.Lerp(a, b, t);
    }

    private static void AddUnique(
        Span<Vector3> points,
        ref int count,
        in Vector3 point,
        float epsilon)
    {
        for (var i = 0; i < count; i++)
        {
            if (Vector3.DistanceSquared(points[i], point) <= epsilon * epsilon)
            {
                return;
            }
        }

        if (count < points.Length)
        {
            points[count++] = point;
        }
    }

    private static void RemoveAdjacentDuplicates(List<Vector3> polygon, float epsilon)
    {
        var epsilonSquared = epsilon * epsilon;
        for (var i = polygon.Count - 1; i > 0; i--)
        {
            if (Vector3.DistanceSquared(polygon[i], polygon[i - 1]) <= epsilonSquared)
            {
                polygon.RemoveAt(i);
            }
        }

        if (polygon.Count > 1 &&
            Vector3.DistanceSquared(polygon[0], polygon[^1]) <= epsilonSquared)
        {
            polygon.RemoveAt(polygon.Count - 1);
        }
    }

    private static List<List<Vector3>> BuildContours(
        IReadOnlyList<Segment> segments,
        float tolerance,
        out int openContourCount)
    {
        var welder = new PointWelder(tolerance);
        var edgeCounts = new Dictionary<Edge, int>();

        foreach (var segment in segments)
        {
            var a = welder.GetOrAdd(segment.A);
            var b = welder.GetOrAdd(segment.B);
            if (a == b)
            {
                continue;
            }

            var edge = new Edge(a, b);
            edgeCounts[edge] = edgeCounts.GetValueOrDefault(edge) + 1;
        }

        // Identical edges from coincident/coplanar facets cancel in pairs.
        var edges = edgeCounts
            .Where(pair => (pair.Value & 1) == 1)
            .Select(pair => pair.Key)
            .ToArray();

        var adjacency = new List<int>[welder.Points.Count];
        for (var i = 0; i < adjacency.Length; i++)
        {
            adjacency[i] = [];
        }

        for (var edgeIndex = 0; edgeIndex < edges.Length; edgeIndex++)
        {
            adjacency[edges[edgeIndex].A].Add(edgeIndex);
            adjacency[edges[edgeIndex].B].Add(edgeIndex);
        }

        var visitedEdges = new bool[edges.Length];
        var contours = new List<List<Vector3>>();
        openContourCount = 0;

        for (var startEdge = 0; startEdge < edges.Length; startEdge++)
        {
            if (visitedEdges[startEdge])
            {
                continue;
            }

            var componentEdges = CollectComponent(startEdge, edges, adjacency, visitedEdges);
            var componentVertices = componentEdges
                .SelectMany(index => new[] { edges[index].A, edges[index].B })
                .Distinct()
                .ToArray();

            if (componentVertices.Any(vertex => adjacency[vertex].Count != 2))
            {
                openContourCount++;
                continue;
            }

            var loop = TraceLoop(componentEdges[0], edges, adjacency, welder.Points);
            if (loop.Count >= 3)
            {
                contours.Add(loop);
            }
            else
            {
                openContourCount++;
            }
        }

        return contours;
    }

    private static List<int> CollectComponent(
        int startEdge,
        IReadOnlyList<Edge> edges,
        IReadOnlyList<List<int>> adjacency,
        bool[] globallyVisited)
    {
        var result = new List<int>();
        var pending = new Stack<int>();
        pending.Push(startEdge);
        globallyVisited[startEdge] = true;

        while (pending.Count > 0)
        {
            var edgeIndex = pending.Pop();
            result.Add(edgeIndex);
            var edge = edges[edgeIndex];

            foreach (var vertex in new[] { edge.A, edge.B })
            {
                foreach (var connectedEdge in adjacency[vertex])
                {
                    if (!globallyVisited[connectedEdge])
                    {
                        globallyVisited[connectedEdge] = true;
                        pending.Push(connectedEdge);
                    }
                }
            }
        }

        return result;
    }

    private static List<Vector3> TraceLoop(
        int startEdge,
        IReadOnlyList<Edge> edges,
        IReadOnlyList<List<int>> adjacency,
        IReadOnlyList<Vector3> points)
    {
        var result = new List<Vector3>();
        var edge = edges[startEdge];
        var startVertex = edge.A;
        var previousEdge = startEdge;
        var currentVertex = edge.B;
        result.Add(points[startVertex]);

        while (currentVertex != startVertex)
        {
            result.Add(points[currentVertex]);
            var candidates = adjacency[currentVertex];
            var nextEdge = candidates[0] == previousEdge ? candidates[1] : candidates[0];
            var next = edges[nextEdge];
            currentVertex = next.A == currentVertex ? next.B : next.A;
            previousEdge = nextEdge;

            if (result.Count > edges.Count + 1)
            {
                return [];
            }
        }

        return result;
    }

    private static int AddCaps(
        List<Vector3> positions,
        List<Vector3> normals,
        List<int> indices,
        IReadOnlyList<List<Vector3>> contours,
        in Plane plane,
        float epsilon,
        CancellationToken cancellationToken)
    {
        if (contours.Count == 0)
        {
            return 0;
        }

        var reference = MathF.Abs(plane.Normal.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY;
        var axisU = Vector3.Normalize(Vector3.Cross(reference, plane.Normal));
        var axisV = Vector3.Cross(plane.Normal, axisU);
        var planeOrigin = -plane.D * plane.Normal;

        var tessellator = new Tess();
        var boundaryChains = new Dictionary<ProjectedEdge, Vec3[]>();
        foreach (var contour in contours)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vertices = ProjectAndSimplifyContour(
                contour,
                axisU,
                axisV,
                epsilon,
                boundaryChains);
            tessellator.AddContour(vertices, ContourOrientation.Original);
        }

        tessellator.Tessellate(WindingRule.EvenOdd, ElementType.Polygons, 3);

        var capNormal = -plane.Normal;
        var added = 0;
        for (var i = 0; i + 2 < tessellator.ElementCount * 3; i += 3)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ia = tessellator.Elements[i];
            var ib = tessellator.Elements[i + 1];
            var ic = tessellator.Elements[i + 2];
            if (ia == Tess.Undef || ib == Tess.Undef || ic == Tess.Undef)
            {
                continue;
            }

            var projectedPolygon = new List<Vec3>(6);
            AppendExpandedEdge(
                projectedPolygon,
                tessellator.Vertices[ia].Position,
                tessellator.Vertices[ib].Position,
                boundaryChains,
                epsilon);
            AppendExpandedEdge(
                projectedPolygon,
                tessellator.Vertices[ib].Position,
                tessellator.Vertices[ic].Position,
                boundaryChains,
                epsilon);
            AppendExpandedEdge(
                projectedPolygon,
                tessellator.Vertices[ic].Position,
                tessellator.Vertices[ia].Position,
                boundaryChains,
                epsilon);

            if (projectedPolygon.Count == 3)
            {
                if (AddOrientedCapTriangle(
                    positions,
                    normals,
                    indices,
                    Unproject(projectedPolygon[0], planeOrigin, axisU, axisV),
                    Unproject(projectedPolygon[1], planeOrigin, axisU, axisV),
                    Unproject(projectedPolygon[2], planeOrigin, axisU, axisV),
                    capNormal,
                    epsilon))
                {
                    added++;
                }
            }
            else
            {
                var centre = new Vec3();
                foreach (var point in projectedPolygon)
                {
                    centre.X += point.X;
                    centre.Y += point.Y;
                }

                centre.X /= projectedPolygon.Count;
                centre.Y /= projectedPolygon.Count;
                var centre3D = Unproject(centre, planeOrigin, axisU, axisV);

                for (var polygonIndex = 0; polygonIndex < projectedPolygon.Count; polygonIndex++)
                {
                    var next = (polygonIndex + 1) % projectedPolygon.Count;
                    if (AddOrientedCapTriangle(
                        positions,
                        normals,
                        indices,
                        centre3D,
                        Unproject(projectedPolygon[polygonIndex], planeOrigin, axisU, axisV),
                        Unproject(projectedPolygon[next], planeOrigin, axisU, axisV),
                        capNormal,
                        epsilon))
                    {
                        added++;
                    }
                }
            }
        }

        return added;
    }

    private static bool AddOrientedCapTriangle(
        List<Vector3> positions,
        List<Vector3> normals,
        List<int> indices,
        in Vector3 a,
        in Vector3 b,
        in Vector3 c,
        in Vector3 capNormal,
        float epsilon)
    {
        var orientedB = b;
        var orientedC = c;
        var cross = Vector3.Cross(orientedB - a, orientedC - a);
        if (cross.LengthSquared() <= epsilon * epsilon)
        {
            return false;
        }

        if (Vector3.Dot(cross, capNormal) < 0)
        {
            (orientedB, orientedC) = (orientedC, orientedB);
        }

        return AddTriangle(positions, normals, indices, a, orientedB, orientedC, capNormal);
    }

    private static ContourVertex[] ProjectAndSimplifyContour(
        IReadOnlyList<Vector3> contour,
        Vector3 axisU,
        Vector3 axisV,
        float tolerance,
        Dictionary<ProjectedEdge, Vec3[]> boundaryChains)
    {
        var projected = contour
            .Select(point => new Vec3
            {
                X = Vector3.Dot(point, axisU),
                Y = Vector3.Dot(point, axisV),
                Z = 0,
            })
            .ToArray();

        var kept = new List<int>(projected.Length);
        for (var i = 0; i < projected.Length; i++)
        {
            var previous = projected[(i - 1 + projected.Length) % projected.Length];
            var current = projected[i];
            var next = projected[(i + 1) % projected.Length];
            if (!IsCollinear(previous, current, next, tolerance))
            {
                kept.Add(i);
            }
        }

        if (kept.Count < 3)
        {
            kept.Clear();
            for (var i = 0; i < projected.Length; i++)
            {
                kept.Add(i);
            }
        }

        for (var i = 0; i < kept.Count; i++)
        {
            var start = kept[i];
            var end = kept[(i + 1) % kept.Count];
            var chain = new List<Vec3> { projected[start] };
            var cursor = start;
            while (cursor != end)
            {
                cursor = (cursor + 1) % projected.Length;
                chain.Add(projected[cursor]);
            }

            if (chain.Count > 2)
            {
                boundaryChains[new ProjectedEdge(chain[0], chain[^1], tolerance)] = [.. chain];
                chain.Reverse();
                boundaryChains[new ProjectedEdge(chain[0], chain[^1], tolerance)] = [.. chain];
            }
        }

        return kept
            .Select(index => new ContourVertex { Position = projected[index] })
            .ToArray();
    }

    private static bool IsCollinear(in Vec3 previous, in Vec3 current, in Vec3 next, float tolerance)
    {
        var incomingX = current.X - previous.X;
        var incomingY = current.Y - previous.Y;
        var outgoingX = next.X - current.X;
        var outgoingY = next.Y - current.Y;
        var incomingLengthSquared = (incomingX * incomingX) + (incomingY * incomingY);
        var outgoingLengthSquared = (outgoingX * outgoingX) + (outgoingY * outgoingY);
        if (incomingLengthSquared <= tolerance * tolerance ||
            outgoingLengthSquared <= tolerance * tolerance)
        {
            return true;
        }

        var cross = (incomingX * outgoingY) - (incomingY * outgoingX);
        var dot = (incomingX * outgoingX) + (incomingY * outgoingY);
        return dot >= 0 &&
               cross * cross <= tolerance * tolerance * MathF.Max(incomingLengthSquared, outgoingLengthSquared);
    }

    private static void AppendExpandedEdge(
        List<Vec3> polygon,
        in Vec3 start,
        in Vec3 end,
        IReadOnlyDictionary<ProjectedEdge, Vec3[]> boundaryChains,
        float tolerance)
    {
        polygon.Add(start);
        if (!boundaryChains.TryGetValue(new ProjectedEdge(start, end, tolerance), out var chain))
        {
            return;
        }

        for (var i = 1; i + 1 < chain.Length; i++)
        {
            polygon.Add(chain[i]);
        }
    }

    private static Vector3 Unproject(
        in Vec3 point,
        in Vector3 planeOrigin,
        in Vector3 axisU,
        in Vector3 axisV) =>
        planeOrigin + (axisU * point.X) + (axisV * point.Y);

    private static bool AddTriangle(
        List<Vector3> positions,
        List<Vector3> normals,
        List<int> indices,
        in Vector3 a,
        in Vector3 b,
        in Vector3 c,
        Vector3? suppliedNormal = null)
    {
        var cross = Vector3.Cross(b - a, c - a);
        if (cross.LengthSquared() <= float.Epsilon)
        {
            return false;
        }

        var normal = suppliedNormal ?? Vector3.Normalize(cross);
        var start = positions.Count;
        positions.Add(a);
        positions.Add(b);
        positions.Add(c);
        normals.Add(normal);
        normals.Add(normal);
        normals.Add(normal);
        indices.Add(start);
        indices.Add(start + 1);
        indices.Add(start + 2);
        return true;
    }

    private static BoundingBox CalculateBounds(IReadOnlyList<Vector3> positions)
    {
        var bounds = BoundingBox.Empty;
        foreach (var point in positions)
        {
            bounds = bounds.Union(point);
        }

        return bounds;
    }

    private readonly record struct Segment(Vector3 A, Vector3 B);

    private readonly record struct ProjectedEdge
    {
        public ProjectedEdge(in Vec3 start, in Vec3 end, float tolerance)
        {
            Start = ProjectedPoint.From(start, tolerance);
            End = ProjectedPoint.From(end, tolerance);
        }

        public ProjectedPoint Start { get; }

        public ProjectedPoint End { get; }
    }

    private readonly record struct ProjectedPoint(long X, long Y)
    {
        public static ProjectedPoint From(in Vec3 point, float tolerance) => new(
            (long)MathF.Round(point.X / tolerance),
            (long)MathF.Round(point.Y / tolerance));
    }

    private readonly record struct Edge
    {
        public Edge(int a, int b)
        {
            A = Math.Min(a, b);
            B = Math.Max(a, b);
        }

        public int A { get; }

        public int B { get; }
    }

    /// <summary>
    /// Spatial hash with neighbour-cell lookup. A plain rounded coordinate key
    /// would miss points less than the tolerance apart when they straddle a cell
    /// boundary — exactly the kind of floating-point discrepancy STL cuts expose.
    /// </summary>
    private sealed class PointWelder(float tolerance)
    {
        private readonly Dictionary<Cell, List<int>> _cells = [];
        private readonly float _toleranceSquared = tolerance * tolerance;

        public List<Vector3> Points { get; } = [];

        public int GetOrAdd(in Vector3 point)
        {
            var cell = Cell.From(point, tolerance);
            for (var x = -1; x <= 1; x++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    for (var z = -1; z <= 1; z++)
                    {
                        var neighbour = new Cell(cell.X + x, cell.Y + y, cell.Z + z);
                        if (!_cells.TryGetValue(neighbour, out var candidates))
                        {
                            continue;
                        }

                        foreach (var candidate in candidates)
                        {
                            if (Vector3.DistanceSquared(Points[candidate], point) <= _toleranceSquared)
                            {
                                return candidate;
                            }
                        }
                    }
                }
            }

            var index = Points.Count;
            Points.Add(point);
            if (!_cells.TryGetValue(cell, out var bucket))
            {
                bucket = [];
                _cells.Add(cell, bucket);
            }

            bucket.Add(index);
            return index;
        }
    }

    private readonly record struct Cell(long X, long Y, long Z)
    {
        public static Cell From(in Vector3 point, float size) => new(
            (long)MathF.Floor(point.X / size),
            (long)MathF.Floor(point.Y / size),
            (long)MathF.Floor(point.Z / size));
    }
}
