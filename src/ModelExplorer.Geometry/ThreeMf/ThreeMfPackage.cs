using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Xml;

namespace ModelExplorer.Geometry.ThreeMf;

/// <summary>Identifies one object resource. Ids are only unique within a model part.</summary>
internal readonly record struct ObjectKey(string Part, int Id);

/// <summary>A reference from one object to another, with the child's placement.</summary>
internal readonly record struct ComponentRef(ObjectKey Target, Matrix4x4 Transform);

/// <summary>One placement of an object on the build plate.</summary>
internal readonly record struct BuildItem(ObjectKey Target, Matrix4x4 Transform);

/// <summary>
/// An object resource: either a mesh, a list of component references, or both.
/// Vertices stay in the object's own coordinate space; instancing applies the
/// transforms.
/// </summary>
internal sealed class ModelObject
{
    public List<Vector3> Vertices { get; } = [];

    /// <summary>Flat vertex indices, three per triangle.</summary>
    public List<int> Indices { get; } = [];

    /// <summary>Null until the object turns out to have any — most objects don't.</summary>
    public List<ComponentRef>? Components { get; set; }

    /// <summary>Largest index seen, so the mesh can be validated in one comparison.</summary>
    public int HighestIndex { get; set; } = -1;

    public int TriangleCount => Indices.Count / 3;
}

/// <summary>One parsed <c>.model</c> part.</summary>
internal sealed class ModelPart
{
    public required string Name { get; init; }

    /// <summary>Multiplier taking this part's units to millimetres.</summary>
    public float UnitScale { get; set; } = 1f;

    public Dictionary<int, ModelObject> Objects { get; } = [];

    public List<BuildItem> Items { get; } = [];
}

/// <summary>
/// The OPC container: locates model parts inside the zip and parses them on
/// demand.
/// </summary>
/// <remarks>
/// A 3MF is not necessarily one XML document. The production extension lets a
/// package split objects across several <c>.model</c> parts referenced by a
/// <c>path</c> attribute, which is how Bambu Studio and OrcaSlicer write project
/// files. Parts are therefore resolved by name and cached, and object references
/// are keyed by (part, id) rather than by id alone.
/// </remarks>
internal sealed class ThreeMfPackage
{
    private const string RootModelRelationship =
        "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel";

    private const string ConventionalModelPart = "3d/3dmodel.model";

    /// <summary>
    /// A 3MF can arrive from anywhere, including a network share, so the reader
    /// is locked down: no DTD means no entity expansion and no external fetches.
    /// (<see cref="XmlReaderSettings.XmlResolver"/> already defaults to null on
    /// .NET Core, so prohibiting DTDs is the whole of it.)
    /// </summary>
    private static readonly XmlReaderSettings XmlSettings = new()
    {
        IgnoreComments = true,
        IgnoreWhitespace = true,
        IgnoreProcessingInstructions = true,
        DtdProcessing = DtdProcessing.Prohibit,
        CloseInput = true,
    };

    private readonly Dictionary<string, ZipArchiveEntry> _entries;
    private readonly Dictionary<string, ModelPart> _parts = new(StringComparer.Ordinal);

    public ThreeMfPackage(ZipArchive archive, string fileName)
    {
        FileName = fileName;

        _entries = new Dictionary<string, ZipArchiveEntry>(archive.Entries.Count, StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            // Part names are compared case-insensitively; zip entry names are
            // stored case-sensitively. Normalising once here means every later
            // lookup is an ordinal dictionary hit.
            _entries[NormalisePartName(entry.FullName)] = entry;
        }
    }

    /// <summary>File name only — every diagnostic message the user sees quotes it.</summary>
    public string FileName { get; }

    /// <summary>
    /// Finds the model part the package designates as its root.
    /// </summary>
    /// <remarks>
    /// The relationship in <c>_rels/.rels</c> is the specified route. The two
    /// fallbacks exist because packages that have lost or mis-targeted that
    /// relationship still contain perfectly readable geometry, and a viewer
    /// refusing to open a file a slicer opens fine is the worse outcome.
    /// </remarks>
    public string ResolveRootModelPart()
    {
        if (_entries.TryGetValue("_rels/.rels", out var rels))
        {
            var target = FindRootModelRelationship(rels);
            if (target is not null && _entries.ContainsKey(target))
            {
                return target;
            }
        }

        if (_entries.ContainsKey(ConventionalModelPart))
        {
            return ConventionalModelPart;
        }

        // Ordered so the choice is deterministic rather than dependent on zip order.
        foreach (var name in _entries.Keys.Order(StringComparer.Ordinal))
        {
            if (name.EndsWith(".model", StringComparison.Ordinal))
            {
                return name;
            }
        }

        throw new GeometryFormatException(
            $"'{FileName}' contains no 3D model part — it is a zip archive but not a 3MF package.");
    }

