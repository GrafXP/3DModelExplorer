using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using ModelExplorer.App.Thumbnails;
using ModelExplorer.Indexing;

namespace ModelExplorer.App.ViewModels;

/// <summary>
/// Library roots, scanning, and the indexed model list.
/// </summary>
/// <remarks>
/// Split from <see cref="MainViewModel"/> because the two share nothing: one owns
/// a GPU scene, the other owns a database and a background pipeline. Keeping the
/// scan's state machine out of the viewer is what lets selection be wired between
/// them as a single, obvious seam.
/// </remarks>
public sealed partial class LibraryViewModel : ObservableObject
{
    private const int SearchDebounceMilliseconds = 60;
    private const long Megabyte = 1024 * 1024;

    private readonly string[] _extensions;
    private readonly ThumbnailService _thumbnails;
    private IndexService? _index;
    private ModelSearchIndex? _searchIndex;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _searchCancellation;
    private bool _suppressSearch;

    public LibraryViewModel(IReadOnlyList<string> extensions, ThumbnailService thumbnails)
    {
        _extensions = [.. extensions];
        _thumbnails = thumbnails;

        ExtensionFilters =
        [
            new("All formats", null),
            .. _extensions
                .OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase)
                .Select(extension => new ExtensionFilterOption(
                    extension.TrimStart('.').ToUpperInvariant(),
                    extension)),
        ];

        SizeFilters =
        [
            new("Any size", null, null),
            new("Under 1 MB", null, Megabyte),
            new("1–10 MB", Megabyte, 10 * Megabyte),
            new("10–100 MB", 10 * Megabyte, 100 * Megabyte),
            new("100 MB or larger", 100 * Megabyte, null),
        ];

