using System.Numerics;
using ModelExplorer.Geometry;

namespace ModelExplorer.Tests;

public class MeshPlaneCutterTests
{
    [Fact]
    public void CutsCubeAndCreatesAWatertightCap()
    {
        var source = CreateCube(-1, 1);

        var result = MeshPlaneCutter.CutAndCap(source, new Plane(Vector3.UnitZ, 0));

        Assert.Equal(1, result.ClosedContourCount);
        Assert.Equal(0, result.OpenContourCount);
        Assert.True(result.CapTriangleCount >= 2);
        Assert.Equal(-1, result.Mesh.Bounds.Min.X, 5);
        Assert.Equal(0, result.Mesh.Bounds.Min.Z, 5);
        Assert.Equal(1, result.Mesh.Bounds.Max.Z, 5);
        Assert.All(result.Mesh.Positions, point => Assert.True(point.Z >= -1e-5f));
        AssertWatertight(result.Mesh);

        var capNormals = result.Mesh.Normals
            .Where(normal => Vector3.Dot(normal, -Vector3.UnitZ) > 0.999f)
            .ToArray();
        Assert.Equal(result.CapTriangleCount * 3, capNormals.Length);
    }

    [Fact]
    public void TessellatesNestedContoursAsAHole()
    {
        var source = CreateSquareTubeWalls();

        var result = MeshPlaneCutter.CutAndCap(source, new Plane(Vector3.UnitZ, 0));

        Assert.Equal(2, result.ClosedContourCount);
        Assert.Equal(0, result.OpenContourCount);
        Assert.True(result.CapTriangleCount > 0);

        var capArea = TriangleAreaWithNormal(result.Mesh, -Vector3.UnitZ);
        Assert.Equal(12f, capArea, 4);
    }

    [Fact]
    public void ReportsAnOpenContourWithoutInventingACap()
    {
        var source = CreateMesh(
            (new Vector3(-1, 0, -1), new Vector3(1, 0, 1), new Vector3(0, 1, 1)));

        var result = MeshPlaneCutter.CutAndCap(source, new Plane(Vector3.UnitZ, 0));

        Assert.Equal(0, result.ClosedContourCount);
        Assert.Equal(1, result.OpenContourCount);
        Assert.Equal(0, result.CapTriangleCount);
        Assert.All(result.Mesh.Positions, point => Assert.True(point.Z >= -1e-5f));
    }

    [Fact]
    public void ReversingThePlaneKeepsTheOtherHalfAndFlipsTheCapNormal()
    {
        var source = CreateCube(-1, 1);

        var result = MeshPlaneCutter.CutAndCap(source, new Plane(-Vector3.UnitZ, 0));

        Assert.Equal(-1, result.Mesh.Bounds.Min.Z, 5);
        Assert.Equal(0, result.Mesh.Bounds.Max.Z, 5);
        var capNormals = result.Mesh.Normals
            .Where(normal => Vector3.Dot(normal, Vector3.UnitZ) > 0.999f)
            .ToArray();
        Assert.Equal(result.CapTriangleCount * 3, capNormals.Length);
    }

    [Fact]
    public void SupportsAnArbitrarilyOrientedPlane()
    {
        var source = CreateCube(-1, 1);
        var normal = Vector3.Normalize(new Vector3(1, 2, 3));
        var plane = new Plane(normal, 0);

        var result = MeshPlaneCutter.CutAndCap(source, plane);

        Assert.Equal(1, result.ClosedContourCount);
        Assert.Equal(0, result.OpenContourCount);
        Assert.True(result.CapTriangleCount > 0);
        Assert.All(
            result.Mesh.Positions,
            point => Assert.True(Vector3.Dot(normal, point) >= -1e-5f));
        AssertWatertight(result.Mesh);
    }

