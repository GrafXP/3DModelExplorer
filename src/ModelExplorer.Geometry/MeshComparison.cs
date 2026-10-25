using System.Numerics;

namespace ModelExplorer.Geometry;

/// <summary>Unsigned distances sampled at each triangle's vertices and centroid.</summary>
public sealed record MeshDistanceResult(float[] TriangleDistances, double MeanDistance, float MaxDistance)
{
    public double FractionWithin(float tolerance) =>
        TriangleDistances.Count(distance => distance <= tolerance) / (double)TriangleDistances.Length;
}

public sealed record MeshComparisonResult(MeshDistanceResult AToB, MeshDistanceResult BToA)
{
    public float MaxDistance => MathF.Max(AToB.MaxDistance, BToA.MaxDistance);
}

/// <summary>
/// Compares surfaces without requiring matching vertices or topology. The maximum
/// is a sampled deviation, not an exact Hausdorff distance or a solid boolean.
/// </summary>
public static class MeshComparison
{
    public static MeshComparisonResult Compare(MeshData a, MeshData b, bool alignCenters = false,
        CancellationToken cancellationToken = default)
    {
        Validate(a, cancellationToken);
        Validate(b, cancellationToken);
        // Subtract a common origin to retain precision on translated models.
        var originA = a.Bounds.Center;
        var originB = alignCenters ? b.Bounds.Center : originA;
        var treeA = new SurfaceTree(a, originA, cancellationToken);
        var treeB = new SurfaceTree(b, originB, cancellationToken);
        var aToB = Sample(a, originA, treeB, cancellationToken);
        var bToA = Sample(b, originB, treeA, cancellationToken);
        return new(aToB, bToA);
    }

    private static void Validate(MeshData mesh, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (mesh.TriangleCount == 0 || mesh.Indices.Length % 3 != 0)
            throw new ArgumentException("Both models must contain complete triangles.");
        for (var i = 0; i < mesh.Indices.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var index = mesh.Indices[i];
            if ((uint)index >= (uint)mesh.Positions.Length)
                throw new ArgumentException("The mesh contains an invalid vertex index.");
            var point = mesh.Positions[index];
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
                throw new ArgumentException("The mesh contains non-finite coordinates.");
        }
    }

    private static MeshDistanceResult Sample(MeshData mesh, Vector3 origin, SurfaceTree target, CancellationToken token)
    {
        var distances = new float[mesh.TriangleCount];
        double sum = 0;
        float maximum = 0;
        for (var triangle = 0; triangle < distances.Length; triangle++)
        {
            if ((triangle & 127) == 0) token.ThrowIfCancellationRequested();
            var a = mesh.Positions[mesh.Indices[triangle * 3]] - origin;
            var b = mesh.Positions[mesh.Indices[triangle * 3 + 1]] - origin;
            var c = mesh.Positions[mesh.Indices[triangle * 3 + 2]] - origin;
            var da = target.Distance(a);
            var db = target.Distance(b);
            var dc = target.Distance(c);
            var dm = target.Distance((a + b + c) / 3);
            distances[triangle] = MathF.Max(MathF.Max(da, db), MathF.Max(dc, dm));
            maximum = MathF.Max(maximum, distances[triangle]);
            sum += (double)da + db + dc + dm;
        }
        return new(distances, sum / (distances.Length * 4.0), maximum);
    }

    // A spatial hierarchy prunes triangles whose bounding boxes are farther
    // away than the best surface point already found. No all-pairs mesh scan.
    private sealed class SurfaceTree
    {
        private readonly MeshData _mesh;
        private readonly Vector3 _origin;
        private readonly int[] _triangles;
        private readonly List<Node> _nodes = [];
        private readonly CancellationToken _token;
        private readonly record struct Node(BoundingBox Bounds, int Start, int Count, int Left, int Right);

        public SurfaceTree(MeshData mesh, Vector3 origin, CancellationToken token)
        {
            _mesh = mesh;
            _origin = origin;
            _token = token;
            _triangles = Enumerable.Range(0, mesh.TriangleCount).ToArray();
            Build(0, _triangles.Length);
        }

        private Vector3 Point(int triangle, int corner) => _mesh.Positions[_mesh.Indices[triangle * 3 + corner]] - _origin;
        private float Center(int triangle, int axis) =>
            (Point(triangle, 0)[axis] + Point(triangle, 1)[axis] + Point(triangle, 2)[axis]) / 3;

