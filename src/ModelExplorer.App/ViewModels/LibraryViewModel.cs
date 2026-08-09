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
    private readonly string[] _extensions;
    private IndexService? _index;
    private CancellationTokenSource? _scanCancellation;

    public LibraryViewModel(IReadOnlyList<string> extensions)
    {
        _extensions = [.. extensions];
    }

    public ObservableCollection<LibraryRootViewModel> Roots { get; } = [];

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
    [NotifyPropertyChangedFor(nameof(ModelCountText))]
    private IReadOnlyList<ModelFile> _models = [];

    public bool HasModels => Models.Count > 0;

    public string ModelCountText => Models.Count switch
    {
        0 => "No models indexed",
        1 => "1 model",
        var n => $"{n:N0} models",
    };

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
            Models = files;
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
        Models = files;
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
            Models = files;

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
