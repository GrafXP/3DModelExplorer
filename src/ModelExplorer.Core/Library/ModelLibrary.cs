using Microsoft.Data.Sqlite;
using ModelExplorer.Indexing;

namespace ModelExplorer.Core.Library;

/// <summary>
/// Library roots, scanning, the folder tree and the searched model list.
/// </summary>
/// <remarks>
/// Lives on the UI thread. Index reads, scans and searches run on the thread pool
/// and come back through the UI thread's synchronization context, so every
/// property here is only ever written by the thread that draws it.
/// </remarks>
public sealed class ModelLibrary : IDisposable
{
    private const int SearchDebounceMilliseconds = 60;
    private const long Megabyte = 1024 * 1024;

    private readonly string[] _extensions;
    private readonly string _indexPath;
    private IndexService? _index;
    private ModelSearchIndex? _searchIndex;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _searchCancellation;
    private bool _suppressSearch;

    private string _searchText = string.Empty;
    private ExtensionFilterOption _extensionFilter;
    private SizeFilterOption _sizeFilter;
    private SortOption _sort;
    private FolderNode? _selectedFolder;

    public ModelLibrary(IReadOnlyList<string> extensions, string indexPath)
    {
        _extensions = [.. extensions];
        _indexPath = indexPath;

        ExtensionFilters =
        [
            new("All formats", null),
            .. _extensions
                .OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase)
                .Select(extension => new ExtensionFilterOption(extension.TrimStart('.').ToUpperInvariant(), extension)),
        ];

        SizeFilters =
        [
            new("Any size", null, null),
            new("Under 1 MB", null, Megabyte),
            new("1–10 MB", Megabyte, 10 * Megabyte),
            new("10–100 MB", 10 * Megabyte, 100 * Megabyte),
            new("100 MB or larger", 100 * Megabyte, null),
        ];

        // Direction is folded into the option rather than split out into a
        // separate ascending/descending toggle. "Newest first" says what it does;
        // "Date modified" plus an arrow somewhere else does not.
        SortOptions =
        [
            new("Best match", ModelSortField.Relevance, false),
            new("Name (A–Z)", ModelSortField.Name, false),
            new("Name (Z–A)", ModelSortField.Name, true),
            new("Newest first", ModelSortField.DateModified, true),
            new("Oldest first", ModelSortField.DateModified, false),
            new("Largest first", ModelSortField.Size, true),
            new("Smallest first", ModelSortField.Size, false),
            new("Format", ModelSortField.Format, false),
            new("Folder", ModelSortField.Folder, false),
        ];