        private int Build(int start, int count)
        {
            _token.ThrowIfCancellationRequested();
            var bounds = BoundingBox.Empty;
            for (var i = start; i < start + count; i++)
            {
                if ((i & 4095) == 0) _token.ThrowIfCancellationRequested();
                for (var corner = 0; corner < 3; corner++) bounds = bounds.Union(Point(_triangles[i], corner));
            }
            var nodeIndex = _nodes.Count;
            _nodes.Add(new(bounds, start, count, -1, -1));
            if (count <= 8) return nodeIndex;
            var size = bounds.Size;
            var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            // Partition around a median without sorting every subtree.
            var middle = start + count / 2;
            Partition(start, start + count - 1, middle, axis);
            var left = Build(start, middle - start);
            var right = Build(middle, start + count - middle);
            _nodes[nodeIndex] = new(bounds, start, 0, left, right);
            return nodeIndex;
        }

        private void Partition(int left, int right, int middle, int axis)
        {
            while (left < right)
            {
                _token.ThrowIfCancellationRequested();
                var pivot = Center(_triangles[left + (right - left) / 2], axis);
                var i = left;
                var j = right;
                while (i <= j)
                {
                    while (Center(_triangles[i], axis) < pivot) i++;
                    while (Center(_triangles[j], axis) > pivot) j--;
                    if (i > j) break;
                    (_triangles[i], _triangles[j]) = (_triangles[j], _triangles[i]);
                    i++;
                    j--;
                }
                if (middle <= j) right = j;
                else if (middle >= i) left = i;
                else return;
            }
        }

        public float Distance(Vector3 point)
        {
            var best = float.PositiveInfinity;
            Search(0, point, ref best);
            return MathF.Sqrt(best);
        }

        private void Search(int index, Vector3 point, ref float best)
        {
            _token.ThrowIfCancellationRequested();
            var node = _nodes[index];
            if (BoxDistanceSquared(point, node.Bounds) >= best) return;
            if (node.Count > 0)
            {
                for (var i = node.Start; i < node.Start + node.Count; i++)
                {
                    var triangle = _triangles[i];
                    best = MathF.Min(best, TriangleDistanceSquared(point, Point(triangle, 0), Point(triangle, 1), Point(triangle, 2)));
                }
                return;
            }
            var first = node.Left;
            var second = node.Right;
            if (BoxDistanceSquared(point, _nodes[first].Bounds) > BoxDistanceSquared(point, _nodes[second].Bounds))
                (first, second) = (second, first);
            Search(first, point, ref best);
            Search(second, point, ref best);
        }

        private static float BoxDistanceSquared(Vector3 point, BoundingBox box) =>
            Vector3.DistanceSquared(point, Vector3.Clamp(point, box.Min, box.Max));
    }

    private static float TriangleDistanceSquared(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var normal = Vector3.Cross(ab, ac);
        // Degenerate triangles still represent their edges or a single point.
        if (normal.LengthSquared() <= 1e-12f * ab.LengthSquared() * ac.LengthSquared())
            return MathF.Min(SegmentDistanceSquared(p, a, b),
                MathF.Min(SegmentDistanceSquared(p, a, c), SegmentDistanceSquared(p, b, c)));
        var ap = p - a;
        var d1 = Vector3.Dot(ab, ap);
        var d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return ap.LengthSquared();
        var bp = p - b;
        var d3 = Vector3.Dot(ab, bp);
        var d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return bp.LengthSquared();
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
            return Vector3.DistanceSquared(p, a + d1 / (d1 - d3) * ab);
        var cp = p - c;
        var d5 = Vector3.Dot(ab, cp);
        var d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return cp.LengthSquared();
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
            return Vector3.DistanceSquared(p, a + d2 / (d2 - d6) * ac);
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return Vector3.DistanceSquared(p, b + (d4 - d3) / (d4 - d3 + d5 - d6) * (c - b));
        var denominator = 1 / (va + vb + vc);
        return Vector3.DistanceSquared(p, a + ab * (vb * denominator) + ac * (vc * denominator));
    }

    private static float SegmentDistanceSquared(Vector3 p, Vector3 a, Vector3 b)
    {
        var edge = b - a;
        var length = edge.LengthSquared();
        return Vector3.DistanceSquared(p, a + edge * (length == 0 ? 0 : Math.Clamp(Vector3.Dot(p - a, edge) / length, 0, 1)));
    }
}
