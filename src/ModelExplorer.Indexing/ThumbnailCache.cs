namespace ModelExplorer.Indexing;

/// <summary>
/// Thumbnails on disk, one PNG per content key.
/// </summary>
/// <remarks>
/// Files rather than BLOBs in the index. A 100k-model library is 100k small
/// images; keeping them out of the database means the index stays a few MB and
/// loads in one read, the OS file cache does the caching for us, and clearing
/// the thumbnails never touches the index.
///
/// Keyed by content, not by path, so a model that appears under three folders is
/// rendered once, and renaming a file keeps its thumbnail.
/// </remarks>
public sealed class ThumbnailCache(string directory)
{
    /// <summary>Sits beside the index, so both live or die together.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ModelExplorer",
        "thumbs");

    public string Directory { get; } = directory;

    /// <summary>
    /// Sharded by the key's first two characters. A single directory holding
    /// 100k entries makes enumeration and deletion slow on NTFS, and a 256-way
    /// fan-out keeps each one at a few hundred files.
    /// </summary>
    public string PathFor(string contentKey) =>
        Path.Combine(Directory, contentKey[..2], $"{contentKey}.png");

    public bool TryGetPath(string contentKey, out string path)
    {
        path = PathFor(contentKey);
        return File.Exists(path);
    }

    /// <summary>
    /// Writes through a temporary file in the same shard, then moves it into
    /// place. Several workers can be rendering the same content key at once, and
    /// a reader must never see a half-written PNG.
    /// </summary>
    public void Write(string contentKey, byte[] png)
    {
        var path = PathFor(contentKey);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = $"{path}.{Environment.CurrentManagedThreadId:x}.tmp";
        try
        {
            File.WriteAllBytes(temporary, png);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>Deletes every cached thumbnail.</summary>
    /// <returns>How many files were removed.</returns>
    public int Clear()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.png", SearchOption.AllDirectories))
        {
            if (TryDelete(file))
            {
                removed++;
            }
        }

        return removed;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A thumbnail another process has open is not worth failing over.
            return false;
        }
    }
}
