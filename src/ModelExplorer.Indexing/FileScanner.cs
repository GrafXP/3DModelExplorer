using System.IO.Enumeration;

namespace ModelExplorer.Indexing;

/// <summary>A file found by the walk, with its path relative to the scan root.</summary>
public readonly record struct ScannedEntry(string RelativePath, long Size, long ModifiedTicks);

/// <summary>Recursive directory walk over one library root.</summary>
public static class FileScanner
{
    /// <summary>
    /// Walks <paramref name="root"/>, yielding only files carrying one of
    /// <paramref name="extensions"/>.
    /// </summary>
    /// <remarks>
    /// Built on <see cref="FileSystemEnumerable{TResult}"/> with a transform
    /// rather than <c>Directory.EnumerateFiles</c> followed by
    /// <see cref="FileInfo"/>. The transform reads name, length and last-write
    /// time straight off the directory entry the OS has already returned, so
    /// there is no second stat per file. Over a network share that one difference
    /// is most of the scan time.
    ///
    /// The walk is lazy and blocking. Callers own cancellation: checking the
    /// token between yielded items stops within one directory read.
    /// </remarks>
    public static IEnumerable<ScannedEntry> Enumerate(string root, string[] extensions)
    {
        // GetFullPath resolves a relative or shortened root; trimming the trailing
        // separator makes the length arithmetic in BuildRelativePath predictable
        // for everything except a drive root, which keeps its separator.
        var start = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,

            // A library root routinely spans folders the user cannot read. Those
            // are skipped rather than aborting the whole walk.
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
            MatchType = MatchType.Simple,
            ReturnSpecialDirectories = false,
        };

        return new FileSystemEnumerable<ScannedEntry>(start, Transform, options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                !entry.IsDirectory && HasExtension(entry.FileName, extensions),

            // Reparse points are excluded from recursion explicitly rather than
            // left to AttributesToSkip: a junction pointing at one of its own
            // ancestors turns the walk into an infinite loop, and a share full of
            // symlinks would otherwise index the same file many times over.
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                (entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) == 0,
        };

        static ScannedEntry Transform(ref FileSystemEntry entry) => new(
            BuildRelativePath(ref entry),
            entry.Length,
            entry.LastWriteTimeUtc.UtcTicks);
    }

    /// <summary>
    /// The entry's path below the enumeration root — the only string the walk
    /// allocates per file.
    /// </summary>
    private static string BuildRelativePath(ref FileSystemEntry entry)
    {
        var directory = entry.Directory;
        var skip = entry.RootDirectory.Length;

        // RootDirectory carries no trailing separator except at a drive root
        // ("C:\"), so the separator is stepped over only when it is really there.
        // Getting this wrong shears the first character off every relative path.
        if (directory.Length > skip &&
            (directory[skip] == Path.DirectorySeparatorChar || directory[skip] == Path.AltDirectorySeparatorChar))
        {
            skip++;
        }

        return directory.Length > skip
            ? Path.Join(directory[skip..], entry.FileName)
            : entry.FileName.ToString();
    }

    private static bool HasExtension(ReadOnlySpan<char> fileName, string[] extensions)
    {
        foreach (var extension in extensions)
        {
            // Length check excludes a file named exactly ".stl", which is an
            // extension with no name rather than a model.
            if (fileName.Length > extension.Length &&
                fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
