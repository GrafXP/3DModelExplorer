using System.Numerics;
using System.Text;
using ModelExplorer.Geometry;
using ModelExplorer.Geometry.Stl;
using ModelExplorer.Geometry.ThreeMf;

namespace ModelExplorer.Tests;

public class ThreeMfReaderTests
{
    private static readonly Vector3 CubeCentre = new(5, 5, 5);

    [Fact]
    public void ReadsSingleObject()
    {
        var path = WriteCube(out var dir);
        using (dir)
        {
            var mesh = ThreeMfReader.Read(path);

            Assert.Equal(StlFixtures.CubeTriangleCount, mesh.TriangleCount);
            Assert.Equal(StlFixtures.CubeTriangleCount * 3, mesh.VertexCount);
            Assert.Equal(new Vector3(0, 0, 0), mesh.Bounds.Min);
            Assert.Equal(new Vector3(10, 10, 10), mesh.Bounds.Max);
            ThreeMfFixtures.AssertNormalsPointOutward(mesh.Positions, mesh.Normals, CubeCentre);
        }
    }

    /// <summary>
    /// The two readers feed the same viewer, so the same cube has to come out of
    /// both identically — same winding, same normals, same bounds.
    /// </summary>
    [Fact]
    public void AgreesWithTheStlReaderOnTheSameCube()
    {
        using var dir = new TempDirectory();
        var stl = StlReader.Read(dir.Write("cube.stl", StlFixtures.BinaryCube()));
        var threeMf = ThreeMfReader.Read(dir.Write("cube.3mf", ThreeMfFixtures.Package(
            ThreeMfFixtures.Model(ThreeMfFixtures.CubeObject(), ThreeMfFixtures.Item(1)))));

        Assert.Equal(stl.TriangleCount, threeMf.TriangleCount);
        Assert.Equal(stl.Bounds, threeMf.Bounds);

        for (var i = 0; i < stl.VertexCount; i++)
        {
            Assert.True(Vector3.Distance(stl.Positions[i], threeMf.Positions[i]) < 1e-4f,
                $"vertex {i} differs: {stl.Positions[i]} vs {threeMf.Positions[i]}");
            Assert.True(Vector3.Distance(stl.Normals[i], threeMf.Normals[i]) < 1e-4f,
                $"normal {i} differs: {stl.Normals[i]} vs {threeMf.Normals[i]}");
        }
    }

    [Fact]
    public void AppliesBuildItemTransform()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("moved.3mf", ThreeMfFixtures.Package(ThreeMfFixtures.Model(
            ThreeMfFixtures.CubeObject(),
            ThreeMfFixtures.Item(1, ThreeMfFixtures.Translation(100, -20, 5)))));

        var mesh = ThreeMfReader.Read(path);

