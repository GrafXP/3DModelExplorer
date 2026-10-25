using System.Numerics;
using ModelExplorer.Geometry;

namespace ModelExplorer.Core.Viewing;

/// <summary>Finds where a ray first meets a mesh: what a right-click on a surface picks.</summary>
public static class MeshPicking
{
    /// <summary>Triangles per parallel chunk: large enough to amortise the scheduling.</summary>
    private const int ChunkTriangles = 1 << 16;

    /// <summary>
    /// The distance along the ray to the nearest hit, or null for a miss. Both
    /// faces count, since an STL's winding is not to be trusted. With
    /// <paramref name="keep"/> given, hits on the half a cut has removed are ignored.
    /// </summary>
    /// <remarks>
    /// A brute-force pass rather than a hierarchy: it runs once per click, and a
    /// parallel scan of two million triangles takes a few milliseconds — less than
    /// building any hierarchy would.
    /// </remarks>
    public static float? Raycast(MeshData mesh, Vector3 origin, Vector3 direction, Plane? keep = null)
    {
        var triangles = mesh.TriangleCount;
        if (triangles == 0)
        {
            return null;
        }

        var chunks = (triangles + ChunkTriangles - 1) / ChunkTriangles;
        var best = new float[chunks];
        Parallel.For(0, chunks, chunk =>
        {
            var nearest = float.PositiveInfinity;
            var end = Math.Min(triangles, (chunk + 1) * ChunkTriangles);
            var positions = mesh.Positions;
            var indices = mesh.Indices;
            for (var triangle = chunk * ChunkTriangles; triangle < end; triangle++)
            {
                var a = positions[indices[triangle * 3]];
                var b = positions[indices[(triangle * 3) + 1]];
                var c = positions[indices[(triangle * 3) + 2]];
                if (Intersect(origin, direction, a, b, c, out var t) && t < nearest &&
                    (keep is not { } plane || Plane.DotCoordinate(plane, origin + (direction * t)) >= -1e-4f))
                {
                    nearest = t;
                }
            }

            best[chunk] = nearest;
        });

        var hit = best.Min();
        return float.IsPositiveInfinity(hit) ? null : hit;
    }

    /// <summary>Möller–Trumbore, two-sided.</summary>
    private static bool Intersect(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0;
        var edge1 = b - a;
        var edge2 = c - a;
        var p = Vector3.Cross(direction, edge2);
        var determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < 1e-12f)
        {
            return false;
        }

        var inverse = 1 / determinant;
        var s = origin - a;
        var u = Vector3.Dot(s, p) * inverse;
        if (u < 0 || u > 1)
        {
            return false;
        }

        var q = Vector3.Cross(s, edge1);
        var v = Vector3.Dot(direction, q) * inverse;
        if (v < 0 || u + v > 1)
        {
            return false;
        }

        t = Vector3.Dot(edge2, q) * inverse;
        return t > 0;
    }
}
