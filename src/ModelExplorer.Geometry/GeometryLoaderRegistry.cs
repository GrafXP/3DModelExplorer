using ModelExplorer.Geometry.Stl;
using ModelExplorer.Geometry.ThreeMf;

namespace ModelExplorer.Geometry;

/// <summary>
/// Routes a file to the loader that handles its format.
/// </summary>
public sealed class GeometryLoaderRegistry
{
    private readonly List<IGeometryLoader> _loaders;

    public GeometryLoaderRegistry(IEnumerable<IGeometryLoader> loaders)
    {
        _loaders = [.. loaders];
        SupportedExtensions = [.. _loaders
            .SelectMany(l => l.SupportedExtensions)
            .Select(e => e.ToLowerInvariant())
            .Distinct()
            .Order()];
    }

    /// <summary>The registry the app runs with.</summary>
    public static GeometryLoaderRegistry CreateDefault() => new([new StlLoader(), new ThreeMfLoader()]);

    public IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>A file dialog filter covering every registered format.</summary>
    public string BuildFileDialogFilter()
    {
        var all = string.Join(';', SupportedExtensions.Select(e => $"*{e}"));
        var perFormat = SupportedExtensions.Select(e => $"{e.TrimStart('.').ToUpperInvariant()} files (*{e})|*{e}");
        return string.Join('|', [$"3D models ({all})|{all}", .. perFormat, "All files (*.*)|*.*"]);
    }

    public bool IsSupported(string path) => _loaders.Any(l => l.CanLoad(path));

    public MeshData Load(string path, CancellationToken cancellationToken = default)
    {
        foreach (var loader in _loaders)
        {
            if (loader.CanLoad(path))
            {
                return loader.Load(path, cancellationToken);
            }
        }

        throw new GeometryFormatException(
            $"No loader registered for '{Path.GetExtension(path)}' files.");
    }
}
