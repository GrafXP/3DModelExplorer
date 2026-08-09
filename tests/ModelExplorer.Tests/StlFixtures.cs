using System.Numerics;
using System.Text;

namespace ModelExplorer.Tests;

/// <summary>
/// Builds STL byte streams in-memory. Generated rather than committed as binary
/// blobs so the expected geometry stays readable and reviewable in the diff.
/// </summary>
internal static class StlFixtures
{
    /// <summary>A 10 mm cube with its minimum corner at the origin.</summary>
    public static readonly Vector3[] CubeCorners =
    [
        new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0),
        new(0, 0, 10), new(10, 0, 10), new(10, 10, 10), new(0, 10, 10),
    ];

    /// <summary>Twelve triangles, wound counter-clockwise when seen from outside.</summary>
    public static readonly int[][] CubeTriangles =
    [
        [0, 2, 1], [0, 3, 2], // bottom  -Z
        [4, 5, 6], [4, 6, 7], // top     +Z
        [0, 1, 5], [0, 5, 4], // front   -Y
        [3, 7, 6], [3, 6, 2], // back    +Y
        [0, 4, 7], [0, 7, 3], // left    -X
        [1, 2, 6], [1, 6, 5], // right   +X
    ];

    public const int CubeTriangleCount = 12;

    public static byte[] BinaryCube(string header = "ModelExplorer test cube", bool zeroNormals = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        var headerBytes = new byte[80];
        var text = Encoding.ASCII.GetBytes(header);
        Array.Copy(text, headerBytes, Math.Min(text.Length, 80));
        writer.Write(headerBytes);
        writer.Write((uint)CubeTriangles.Length);

        foreach (var triangle in CubeTriangles)
        {
            var a = CubeCorners[triangle[0]];
            var b = CubeCorners[triangle[1]];
            var c = CubeCorners[triangle[2]];
            var normal = zeroNormals ? Vector3.Zero : Vector3.Normalize(Vector3.Cross(b - a, c - a));

            WriteVector(writer, normal);
            WriteVector(writer, a);
            WriteVector(writer, b);
            WriteVector(writer, c);
            writer.Write((ushort)0);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static string AsciiCube()
    {
        var sb = new StringBuilder();
        sb.AppendLine("solid cube");

        foreach (var triangle in CubeTriangles)
        {
            var a = CubeCorners[triangle[0]];
            var b = CubeCorners[triangle[1]];
            var c = CubeCorners[triangle[2]];
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));

            sb.AppendLine(FormattableString.Invariant($"  facet normal {n.X:F6} {n.Y:F6} {n.Z:F6}"));
            sb.AppendLine("    outer loop");
            foreach (var v in (Vector3[])[a, b, c])
            {
                sb.AppendLine(FormattableString.Invariant($"      vertex {v.X:F6} {v.Y:F6} {v.Z:F6}"));
            }

            sb.AppendLine("    endloop");
            sb.AppendLine("  endfacet");
        }

        sb.AppendLine("endsolid cube");
        return sb.ToString();
    }

    /// <summary>
    /// A binary STL with an arbitrary triangle count, for exercising the
    /// parallel parse path. Triangle i spans x = [i, i+1], so bounds are
    /// predictable: (0,0,0) to (count, 1, 0).
    /// </summary>
    public static byte[] BinaryStrip(int triangleCount, string header = "strip")
    {
        using var stream = new MemoryStream(84 + (triangleCount * 50));
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        var headerBytes = new byte[80];
        var text = Encoding.ASCII.GetBytes(header);
        Array.Copy(text, headerBytes, Math.Min(text.Length, 80));
        writer.Write(headerBytes);
        writer.Write((uint)triangleCount);

        for (var i = 0; i < triangleCount; i++)
        {
            var a = new Vector3(i, 0, 0);
            var b = new Vector3(i + 1, 0, 0);
            var c = new Vector3(i, 1, 0);

            WriteVector(writer, new Vector3(0, 0, 1));
            WriteVector(writer, a);
            WriteVector(writer, b);
            WriteVector(writer, c);
            writer.Write((ushort)0);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteVector(BinaryWriter writer, Vector3 v)
    {
        writer.Write(v.X);
        writer.Write(v.Y);
        writer.Write(v.Z);
    }
}

/// <summary>Scratch directory that cleans itself up.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mx-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string name, byte[] content)
    {
        var full = System.IO.Path.Combine(Path, name);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string Write(string name, string content)
    {
        var full = System.IO.Path.Combine(Path, name);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory must never fail a test run.
        }
    }
}
