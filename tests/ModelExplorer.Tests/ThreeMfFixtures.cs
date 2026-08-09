using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace ModelExplorer.Tests;

/// <summary>
/// Builds 3MF packages in-memory. Same rationale as <see cref="StlFixtures"/>:
/// generated rather than committed as binary blobs, so the expected geometry is
/// readable in the diff. The cube geometry is shared with the STL fixtures so
/// the two readers can be compared directly.
/// </summary>
internal static class ThreeMfFixtures
{
    public const string RootModelRelationship =
        "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel";

    public const string CoreNamespace =
        "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";

    public const string ProductionNamespace =
        "http://schemas.microsoft.com/3dmanufacturing/production/2015/06";

    public const string DefaultModelPart = "3D/3dmodel.model";

    /// <summary>A complete package: root relationships, the model part, and any extras.</summary>
    public static byte[] Package(
        string modelXml,
        string modelPart = DefaultModelPart,
        params (string Name, string Xml)[] extraParts)
    {
        using var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "_rels/.rels", RootRelationships(modelPart));
            AddEntry(archive, modelPart, modelXml);

            foreach (var (name, xml) in extraParts)
            {
                AddEntry(archive, name, xml);
            }
        }

        return stream.ToArray();
    }

    /// <summary>A package with no <c>_rels/.rels</c> at all, to exercise the fallbacks.</summary>
    public static byte[] PackageWithoutRelationships(string modelXml, string modelPart = DefaultModelPart)
    {
        using var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, modelPart, modelXml);
        }

        return stream.ToArray();
    }

    public static string RootRelationships(string modelPart) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
           <Relationship Id="rel0" Type="{RootModelRelationship}" Target="/{modelPart}" />
         </Relationships>
         """;

    public static string Model(string resources, string build, string unit = "millimeter") =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <model unit="{unit}" xml:lang="en-US"
                xmlns="{CoreNamespace}"
                xmlns:p="{ProductionNamespace}">
           <resources>
         {resources}
           </resources>
           <build>
         {build}
           </build>
         </model>
         """;

    /// <summary>The 10 mm cube from <see cref="StlFixtures"/>, as a 3MF object resource.</summary>
    public static string CubeObject(int id = 1)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"    <object id=\"{id}\" type=\"model\">");
        sb.AppendLine("      <mesh>");
        sb.AppendLine("        <vertices>");

        foreach (var v in StlFixtures.CubeCorners)
        {
            sb.AppendLine(FormattableString.Invariant(
                $"          <vertex x=\"{v.X}\" y=\"{v.Y}\" z=\"{v.Z}\" />"));
        }

        sb.AppendLine("        </vertices>");
        sb.AppendLine("        <triangles>");

        foreach (var t in StlFixtures.CubeTriangles)
        {
            sb.AppendLine(FormattableString.Invariant(
                $"          <triangle v1=\"{t[0]}\" v2=\"{t[1]}\" v3=\"{t[2]}\" />"));
        }

        sb.AppendLine("        </triangles>");
        sb.AppendLine("      </mesh>");
        sb.Append("    </object>");
        return sb.ToString();
    }

    public static string Item(int objectId, string? transform = null, string? path = null)
    {
        var attributes = new StringBuilder($"    <item objectid=\"{objectId}\"");
        if (path is not null)
        {
            attributes.Append($" p:path=\"{path}\"");
        }

        if (transform is not null)
        {
            attributes.Append($" transform=\"{transform}\"");
        }

        return attributes.Append(" />").ToString();
    }

    /// <summary>A 4×3 transform attribute, given a 3×3 basis and a translation.</summary>
    public static string Transform(
        float m00 = 1, float m01 = 0, float m02 = 0,
        float m10 = 0, float m11 = 1, float m12 = 0,
        float m20 = 0, float m21 = 0, float m22 = 1,
        float m30 = 0, float m31 = 0, float m32 = 0) =>
        FormattableString.Invariant(
            $"{m00} {m01} {m02} {m10} {m11} {m12} {m20} {m21} {m22} {m30} {m31} {m32}");

    public static string Translation(float x, float y, float z) => Transform(m30: x, m31: y, m32: z);

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>
    /// For a convex solid, every outward face normal points away from the centre.
    /// Cheaper to assert than per-face expectations and catches both a flipped
    /// winding and a normal computed from the wrong corners.
    /// </summary>
    public static void AssertNormalsPointOutward(Vector3[] positions, Vector3[] normals, Vector3 centre)
    {
        for (var t = 0; t < positions.Length; t += 3)
        {
            var faceCentre = (positions[t] + positions[t + 1] + positions[t + 2]) / 3f;
            var outward = Vector3.Dot(normals[t], faceCentre - centre);

            Assert.True(outward > 0f,
                $"triangle {t / 3} normal {normals[t]} points inward at face centre {faceCentre}");
        }
    }
}
