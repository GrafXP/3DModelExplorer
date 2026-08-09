namespace ModelExplorer.Geometry;

/// <summary>
/// Loads a mesh from a single file. One implementation per file format.
/// </summary>
/// <remarks>
/// Everything downstream — the viewer, the thumbnail renderer, the indexer —
/// talks to this interface rather than to a specific format. Adding STEP later
/// means adding one implementation and registering it; nothing else changes.
/// </remarks>
public interface IGeometryLoader
{
    /// <summary>Lower-case extensions including the leading dot, e.g. ".stl".</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Cheap extension test. A true result is a candidate, not a guarantee —
    /// <see cref="Load"/> still validates the actual content.
    /// </summary>
    bool CanLoad(string path);

    /// <summary>
    /// Reads the file into a mesh.
    /// </summary>
    /// <exception cref="GeometryFormatException">The file is malformed or not the expected format.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    MeshData Load(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised when a file cannot be interpreted as its apparent format. Distinct
/// from <see cref="IOException"/> so the UI can tell "file is corrupt" apart
/// from "file could not be read".
/// </summary>
public sealed class GeometryFormatException : Exception
{
    public GeometryFormatException(string message)
        : base(message)
    {
    }

    public GeometryFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