    public ModelPart GetPart(string partName, CancellationToken cancellationToken)
    {
        if (_parts.TryGetValue(partName, out var cached))
        {
            return cached;
        }

        if (!_entries.TryGetValue(partName, out var entry))
        {
            throw new GeometryFormatException(
                $"'{FileName}' references model part '{partName}', which is not in the package.");
        }

        var part = ParseModelPart(entry, partName, cancellationToken);
        _parts[partName] = part;
        return part;
    }

    public ModelObject GetObject(in ObjectKey key, CancellationToken cancellationToken)
    {
        var part = GetPart(key.Part, cancellationToken);
        if (!part.Objects.TryGetValue(key.Id, out var value))
        {
            throw new GeometryFormatException(
                $"'{FileName}' references object {key.Id} in '{key.Part}', which does not exist.");
        }

        return value;
    }

    private string? FindRootModelRelationship(ZipArchiveEntry rels)
    {
        using var stream = rels.Open();
        using var reader = XmlReader.Create(stream, XmlSettings);

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element ||
                !string.Equals(reader.LocalName, "Relationship", StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(reader.GetAttribute("Type"), RootModelRelationship, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = reader.GetAttribute("Target");

            // Targets in the package-level rels are resolved against the root.
            return string.IsNullOrEmpty(target) ? null : ResolvePartName(string.Empty, target);
        }

        return null;
    }

    /// <summary>
    /// Streams one model part into objects and build items.
    /// </summary>
    /// <remarks>
    /// Elements are matched on local name and never on namespace. Writers differ
    /// on which prefix they bind the core and extension namespaces to, and some
    /// omit the extension declarations entirely; matching the name is what
    /// actually works across real files. Mesh elements are only acted on while an
    /// <c>&lt;object&gt;</c> is open, which keeps unrelated extension content —
    /// the slice extension also has <c>&lt;vertex&gt;</c> elements — out of the
    /// geometry.
    /// </remarks>
    private ModelPart ParseModelPart(ZipArchiveEntry entry, string partName, CancellationToken cancellationToken)
    {
        var part = new ModelPart { Name = partName };
        var directory = DirectoryOf(partName);

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, XmlSettings);

        ModelObject? current = null;
        var nodes = 0;

        while (reader.Read())
        {
            // A dense part runs to millions of nodes; checking every 64K keeps
            // cancellation responsive without polling in the inner loop.
            if ((++nodes & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (string.Equals(reader.LocalName, "object", StringComparison.Ordinal))
                {
                    ValidateObject(current, partName);
                    current = null;
                }

                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "model":
                    part.UnitScale = ParseUnit(reader.GetAttribute("unit"));
                    break;

                case "object":
                    // A duplicate id is invalid, but taking the later definition
                    // beats failing a whole file over a resource the build may
                    // never reference.
                    current = new ModelObject();
                    part.Objects[ReadInt(reader, "id", partName)] = current;
                    if (reader.IsEmptyElement)
                    {
                        current = null;
                    }

                    break;

                case "vertex":
                    // Tested before reading attributes, which is what keeps the
                    // slice extension's 2D vertices from throwing on their
                    // missing z.
                    if (current is not null)
                    {
                        current.Vertices.Add(ReadVertex(reader, partName));
                    }

                    break;

                case "triangle":
                    if (current is not null)
                    {
                        AddTriangle(current, reader, partName);
                    }

                    break;

                case "component":
                    if (current is not null)
                    {
                        (current.Components ??= []).Add(new ComponentRef(
                            ReadReference(reader, directory, partName),
                            ParseTransform(reader.GetAttribute("transform"), partName)));
                    }

                    break;

                case "item":
                    part.Items.Add(new BuildItem(
                        ReadReference(reader, directory, partName),
                        ParseTransform(reader.GetAttribute("transform"), partName)));
                    break;
            }
        }

        return part;
    }

    /// <summary>
    /// Checks the mesh once the object is complete. Triangles may legally be
    /// declared before their vertices, so this cannot be a per-triangle test.
    /// </summary>
    private static void ValidateObject(ModelObject? current, string partName)
    {
        if (current is null || current.HighestIndex < current.Vertices.Count)
        {
            return;
        }

        throw new GeometryFormatException(
            $"A mesh in '{partName}' indexes vertex {current.HighestIndex} but declares only {current.Vertices.Count}.");
    }

    /// <summary>
    /// Reads a vertex in a single pass over its attributes.
    /// </summary>
    /// <remarks>
    /// <see cref="XmlReader.GetAttribute(string)"/> rescans the element's whole
    /// attribute list on each call. That is twelve scans per triangle across the
    /// vertex and triangle elements, and on a million-triangle part it is most of
    /// the parse time. Walking the list once and dispatching on the name is the
    /// same shape of code and measurably faster.
    /// </remarks>
    private static Vector3 ReadVertex(XmlReader reader, string partName)
    {
        float x = 0f, y = 0f, z = 0f;
        var seen = 0;

        if (reader.MoveToFirstAttribute())
        {
            do
            {
                switch (reader.LocalName)
                {
                    case "x":
                        x = ParseFloat(reader, partName);
                        seen |= 0b001;
                        break;
                    case "y":
                        y = ParseFloat(reader, partName);
                        seen |= 0b010;
                        break;
                    case "z":
                        z = ParseFloat(reader, partName);
                        seen |= 0b100;
                        break;
                }
            }
            while (reader.MoveToNextAttribute());

            reader.MoveToElement();
        }

        if (seen != 0b111)
        {
            throw new GeometryFormatException($"A <vertex> in '{partName}' is missing a coordinate.");
        }

        return new Vector3(x, y, z);
    }

    private static void AddTriangle(ModelObject target, XmlReader reader, string partName)
    {
        int v1 = 0, v2 = 0, v3 = 0;
        var seen = 0;

        if (reader.MoveToFirstAttribute())
        {
            do
            {
                // Material attributes (pid, p1..p3) fall through: 3MF colour and
                // texture data has no bearing on the geometry.
                switch (reader.LocalName)
                {
                    case "v1":
                        v1 = ParseInt(reader, partName);
                        seen |= 0b001;
                        break;
                    case "v2":
                        v2 = ParseInt(reader, partName);
                        seen |= 0b010;
                        break;
                    case "v3":
                        v3 = ParseInt(reader, partName);
                        seen |= 0b100;
                        break;
                }
            }
            while (reader.MoveToNextAttribute());

            reader.MoveToElement();
        }

        if (seen != 0b111)
        {
            throw new GeometryFormatException($"A <triangle> in '{partName}' is missing a vertex reference.");
        }

        if (v1 < 0 || v2 < 0 || v3 < 0)
        {
            throw new GeometryFormatException($"A mesh in '{partName}' has a negative vertex index.");
        }

        target.Indices.Add(v1);
        target.Indices.Add(v2);
        target.Indices.Add(v3);
        target.HighestIndex = Math.Max(target.HighestIndex, Math.Max(v1, Math.Max(v2, v3)));
    }

    /// <summary>Reads the <c>objectid</c>, plus the production extension's optional <c>path</c>.</summary>
    private static ObjectKey ReadReference(XmlReader reader, string directory, string partName)
    {
        var id = ReadInt(reader, "objectid", partName);
        var path = ReadPathAttribute(reader);

        return new ObjectKey(
            string.IsNullOrEmpty(path) ? partName : ResolvePartName(directory, path),
            id);
    }

    /// <summary>
    /// Matched by local name because the prefix bound to the production namespace
    /// varies between writers, and some bind it without declaring the extension.
    /// </summary>
    private static string? ReadPathAttribute(XmlReader reader)
    {
        if (!reader.HasAttributes)
        {
            return null;
        }

        string? value = null;
        for (var i = 0; i < reader.AttributeCount; i++)
        {
            reader.MoveToAttribute(i);
            if (string.Equals(reader.LocalName, "path", StringComparison.Ordinal))
            {
                value = reader.Value;
                break;
            }
        }

        reader.MoveToElement();
        return value;
    }

    /// <summary>
    /// Model units to millimetres, matching the dimensions a slicer reports.
    /// </summary>
    /// <remarks>
    /// An unrecognised unit falls back to the spec default of millimetre.
    /// Guessing a factor would be worse than showing raw numbers.
    /// </remarks>
    private static float ParseUnit(string? unit) => unit?.ToLowerInvariant() switch
    {
        "micron" => 0.001f,
        "centimeter" => 10f,
        "inch" => 25.4f,
        "foot" => 304.8f,
        "meter" => 1000f,
        _ => 1f,
    };

    /// <summary>
    /// Parses the twelve-value transform attribute.
    /// </summary>
    /// <remarks>
    /// 3MF writes a 4×3 matrix in row-major order, the fourth column being an
    /// implicit (0 0 0 1), and composes it with row vectors — v' = v × M. That is
    /// exactly <see cref="Matrix4x4"/>'s own convention, so the values drop
    /// straight in and <see cref="Vector3.Transform(Vector3, Matrix4x4)"/> gives
    /// the right answer with no transposition.
    /// </remarks>
    private static Matrix4x4 ParseTransform(string? raw, string partName)
    {
        if (raw is null)
        {
            return Matrix4x4.Identity;
        }

        var span = raw.AsSpan().Trim();
        if (span.IsEmpty)
        {
            return Matrix4x4.Identity;
        }

        Span<float> m = stackalloc float[12];
        var count = 0;

        while (count < m.Length && !span.IsEmpty)
        {
            var separator = span.IndexOfAny(" \t\r\n");
            var token = separator < 0 ? span : span[..separator];

            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out m[count]))
            {
                throw new GeometryFormatException($"Malformed transform '{raw}' in '{partName}'.");
            }

            count++;
            span = separator < 0 ? [] : span[(separator + 1)..].TrimStart();
        }

