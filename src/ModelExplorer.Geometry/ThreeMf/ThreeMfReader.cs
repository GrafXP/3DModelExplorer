using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Xml;

namespace ModelExplorer.Geometry.ThreeMf;

/// <summary>
/// Reads 3MF packages: resolves the build plate into a single flattened mesh in
/// millimetres, with component hierarchies and instance transforms applied.
/// </summary>
public static class ThreeMfReader
{
    /// <summary>
    /// Component nesting is shallow in practice — an assembly of assemblies is
    /// two or three levels. The limit only exists to stop a pathological file
    /// from recursing until the stack gives out.
    /// </summary>
    private const int MaxComponentDepth = 64;

    /// <summary>Three indices per triangle still have to address with an int.</summary>
    private const long MaxTriangles = int.MaxValue / 3;

    public static MeshData Read(string path, CancellationToken cancellationToken = default)
    {
        var fileName = Path.GetFileName(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("3MF file not found.", path);
        }

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            throw new GeometryFormatException(
                $"'{fileName}' is not a 3MF package — it is not a readable zip archive.", ex);
        }

        using (archive)
        {
            try
            {
                var package = new ThreeMfPackage(archive, fileName);
                var root = package.GetPart(package.ResolveRootModelPart(), cancellationToken);

                // Applied outermost so build-item translations scale with the model
                // instead of staying in the document's own units. All model parts in
                // a package share the root's unit, so one scale covers the package.
                var unitScale = Matrix4x4.CreateScale(root.UnitScale);

                var instances = new List<Instance>();
                var stack = new HashSet<ObjectKey>();
                var triangles = 0L;

                foreach (var item in ResolveBuild(root))
                {
                    Flatten(package, item.Target, item.Transform * unitScale,
                        instances, stack, ref triangles, 0, cancellationToken);
                }

                return Assemble(instances, triangles, cancellationToken);
            }
            catch (XmlException ex)
            {
                // Callers distinguish "corrupt file" from "unreadable file" by
                // exception type, so a broken document has to arrive as a format
                // error rather than as a raw XmlException nothing is looking for.
                throw new GeometryFormatException($"'{fileName}' contains malformed XML: {ex.Message}", ex);
            }
            catch (InvalidDataException ex)
            {
                // A part whose compressed data is damaged; the archive's directory
                // was intact enough to open.
                throw new GeometryFormatException($"'{fileName}' is corrupt: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// The declared build items, or — when a package declares none — every object
    /// no component references.
    /// </summary>
    /// <remarks>
    /// An absent or empty <c>&lt;build&gt;</c> is invalid, but the geometry is
    /// still in the file and a viewer that shows nothing is no help. Objects that
    /// are referenced as components are excluded so an assembly is not drawn once
    /// through its parent and again on its own.
    /// </remarks>
    private static IReadOnlyList<BuildItem> ResolveBuild(ModelPart root)
    {
        if (root.Items.Count > 0)
        {
            return root.Items;
        }

        var referenced = new HashSet<int>();
        foreach (var (_, value) in root.Objects)
        {
            foreach (var component in value.Components ?? [])
            {
                if (string.Equals(component.Target.Part, root.Name, StringComparison.Ordinal))
                {
                    referenced.Add(component.Target.Id);
                }
            }
        }

        return
        [
            .. root.Objects.Keys
                .Where(id => !referenced.Contains(id))
                .Order()
                .Select(id => new BuildItem(new ObjectKey(root.Name, id), Matrix4x4.Identity)),
        ];
    }

    /// <summary>
    /// Walks an object's component tree, accumulating one entry per mesh instance
    /// with its fully composed transform.
    /// </summary>
    private static void Flatten(
        ThreeMfPackage package,
        ObjectKey key,
        Matrix4x4 transform,
        List<Instance> instances,
        HashSet<ObjectKey> stack,
        ref long triangles,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (depth > MaxComponentDepth)
        {
            throw new GeometryFormatException(
                $"'{package.FileName}' nests components more than {MaxComponentDepth} levels deep.");
        }

        // The stack, not a visited set: the same object legitimately appears many
        // times in one build. Only re-entering it while it is still being expanded
        // is a cycle.
        if (!stack.Add(key))
        {
            throw new GeometryFormatException(
                $"Object {key.Id} in '{package.FileName}' contains itself through <components>.");
        }

        try
        {
            var target = package.GetObject(key, cancellationToken);

            if (target.TriangleCount > 0)
            {
                triangles += target.TriangleCount;
                if (triangles > MaxTriangles)
                {
                    throw new GeometryFormatException(
                        $"'{package.FileName}' expands to more than {MaxTriangles:N0} triangles.");
                }

                instances.Add(new Instance(target, transform));
            }

            foreach (var component in target.Components ?? [])
            {
                // Row-vector convention: the child's placement applies first, so
                // it multiplies on the left.
                Flatten(package, component.Target, component.Transform * transform,
                    instances, stack, ref triangles, depth + 1, cancellationToken);
            }
        }
        finally
        {
            stack.Remove(key);
        }
    }

    /// <summary>
    /// Transforms every instance into one unwelded vertex buffer.
    /// </summary>
    /// <remarks>
    /// Deliberately single-threaded, unlike the binary STL path. Decompressing and
    /// parsing the XML dominates a 3MF load by an order of magnitude, so the win
    /// from parallelising the transform pass would not be measurable.
    /// </remarks>
    private static MeshData Assemble(List<Instance> instances, long triangles, CancellationToken cancellationToken)
    {
        if (triangles == 0)
        {
            return MeshData.Empty;
        }

        var vertexCount = (int)(triangles * 3);
        var positions = GC.AllocateUninitializedArray<Vector3>(vertexCount);
        var normals = GC.AllocateUninitializedArray<Vector3>(vertexCount);
        var indices = GC.AllocateUninitializedArray<int>(vertexCount);
        var bounds = BoundingBox.Empty;
        var write = 0;

        foreach (var instance in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var transform = instance.Transform;

            // A negative determinant means the instance is mirrored, which
            // reverses the triangle winding. Swapping two corners puts it back to
            // counter-clockwise-seen-from-outside so the recomputed normal still
            // points out of the solid instead of into it.
            var mirrored = transform.GetDeterminant() < 0;

            var vertices = CollectionsMarshal.AsSpan(instance.Object.Vertices);
            var triangleIndices = CollectionsMarshal.AsSpan(instance.Object.Indices);

            for (var t = 0; t < triangleIndices.Length; t += 3)
            {
                var a = Vector3.Transform(vertices[triangleIndices[t]], transform);
                var b = Vector3.Transform(vertices[triangleIndices[t + 1]], transform);
                var c = Vector3.Transform(vertices[triangleIndices[t + 2]], transform);

                if (mirrored)
                {
                    (b, c) = (c, b);
                }

                // 3MF supplies no normals at all, so unlike STL there is nothing
                // to fall back to on a degenerate triangle.
                var normal = Vector3.Cross(b - a, c - a);
                var lengthSquared = normal.LengthSquared();
                normal = lengthSquared > 1e-20f
                    ? normal * (1f / MathF.Sqrt(lengthSquared))
                    : Vector3.UnitZ;

                positions[write] = a;
                positions[write + 1] = b;
                positions[write + 2] = c;
                normals[write] = normal;
                normals[write + 1] = normal;
                normals[write + 2] = normal;
                indices[write] = write;
                indices[write + 1] = write + 1;
                indices[write + 2] = write + 2;

                bounds = bounds.Union(a).Union(b).Union(c);
                write += 3;
            }
        }

        return new MeshData
        {
            Positions = positions,
            Normals = normals,
            Indices = indices,
            Bounds = bounds,
        };
    }

    /// <summary>One placement of one mesh, with the transforms above it composed in.</summary>
    private readonly record struct Instance(ModelObject Object, Matrix4x4 Transform);
}
