using System.Diagnostics;
using System.Numerics;
using ModelExplorer.Core.Models;
using ModelExplorer.Geometry;

namespace ModelExplorer.Core.Viewing;

/// <summary>How the shown models are put on screen.</summary>
public enum CompareMode
{
    /// <summary>A pane per model, arranged in a grid.</summary>
    Separate,

    /// <summary>All models in one pane, each in its own colour, see-through.</summary>
    Overlay,

    /// <summary>A pane per model, coloured by its distance from the active model's surface.</summary>
    Deviation,
}

/// <summary>A cut result ready to draw in place of the model.</summary>
public sealed record CutView(MeshData Mesh, IDisposable? Gpu);

/// <summary>A model's surface deviation, ready to colour it with.</summary>
public sealed record DeviationView(MeshDistanceResult Distances, IDisposable? Gpu);

/// <summary>
/// What the viewer shows: which models, in which panes, seen how.
/// </summary>
/// <remarks>
/// <para>There is no layout to choose. Every model handed to <see cref="Show"/> —
/// the library selection, or files opened or dropped — gets a pane, and the panes
/// are arranged to show them as large as possible. A model that is still loading
/// leaves the previous one in its pane until it can be drawn, so stepping through
/// the library never flashes an empty view.</para>
/// <para>Lives on the UI thread, like <see cref="ModelCache"/>, whose results it
/// takes in through <see cref="ModelCache.Completed"/>.</para>
/// </remarks>
public sealed class Workspace : IDisposable
{
    /// <summary>More than this is a selection to act on, not something to look at side by side.</summary>
    public const int MaxModels = 16;

    /// <summary>
    /// How long the plane has to be still before the real cut is computed. While it
    /// moves, the renderer clips the original mesh on the GPU instead.
    /// </summary>
    private static readonly TimeSpan CutDebounce = TimeSpan.FromMilliseconds(180);

    private static readonly Vector3[] OverlayPalette =
    [
        new(0.20f, 0.64f, 1.00f),
        new(1.00f, 0.38f, 0.16f),
        new(0.30f, 0.85f, 0.45f),
        new(0.86f, 0.40f, 0.92f),
        new(0.96f, 0.84f, 0.24f),
        new(0.22f, 0.88f, 0.88f),
        new(0.95f, 0.30f, 0.40f),
        new(0.85f, 0.85f, 0.85f),
    ];

    private readonly ModelCache _cache;
    private readonly Func<MeshData, CancellationToken, IDisposable?>? _prepareMesh;
    private readonly Func<float[], IDisposable?>? _prepareDistances;
    private readonly List<string> _paths = [];
    private LoadedModel[] _requested = [];
    private LoadedModel?[] _shown = [];
    private OrbitCamera[] _paneCameras = new OrbitCamera[MaxModels];
    private PaneRect[] _panes = [];
    private bool _fitPending;
    private bool _syncCameras = true;
    private bool _alignCenters;
    private int _active;
    private CompareMode _mode;

    private readonly Dictionary<(MeshData Model, MeshData Reference, bool Align), DeviationJob> _deviations = [];
    private CancellationTokenSource _deviationCancellation = new();
    private float _tolerance = 0.1f;

    private readonly Dictionary<MeshData, CutJob> _cuts = new(ReferenceEqualityComparer.Instance);
    private CancellationTokenSource? _cutDelay;
    private CutPlaneSettings _cut = CutPlaneSettings.Default;
    private bool _cutEnabled;
    private bool _cutDragging;
    private bool _disposed;

    public Workspace(
        ModelCache cache,
        Func<MeshData, CancellationToken, IDisposable?>? prepareMesh = null,
        Func<float[], IDisposable?>? prepareDistances = null)
    {
        _cache = cache;
        _prepareMesh = prepareMesh;
        _prepareDistances = prepareDistances;
        _cache.Completed += OnModelCompleted;
        Array.Fill(Opacity, 0.55f);
    }

    /// <summary>The paths shown, in pane order.</summary>
    public IReadOnlyList<string> Paths => _paths;

    public int Count => _paths.Count;

    /// <summary>How many panes the current mode draws.</summary>
    public int PaneCount => _mode == CompareMode.Overlay ? Math.Min(1, Count) : Count;

