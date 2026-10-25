using System.Numerics;
using ModelExplorer.Geometry;

namespace ModelExplorer.Tests;

/// <summary>Small meshes built in code, unwelded as the loaders produce them.</summary>
internal static class TestMeshes
{
    /// <summary>An axis-aligned cube, wound counter-clockwise when seen from outside.</summary>
    public static MeshData Cube(float min, float max)
    {
        var corners = StlFixtures.CubeCorners.Select(c => new Vector3(min) + (c / 10f * (max - min))).ToArray();
        return FromTriangles(StlFixtures.CubeTriangles.Select(t => (corners[t[0]], corners[t[1]], corners[t[2]])));
    }

    /// <summary>A square in the XY plane at height <paramref name="z"/>.</summary>
    public static MeshData Square(float size, float z = 0) => FromTriangles(
    [
        (new(0, 0, z), new(size, 0, z), new(size, size, z)),
        (new(0, 0, z), new(size, size, z), new(0, size, z)),
    ]);

    public static MeshData FromTriangles(IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> triangles)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        foreach (var (a, b, c) in triangles)
        {
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            positions.AddRange([a, b, c]);
            normals.AddRange([normal, normal, normal]);
        }

        return new MeshData
        {
            Positions = [.. positions],
            Normals = [.. normals],
            Indices = [.. Enumerable.Range(0, positions.Count)],
            Bounds = positions.Aggregate(BoundingBox.Empty, (bounds, point) => bounds.Union(point)),
        };
    }
}
