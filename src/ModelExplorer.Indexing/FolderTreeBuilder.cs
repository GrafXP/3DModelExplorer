namespace ModelExplorer.Indexing;

/// <summary>
/// Projects the flat file list into a folder hierarchy, one subtree per root.
/// </summary>
/// <remarks>
/// A projection of the index rather than a view concern, which is why it lives
/// here: it derives entirely from the roots and files, has no UI state, and the
/// counting rules are worth testing on their own.
/// </remarks>
public static class FolderTreeBuilder
{
    /// <summary>
    /// Builds one subtree per root, ordered by root path, with children ordered by
    /// name. Only folders that actually contain indexed files appear.
    /// </summary>
    /// <remarks>
    /// Every file contributes to the count of each folder above it, so a node's
    /// count is its whole subtree — which is exactly what filtering to that node
    /// yields. Path segments are matched as spans against the builder
    /// dictionaries, so a 100k-file library allocates one string per distinct
    /// folder rather than one per segment per file.
    /// </remarks>
    public static IReadOnlyList<FolderSummary> Build(
        IReadOnlyList<LibraryRoot> roots,
        IReadOnlyList<ModelFile> files)
    {
        var builders = new Dictionary<long, Builder>();
        foreach (var root in roots)
        {
            builders[root.Id] = new Builder(root.DisplayName);
        }

        foreach (var file in files)
        {
            // A file whose root has been removed is ignored rather than given a
            // subtree of its own: the roots define the shape of the library.
            if (!builders.TryGetValue(file.RootId, out var node))
            {
                continue;
            }

            node.FileCount++;

            var remaining = Path.GetDirectoryName(file.RelativePath.AsSpan());
            while (!remaining.IsEmpty)
            {
                var separator = remaining.IndexOfAny(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);

                ReadOnlySpan<char> segment;
                if (separator < 0)
                {
                    segment = remaining;
                    remaining = default;
                }
                else
                {
                    segment = remaining[..separator];
                    remaining = remaining[(separator + 1)..];
                }

                if (segment.IsEmpty)
                {
                    continue;
                }

                node = node.Child(segment);
                node.FileCount++;
            }
        }

        return
        [
            .. roots
                .OrderBy(root => root.Path, StringComparer.OrdinalIgnoreCase)
                .Select(root => ToSummary(builders[root.Id], root.Id, string.Empty, root.Path)),
        ];
    }

    private static FolderSummary ToSummary(
        Builder builder,
        long rootId,
        string relativePath,
        string fullPath)
    {
        IReadOnlyList<FolderSummary> children = builder.Children is null
            ?
            []
            :
            [
                .. builder.Children.Values
                    .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(child => ToSummary(
                        child,
                        rootId,
                        relativePath.Length == 0 ? child.Name : Path.Join(relativePath, child.Name),
                        Path.Join(fullPath, child.Name))),
            ];

        return new FolderSummary
        {
            Name = builder.Name,
            RootId = rootId,
            RelativePath = relativePath,
            FullPath = fullPath,
            FileCount = builder.FileCount,
            Children = children,
        };
    }

    /// <summary>Mutable scaffolding, discarded once the immutable tree is built.</summary>
    private sealed class Builder(string name)
    {
        public string Name { get; } = name;

        public int FileCount { get; set; }

        public Dictionary<string, Builder>? Children { get; private set; }

        public Builder Child(ReadOnlySpan<char> segment)
        {
            Children ??= new Dictionary<string, Builder>(StringComparer.OrdinalIgnoreCase);

            var lookup = Children.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(segment, out var existing))
            {
                return existing;
            }

            var child = new Builder(segment.ToString());
            Children.Add(child.Name, child);
            return child;
        }
    }
}

/// <summary>One folder in the library, with everything indexed beneath it.</summary>
public sealed class FolderSummary
{
    public required string Name { get; init; }

    public required long RootId { get; init; }

    /// <summary>Path below the root. Empty for a root, which means "the whole root".</summary>
    public required string RelativePath { get; init; }

    public required string FullPath { get; init; }

    /// <summary>Files in this folder and every folder under it.</summary>
    public required int FileCount { get; init; }

    public required IReadOnlyList<FolderSummary> Children { get; init; }
}