    /// <summary>
    /// The model the user is working with: the one the status bar describes, that a
    /// keyboard shortcut acts on, and the reference the deviation view measures against.
    /// </summary>
    public int Active
    {
        get => _active;
        set
        {
            value = Count == 0 ? 0 : Math.Clamp(value, 0, Count - 1);
            if (_active != value)
            {
                _active = value;
                if (_mode == CompareMode.Deviation)
                {
                    StartDeviations();
                }
            }
        }
    }

    public CompareMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            if (_mode == CompareMode.Overlay || value == CompareMode.Overlay)
            {
                // One pane becomes several or the other way round; their shapes change.
                _fitPending = true;
            }

            _mode = value;
            if (value == CompareMode.Deviation)
            {
                StartDeviations();
            }
            else
            {
                CancelDeviations();
            }
        }
    }

    /// <summary>
    /// Whether every pane is seen through one camera. Linked, a turn, pan or zoom in
    /// any pane moves them all, so the same side of each model is always compared.
    /// </summary>
    public bool SyncCameras
    {
        get => _syncCameras;
        set
        {
            if (_syncCameras == value)
            {
                return;
            }

            _syncCameras = value;
            if (value)
            {
                SharedCamera = _paneCameras[Math.Clamp(Active, 0, MaxModels - 1)];
            }
            else
            {
                Array.Fill(_paneCameras, SharedCamera);
            }
        }
    }

    /// <summary>
    /// Whether models are drawn with their bounding-box centres on top of each other,
    /// instead of where their files put them. Scale and orientation are kept.
    /// </summary>
    public bool AlignCenters
    {
        get => _alignCenters;
        set
        {
            if (_alignCenters == value)
            {
                return;
            }

            _alignCenters = value;
            _fitPending = true;
            if (_mode == CompareMode.Deviation)
            {
                StartDeviations();
            }
        }
    }

    /// <summary>Keep the camera when the models change, instead of framing the new ones.</summary>
    public bool KeepView { get; set; }

    public OrbitCamera SharedCamera;

    /// <summary>Per-model opacity in the overlay.</summary>
    public float[] Opacity { get; } = new float[MaxModels];

    public bool ShowBoundingBox { get; set; }

    public bool ShowBuildPlate { get; set; }

    public BuildPlate Plate { get; set; } = BuildPlates.Default;

    public Appearance Appearance { get; } = new();

    /// <summary>Paths that were asked for but did not fit, for the caller to mention.</summary>
    public int Overflow { get; private set; }

    /// <summary>Changes whenever something visible changes outside of a user action.</summary>
    public int Version { get; private set; }

    // ------------------------------------------------------------------ models

    /// <summary>
    /// Shows <paramref name="paths"/>, one pane each (up to <see cref="MaxModels"/>).
    /// <paramref name="prefetch"/> names models likely to be asked for next; they are
    /// loaded in the background at lower priority.
    /// </summary>
    public void Show(IReadOnlyList<string> paths, IReadOnlyList<string>? prefetch = null)
    {
        var distinct = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Overflow = Math.Max(0, distinct.Count - MaxModels);
        var next = distinct.Take(MaxModels).ToList();
        var previousShown = _shown;
        var previousActive = Active < _paths.Count ? _paths[Active] : null;

        _paths.Clear();
        _paths.AddRange(next);

        // A model leaving the screen keeps its pane until its replacement can be
        // drawn — unless it is staying on screen in another pane anyway.
        var stale = new List<string>();
        _shown = new LoadedModel?[next.Count];
        var requested = _cache.SetWanted([.. next, .. previousShown.Where(m => m is not null).Select(m => m!.Path)], prefetch);
        _requested = requested[..next.Count];
        for (var i = 0; i < next.Count; i++)
        {
            var entry = _requested[i];
            if (entry.State != ModelState.Loading)
            {
                _shown[i] = entry;
            }
            else if (i < previousShown.Length && previousShown[i] is { } previous &&
                     !next.Contains(previous.Path, StringComparer.OrdinalIgnoreCase) &&
                     ReferenceEquals(_cache.Find(previous.Path), previous))
            {
                // Only the cache's own instance: a file that changed on disk has just
                // been replaced, and the old instance's GPU mesh released.
                _shown[i] = previous;
            }
        }

        var activeIndex = previousActive is null ? -1 : next.FindIndex(p => string.Equals(p, previousActive, StringComparison.OrdinalIgnoreCase));
        _active = activeIndex >= 0 ? activeIndex : 0;
        _fitPending = true;
        ClearPivots();
        PruneJobs();
        if (_mode == CompareMode.Deviation)
        {
            StartDeviations();
        }

        ScheduleCuts(TimeSpan.Zero);
        Version++;
    }

    /// <summary>Adds models to those shown.</summary>
    public void Add(IEnumerable<string> paths) => Show([.. _paths, .. paths]);

    /// <summary>The model asked for in a pane: loading, loaded or failed.</summary>
    public LoadedModel? Requested(int index) => index >= 0 && index < _requested.Length ? _requested[index] : null;

    /// <summary>
    /// The model drawn in a pane: the one asked for once it is ready, the previous
    /// one until then, or null when there is nothing to draw yet.
    /// </summary>
    public LoadedModel? Shown(int index) => index >= 0 && index < _shown.Length ? _shown[index] : null;

    /// <summary>Loaded meshes of the panes, in pane order, for the overlay.</summary>
    public IEnumerable<(int Index, LoadedModel Model)> LoadedModels()
    {
        for (var i = 0; i < _shown.Length; i++)
        {
            if (_shown[i] is { IsLoaded: true, Mesh.TriangleCount: > 0 } model)
            {
                yield return (i, model);
            }
        }
    }

    /// <summary>
    /// Where a model is moved to when drawn: by minus its centre when centres are
    /// aligned, otherwise not at all.
    /// </summary>
    public Vector3 Offset(LoadedModel model) =>
        _alignCenters && model.Mesh is { } mesh ? -mesh.Bounds.Center : Vector3.Zero;

    public static Vector3 OverlayColor(int index) => OverlayPalette[index % OverlayPalette.Length];

    private void OnModelCompleted(LoadedModel model)
    {
        var changed = false;
        for (var i = 0; i < _requested.Length; i++)
        {
            if (ReferenceEquals(_requested[i], model) && !ReferenceEquals(_shown[i], model))
            {
                _shown[i] = model;
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        _fitPending = true;
        ClearPivots();
        if (_mode == CompareMode.Deviation)
        {
            StartDeviations();
        }

        ScheduleCuts(TimeSpan.Zero);
        Version++;
    }

    // ------------------------------------------------------------------ cameras

    /// <summary>The camera of a pane: the shared one while linked, its own otherwise.</summary>
    public ref OrbitCamera Camera(int pane)
    {
        if (_syncCameras || _mode == CompareMode.Overlay || pane < 0 || pane >= MaxModels)
        {
            return ref SharedCamera;
        }

        return ref _paneCameras[pane];
    }

    /// <summary>
    /// Takes the pane rectangles of this frame, and frames the models if they changed.
    /// Called by the window before it draws.
    /// </summary>
    public void Layout(PaneRect[] panes)
    {
        _panes = panes;
        if (_fitPending && panes.Length > 0 && LoadedModels().Any())
        {
            _fitPending = false;
            if (!KeepView || !SharedCamera.IsSet)
            {
                FitAll();
            }
            else if (!_syncCameras)
            {
                // Keeping the view still has to give a pane that never had one a camera.
                for (var i = 0; i < panes.Length; i++)
                {
                    if (!_paneCameras[i].IsSet)
                    {
                        FitPane(i);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Frames every model. Linked, at one common scale so sizes compare directly;
    /// otherwise each pane frames its own.
    /// </summary>
    public void FitAll()
    {
        if (_panes.Length == 0)
        {
            return;
        }

        if (_syncCameras || _mode == CompareMode.Overlay)
        {
            var minAspect = _panes.Min(pane => pane.Aspect);
            SharedCamera = OrbitCamera.Framing(SceneBounds(), minAspect);
            Array.Fill(_paneCameras, SharedCamera);
            return;
        }

        for (var i = 0; i < _panes.Length; i++)
        {
            FitPane(i);
        }
    }

    public void FitPane(int pane)
    {
        if (pane < 0 || pane >= _panes.Length)
        {
            return;
        }

        if (_syncCameras || _mode == CompareMode.Overlay)
        {
            FitAll();
            return;
        }

        if (Shown(pane) is { IsLoaded: true } model)
        {
            _paneCameras[pane] = OrbitCamera.Framing(Bounds(model), _panes[pane].Aspect);
        }
    }

    /// <summary>A model's bounds where it is drawn.</summary>
    public BoundingBox Bounds(LoadedModel model)
    {
        if (model.Mesh is not { } mesh || mesh.Bounds.IsEmpty)
        {
            return BoundingBox.Empty;
        }

        var offset = Offset(model);
        return new BoundingBox(mesh.Bounds.Min + offset, mesh.Bounds.Max + offset);
    }

    /// <summary>All drawn models together, where they are drawn.</summary>
    public BoundingBox SceneBounds()
    {
        var bounds = BoundingBox.Empty;
        foreach (var (_, model) in LoadedModels())
        {
            bounds = bounds.Union(Bounds(model));
        }

        return bounds;
    }

    /// <summary>Turns about a picked point, or about the camera target again when null.</summary>
    public void SetPivot(int pane, Vector3? point) => Camera(pane).Pivot = point;

    private void ClearPivots()
    {
        SharedCamera.Pivot = null;
        for (var i = 0; i < _paneCameras.Length; i++)
        {
            _paneCameras[i].Pivot = null;
        }
    }

    // ------------------------------------------------------------------ deviation

    /// <summary>Distances within this many millimetres count as matching (green).</summary>
    public float Tolerance
    {
        get => _tolerance;
        set => _tolerance = float.IsFinite(value) ? Math.Clamp(value, 0, 1e6f) : _tolerance;
    }

    public bool IsComputingDeviation => _deviations.Values.Any(job => job.Running);

    /// <summary>The top of the colour scale: the largest deviation shown, at least the tolerance.</summary>
    public float DeviationMaximum
    {
        get
        {
            var maximum = _tolerance;
            for (var i = 0; i < Count; i++)
            {
                if (Deviation(i) is { } view)
                {
                    maximum = MathF.Max(maximum, view.Distances.MaxDistance);
                }
            }

            return maximum;
        }
    }

    /// <summary>
    /// The deviation colouring of a pane, or null if it has none (yet). Each model
    /// is measured against the active one. With two models the active one is
    /// coloured by its distance from the other, so both directions are on screen;
    /// with more it stays neutral as the reference.
    /// </summary>
    public DeviationView? Deviation(int index)
    {
        if (_mode != CompareMode.Deviation || DeviationPair(index) is not var (model, reference))
        {
            return null;
        }

        return _deviations.TryGetValue((model, reference, _alignCenters), out var job) && job.View is { } view ? view : null;
    }

    /// <summary>What a pane's deviation looks like in words, for its label.</summary>
    public string DeviationText(int index)
    {
        if (_mode != CompareMode.Deviation)
        {
            return string.Empty;
        }

        if (DeviationPair(index) is not var (model, reference))
        {
            return index == Active ? "Reference" : string.Empty;
        }

        if (!_deviations.TryGetValue((model, reference, _alignCenters), out var job))
        {
            return string.Empty;
        }

        if (job.Error is { } error)
        {
            return $"Comparison failed: {error}";
        }

        if (job.View is not { } view)
        {
            return "Measuring surface distances…";
        }

        var prefix = index == Active ? "Reference · " : string.Empty;
        return $"{prefix}max {view.Distances.MaxDistance:G4} mm · mean {view.Distances.MeanDistance:G4} mm · " +
            $"{view.Distances.FractionWithin(_tolerance):P1} within tolerance";
    }

    public void CancelDeviations()
    {
        _deviationCancellation.Cancel();
        _deviationCancellation.Dispose();
        _deviationCancellation = new CancellationTokenSource();
        foreach (var (key, job) in _deviations.ToList())
        {
            if (job.Running)
            {
                _deviations.Remove(key);
            }
        }
    }

    private (MeshData Model, MeshData Reference)? DeviationPair(int index)
    {
        if (Count < 2 || Shown(Active) is not { IsLoaded: true, Mesh: { TriangleCount: > 0 } reference } ||
            Shown(index) is not { IsLoaded: true, Mesh: { TriangleCount: > 0 } model })
        {
            return null;
        }

        if (index != Active)
        {
            return (model, reference);
        }

        // The reference itself: coloured only when there is exactly one other model.
        return Count == 2 && Shown(1 - Active) is { IsLoaded: true, Mesh: { TriangleCount: > 0 } other } ? (reference, other) : null;
    }

    private void StartDeviations()
    {
        if (_mode != CompareMode.Deviation)
        {
            return;
        }

        for (var i = 0; i < Count; i++)
        {
            if (i == Active || DeviationPair(i) is not var (model, reference))
            {
                continue;
            }

            var key = (model, reference, _alignCenters);
            if (!_deviations.ContainsKey(key))
            {
                var job = new DeviationJob();
                job.Running = job.Reverse.Running = true;
                _deviations[key] = job;
                _deviations[(reference, model, _alignCenters)] = job.Reverse;
                _ = RunDeviationAsync(key, job, _deviationCancellation.Token);
            }
        }
    }

    private async Task RunDeviationAsync((MeshData Model, MeshData Reference, bool Align) key, DeviationJob job, CancellationToken token)
    {
        var prepare = _prepareDistances;
        try
        {
            var (result, forward, backward) = await Task.Run(() =>
            {
                // Measured from the reference's frame: aligning moves the model's
                // centre onto the reference's, as drawing does.
                var comparison = MeshComparison.Compare(key.Model, key.Reference, key.Align, token);
                return (comparison, prepare?.Invoke(comparison.AToB.TriangleDistances), prepare?.Invoke(comparison.BToA.TriangleDistances));
            }, token);

            if (!_deviations.TryGetValue(key, out var current) || !ReferenceEquals(current, job))
            {
                forward?.Dispose();
                backward?.Dispose();
                return;
            }

            job.View = new DeviationView(result.AToB, forward);
            job.Reverse.View = new DeviationView(result.BToA, backward);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or InvalidOperationException)
        {
            job.Error = job.Reverse.Error = ex.Message;
        }
        finally
        {
            job.Running = job.Reverse.Running = false;
            Version++;
        }
    }

    // ------------------------------------------------------------------ cut plane

    public bool CutEnabled
    {
        get => _cutEnabled;
        set
        {
            if (_cutEnabled == value)
            {
                return;
            }

            _cutEnabled = value;
            if (value)
            {
                ScheduleCuts(TimeSpan.Zero);
            }
            else
            {
                CancelCuts(dispose: true);
            }
        }
    }

    /// <summary>The plane. Setting it shows the GPU preview at once and recomputes the real cut once it is still.</summary>
    public CutPlaneSettings Cut
    {
        get => _cut;
        set
        {
            if (_cut == value)
            {
                return;
            }

            _cut = value;
            ScheduleCuts(CutDebounce);
        }
    }

    /// <summary>A manipulator started dragging the plane: no real cut until it lets go.</summary>
    public void BeginCutDrag()
    {
        _cutDragging = true;
        _cutDelay?.Cancel();
    }

    /// <summary>The manipulator let go: cut now.</summary>
    public void EndCutDrag()
    {
        _cutDragging = false;
        ScheduleCuts(TimeSpan.Zero);
    }

    /// <summary>The finished cut of a pane's model for the current plane, or null while there is none.</summary>
    public CutView? CutResult(int index)
    {
        if (!_cutEnabled || Shown(index) is not { IsLoaded: true, Mesh: { } mesh } ||
            !_cuts.TryGetValue(mesh, out var job) || job.Settings != _cut || job.Result is null)
        {
            return null;
        }

        return new CutView(job.Result.Mesh, job.Gpu);
    }

    /// <summary>What the cut of a pane's model came to, in words.</summary>
    public string CutText(int index)
    {
        if (!_cutEnabled || Shown(index) is not { IsLoaded: true, Mesh: { } mesh })
        {
            return string.Empty;
        }

        if (!_cuts.TryGetValue(mesh, out var job) || job.Settings != _cut)
        {
            return "Previewing cut…";
        }

        return job.Text;
    }

    /// <summary>The plane for a model, in the model's own coordinates, with the kept half positive.</summary>
    public Plane CutPlane(LoadedModel model) =>
        _cut.For(model.Mesh?.Bounds ?? BoundingBox.Empty);

    private void ScheduleCuts(TimeSpan delay)
    {
        _cutDelay?.Cancel();
        _cutDelay = null;
        if (!_cutEnabled || _cutDragging || _disposed)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _cutDelay = cancellation;
        _ = StartCutsAfterAsync(delay, cancellation);
    }

    private async Task StartCutsAfterAsync(TimeSpan delay, CancellationTokenSource cancellation)
    {
        if (delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(delay, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (cancellation.IsCancellationRequested || !_cutEnabled)
        {
            return;
        }

        foreach (var (_, model) in LoadedModels())
        {
            var mesh = model.Mesh!;
            if (_cuts.TryGetValue(mesh, out var existing))
            {
                if (existing.Settings == _cut)
                {
                    continue;
                }

                existing.Cancellation.Cancel();
                existing.Gpu?.Dispose();
            }

            var job = new CutJob(_cut, new CancellationTokenSource());
            _cuts[mesh] = job;
            _ = RunCutAsync(mesh, job);
        }
    }

    private async Task RunCutAsync(MeshData mesh, CutJob job)
    {
        var token = job.Cancellation.Token;
        var plane = job.Settings.For(mesh.Bounds);
        var prepare = _prepareMesh;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var (result, gpu) = await Task.Run(() =>
            {
                var cut = MeshPlaneCutter.CutAndCap(mesh, plane, token);
                token.ThrowIfCancellationRequested();
                return (cut, cut.Mesh.TriangleCount > 0 ? prepare?.Invoke(cut.Mesh, token) : null);
            }, token);

            if (!_cuts.TryGetValue(mesh, out var current) || !ReferenceEquals(current, job) || !_cutEnabled)
            {
                gpu?.Dispose();
                return;
            }

            job.Result = result;
            job.Gpu = gpu;
            job.Text = Describe(result, stopwatch.Elapsed);
            Version++;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            if (_cuts.TryGetValue(mesh, out var current) && ReferenceEquals(current, job))
            {
                job.Text = $"Could not create a solid cut — {ex.Message}";
                Version++;
            }
        }
    }

    private static string Describe(MeshPlaneCutResult result, TimeSpan elapsed)
    {
        var timing = $" · {elapsed.TotalMilliseconds:N0} ms";
        if (result.Mesh.TriangleCount == 0)
        {
            return "The kept side contains no geometry";
        }

        if (result.OpenContourCount > 0)
        {
            return result.OpenContourCount == 1
                ? "Cut is open — 1 contour could not be capped" + timing
                : $"Cut is open — {result.OpenContourCount:N0} contours could not be capped" + timing;
        }

        if (result.ClosedContourCount == 0)
        {
            return "The plane does not intersect the model";
        }

        return (result.ClosedContourCount == 1 ? "Solid cut · 1 section capped" : $"Solid cut · {result.ClosedContourCount:N0} sections capped") + timing;
    }

    private void CancelCuts(bool dispose)
    {
        _cutDelay?.Cancel();
        _cutDelay = null;
        foreach (var job in _cuts.Values)
        {
            job.Cancellation.Cancel();
            if (dispose)
            {
                job.Gpu?.Dispose();
            }
        }

        if (dispose)
        {
            _cuts.Clear();
        }
    }

    /// <summary>Forgets cuts and deviations of models that are no longer shown.</summary>
    private void PruneJobs()
    {
        var shown = new HashSet<MeshData>(_shown.Concat(_requested).Where(m => m?.Mesh is not null).Select(m => m!.Mesh!), ReferenceEqualityComparer.Instance);
        foreach (var (mesh, job) in _cuts.ToList())
        {
            if (!shown.Contains(mesh))
            {
                job.Cancellation.Cancel();
                job.Gpu?.Dispose();
                _cuts.Remove(mesh);
            }
        }

        foreach (var (key, job) in _deviations.ToList())
        {
            if (!shown.Contains(key.Model) || !shown.Contains(key.Reference))
            {
                job.View?.Gpu?.Dispose();
                _deviations.Remove(key);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Completed -= OnModelCompleted;
        CancelCuts(dispose: true);
        _deviationCancellation.Cancel();
        foreach (var job in _deviations.Values)
        {
            job.View?.Gpu?.Dispose();
        }

        _deviations.Clear();
    }

    private sealed class CutJob(CutPlaneSettings settings, CancellationTokenSource cancellation)
    {
        public CutPlaneSettings Settings { get; } = settings;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public MeshPlaneCutResult? Result { get; set; }

        public IDisposable? Gpu { get; set; }

        public string Text { get; set; } = "Building solid cut…";
    }

    /// <summary>One direction of a comparison; its <see cref="Reverse"/> is the other, filled by the same run.</summary>
    private sealed class DeviationJob
    {
        public DeviationJob()
        {
            Reverse = new DeviationJob(this);
        }

        private DeviationJob(DeviationJob reverse)
        {
            Reverse = reverse;
        }

        public DeviationJob Reverse { get; }

        public DeviationView? View { get; set; }

        public string? Error { get; set; }

        public bool Running { get; set; }
    }
}