        Assert.Equal(new Vector3(100, -20, 5), mesh.Bounds.Min);
        Assert.Equal(new Vector3(110, -10, 15), mesh.Bounds.Max);
    }

    /// <summary>
    /// Scale has to reach the item transform as well as the geometry — a cube
    /// placed one inch along X sits at 25.4 mm, not at 1 mm.
    /// </summary>
    [Theory]
    [InlineData("millimeter", 1f)]
    [InlineData("centimeter", 10f)]
    [InlineData("inch", 25.4f)]
    [InlineData("micron", 0.001f)]
    [InlineData("meter", 1000f)]
    public void HonoursUnitAttribute(string unit, float scale)
    {
        using var dir = new TempDirectory();
        var path = dir.Write("units.3mf", ThreeMfFixtures.Package(ThreeMfFixtures.Model(
            ThreeMfFixtures.CubeObject(),
            ThreeMfFixtures.Item(1, ThreeMfFixtures.Translation(1, 0, 0)),
            unit)));

        var mesh = ThreeMfReader.Read(path);

        AssertClose(new Vector3(1 * scale, 0, 0), mesh.Bounds.Min);
        AssertClose(new Vector3(11 * scale, 10 * scale, 10 * scale), mesh.Bounds.Max);
    }

    /// <summary>An unknown unit falls back to the spec default rather than failing the load.</summary>
    [Fact]
    public void UnknownUnitIsTreatedAsMillimetres()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("odd.3mf", ThreeMfFixtures.Package(ThreeMfFixtures.Model(
            ThreeMfFixtures.CubeObject(), ThreeMfFixtures.Item(1), unit: "furlong")));

        Assert.Equal(new Vector3(10, 10, 10), ThreeMfReader.Read(path).Bounds.Max);
    }

    [Fact]
    public void ResolvesComponentsAndTheirTransforms()
    {
        var resources = new StringBuilder()
            .AppendLine(ThreeMfFixtures.CubeObject(1))
            .AppendLine("    <object id=\"2\" type=\"model\">")
            .AppendLine("      <components>")
            .AppendLine("        <component objectid=\"1\" />")
            .AppendLine($"        <component objectid=\"1\" transform=\"{ThreeMfFixtures.Translation(20, 0, 0)}\" />")
            .AppendLine("      </components>")
            .Append("    </object>")
            .ToString();

        using var dir = new TempDirectory();
        var path = dir.Write("assembly.3mf", ThreeMfFixtures.Package(
            ThreeMfFixtures.Model(resources, ThreeMfFixtures.Item(2))));

        var mesh = ThreeMfReader.Read(path);

        Assert.Equal(StlFixtures.CubeTriangleCount * 2, mesh.TriangleCount);
        Assert.Equal(new Vector3(0, 0, 0), mesh.Bounds.Min);
        Assert.Equal(new Vector3(30, 10, 10), mesh.Bounds.Max);
    }

    /// <summary>
    /// Component and item transforms have to compose in the right order: an
    /// assembly translated on the plate moves its parts with it.
    /// </summary>
    [Fact]
    public void ComposesComponentTransformUnderTheBuildItem()
    {
        var resources = new StringBuilder()
            .AppendLine(ThreeMfFixtures.CubeObject(1))
            .AppendLine("    <object id=\"2\" type=\"model\">")
            .AppendLine("      <components>")
            .AppendLine($"        <component objectid=\"1\" transform=\"{ThreeMfFixtures.Translation(0, 0, 100)}\" />")
            .AppendLine("      </components>")
            .Append("    </object>")
            .ToString();

        using var dir = new TempDirectory();
        var path = dir.Write("nested.3mf", ThreeMfFixtures.Package(ThreeMfFixtures.Model(
            resources, ThreeMfFixtures.Item(2, ThreeMfFixtures.Translation(50, 0, 0)))));

        var mesh = ThreeMfReader.Read(path);

        Assert.Equal(new Vector3(50, 0, 100), mesh.Bounds.Min);
        Assert.Equal(new Vector3(60, 10, 110), mesh.Bounds.Max);
    }

    /// <summary>
    /// A mirrored instance reverses triangle winding. Left uncorrected the whole
    /// part renders inside-out — lit from within and invisible where backfaces
    /// are culled.
    /// </summary>
    [Fact]
    public void MirroredInstanceStillFacesOutward()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("mirror.3mf", ThreeMfFixtures.Package(ThreeMfFixtures.Model(
            ThreeMfFixtures.CubeObject(),
            ThreeMfFixtures.Item(1, ThreeMfFixtures.Transform(m00: -1, m30: 10)))));

        var mesh = ThreeMfReader.Read(path);

        // The mirror maps x to 10 - x, so the cube lands back on itself.
        AssertClose(new Vector3(0, 0, 0), mesh.Bounds.Min);
        AssertClose(new Vector3(10, 10, 10), mesh.Bounds.Max);
        ThreeMfFixtures.AssertNormalsPointOutward(mesh.Positions, mesh.Normals, CubeCentre);
    }

    [Fact]
    public void ResolvesModelPartFromTheRootRelationship()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("renamed.3mf", ThreeMfFixtures.Package(
            ThreeMfFixtures.Model(ThreeMfFixtures.CubeObject(), ThreeMfFixtures.Item(1)),
            modelPart: "3D/some other name.model"));

        Assert.Equal(StlFixtures.CubeTriangleCount, ThreeMfReader.Read(path).TriangleCount);
    }

    [Fact]
    public void FallsBackToTheConventionalPartWhenRelationshipsAreMissing()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("norels.3mf", ThreeMfFixtures.PackageWithoutRelationships(
            ThreeMfFixtures.Model(ThreeMfFixtures.CubeObject(), ThreeMfFixtures.Item(1))));

        Assert.Equal(StlFixtures.CubeTriangleCount, ThreeMfReader.Read(path).TriangleCount);
    }

    /// <summary>
    /// The production extension splits objects across model parts. Bambu Studio
    /// and OrcaSlicer write project files this way, so this is the common case
    /// for multi-part plates, not an exotic one.
    /// </summary>
    [Fact]
    public void ResolvesObjectsInOtherModelPartsViaPath()
    {
        const string ObjectPart = "3D/Objects/object_1.model";

        using var dir = new TempDirectory();
        var path = dir.Write("production.3mf", ThreeMfFixtures.Package(
            ThreeMfFixtures.Model(
                resources: string.Empty,
                build: ThreeMfFixtures.Item(1, ThreeMfFixtures.Translation(7, 0, 0), path: "/" + ObjectPart)),
            ThreeMfFixtures.DefaultModelPart,
            (ObjectPart, ThreeMfFixtures.Model(ThreeMfFixtures.CubeObject(), build: string.Empty))));

        var mesh = ThreeMfReader.Read(path);

        Assert.Equal(StlFixtures.CubeTriangleCount, mesh.TriangleCount);
        Assert.Equal(new Vector3(7, 0, 0), mesh.Bounds.Min);
    }

    /// <summary>
    /// An empty build is invalid, but the geometry is right there. Showing it
    /// beats showing an empty viewport.
    /// </summary>
    [Fact]
    public void FallsBackToUnreferencedObjectsWhenBuildIsEmpty()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("nobuild.3mf", ThreeMfFixtures.Package(
            ThreeMfFixtures.Model(ThreeMfFixtures.CubeObject(), build: string.Empty)));

        Assert.Equal(StlFixtures.CubeTriangleCount, ThreeMfReader.Read(path).TriangleCount);
    }

    /// <summary>An object reached only through a component must not also be drawn on its own.</summary>
    [Fact]
    public void EmptyBuildFallbackSkipsObjectsUsedAsComponents()
    {
        var resources = new StringBuilder()
            .AppendLine(ThreeMfFixtures.CubeObject(1))
            .AppendLine("    <object id=\"2\" type=\"model\">")
            .AppendLine("      <components>")
            .AppendLine("        <component objectid=\"1\" />")
            .AppendLine("      </components>")
            .Append("    </object>")
            .ToString();

        using var dir = new TempDirectory();
        var path = dir.Write("implicit.3mf", ThreeMfFixtures.Package(
            ThreeMfFixtures.Model(resources, build: string.Empty)));

        Assert.Equal(StlFixtures.CubeTriangleCount, ThreeMfReader.Read(path).TriangleCount);
    }

    [Fact]
    public void DetectsComponentCycles()
    {
        var resources = new StringBuilder()
            .AppendLine("    <object id=\"1\" type=\"model\">")
            .AppendLine("      <components><component objectid=\"2\" /></components>")
            .AppendLine("    </object>")
            .AppendLine("    <object id=\"2\" type=\"model\">")
            .AppendLine("      <components><component objectid=\"1\" /></components>")
            .Append("    </object>")
            .ToString();

        using var dir = new TempDirectory();
        var path = dir.Write("cycle.3mf", ThreeMfFixtures.Package(
            ThreeMfFixtures.Model(resources, ThreeMfFixtures.Item(1))));

        var ex = Assert.Throws<GeometryFormatException>(() => ThreeMfReader.Read(path));
        Assert.Contains("itself", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsTrianglesIndexingMissingVertices()
    {
        var resources = """
                            <object id="1" type="model">
                              <mesh>
                                <vertices>
                                  <vertex x="0" y="0" z="0" />
                                  <vertex x="1" y="0" z="0" />
                                  <vertex x="0" y="1" z="0" />
                                </vertices>
                                <triangles>
                                  <triangle v1="0" v2="1" v3="9" />
                                </triangles>
                              </mesh>
                            </object>
                        """;

        using var dir = new TempDirectory();
        var path = dir.Write("bad-index.3mf", ThreeMfFixtures.Package(
            ThreeMfFixtures.Model(resources, ThreeMfFixtures.Item(1))));

        Assert.Throws<GeometryFormatException>(() => ThreeMfReader.Read(path));
    }

    [Fact]
    public void RejectsAnObjectReferenceThatDoesNotExist()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("dangling.3mf", ThreeMfFixtures.Package(
            ThreeMfFixtures.Model(ThreeMfFixtures.CubeObject(1), ThreeMfFixtures.Item(42))));

        Assert.Throws<GeometryFormatException>(() => ThreeMfReader.Read(path));
    }

    [Fact]
    public void RejectsAFileThatIsNotAZipArchive()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("fake.3mf", "this is plainly not a 3MF package"u8.ToArray());

        var ex = Assert.Throws<GeometryFormatException>(() => ThreeMfReader.Read(path));
        Assert.Contains("fake.3mf", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A truncated document has to surface as a format error. The viewer tells
    /// "corrupt file" from "unreadable file" by exception type, and a raw
    /// XmlException is caught by neither.
    /// </summary>
    [Fact]
    public void ReportsMalformedXmlAsAFormatError()
    {
        var truncated = ThreeMfFixtures.Model(ThreeMfFixtures.CubeObject(), ThreeMfFixtures.Item(1));
        truncated = truncated[..(truncated.Length / 2)];

        using var dir = new TempDirectory();
        var path = dir.Write("truncated.3mf", ThreeMfFixtures.Package(truncated));

        var ex = Assert.Throws<GeometryFormatException>(() => ThreeMfReader.Read(path));
        Assert.IsType<System.Xml.XmlException>(ex.InnerException);
    }

    [Fact]
    public void RejectsAZipThatContainsNoModelPart()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("empty.3mf", ThreeMfFixtures.PackageWithoutRelationships(
            "not xml", modelPart: "readme.txt"));

        Assert.Throws<GeometryFormatException>(() => ThreeMfReader.Read(path));
    }

    /// <summary>An object with no geometry is a valid, if pointless, package.</summary>
    [Fact]
    public void ReadsAPackageWithNoTrianglesAsAnEmptyMesh()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("hollow.3mf", ThreeMfFixtures.Package(ThreeMfFixtures.Model(
            "    <object id=\"1\" type=\"model\"><mesh><vertices /><triangles /></mesh></object>",
            ThreeMfFixtures.Item(1))));

        Assert.Equal(0, ThreeMfReader.Read(path).TriangleCount);
    }

    [Fact]
    public void CancellationStopsTheLoad()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("cancel.3mf", ThreeMfFixtures.Package(
            ThreeMfFixtures.Model(ThreeMfFixtures.CubeObject(), ThreeMfFixtures.Item(1))));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => ThreeMfReader.Read(path, cancellation.Token));
    }

    [Fact]
    public void RegistryRoutesThreeMfFilesToTheThreeMfLoader()
    {
        var registry = GeometryLoaderRegistry.CreateDefault();

        Assert.Contains(".3mf", registry.SupportedExtensions);
        Assert.True(registry.IsSupported(@"C:\models\thing.3MF"));

        using var dir = new TempDirectory();
        var path = dir.Write("via-registry.3mf", ThreeMfFixtures.Package(
            ThreeMfFixtures.Model(ThreeMfFixtures.CubeObject(), ThreeMfFixtures.Item(1))));

        Assert.Equal(StlFixtures.CubeTriangleCount, registry.Load(path).TriangleCount);
    }

    private static string WriteCube(out TempDirectory directory)
    {
        directory = new TempDirectory();
        return directory.Write("cube.3mf", ThreeMfFixtures.Package(
            ThreeMfFixtures.Model(ThreeMfFixtures.CubeObject(), ThreeMfFixtures.Item(1))));
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"expected {expected}, got {actual}");
    }
}
