namespace ModelExplorer.Indexing;

/// <summary>A folder the user has added to the library.</summary>
/// <param name="LastScanUtc">
/// Null until a scan of this root has run to completion. A cancelled scan leaves
/// it null, so the root is known to be only partly indexed.
/// </param>
public sealed record LibraryRoot(
    long Id,
    string Path,
    bool IsNetwork,
    DateTime AddedUtc,
    DateTime? LastScanUtc)
{
    /// <summary>Leaf folder name, falling back to the whole path for a drive root.</summary>
    public string DisplayName =>
        System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name
            ? name
            : Path;

    /// <summary>
    /// Whether a path lives on a network share, which halves the scan pipeline in
    /// two: local roots must never wait behind a slow NAS.
    /// </summary>
    /// <remarks>
    /// UNC paths are decided by shape. Mapped drive letters need
    /// <see cref="DriveInfo"/>, which touches the drive and can throw on one that
    /// has gone away — treating that as local is harmless, because a root that
    /// cannot be reached yields nothing either way.
    /// </remarks>
    public static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        var root = System.IO.Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