    private static MeshData CreateCube(float min, float max)
    {
        var triangles = new List<(Vector3 A, Vector3 B, Vector3 C)>();

        AddQuad(new(min, min, min), new(max, min, min), new(max, min, max), new(min, min, max));
        AddQuad(new(max, max, min), new(min, max, min), new(min, max, max), new(max, max, max));
        AddQuad(new(min, max, min), new(min, min, min), new(min, min, max), new(min, max, max));
        AddQuad(new(max, min, min), new(max, max, min), new(max, max, max), new(max, min, max));
        AddQuad(new(min, max, min), new(max, max, min), new(max, min, min), new(min, min, min));
        AddQuad(new(min, min, max), new(max, min, max), new(max, max, max), new(min, max, max));

        return CreateMesh([.. triangles]);

        void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            triangles.Add((a, b, c));
            triangles.Add((a, c, d));
        }
    }

    private static MeshData CreateSquareTubeWalls()
    {
        var triangles = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        AddWalls([new(-2, -2), new(2, -2), new(2, 2), new(-2, 2)]);
        AddWalls([new(-1, -1), new(-1, 1), new(1, 1), new(1, -1)]);
        return CreateMesh([.. triangles]);

        void AddWalls(IReadOnlyList<Vector2> ring)
        {
            for (var i = 0; i < ring.Count; i++)
            {
                var next = (i + 1) % ring.Count;
                var a = new Vector3(ring[i], -1);
                var b = new Vector3(ring[next], -1);
                var c = new Vector3(ring[next], 1);
                var d = new Vector3(ring[i], 1);
                triangles.Add((a, b, c));
                triangles.Add((a, c, d));
            }
        }
    }

    private static MeshData CreateMesh(params (Vector3 A, Vector3 B, Vector3 C)[] triangles)
    {
        var positions = new Vector3[triangles.Length * 3];
        var normals = new Vector3[positions.Length];
        var indices = new int[positions.Length];
        var bounds = ModelExplorer.Geometry.BoundingBox.Empty;

        for (var i = 0; i < triangles.Length; i++)
        {
            var (a, b, c) = triangles[i];
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            var start = i * 3;
            positions[start] = a;
            positions[start + 1] = b;
            positions[start + 2] = c;
            normals[start] = normal;
            normals[start + 1] = normal;
            normals[start + 2] = normal;
            indices[start] = start;
            indices[start + 1] = start + 1;
            indices[start + 2] = start + 2;
            bounds = bounds.Union(a).Union(b).Union(c);
        }

        return new MeshData
        {
            Positions = positions,
            Normals = normals,
            Indices = indices,
            Bounds = bounds,
        };
    }

    private static float TriangleAreaWithNormal(MeshData mesh, Vector3 expectedNormal)
    {
        var area = 0f;
        for (var i = 0; i < mesh.Indices.Length; i += 3)
        {
            var normal = mesh.Normals[mesh.Indices[i]];
            if (Vector3.Dot(normal, expectedNormal) < 0.999f)
            {
                continue;
            }

            var a = mesh.Positions[mesh.Indices[i]];
            var b = mesh.Positions[mesh.Indices[i + 1]];
            var c = mesh.Positions[mesh.Indices[i + 2]];
            area += Vector3.Cross(b - a, c - a).Length() * 0.5f;
        }

        return area;
    }

    private static Dictionary<(PointKey A, PointKey B), int> CountGeometricEdges(MeshData mesh)
    {
        var counts = new Dictionary<(PointKey A, PointKey B), int>();
        for (var i = 0; i < mesh.Indices.Length; i += 3)
        {
            Add(mesh.Positions[mesh.Indices[i]], mesh.Positions[mesh.Indices[i + 1]]);
            Add(mesh.Positions[mesh.Indices[i + 1]], mesh.Positions[mesh.Indices[i + 2]]);
            Add(mesh.Positions[mesh.Indices[i + 2]], mesh.Positions[mesh.Indices[i]]);
        }

        return counts;

        void Add(Vector3 a, Vector3 b)
        {
            var first = PointKey.From(a);
            var second = PointKey.From(b);
            var edge = first.CompareTo(second) <= 0 ? (first, second) : (second, first);
            counts[edge] = counts.GetValueOrDefault(edge) + 1;
        }
    }

    private static void AssertWatertight(MeshData mesh)
    {
        var invalid = CountGeometricEdges(mesh)
            .Where(pair => pair.Value != 2)
            .ToArray();
        Assert.True(
            invalid.Length == 0,
            string.Join(Environment.NewLine, invalid.Select(pair => $"{pair.Key}: {pair.Value}")));
    }

    private readonly record struct PointKey(int X, int Y, int Z) : IComparable<PointKey>
    {
        public static PointKey From(Vector3 point) => new(
            (int)MathF.Round(point.X * 100_000),
            (int)MathF.Round(point.Y * 100_000),
            (int)MathF.Round(point.Z * 100_000));

        public int CompareTo(PointKey other)
        {
            var x = X.CompareTo(other.X);
            if (x != 0)
            {
                return x;
            }

            var y = Y.CompareTo(other.Y);
            return y != 0 ? y : Z.CompareTo(other.Z);
        }
    }
}