        _extensionFilter = ExtensionFilters[0];
        _sizeFilter = SizeFilters[0];
        _sort = SortOptions[0];
    }

    public IReadOnlyList<ExtensionFilterOption> ExtensionFilters { get; }

    public IReadOnlyList<SizeFilterOption> SizeFilters { get; }

    public IReadOnlyList<SortOption> SortOptions { get; }

    /// <summary>
    /// The library as a folder hierarchy: one node per root, subfolders beneath.
    /// This doubles as the roots list — the roots are simply its top level.
    /// </summary>
    public IReadOnlyList<FolderNode> FolderTree { get; private set; } = [];

    public bool HasRoots => FolderTree.Count > 0;

    /// <summary>A plain list, replaced wholesale by each search.</summary>
    public IReadOnlyList<ModelFile> Models { get; private set; } = [];

    /// <summary>Changes whenever <see cref="Models"/> is replaced.</summary>
    public int ResultsVersion { get; private set; }

    public int TotalModelCount { get; private set; }

    public string ModelCountText => TotalModelCount switch
    {
        0 => "No models indexed",
        1 => "1 model",
        var n => $"{n:N0} models",
    };

    public string EmptyModelsText => TotalModelCount > 0
        ? "No models match the current search and filters."
        : "Nothing indexed yet. Add a library folder to scan.";

    public string ResultStatus { get; private set; } = "0 results";

    public bool IsScanning { get; private set; }

    public string ScanStatus { get; private set; } = string.Empty;

    /// <summary>False until the index is open; nothing that writes to it is offered before.</summary>
    public bool IsReady { get; private set; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText != value)
            {
                _searchText = value;
                ScheduleSearch();
            }
        }
    }

    public ExtensionFilterOption ExtensionFilter
    {
        get => _extensionFilter;
        set => Set(ref _extensionFilter, value);
    }

    public SizeFilterOption SizeFilter
    {
        get => _sizeFilter;
        set => Set(ref _sizeFilter, value);
    }

    public SortOption Sort
    {
        get => _sort;
        set => Set(ref _sort, value);
    }

    /// <summary>The folder subtree the results are restricted to. Null means the whole library.</summary>
    public FolderNode? SelectedFolder
    {
        get => _selectedFolder;
        set => Set(ref _selectedFolder, value);
    }

    /// <summary>
    /// Opens the index and loads whatever the last session left behind. Nothing is
    /// rescanned on startup: reopening has to show the full list immediately.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            var path = _indexPath;
            var extensions = _extensions;
            _index = await Task.Run(() => new IndexService(new ModelIndexStore(path), extensions));
            await ReloadAsync();
            IsReady = true;
            ScanStatus = HasRoots ? string.Empty : "Add a folder to build your library";
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            ScanStatus = $"Could not open the index: {ex.Message}";
        }
    }

    /// <summary>
    /// Adds folders and scans only them. Re-walking roots that are already indexed
    /// would turn "add one folder" into a full library rebuild.
    /// </summary>
    public async Task AddRootsAsync(IEnumerable<string> folders)
    {
        if (_index is null || IsScanning)
        {
            return;
        }

        var added = new List<LibraryRoot>();
        foreach (var folder in folders)
        {
            added.Add(await _index.AddRootAsync(folder));
        }

        await ScanAsync(added);
    }

    public async Task RescanAsync()
    {
        if (_index is null || IsScanning)
        {
            return;
        }

        await ScanAsync(await _index.GetRootsAsync());
    }

    public async Task RemoveRootAsync(long rootId)
    {
        if (_index is null || IsScanning)
        {
            return;
        }

        await _index.RemoveRootAsync(rootId);
        await ReloadAsync();
    }

    /// <summary>
    /// Cancels the running scan. The status text changes at once, so the click is
    /// acknowledged even if a directory read is still in flight.
    /// </summary>
    public void CancelScan()
    {
        if (_scanCancellation is null)
        {
            return;
        }

        ScanStatus = "Cancelling…";
        _scanCancellation.Cancel();
    }

    /// <summary>Runs the current search again, e.g. after the thumbnails were cleared.</summary>
    public Task RefreshAsync() => StartSearchAsync(debounce: false);

    public void ClearFolderFilter() => SelectedFolder = null;

    /// <summary>Shows a transient line where the scan status goes.</summary>
    public void ReportStatus(string text) => ScanStatus = text;

    public void Dispose()
    {
        _scanCancellation?.Cancel();
        _searchCancellation?.Cancel();
        _index?.Dispose();
    }

    private void Set<T>(ref T field, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            ScheduleSearch();
        }
    }

    private async Task ScanAsync(IReadOnlyList<LibraryRoot> roots)
    {
        if (_index is null || roots.Count == 0)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        IsScanning = true;

        // Created on the UI thread, so its callbacks come back to it; the scan
        // itself never touches the UI.
        var progress = new Progress<ScanProgress>(p =>
        {
            if (!cancellation.IsCancellationRequested)
            {
                var folder = string.IsNullOrEmpty(p.CurrentRoot) ? "library" : Path.GetFileName(p.CurrentRoot);
                ScanStatus = $"Scanning {folder} — {p.Found:N0} files · {p.FilesPerSecond:N0}/s";
            }
        });

        try
        {
            var summary = await _index.ScanAsync(roots, progress, cancellation.Token);
            await ReloadAsync();
            ScanStatus = summary.Cancelled
                ? $"Scan cancelled — {summary.Indexed:N0} files indexed"
                : $"Indexed {summary.Indexed:N0} files in {summary.Elapsed.TotalSeconds:N1} s · {summary.FilesPerSecond:N0}/s";
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            ScanStatus = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _scanCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task ReloadAsync()
    {
        if (_index is null)
        {
            return;
        }

        var roots = await _index.GetRootsAsync();
        var files = await _index.LoadFilesAsync();
        await ReplaceFilesAsync(roots, files);
    }

    /// <summary>
    /// Builds a new immutable search snapshot after loading or scanning. The
    /// lower-casing, sorting and folder tree projection stay off the UI thread;
    /// only the final swaps happen here.
    /// </summary>
    private async Task ReplaceFilesAsync(IReadOnlyList<LibraryRoot> roots, IReadOnlyList<ModelFile> files)
    {
        CancelActiveSearch();

        // Captured before the rebuild replaces every node, so the tree can be put
        // back the way the user left it.
        var previousFolder = SelectedFolder;
        var expanded = CollectExpanded(FolderTree);
        var firstBuild = FolderTree.Count == 0;

        var (searchIndex, tree) = await Task.Run(() => (new ModelSearchIndex(files), BuildFolderTree(roots, files)));

        _searchIndex = searchIndex;
        TotalModelCount = searchIndex.Count;

        _suppressSearch = true;
        FolderTree = tree;
        _selectedFolder = null;
        if (firstBuild)
        {
            // First build of the session: open the roots so the library's shape
            // is visible without a click.
            foreach (var root in tree)
            {
                root.IsExpanded = true;
            }
        }

        _selectedFolder = RestoreFolderState(tree, expanded, previousFolder);
        _suppressSearch = false;

        await StartSearchAsync(debounce: false);
    }

    private void ScheduleSearch()
    {
        if (!_suppressSearch)
        {
            _ = StartSearchAsync(debounce: true);
        }
    }

    private async Task StartSearchAsync(bool debounce)
    {
        if (_searchIndex is not { } searchIndex)
        {
            return;
        }

        CancelActiveSearch();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var token = cancellation.Token;
        var query = new ModelSearchQuery(
            SearchText,
            ExtensionFilter.Extension,
            SizeFilter.MinimumBytes,
            SizeFilter.MaximumBytesExclusive,
            SelectedFolder?.RootId,
            SelectedFolder?.RelativePath,
            Sort.Field,
            Sort.Descending);

        try
        {
            if (debounce)
            {
                await Task.Delay(SearchDebounceMilliseconds, token);
            }

            var result = await Task.Run(() => searchIndex.Search(query, token), token);
            token.ThrowIfCancellationRequested();

            // A rescan can replace the snapshot while this worker is finishing.
            // Its results belong to the old snapshot and must not land.
            if (ReferenceEquals(searchIndex, _searchIndex) && ReferenceEquals(cancellation, _searchCancellation))
            {
                Models = result.Models;
                ResultsVersion++;
                ResultStatus = result.Models.Count == 1
                    ? $"1 result · {result.Elapsed.TotalMilliseconds:N1} ms"
                    : $"{result.Models.Count:N0} results · {result.Elapsed.TotalMilliseconds:N1} ms";
            }
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke or index snapshot owns the visible results.
        }
        finally
        {
            Interlocked.CompareExchange(ref _searchCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private void CancelActiveSearch() => Interlocked.Exchange(ref _searchCancellation, null)?.Cancel();

    private static HashSet<string> CollectExpanded(IReadOnlyList<FolderNode> nodes)
    {
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(nodes);
        return expanded;

        void Walk(IReadOnlyList<FolderNode> level)
        {
            foreach (var node in level)
            {
                if (node.IsExpanded)
                {
                    expanded.Add(node.FullPath);
                }

                Walk(node.Children);
            }
        }
    }

    /// <summary>
    /// Reapplies expansion to a freshly built tree and finds the node standing for
    /// the previous selection. An ancestor of that node is opened, or the
    /// highlighted row would be somewhere the user cannot see.
    /// </summary>
    private static FolderNode? RestoreFolderState(IReadOnlyList<FolderNode> nodes, HashSet<string> expanded, FolderNode? previous)
    {
        FolderNode? found = null;
        foreach (var node in nodes)
        {
            if (expanded.Contains(node.FullPath))
            {
                node.IsExpanded = true;
            }

            if (previous is not null && node.SameSubtreeAs(previous))
            {
                found = node;
            }

            if (RestoreFolderState(node.Children, expanded, previous) is { } below)
            {
                node.IsExpanded = true;
                found = below;
            }
        }

        return found;
    }

    private static IReadOnlyList<FolderNode> BuildFolderTree(IReadOnlyList<LibraryRoot> roots, IReadOnlyList<ModelFile> files)
    {
        var rootsById = roots.ToDictionary(root => root.Id);
        return [.. FolderTreeBuilder.Build(roots, files).Select(summary => ToNode(summary, rootsById[summary.RootId]))];

        static FolderNode ToNode(FolderSummary summary, LibraryRoot? root) =>
            new(summary, root, [.. summary.Children.Select(child => ToNode(child, null))]);
    }
}

/// <summary>One folder in the library tree. Roots are the top level and carry their <see cref="LibraryRoot"/>.</summary>
public sealed class FolderNode(FolderSummary summary, LibraryRoot? root, IReadOnlyList<FolderNode> children)
{
    public string Name => summary.Name;

    public long RootId => summary.RootId;

    /// <summary>Path below the root. Empty for a root node, which means "the whole root".</summary>
    public string RelativePath => summary.RelativePath;

    public string FullPath => summary.FullPath;

    /// <summary>Files in this folder and everything under it.</summary>
    public int FileCount => summary.FileCount;

    public IReadOnlyList<FolderNode> Children { get; } = children;

    /// <summary>Non-null only for a root node.</summary>
    public LibraryRoot? Root { get; } = root;

    public bool IsRoot => Root is not null;

    /// <summary>Flagged in the UI: the root was only partly scanned.</summary>
    public bool IsIncomplete => Root is { LastScanUtc: null };

    public string CountText => FileCount == 1 ? "1 model" : $"{FileCount:N0} models";

    public bool IsExpanded { get; set; }

    public bool SameSubtreeAs(FolderNode? other) =>
        other is not null &&
        RootId == other.RootId &&
        string.Equals(RelativePath, other.RelativePath, StringComparison.OrdinalIgnoreCase);
}

public sealed record ExtensionFilterOption(string Label, string? Extension);

public sealed record SizeFilterOption(string Label, long? MinimumBytes, long? MaximumBytesExclusive);

/// <summary>One entry in the sort list: a field and the direction to read it in.</summary>
public sealed record SortOption(string Label, ModelSortField Field, bool Descending);
