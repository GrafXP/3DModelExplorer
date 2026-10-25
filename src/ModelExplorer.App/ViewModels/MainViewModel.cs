using System.ComponentModel;
using System.IO;
using System.Numerics;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HelixToolkit.SharpDX;
using Microsoft.Win32;
using ModelExplorer.App.Thumbnails;
using ModelExplorer.Geometry;
using ModelExplorer.Indexing;
using Bounds = ModelExplorer.Geometry.BoundingBox;

namespace ModelExplorer.App.ViewModels;

public sealed record ViewerLayout(int Count, string Label);
public enum ComparisonDisplayKind { BlendMeshes, Heatmap }
public sealed record ComparisonDisplayMode(string Label, ComparisonDisplayKind Kind);

/// <summary>Owns the library, independent model slots and their linked comparison.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly GeometryLoaderRegistry _loaders = GeometryLoaderRegistry.CreateDefault();
    private readonly DefaultEffectsManager _effectsManager = new();
    private CancellationTokenSource? _comparisonCancellation;
    private CancellationTokenSource? _heatmapCancellation;
    private int _comparisonGeneration;
    private int _heatmapGeneration;
    private bool _synchronizing;
    private bool _stopped;
    private (MeshData A, MeshData B, MeshComparisonResult Result)? _comparison;

    public ThumbnailService Thumbnails { get; }
    public LibraryViewModel Library { get; }
    public IReadOnlyList<ModelViewModel> Panes { get; }
    public ModelViewModel ComparisonPane { get; }
    public IReadOnlyList<ViewerLayout> LayoutOptions { get; } =
        [new(1, "1 model"), new(2, "2 models"), new(4, "4 models")];
    public IReadOnlyList<ComparisonDisplayMode> ComparisonModes { get; } =
        [new("Blend meshes", ComparisonDisplayKind.BlendMeshes), new("Heatmap", ComparisonDisplayKind.Heatmap)];
    public IReadOnlyList<string> ComparisonSides { get; } = ["A", "B"];
    public IReadOnlyList<ModelViewModel> VisiblePanes => ShowComparison
        ? [Panes[0], Panes[1], ComparisonPane] : Panes.Take(SelectedLayout.Count).ToArray();

    [ObservableProperty] private ViewerLayout _selectedLayout;
    [ObservableProperty] private ModelViewModel _activePane;
    [ObservableProperty] private bool _syncCameras = true;
    [ObservableProperty] private bool _alignCenters;
    [ObservableProperty] private bool _showLibrary = true;
    [ObservableProperty] private bool _showComparison;
    [ObservableProperty] private bool _hasComparison;
    [ObservableProperty] private bool _isComparing;
    [ObservableProperty] private string _comparisonStatus = string.Empty;
    [ObservableProperty] private string _comparisonSide = "B";
    [ObservableProperty] private ComparisonDisplayMode _selectedComparisonMode;
    private double _comparisonTolerance = 0.1;

    public double ComparisonTolerance
    {
        get => _comparisonTolerance;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > float.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value), "Enter a finite, non-negative tolerance.");
            if (SetProperty(ref _comparisonTolerance, value))
            {
                OnPropertyChanged(nameof(LegendLow));
                OnPropertyChanged(nameof(LegendHigh));
                if (ShowHeatmapControls) _ = RefreshHeatmapAsync();
            }
        }
    }

    public bool ShowBlendControls => ShowComparison && SelectedComparisonMode.Kind == ComparisonDisplayKind.BlendMeshes;
    public bool ShowHeatmapControls => ShowComparison && SelectedComparisonMode.Kind == ComparisonDisplayKind.Heatmap;
    public bool ShowHeatmapLegend => ShowHeatmapControls && HasComparison;
    public string LegendLow => $"≤ {ComparisonTolerance:G4} mm";
    public string LegendHigh => $"{Math.Max(_comparison?.Result.MaxDistance ?? 0, ComparisonTolerance):G4} mm";
    private bool HasComparisonSources => Panes.Take(2).All(p =>
        !p.IsLoadPending && p.SourceMesh is { TriangleCount: > 0 });
    private bool CanCompare => !_stopped && !IsComparing && (ShowComparison || SelectedLayout.Count >= 2) &&
        HasComparisonSources;

    public MainViewModel()
    {
        Thumbnails = new ThumbnailService(_loaders, new ThumbnailCache(ThumbnailCache.DefaultDirectory));
        Library = new LibraryViewModel(_loaders.SupportedExtensions, Thumbnails);
        Panes = new[] { "A", "B", "C", "D" }.Select(label => new ModelViewModel(_effectsManager, label)).ToArray();
        ComparisonPane = new ModelViewModel(_effectsManager, "Comparison", true);
        _selectedLayout = LayoutOptions[0];
        _selectedComparisonMode = ComparisonModes[0];
        _activePane = Panes[0];
        _activePane.IsActive = true;
        foreach (var pane in Panes.Append(ComparisonPane))
        {
            pane.Camera.Changed += (_, _) => SynchronizeFrom(pane);
            pane.PropertyChanged += OnPaneChanged;
            pane.FitRequested += (_, _) => { if (SyncCameras) FitAll(); else FitPane(pane); };
        }
        Library.PropertyChanged += OnLibraryChanged;
    }

    private void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_stopped && e.PropertyName == nameof(LibraryViewModel.SelectedModel) && Library.SelectedModel is { } model)
            _ = ActivePane.LoadAsync(model.FullPath, TimeSpan.FromMilliseconds(120));
    }

    public bool IsSupported(string path) => _loaders.IsSupported(path);
    public Task LoadAsync(string path) => ActivePane.LoadAsync(path);

    [RelayCommand]
    private Task OpenPathAsync(string? path) => path is null ? Task.CompletedTask : LoadAsync(path);

    [RelayCommand]
    private async Task OpenModelAsync()
    {
        var dialog = new OpenFileDialog { Title = "Open models", Filter = _loaders.BuildFileDialogFilter(), Multiselect = true };
        if (dialog.ShowDialog() == true) await LoadFilesAsync(dialog.FileNames);
    }

    public async Task LoadFilesAsync(IEnumerable<string> paths)
    {
        var files = paths.Where(IsSupported).ToArray();
        if (files.Length == 0 || _stopped) return;
        var start = Panes.ToList().IndexOf(ActivePane);
        if (files.Length > 1)
        {
            CloseComparison();
            var needed = Math.Min(start + files.Length, 4);
            if (SelectedLayout.Count < needed) SelectedLayout = LayoutOptions.First(l => l.Count >= needed);
        }
        var targets = Panes.Skip(start).Take(SelectedLayout.Count - start).ToArray();
        await Task.WhenAll(files.Take(targets.Length).Select((path, i) => targets[i].LoadAsync(path)));
        if (files.Length > targets.Length)
            ActivePane.StatusMessage = $"Opened {targets.Length} models. Up to four panes are available; {files.Length - targets.Length} files were not opened.";
    }

    public void ActivatePane(ModelViewModel pane)
    {
        if (!pane.IsComparison) ActivePane = pane;
        if (SyncCameras)
            foreach (var other in VisiblePanes.Where(p => p != pane)) other.StopMotion();
    }

    partial void OnActivePaneChanged(ModelViewModel value)
    {
        foreach (var pane in Panes) pane.IsActive = pane == value;
    }

    partial void OnSelectedLayoutChanged(ViewerLayout value)
    {
        CloseComparison();
        foreach (var pane in Panes) pane.StopMotion();
        if (!VisiblePanes.Contains(ActivePane)) ActivePane = Panes[0];
        OnPropertyChanged(nameof(VisiblePanes));
        CompareCommand.NotifyCanExecuteChanged();
        if (SyncCameras) FitAll();
    }

    partial void OnShowComparisonChanged(bool value)
    {
        OnPropertyChanged(nameof(VisiblePanes));
        OnPropertyChanged(nameof(ShowBlendControls));
        OnPropertyChanged(nameof(ShowHeatmapControls));
        OnPropertyChanged(nameof(ShowHeatmapLegend));
        CompareCommand.NotifyCanExecuteChanged();
    }

    partial void OnSyncCamerasChanged(bool value)
    {
        foreach (var pane in Panes.Append(ComparisonPane))
        {
            pane.StopMotion();
            pane.CameraInertiaEnabled = !value;
        }
        if (value) SynchronizeFrom(ActivePane);
    }

    partial void OnAlignCentersChanged(bool value)
    {
        foreach (var pane in Panes.Append(ComparisonPane)) pane.PivotPoint = null;
        FitAll();
        InvalidateComparison();
        if (ShowComparison && CanCompare) _ = CompareAsync();
    }

    partial void OnIsComparingChanged(bool value) => CompareCommand.NotifyCanExecuteChanged();

    partial void OnHasComparisonChanged(bool value) => OnPropertyChanged(nameof(ShowHeatmapLegend));

    partial void OnComparisonSideChanged(string value)
    {
        if (ShowHeatmapControls) _ = RefreshHeatmapAsync();
    }

    partial void OnSelectedComparisonModeChanged(ComparisonDisplayMode value)
    {
        OnPropertyChanged(nameof(ShowBlendControls));
        OnPropertyChanged(nameof(ShowHeatmapControls));
        OnPropertyChanged(nameof(ShowHeatmapLegend));
        if (!ShowComparison || _stopped || !HasComparisonSources) return;

        if (value.Kind == ComparisonDisplayKind.BlendMeshes)
        {
            CancelComparisonCalculations(clearCachedResult: false);
            ShowBlendComparison();
        }
        else
        {
            ComparisonPane.ClearComparison();
            ComparisonPane.StatusMessage = "Calculating surface distances…";
            HasComparison = false;
            _ = BuildHeatmapComparisonAsync();
        }
    }

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_stopped || sender is not ModelViewModel pane) return;
        if (e.PropertyName == nameof(ModelViewModel.SourceMesh) && !pane.IsComparison)
        {
            if (pane == Panes[0] || pane == Panes[1]) InvalidateComparison();
        }
        if (e.PropertyName == nameof(ModelViewModel.IsLoadPending) && !pane.IsLoadPending)
        {
            if (SyncCameras) FitAll();
            if (ShowComparison && CanCompare && (pane == Panes[0] || pane == Panes[1])) _ = CompareAsync();
        }
        if (e.PropertyName is nameof(ModelViewModel.SourceMesh) or nameof(ModelViewModel.IsLoadPending))
            CompareCommand.NotifyCanExecuteChanged();
        if (e.PropertyName == nameof(ModelViewModel.PivotPoint) && SyncCameras && !_synchronizing)
        {
            _synchronizing = true;
            try
            {
                foreach (var target in VisiblePanes.Where(p => p != pane))
                    target.PivotPoint = pane.PivotPoint is { } point ? point + ToVector(Origin(target) - Origin(pane)) : null;
            }
            finally { _synchronizing = false; }
        }
    }

    private Vector3 Origin(ModelViewModel pane) => AlignCenters ? pane.SourceMesh?.Bounds.Center ?? Vector3.Zero : Vector3.Zero;
    private static Vector3D ToVector(Vector3 v) => new(v.X, v.Y, v.Z);

    private void SynchronizeFrom(ModelViewModel source)
    {
        if (_stopped || !SyncCameras || _synchronizing || !VisiblePanes.Contains(source)) return;
        _synchronizing = true;
        try
        {
            foreach (var target in VisiblePanes.Where(p => p != source))
            {
                var offset = Origin(target) - Origin(source);
                CameraSynchronization.Copy(source.Camera, target.Camera, offset);
                target.PivotPoint = source.PivotPoint is { } point ? point + ToVector(offset) : null;
                target.UpdateClipPlanes();
            }
        }
        finally { _synchronizing = false; }
    }

    [RelayCommand]
    private void FitAll()
    {
        var loaded = VisiblePanes.Where(p => p.SourceMesh is { TriangleCount: > 0 }).ToArray();
        if (loaded.Length == 0) return;
        foreach (var pane in VisiblePanes) pane.StopMotion();
        if (!SyncCameras) { foreach (var pane in loaded) FitPane(pane); return; }
        var bounds = Bounds.Empty;
        foreach (var pane in loaded)
        {
            var box = pane.SourceMesh!.Bounds;
            bounds = bounds.Union(new Bounds(box.Min - Origin(pane), box.Max - Origin(pane)));
        }
        var source = loaded[0];
        _synchronizing = true;
        try
        {
            foreach (var pane in VisiblePanes) pane.PivotPoint = null;
            source.FrameModel(new Bounds(bounds.Min + Origin(source), bounds.Max + Origin(source)));
        }
        finally { _synchronizing = false; }
        SynchronizeFrom(source);
    }

    private static void FitPane(ModelViewModel pane)
    {
        if (pane.SourceMesh is not { } mesh) return;
        pane.StopMotion();
        pane.PivotPoint = null;
        pane.FrameModel(mesh.Bounds);
    }

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task CompareAsync()
    {
        if (!CanCompare) return;
        var isOpening = !ShowComparison;
        InvalidateComparison();
        if (isOpening) SelectedComparisonMode = ComparisonModes[0];
        ShowComparison = true;
        if (!VisiblePanes.Contains(ActivePane)) ActivePane = Panes[0];
        SyncCameras = true;
        await ShowSelectedComparisonAsync();
        SynchronizeFrom(Panes[0]);
    }

    private Task ShowSelectedComparisonAsync()
    {
        if (SelectedComparisonMode.Kind == ComparisonDisplayKind.Heatmap)
            return BuildHeatmapComparisonAsync();

        ShowBlendComparison();
        return Task.CompletedTask;
    }

    private void ShowBlendComparison()
    {
        if (!CanCompare || Panes[0].OriginalMesh is not { } geometryA || Panes[1].OriginalMesh is not { } geometryB)
            return;

        var a = Panes[0].SourceMesh!;
        var b = Panes[1].SourceMesh!;
        ConfigureComparisonPane(() => ComparisonPane.SetBlendComparison(
            a,
            geometryA,
            b,
            geometryB,
            AlignCenters,
            Panes[0].ModelName,
            Panes[1].ModelName));
        HasComparison = true;
        ComparisonStatus = "Blend meshes · A is blue, B is orange. Adjust either opacity to expose overlaps and differences.";
        SynchronizeFrom(Panes[0]);
    }

    private async Task BuildHeatmapComparisonAsync()
    {
        if (!CanCompare || SelectedComparisonMode.Kind != ComparisonDisplayKind.Heatmap) return;
        if (_comparison is { } cached)
        {
            await RefreshHeatmapAsync();
            return;
        }

        var a = Panes[0].SourceMesh!;
        var b = Panes[1].SourceMesh!;
        var align = AlignCenters;
        var generation = _comparisonGeneration;
        var cancellation = new CancellationTokenSource();
        _comparisonCancellation = cancellation;
        IsComparing = true;
        ComparisonStatus = "Comparing both surfaces…";
        ComparisonPane.StatusMessage = "Calculating surface distances…";
        try
        {
            var result = await Task.Run(() => MeshComparison.Compare(a, b, align, cancellation.Token), cancellation.Token);
            if (generation != _comparisonGeneration || _stopped) return;
            _comparison = (a, b, result);
            if (SelectedComparisonMode.Kind == ComparisonDisplayKind.Heatmap)
                await RefreshHeatmapAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _comparisonGeneration) ComparisonStatus = $"Comparison failed: {ex.Message}";
        }
        finally
        {
            if (generation == _comparisonGeneration)
            {
                IsComparing = false;
                _comparisonCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task RefreshHeatmapAsync()
    {
        OnPropertyChanged(nameof(LegendLow));
        OnPropertyChanged(nameof(LegendHigh));
        if (_comparison is not { } comparison || !ShowComparison ||
            SelectedComparisonMode.Kind != ComparisonDisplayKind.Heatmap || _stopped) return;
        _heatmapCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _heatmapCancellation = cancellation;
        var generation = ++_heatmapGeneration;
        var useA = ComparisonSide == "A";
        var mesh = useA ? comparison.A : comparison.B;
        var result = useA ? comparison.Result.AToB : comparison.Result.BToA;
        var tolerance = (float)ComparisonTolerance;
        var maximum = MathF.Max(comparison.Result.MaxDistance, tolerance);
        try
        {
            var geometry = await Task.Run(() => ComparisonHeatmap.Build(mesh, result, tolerance, maximum, cancellation.Token), cancellation.Token);
            if (generation != _heatmapGeneration || _stopped) return;
            ConfigureComparisonPane(() =>
                ComparisonPane.SetHeatmapComparison(mesh, geometry, $"{ComparisonSide} → {(useA ? "B" : "A")}"));
            HasComparison = true;
            ComparisonStatus = $"Sampled max {comparison.Result.MaxDistance:G4} mm · " +
                $"mean A→B {comparison.Result.AToB.MeanDistance:G4}, B→A {comparison.Result.BToA.MeanDistance:G4} mm · " +
                $"triangles within tolerance: A {comparison.Result.AToB.FractionWithin(tolerance):P1}, B {comparison.Result.BToA.FractionWithin(tolerance):P1}. " +
                "Original meshes; vertices + face centers sampled.";
            SynchronizeFrom(Panes[0]);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _heatmapGeneration) ComparisonStatus = $"Could not draw comparison: {ex.Message}";
        }
        finally
        {
            if (generation == _heatmapGeneration) _heatmapCancellation = null;
            cancellation.Dispose();
        }
    }

    private void InvalidateComparison()
    {
        CancelComparisonCalculations(clearCachedResult: true);
        HasComparison = false;
        ComparisonPane.ClearComparison();
        ComparisonPane.StatusMessage = "No comparison to display.";
        ComparisonStatus = "Load models in A and B, then select Compare A / B.";
    }

    private void ConfigureComparisonPane(Action configure)
    {
        var wasSynchronizing = _synchronizing;
        _synchronizing = true;
        try { configure(); }
        finally { _synchronizing = wasSynchronizing; }
    }

    private void CancelComparisonCalculations(bool clearCachedResult)
    {
        ++_comparisonGeneration;
        ++_heatmapGeneration;
        _comparisonCancellation?.Cancel();
        _comparisonCancellation = null;
        _heatmapCancellation?.Cancel();
        _heatmapCancellation = null;
        if (clearCachedResult) _comparison = null;
        IsComparing = false;
    }

    [RelayCommand]
    private void CancelComparison()
    {
        InvalidateComparison();
        ComparisonStatus = "Comparison canceled. Select Compare A / B to retry.";
    }

    [RelayCommand]
    private void CloseComparison()
    {
        InvalidateComparison();
        ShowComparison = false;
    }

    [RelayCommand] private void Reveal(string? path) { if (!string.IsNullOrEmpty(path)) ActivePane.StatusMessage = ShellIntegration.Reveal(path); }
    [RelayCommand] private void CopyPath(string? path) { if (!string.IsNullOrEmpty(path)) ActivePane.StatusMessage = ShellIntegration.CopyText(path, Path.GetFileName(path)); }

    public void StopWork()
    {
        _stopped = true;
        InvalidateComparison();
        foreach (var pane in Panes.Append(ComparisonPane)) pane.StopWork();
    }

    public void Dispose()
    {
        StopWork();
        Library.PropertyChanged -= OnLibraryChanged;
        Thumbnails.Dispose();
        _effectsManager.Dispose();
    }
}
