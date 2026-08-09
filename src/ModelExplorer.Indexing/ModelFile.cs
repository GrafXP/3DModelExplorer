namespace ModelExplorer.Indexing;

/// <summary>
/// One indexed model file.
/// </summary>
/// <remarks>
/// The full path is composed on demand from the root's path rather than stored.
/// At 100k files that is 100k fewer strings, and every file under a root shares
/// one <see cref="RootPath"/> instance, so the per-file cost is just the part
/// below the root.
/// </remarks>
public sealed class ModelFile
{
    public required long Id { get; init; }

    public required long RootId { get; init; }

    /// <summary>Shared instance, owned by the root this file was found under.</summary>
    public required string RootPath { get; init; }

    /// <summary>Path below the root, including the file name.</summary>
    public required string RelativePath { get; init; }

    public required long Size { get; init; }

    public required long ModifiedTicks { get; init; }

    public string Name => Path.GetFileName(RelativePath);

    public string FullPath => Path.Join(RootPath, RelativePath);

    public string Folder => Path.GetDirectoryName(FullPath) ?? RootPath;

    public DateTime ModifiedUtc => new(ModifiedTicks, DateTimeKind.Utc);
}

/// <summary>A file the scanner found, before it has been written to the index.</summary>
public readonly record struct ScannedFile(long RootId, string RelativePath, long Size, long ModifiedTicks);

/// <summary>Live scan state, sampled on a timer rather than pushed per file.</summary>
public readonly record struct ScanProgress(long Found, long Indexed, TimeSpan Elapsed, string CurrentRoot)
{
    public double FilesPerSecond => Elapsed.TotalSeconds > 0.001 ? Found / Elapsed.TotalSeconds : 0;
}

/// <summary>The outcome of a completed or cancelled scan.</summary>
public readonly record struct ScanSummary(long Found, long Indexed, TimeSpan Elapsed, bool Cancelled)
{
    public double FilesPerSecond => Elapsed.TotalSeconds > 0.001 ? Found / Elapsed.TotalSeconds : 0;
}