        if (count != 12 || !span.IsEmpty)
        {
            throw new GeometryFormatException(
                $"Transform '{raw}' in '{partName}' does not have the required 12 values.");
        }

        return new Matrix4x4(
            m[0], m[1], m[2], 0f,
            m[3], m[4], m[5], 0f,
            m[6], m[7], m[8], 0f,
            m[9], m[10], m[11], 1f);
    }

    private static int ReadInt(XmlReader reader, string name, string partName)
    {
        var raw = reader.GetAttribute(name);
        if (raw is null ||
            !int.TryParse(raw.AsSpan().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new GeometryFormatException(
                $"<{reader.LocalName}> in '{partName}' has a missing or malformed '{name}' attribute.");
        }

        return value;
    }

    /// <summary>Parses the attribute the reader is currently positioned on.</summary>
    private static float ParseFloat(XmlReader reader, string partName)
    {
        var raw = reader.Value;
        if (!float.TryParse(raw.AsSpan().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new GeometryFormatException(
                $"Attribute '{reader.LocalName}' in '{partName}' has a malformed number '{raw}'.");
        }

        return value;
    }

    /// <inheritdoc cref="ParseFloat" />
    private static int ParseInt(XmlReader reader, string partName)
    {
        var raw = reader.Value;
        if (!int.TryParse(raw.AsSpan().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new GeometryFormatException(
                $"Attribute '{reader.LocalName}' in '{partName}' has a malformed integer '{raw}'.");
        }

        return value;
    }

    /// <summary>Lookup key for a part name: forward slashes, no leading slash, lower case.</summary>
    private static string NormalisePartName(string name) =>
        name.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    private static string DirectoryOf(string partName)
    {
        var slash = partName.LastIndexOf('/');
        return slash < 0 ? string.Empty : partName[..slash];
    }

    /// <summary>
    /// Resolves a relationship or <c>path</c> target against the referring part's
    /// directory, collapsing <c>.</c> and <c>..</c> segments.
    /// </summary>
    private static string ResolvePartName(string baseDirectory, string target)
    {
        var combined = target.Replace('\\', '/');
        combined = combined.StartsWith('/') || baseDirectory.Length == 0
            ? combined
            : baseDirectory + "/" + combined;

        var segments = new List<string>();
        foreach (var segment in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    break;
                case "..":
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    break;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        return string.Join('/', segments).ToLowerInvariant();
    }
}