        _selectedExtensionFilter = ExtensionFilters[0];
        _selectedSizeFilter = SizeFilters[0];
    }

    public IReadOnlyList<ExtensionFilterOption> ExtensionFilters { get; }

    public IReadOnlyList<SizeFilterOption> SizeFilters { get; }

    /// <summary>
    /// The library as a folder hierarchy: one node per root, subfolders beneath.
    /// </summary>
    /// <remarks>
    /// This doubles as the roots list — the roots are simply its top level — so
    /// there is one place in the sidebar that shows the shape of the library
    /// rather than a flat root list and a redundant folder picker beside it.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoots))]
    private IReadOnlyList<FolderNode> _folderTree = [];

    public bool HasRoots => FolderTree.Count > 0;

    /// <summary>The folder subtree the results are restricted to. Null means the whole library.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolderFilter))]
    [NotifyCanExecuteChangedFor(nameof(ClearFolderFilterCommand))]
    private FolderNode? _selectedFolder;

    public bool HasFolderFilter => SelectedFolder is not null;

    [ObservableProperty]
    private ExtensionFilterOption _selectedExtensionFilter = null!;

    [ObservableProperty]
    private SizeFilterOption _selectedSizeFilter = null!;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// A plain list, not an observable collection.
    /// </summary>
    /// <remarks>
    /// The list is replaced wholesale when a scan finishes rather than being added
    /// to as files arrive. Appending 100k items one at a time raises 100k
    /// collection-changed notifications, and the <see cref="System.Windows.Data.CollectionView"/>
    /// behind an ItemsSource does per-notification work — that alone would make the
    /// window stutter for the whole scan, which is exactly what this step is
    /// supposed to avoid. Live counts go to the status bar instead.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModels))]
    [NotifyPropertyChangedFor(nameof(EmptyModelsText))]
    private IReadOnlyList<ModelFile> _models = [];

    public bool HasModels => Models.Count > 0;

    /// <summary>
    /// The row the viewer is following.
    /// </summary>
    /// <remarks>
    /// Set back to null by the ListBox itself every time <see cref="Models"/> is
    /// replaced, which happens on every keystroke in the search box.
    /// <see cref="StartSearchAsync"/> restores it when the same file survives the
    /// new filter, and consumers treat null as "nothing new to show" rather than
    /// as "unload" — otherwise refining a search would blank the viewport.
    /// </remarks>
    [ObservableProperty]
    private ModelFile? _selectedModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIndexedModels))]
    [NotifyPropertyChangedFor(nameof(ModelCountText))]
    [NotifyPropertyChangedFor(nameof(EmptyModelsText))]
    private int _totalModelCount;

    public bool HasIndexedModels => TotalModelCount > 0;

    public string ModelCountText => TotalModelCount switch
    {
        0 => "No models indexed",
        1 => "1 model",
        var n => $"{n:N0} models",
    };

    public string EmptyModelsText => HasIndexedModels
        ? "No models match the current search and filters."
        : "Nothing indexed yet.\n\nAdd a library folder to scan.\nThumbnail grid arrives in Step 6.";

    [ObservableProperty]
    private string _resultStatus = "0 results";

    /// <summary>Thumbnail grid rather than the detail list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListView))]
    private bool _isGridView = true;

    public bool IsListView => !IsGridView;

    /// <summary>
    /// Tile edge in device-independent pixels.
    /// </summary>
    /// <remarks>
    /// Capped at the size thumbnails are rendered, so the grid only ever scales
    /// them down. Letting a tile grow past it would show a blurred upscale.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TileSize))]
    private double _thumbnailSize = 144;

    public double MinimumThumbnailSize => 88;

    public double MaximumThumbnailSize => ThumbnailService.PixelSize;

    /// <summary>
    /// The whole tile, thumbnail plus its two-line caption.
    /// </summary>
    /// <remarks>
    /// Given to the wrap panel up front. Told the size, it can work out which
    /// rows fall inside the viewport arithmetically; left to discover it, it has
    /// to realize and measure containers to find out, which is the difference
    /// between scrolling 10k items and paging through them.
    /// </remarks>
    public Size TileSize => new(ThumbnailSize + 16, ThumbnailSize + 48);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddRootCommand))]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveRootCommand))]
    private bool _isScanning;

    [ObservableProperty]
    private string _scanStatus = string.Empty;

    /// <summary>
    /// False until the index is open. Every command's CanExecute reads it, so it
    /// has to re-raise them exactly like <see cref="IsScanning"/> does — a
    /// CanExecute that depends on a property nobody notifies about latches at
    /// whatever it evaluated to when the binding was first made, which here is
    /// disabled, forever.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddRootCommand))]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveRootCommand))]
    private bool _isReady;

    /// <summary>
    /// Opens the index and loads whatever the last session left behind.
    /// </summary>
    /// <remarks>
    /// Nothing is rescanned on startup. Reopening the app has to show the full
    /// list immediately, so the only work here is a read.
    /// </remarks>
    public async Task InitializeAsync()
    {
        try
        {
            _index = await Task.Run(() =>
                new IndexService(new ModelIndexStore(ModelIndexStore.DefaultPath), _extensions));

            await ReloadAsync();
            IsReady = true;

            ScanStatus = HasRoots ? string.Empty : "Add a folder to build your library";
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            ScanStatus = $"Could not open the index: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task AddRootAsync()
    {
        if (_index is null)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Add library folder",
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var added = new List<LibraryRoot>();
        foreach (var folder in dialog.FolderNames)
        {
            added.Add(await _index.AddRootAsync(folder));
        }

        // Only the new folders are scanned. Re-walking roots that are already
        // indexed would turn "add one folder" into a full library rebuild.
        await ScanAsync(added);
    }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task RescanAsync()
    {
        if (_index is null)
        {
            return;
        }

        await ScanAsync(await _index.GetRootsAsync());
    }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task RemoveRootAsync(LibraryRootViewModel? root)
    {
        if (_index is null || root is null)
        {
            return;
        }

        await _index.RemoveRootAsync(root.Id);
        await ReloadAsync();
    }

    /// <summary>
    /// Throws away every rendered thumbnail so they are generated again.
    /// </summary>
    /// <remarks>
    /// Re-running the search is not incidental: it hands the results list a new
    /// array, which makes the grid rebuild its containers, which is what makes
    /// the rows on screen ask for their thumbnails again. Without it the visible
    /// tiles would keep showing images that are no longer cached anywhere.
    /// </remarks>
    [RelayCommand]
    private async Task ClearThumbnailCacheAsync()
    {
        var removed = await Task.Run(_thumbnails.ClearCache);
        await StartSearchAsync(debounce: false);

        ScanStatus = removed == 1
            ? "Cleared 1 cached thumbnail"
            : $"Cleared {removed:N0} cached thumbnails";
    }

    /// <summary>Drops the folder restriction and shows the whole library again.</summary>
    [RelayCommand(CanExecute = nameof(HasFolderFilter))]
    private void ClearFolderFilter()
    {
        if (SelectedFolder is { } node)
        {
            // Clears the highlight in the tree; the TreeView owns that flag and
            // will not drop it just because the filter did.
            node.IsSelected = false;
            SelectedFolder = null;
        }
    }

    /// <summary>
    /// Cancels the running scan.
    /// </summary>
    /// <remarks>
    /// The status text changes here rather than waiting for the scan to unwind, so
    /// the click is acknowledged on the same frame it happens even if a directory
    /// read is still in flight.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void CancelScan()
    {
        ScanStatus = "Cancelling…";
        _scanCancellation?.Cancel();
    }

    private bool CanStartScan() => IsReady && !IsScanning;

    private async Task ScanAsync(IReadOnlyList<LibraryRoot> roots)
    {
        if (_index is null || roots.Count == 0)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        IsScanning = true;

        // Constructed on the UI thread, so its callbacks post back to the UI
        // thread; the scan itself never touches a dispatcher.
        var progress = new Progress<ScanProgress>(p =>
        {
            var folder = string.IsNullOrEmpty(p.CurrentRoot) ? "library" : Path.GetFileName(p.CurrentRoot);
            ScanStatus = $"Scanning {folder} — {p.Found:N0} files · {p.FilesPerSecond:N0}/s";
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

    /// <summary>Re-reads the index and rebuilds everything derived from it.</summary>
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

    partial void OnSearchTextChanged(string value) => ScheduleSearch();

    partial void OnSelectedExtensionFilterChanged(ExtensionFilterOption value) => ScheduleSearch();

    partial void OnSelectedSizeFilterChanged(SizeFilterOption value) => ScheduleSearch();

    partial void OnSelectedFolderChanged(FolderNode? value) => ScheduleSearch();

    /// <summary>Called by a node when the TreeView selects it.</summary>
    private void OnFolderNodeSelected(FolderNode node) => SelectedFolder = node;

    /// <summary>
    /// Builds a new immutable search snapshot after loading or scanning. The
    /// lower-casing, one-time sorting, and folder tree projection all stay off the
    /// UI thread; only the final property swaps happen here.
    /// </summary>
    private async Task ReplaceFilesAsync(
        IReadOnlyList<LibraryRoot> roots,
        IReadOnlyList<ModelFile> files)
    {
        CancelActiveSearch();

        // Captured before the rebuild replaces every node instance, so the tree
        // can be put back the way the user left it.
        var previousFolder = SelectedFolder;
        var expanded = CollectExpanded(FolderTree);

        var (searchIndex, tree) = await Task.Run(() =>
            (new ModelSearchIndex(files), BuildFolderTree(roots, files, OnFolderNodeSelected)));

        _searchIndex = searchIndex;
        TotalModelCount = searchIndex.Count;

        _suppressSearch = true;
        FolderTree = tree;
        SelectedFolder = null;

        // First build of the session: open the roots so the library's shape is
        // visible without a click.
        if (expanded.Count == 0)
        {
            foreach (var root in tree)
            {
                root.IsExpanded = true;
            }
        }

        RestoreFolderState(tree, expanded, previousFolder);
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
        var query = CreateSearchQuery();

        try
        {
            if (debounce)
            {
                await Task.Delay(SearchDebounceMilliseconds, token);
            }

            var result = await Task.Run(() => searchIndex.Search(query, token), token);
            token.ThrowIfCancellationRequested();

            // A rescan can replace the immutable snapshot while this worker is
            // finishing. Its results belong to the old snapshot and must not land.
            if (ReferenceEquals(searchIndex, _searchIndex) &&
                ReferenceEquals(cancellation, _searchCancellation))
            {
                var previousSelection = SelectedModel;

                Models = result.Models;
                ResultStatus = result.Models.Count == 1
                    ? $"1 result · {result.Elapsed.TotalMilliseconds:N1} ms"
                    : $"{result.Models.Count:N0} results · {result.Elapsed.TotalMilliseconds:N1} ms";

                RestoreSelection(previousSelection, result.Models);
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

    /// <summary>
    /// Re-selects the previously selected file if it is still in the results.
    /// </summary>
    /// <remarks>
    /// Assigning <see cref="Models"/> makes the ListBox drop its selection, so
    /// without this every keystroke would leave the highlighted row behind even
    /// when it is still on screen. Matched on identity, not path: the snapshot
    /// hands out the same <see cref="ModelFile"/> instances to every query, so a
    /// reference comparison is both correct and free.
    /// </remarks>
    private void RestoreSelection(ModelFile? previous, IReadOnlyList<ModelFile> results)
    {
        if (previous is null)
        {
            return;
        }

        for (var i = 0; i < results.Count; i++)
        {
            if (ReferenceEquals(results[i], previous))
            {
                SelectedModel = previous;
                return;
            }
        }
    }

    private ModelSearchQuery CreateSearchQuery() => new(
        SearchText,
        SelectedExtensionFilter.Extension,
        SelectedSizeFilter.MinimumBytes,
        SelectedSizeFilter.MaximumBytesExclusive,
        SelectedFolder?.RootId,
        SelectedFolder?.RelativePath);

    private void CancelActiveSearch()
    {
        var previous = Interlocked.Exchange(ref _searchCancellation, null);
        previous?.Cancel();
    }

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
    /// Reapplies expansion and selection to a freshly built tree.
    /// </summary>
    /// <returns>Whether this level contains the restored selection.</returns>
    private static bool RestoreFolderState(
        IReadOnlyList<FolderNode> nodes,
        HashSet<string> expanded,
        FolderNode? previous)
    {
        var found = false;

        foreach (var node in nodes)
        {
            if (expanded.Contains(node.FullPath))
            {
                node.IsExpanded = true;
            }

            var hit = previous is not null && node.SameSubtreeAs(previous);
            if (hit)
            {
                node.IsSelected = true;
            }

            // An ancestor of the restored selection has to be open, or the
            // highlighted row is somewhere the user cannot see.
            if (RestoreFolderState(node.Children, expanded, previous))
            {
                node.IsExpanded = true;
                hit = true;
            }

            found |= hit;
        }

        return found;
    }

    /// <summary>
    /// Wraps the index's folder projection in the view state the TreeView needs.
    /// The shape and the counts come from <see cref="FolderTreeBuilder"/>; only expansion
    /// and selection are added here.
    /// </summary>
    private static IReadOnlyList<FolderNode> BuildFolderTree(
        IReadOnlyList<LibraryRoot> roots,
        IReadOnlyList<ModelFile> files,
        Action<FolderNode> onSelected)
    {
        var rootsById = roots.ToDictionary(root => root.Id);

        return
        [
            .. FolderTreeBuilder.Build(roots, files)
                .Select(summary => ToNode(
                    summary,
                    new LibraryRootViewModel(rootsById[summary.RootId], summary.FileCount),
                    onSelected)),
        ];
    }

    private static FolderNode ToNode(
        FolderSummary summary,
        LibraryRootViewModel? root,
        Action<FolderNode> onSelected) =>
        new(
            onSelected,
            summary,
            root,
            [.. summary.Children.Select(child => ToNode(child, null, onSelected))]);
}

/// <summary>
/// One folder in the library tree. Roots are the top level and carry the extra
/// state the sidebar shows for them.
/// </summary>
public sealed partial class FolderNode : ObservableObject
{
    private readonly Action<FolderNode> _onSelected;
    private readonly FolderSummary _summary;

    internal FolderNode(
        Action<FolderNode> onSelected,
        FolderSummary summary,
        LibraryRootViewModel? root,
        IReadOnlyList<FolderNode> children)
    {
        _onSelected = onSelected;
        _summary = summary;
        Root = root;
        Children = children;
    }

    public string Name => _summary.Name;

    public long RootId => _summary.RootId;

    /// <summary>Path below the root. Empty for a root node, which means "the whole root".</summary>
    public string RelativePath => _summary.RelativePath;

    public string FullPath => _summary.FullPath;

    /// <summary>Files in this folder and everything under it.</summary>
    public int FileCount => _summary.FileCount;

    public IReadOnlyList<FolderNode> Children { get; }

    /// <summary>Non-null only for a root node.</summary>
    public LibraryRootViewModel? Root { get; }

    public bool IsRoot => Root is not null;

    public string CountText => FileCount == 1 ? "1 model" : $"{FileCount:N0} models";

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// A TreeView's SelectedItem is read-only, so selection is picked up from the
    /// container's IsSelected instead. Only the transition to true is interesting:
    /// WPF clears the old node before setting the new one, and reacting to the
    /// clear would filter to the whole library for one frame in between.
    /// </summary>
    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            _onSelected(this);
        }
    }

    public bool SameSubtreeAs(FolderNode? other) =>
        other is not null &&
        RootId == other.RootId &&
        string.Equals(RelativePath, other.RelativePath, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A library root, as shown on the tree's top-level nodes.</summary>
public sealed class LibraryRootViewModel(LibraryRoot root, int fileCount)
{
    public long Id => root.Id;

    public string Path => root.Path;

    public string DisplayName => root.DisplayName;

    public bool IsNetwork => root.IsNetwork;

    public string CountText => fileCount == 1 ? "1 model" : $"{fileCount:N0} models";

    /// <summary>Flagged in the UI: the root was only partly scanned.</summary>
    public bool IsIncomplete => root.LastScanUtc is null;
}

public sealed record ExtensionFilterOption(string Label, string? Extension);

public sealed record SizeFilterOption(
    string Label,
    long? MinimumBytes,
    long? MaximumBytesExclusive);
