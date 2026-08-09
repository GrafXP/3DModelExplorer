using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using ModelExplorer.Indexing;

namespace ModelExplorer.App.ViewModels;

/// <summary>
/// Library roots, scanning, and the indexed model list.
/// </summary>
/// <remarks>
/// Split from <see cref="MainViewModel"/> because the two share nothing: one owns
/// a GPU scene, the other owns a database and a background pipeline. Keeping the
/// scan's state machine out of the viewer is what lets step 5 wire selection
/// between them as a single, obvious seam.
/// </remarks>
public sealed partial class LibraryViewModel : ObservableObject
{
    private const int SearchDebounceMilliseconds = 60;
    private const long Megabyte = 1024 * 1024;

    private readonly string[] _extensions;
    private IndexService? _index;
    private ModelSearchIndex? _searchIndex;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _searchCancellation;
    private bool _suppressSearch;

    public LibraryViewModel(IReadOnlyList<string> extensions)
    {
        _extensions = [.. extensions];

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
        _selectedFolderFilter = FolderFilters[0];
    }

    public ObservableCollection<LibraryRootViewModel> Roots { get; } = [];

    public IReadOnlyList<ExtensionFilterOption> ExtensionFilters { get; }

    public IReadOnlyList<SizeFilterOption> SizeFilters { get; }

    [ObservableProperty]
    private IReadOnlyList<FolderFilterOption> _folderFilters = [FolderFilterOption.All];

    [ObservableProperty]
    private ExtensionFilterOption _selectedExtensionFilter = null!;

    [ObservableProperty]
    private SizeFilterOption _selectedSizeFilter = null!;

    [ObservableProperty]
    private FolderFilterOption _selectedFolderFilter = null!;

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

            var roots = await _index.GetRootsAsync();
            var files = await _index.LoadFilesAsync();

            ApplyRoots(roots, files);
            await ReplaceFilesAsync(roots, files);
            IsReady = true;

            ScanStatus = Roots.Count == 0
                ? "Add a folder to build your library"
                : string.Empty;
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

        var roots = await _index.GetRootsAsync();
        var files = await _index.LoadFilesAsync();
        ApplyRoots(roots, files);
        await ReplaceFilesAsync(roots, files);
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

            var allRoots = await _index.GetRootsAsync();
            var files = await _index.LoadFilesAsync();
            ApplyRoots(allRoots, files);
            await ReplaceFilesAsync(allRoots, files);

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

    /// <summary>Rebuilds the sidebar, with each root's share of the file count.</summary>
    private void ApplyRoots(IReadOnlyList<LibraryRoot> roots, IReadOnlyList<ModelFile> files)
    {
        var counts = new Dictionary<long, int>();
        foreach (var file in files)
        {
            counts[file.RootId] = counts.GetValueOrDefault(file.RootId) + 1;
        }

        Roots.Clear();
        foreach (var root in roots)
        {
            Roots.Add(new LibraryRootViewModel(root, counts.GetValueOrDefault(root.Id)));
        }
    }

    partial void OnSearchTextChanged(string value) => ScheduleSearch();

    partial void OnSelectedExtensionFilterChanged(ExtensionFilterOption value) => ScheduleSearch();

    partial void OnSelectedSizeFilterChanged(SizeFilterOption value) => ScheduleSearch();

    partial void OnSelectedFolderFilterChanged(FolderFilterOption value) => ScheduleSearch();

    /// <summary>
    /// Builds a new immutable search snapshot after loading or scanning. The
    /// lower-casing, one-time sorting, and folder projection all stay off the UI
    /// thread; only the final property swaps happen here.
    /// </summary>
    private async Task ReplaceFilesAsync(
        IReadOnlyList<LibraryRoot> roots,
        IReadOnlyList<ModelFile> files)
    {
        CancelActiveSearch();

        var (searchIndex, folderFilters) = await Task.Run(() =>
            (new ModelSearchIndex(files), BuildFolderFilters(roots, files)));

        var previousFolder = SelectedFolderFilter;
        _searchIndex = searchIndex;
        TotalModelCount = searchIndex.Count;

        _suppressSearch = true;
        FolderFilters = folderFilters;
        SelectedFolderFilter = folderFilters.FirstOrDefault(option => option.SameSubtreeAs(previousFolder))
            ?? FolderFilterOption.All;
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
                Models = result.Models;
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

    private ModelSearchQuery CreateSearchQuery() => new(
        SearchText,
        SelectedExtensionFilter.Extension,
        SelectedSizeFilter.MinimumBytes,
        SelectedSizeFilter.MaximumBytesExclusive,
        SelectedFolderFilter.RootId,
        SelectedFolderFilter.RelativePath);

    private void CancelActiveSearch()
    {
        var previous = Interlocked.Exchange(ref _searchCancellation, null);
        previous?.Cancel();
    }

    private static IReadOnlyList<FolderFilterOption> BuildFolderFilters(
        IReadOnlyList<LibraryRoot> roots,
        IReadOnlyList<ModelFile> files)
    {
        var foldersByRoot = roots.ToDictionary(
            root => root.Id,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        foreach (var file in files)
        {
            if (!foldersByRoot.TryGetValue(file.RootId, out var folders))
            {
                continue;
            }

            var folder = Path.GetDirectoryName(file.RelativePath);
            while (!string.IsNullOrEmpty(folder))
            {
                folders.Add(folder);
                folder = Path.GetDirectoryName(folder);
            }
        }

        var options = new List<FolderFilterOption> { FolderFilterOption.All };
        foreach (var root in roots.OrderBy(root => root.Path, StringComparer.OrdinalIgnoreCase))
        {
            options.Add(new FolderFilterOption(root.DisplayName, root.Id, string.Empty, root.Path));

            foreach (var folder in foldersByRoot[root.Id].OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                options.Add(new FolderFilterOption(
                    $"{root.DisplayName}  ›  {folder}",
                    root.Id,
                    folder,
                    Path.Join(root.Path, folder)));
            }
        }

        return options;
    }
}

/// <summary>One row in the library sidebar.</summary>
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

public sealed record FolderFilterOption(
    string Label,
    long? RootId,
    string? RelativePath,
    string? FullPath)
{
    public static FolderFilterOption All { get; } = new("All folders", null, null, null);

    public bool SameSubtreeAs(FolderFilterOption? other) =>
        other is not null &&
        RootId == other.RootId &&
        string.Equals(RelativePath, other.RelativePath, StringComparison.OrdinalIgnoreCase);
}
