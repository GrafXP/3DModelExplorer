namespace ModelExplorer.Geometry.ThreeMf;

/// <inheritdoc />
public sealed class ThreeMfLoader : IGeometryLoader
{
    public IReadOnlyList<string> SupportedExtensions { get; } = [".3mf"];

    public bool CanLoad(string path) =>
        Path.GetExtension(path.AsSpan()).Equals(".3mf", StringComparison.OrdinalIgnoreCase);

    public MeshData Load(string path, CancellationToken cancellationToken = default) =>
        ThreeMfReader.Read(path, cancellationToken);
}
