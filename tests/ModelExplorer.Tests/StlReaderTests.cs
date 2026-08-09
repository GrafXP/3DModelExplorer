using System.Numerics;
using ModelExplorer.Geometry;
using ModelExplorer.Geometry.Stl;

namespace ModelExplorer.Tests;

public class StlReaderTests
{
    [Fact]
    public void ReadsBinaryCube()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("cube.stl", StlFixtures.BinaryCube());

        var mesh = StlReader.Read(path);

        Assert.Equal(StlFixtures.CubeTriangleCount, mesh.TriangleCount);
        Assert.Equal(StlFixtures.CubeTriangleCount * 3, mesh.VertexCount);
        Assert.Equal(new Vector3(0, 0, 0), mesh.Bounds.Min);
        Assert.Equal(new Vector3(10, 10, 10), mesh.Bounds.Max);
    }

    [Fact]
    public void ReadsAsciiCube()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("cube.stl", StlFixtures.AsciiCube());

        var mesh = StlReader.Read(path);

        Assert.Equal(StlFixtures.CubeTriangleCount, mesh.TriangleCount);
        Assert.Equal(new Vector3(0, 0, 0), mesh.Bounds.Min);
        Assert.Equal(new Vector3(10, 10, 10), mesh.Bounds.Max);
    }

    [Fact]
    public void BinaryAndAsciiAgree()
    {
        using var dir = new TempDirectory();
        var binary = StlReader.Read(dir.Write("b.stl", StlFixtures.BinaryCube()));
        var ascii = StlReader.Read(dir.Write("a.stl", StlFixtures.AsciiCube()));

        Assert.Equal(binary.TriangleCount, ascii.TriangleCount);
        Assert.Equal(binary.Bounds, ascii.Bounds);

        for (var i = 0; i < binary.VertexCount; i++)
        {
            Assert.True(Vector3.Distance(binary.Positions[i], ascii.Positions[i]) < 1e-4f,
                $"vertex {i} differs: {binary.Positions[i]} vs {ascii.Positions[i]}");
        }
    }

    /// <summary>
    /// Many CAD tools write binary STLs whose 80-byte header starts with "solid".
    /// Sniffing on the leading keyword alone would parse these as ASCII and yield
    /// zero triangles, so the length check has to win.
    /// </summary>
    [Fact]
    public void BinaryFileWithSolidHeaderIsNotMistakenForAscii()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("trap.stl", StlFixtures.BinaryCube("solid cube exported by a careless tool"));

        var mesh = StlReader.Read(path);

        Assert.Equal(StlFixtures.CubeTriangleCount, mesh.TriangleCount);
        Assert.Equal(new Vector3(10, 10, 10), mesh.Bounds.Max);
    }

    /// <summary>Zeroed normals are common in the wild; they must be recomputed.</summary>
    [Fact]
    public void RecomputesNormalsWhenStoredNormalsAreZero()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("zero.stl", StlFixtures.BinaryCube(zeroNormals: true));

        var mesh = StlReader.Read(path);

        Assert.All(mesh.Normals, n =>
            Assert.True(Math.Abs(n.Length() - 1f) < 1e-4f, $"normal {n} is not unit length"));
    }

    [Fact]
    public void CubeNormalsAreAxisAligned()
    {
        using var dir = new TempDirectory();
        var mesh = StlReader.Read(dir.Write("cube.stl", StlFixtures.BinaryCube()));

        // Every face of an axis-aligned cube points down exactly one axis.
        Assert.All(mesh.Normals, n =>
        {
            var components = new[] { Math.Abs(n.X), Math.Abs(n.Y), Math.Abs(n.Z) };
            Assert.Equal(1, components.Count(c => c > 0.99f));
            Assert.Equal(2, components.Count(c => c < 0.01f));
        });
    }

    [Fact]
    public void NormalsPointOutward()
    {
        using var dir = new TempDirectory();
        var mesh = StlReader.Read(dir.Write("cube.stl", StlFixtures.BinaryCube()));
        var centre = mesh.Bounds.Center;

        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var i = t * 3;
            var faceCentre = (mesh.Positions[i] + mesh.Positions[i + 1] + mesh.Positions[i + 2]) / 3f;
            var outward = faceCentre - centre;
            Assert.True(Vector3.Dot(outward, mesh.Normals[i]) > 0,
                $"triangle {t} normal {mesh.Normals[i]} points inward");
        }
    }

    /// <summary>Crosses the 64K threshold that switches the reader to the parallel path.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(65_535)]
    [InlineData(65_536)]
    [InlineData(200_000)]
    public void ParallelAndSequentialPathsProduceSameShape(int triangleCount)
    {
        using var dir = new TempDirectory();
        var path = dir.Write("strip.stl", StlFixtures.BinaryStrip(triangleCount));

        var mesh = StlReader.Read(path);

        Assert.Equal(triangleCount, mesh.TriangleCount);
        Assert.Equal(new Vector3(0, 0, 0), mesh.Bounds.Min);
        Assert.Equal(new Vector3(triangleCount, 1, 0), mesh.Bounds.Max);

        // Indices are sequential for unwelded geometry.
        Assert.Equal(0, mesh.Indices[0]);
        Assert.Equal(mesh.VertexCount - 1, mesh.Indices[^1]);
    }

    [Fact]
    public void ZeroTriangleFileYieldsEmptyMesh()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("empty.stl", StlFixtures.BinaryStrip(0));

        var mesh = StlReader.Read(path);

        Assert.Equal(0, mesh.TriangleCount);
    }

    [Fact]
    public void EmptyFileThrowsFormatException()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("empty.stl", Array.Empty<byte>());

        Assert.Throws<GeometryFormatException>(() => StlReader.Read(path));
    }

    [Fact]
    public void GarbageFileThrowsFormatException()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("garbage.stl", "this is not a model, it is a shopping list");

        Assert.Throws<GeometryFormatException>(() => StlReader.Read(path));
    }

    [Fact]
    public void TruncatedBinaryFileThrowsFormatException()
    {
        using var dir = new TempDirectory();
        var full = StlFixtures.BinaryCube();
        // Declares 12 triangles but only carries data for about half of them.
        var path = dir.Write("truncated.stl", full[..(84 + (50 * 5))]);

        Assert.Throws<GeometryFormatException>(() => StlReader.Read(path));
    }

    [Fact]
    public void MissingFileThrowsFileNotFound()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "nope.stl");

        Assert.Throws<FileNotFoundException>(() => StlReader.Read(path));
    }

    [Fact]
    public void CancellationIsObserved()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("strip.stl", StlFixtures.BinaryStrip(500_000));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => StlReader.Read(path, cts.Token));
    }

    [Fact]
    public void RegistryRoutesStlToStlLoader()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("cube.stl", StlFixtures.BinaryCube());
        var registry = GeometryLoaderRegistry.CreateDefault();

        Assert.True(registry.IsSupported(path));
        Assert.True(registry.IsSupported("SHOUTING.STL"));
        Assert.False(registry.IsSupported("model.step"));
        Assert.Equal(StlFixtures.CubeTriangleCount, registry.Load(path).TriangleCount);
    }
}
