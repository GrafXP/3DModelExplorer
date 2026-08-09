namespace ModelExplorer.Geometry.Stl;

/// <inheritdoc />
public sealed class StlLoader : IGeometryLoader
{
    public IReadOnlyList<string> SupportedExtensions { get; } = [".stl"];

    public bool CanLoad(string path) =>
        Path.GetExtension(path.AsSpan()).Equals(".stl", StringComparison.OrdinalIgnoreCase);

    public MeshData Load(string path, CancellationToken cancellationToken = default) =>
        StlReader.Read(path, cancellationToken);
}
