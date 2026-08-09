using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Microsoft.Win32;
using ModelExplorer.Geometry;

// System.Windows.Media.Media3D (needed for Point3D/Vector3D) declares types with
// the same names as HelixToolkit's DirectX equivalents. Alias the DX ones so it
// is always obvious which pipeline a type belongs to.
using HxCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using HxMesh = HelixToolkit.SharpDX.MeshGeometry3D;
using MxBounds = ModelExplorer.Geometry.BoundingBox;

namespace ModelExplorer.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>
    /// How long a row must stay selected before its file is opened. Long enough
    /// that rows passed through while holding an arrow key are never read from
    /// disk at all, short enough to feel immediate when you stop.
    /// </summary>
    private static readonly TimeSpan SelectionDebounce = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// How long a load may run before the busy overlay appears. Most models parse
    /// in well under this, and an overlay that appears and disappears inside two
    /// frames reads as a flicker rather than as feedback.
    /// </summary>
    private static readonly TimeSpan BusyIndicatorDelay = TimeSpan.FromMilliseconds(180);

    private readonly GeometryLoaderRegistry _loaders = GeometryLoaderRegistry.CreateDefault();
    private readonly GeometryLoadScheduler _loads;

    /// <summary>
    /// Incremented on every accepted request. The scheduler already guarantees
    /// that only the newest parse produces a mesh; this guards the steps that
    /// happen after it — building the GPU buffers and writing the view state —
    /// so a superseded load can never repaint the viewport.
    /// </summary>
    private int _generation;

    public DefaultEffectsManager EffectsManager { get; } = new();

    /// <summary>Library roots, scanning, search and the indexed list.</summary>
    public LibraryViewModel Library { get; }

    public MainViewModel()
    {
        _loads = new GeometryLoadScheduler(_loaders.Load);
        Library = new LibraryViewModel(_loaders.SupportedExtensions);

        // The library knows nothing about the viewer; the viewer follows it. That
        // keeps the scan/search state machine free of any rendering concern and
        // leaves this one subscription as the whole seam between them.
        Library.PropertyChanged += OnLibraryPropertyChanged;
    }

    private void OnLibraryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A null selection means the results list was replaced underneath the
        // ListBox, not that the user asked for an empty viewport — so the current
        // model stays on screen.
        if (e.PropertyName == nameof(LibraryViewModel.SelectedModel) &&
            Library.SelectedModel is { } selected)
        {
            _ = LoadAsync(selected.FullPath, SelectionDebounce);
        }
    }

    /// <summary>Whether a dropped or supplied path is a format we can open.</summary>
    public bool IsSupported(string path) => _loaders.IsSupported(path);

    /// <summary>
    /// Z-up, matching how slicers and printers present a model. HelixToolkit
    /// defaults to Y-up, which would lay every print on its side.
    /// </summary>
    [ObservableProperty]
    private HxCamera _camera = new()
    {
        Position = new Point3D(120, -160, 100),
        LookDirection = new Vector3D(-120, 160, -100),
        UpDirection = new Vector3D(0, 0, 1),
        NearPlaneDistance = 0.05,
        FarPlaneDistance = 100_000,
        FieldOfView = 45,
    };

    public PhongMaterial ModelMaterial { get; } = new()
    {
        DiffuseColor = new Color4(0.62f, 0.67f, 0.74f, 1f),
        SpecularColor = new Color4(0.22f, 0.23f, 0.25f, 1f),
        SpecularShininess = 24f,
        AmbientColor = new Color4(0.10f, 0.11f, 0.13f, 1f),
    };

    /// <summary>Emissive so the pivot stays legible even on an unlit face.</summary>
    public PhongMaterial PivotMaterial { get; } = new()
    {
        DiffuseColor = new Color4(1f, 0.55f, 0.15f, 1f),
        EmissiveColor = new Color4(0.85f, 0.42f, 0.05f, 1f),
        SpecularColor = new Color4(0f, 0f, 0f, 1f),
    };

    /// <summary>Unit octahedron reused for every pivot; only its transform changes.</summary>
    public HxMesh PivotMarker { get; } = BuildOctahedron();

    /// <summary>
    /// Orbit centre picked off the model surface, or null to orbit the model's
    /// own centre.
    /// </summary>
    /// <remarks>
    /// The single source of truth for the pivot. The marker's transform is derived
    /// from it, and the code-behind mirrors it onto the camera controller — which
    /// holds the pivot as viewport state that nothing resets on its own.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPivot))]
    [NotifyPropertyChangedFor(nameof(PivotTransform))]
    private Point3D? _pivotPoint;

    public bool HasPivot => PivotPoint is not null;

    /// <summary>Places and scales the pivot marker. Null when there is no pivot.</summary>
    public Transform3D? PivotTransform
    {
        get
        {
            if (PivotPoint is not { } point)
            {
                return null;
            }

            // Scaled to the model so the marker is neither a speck on a large
            // print nor larger than a small one.
            var scale = Math.Max(_modelRadius * 0.012, 0.01);

            var transform = new Transform3DGroup();
            transform.Children.Add(new ScaleTransform3D(scale, scale, scale));
            transform.Children.Add(new TranslateTransform3D(point.X, point.Y, point.Z));
            transform.Freeze();
            return transform;
        }
    }

    /// <summary>Bounding sphere of the current model: drives pivot scale and clip planes.</summary>
    private System.Numerics.Vector3 _modelCentre;
    private float _modelRadius = 1f;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModel))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private HxMesh? _mesh;

    public bool HasModel => Mesh is not null;

    /// <summary>
    /// The "drop a file here" prompt. Suppressed while an error panel is up: two
    /// competing explanations of the same empty viewport is one too many.
    /// </summary>
    public bool ShowEmptyState => !HasModel && !HasError;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _modelName = string.Empty;

    [ObservableProperty]
    private string _triangleCountText = string.Empty;

    [ObservableProperty]
    private string _timingText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentPath))]
    private string? _currentPath;

    public bool HasCurrentPath => !string.IsNullOrEmpty(CurrentPath);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _hasError;

    /// <summary>Why the current file could not be opened. Empty unless <see cref="HasError"/>.</summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [RelayCommand]
    private async Task OpenModelAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open 3D model",
            Filter = _loaders.BuildFileDialogFilter(),
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            await LoadAsync(dialog.FileName);
        }
    }

    /// <summary>Opens a file immediately — the Ctrl+O, drag-drop and startup path.</summary>
    public Task LoadAsync(string path) => LoadAsync(path, TimeSpan.Zero);

    private async Task LoadAsync(string path, TimeSpan debounce)
    {
        // Selection fires again whenever the results list is rebuilt and the same
        // row is restored, which happens on every keystroke in the search box.
        // Re-reading a file that is already on screen would be pure waste.
        if (debounce > TimeSpan.Zero &&
            string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var generation = ++_generation;

        CurrentPath = path;
        ModelName = Path.GetFileName(path);
        StatusMessage = $"Loading {ModelName}…";
        TriangleCountText = string.Empty;
        TimingText = string.Empty;
        HasError = false;
        ErrorMessage = string.Empty;

        // Armed before the load starts so the overlay can appear while the parse
        // is still running, and cancelled once it is over so a quick load never
        // shows one at all.
        using var busy = new CancellationTokenSource();
        _ = ShowBusyWhenSlowAsync(generation, debounce + BusyIndicatorDelay, busy.Token);

        try
        {
            var outcome = await _loads.RequestAsync(path, debounce);

            if (generation != _generation)
            {
                // A newer selection owns the viewport, including its busy state.
                return;
            }

            switch (outcome.Status)
            {
                case GeometryLoadStatus.Loaded:
                    await ShowAsync(generation, outcome.Mesh!, outcome.ParseTime);
                    break;

                case GeometryLoadStatus.Failed:
                    ShowFailure(outcome.ErrorMessage);
                    break;

                case GeometryLoadStatus.Superseded:
                    break;
            }
        }
        finally
        {
            // Every exit clears the overlay, including an unexpected parser fault.
            // This runs fire-and-forget from a selection change, so an exception
            // that escaped here would leave a spinner over the viewport for the
            // rest of the session with nothing on screen to explain it.
            busy.Cancel();
            if (generation == _generation)
            {
                IsLoading = false;
            }
        }
    }

    private async Task ShowBusyWhenSlowAsync(int generation, TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (generation == _generation)
        {
            IsLoading = true;
        }
    }

    /// <summary>
    /// Copies the parsed mesh into the renderer's buffers and frames it.
    /// </summary>
    /// <remarks>
    /// The copy runs off the UI thread because it touches every vertex, and both
    /// representations are necessarily alive while it does — that transient
    /// doubling is the peak this step costs, and it is released as soon as the
    /// parsed arrays go out of scope.
    /// </remarks>
    private async Task ShowAsync(int generation, MeshData mesh, TimeSpan parseTime)
    {
        var triangles = mesh.TriangleCount;
        var bounds = mesh.Bounds;

        var stopwatch = Stopwatch.StartNew();
        var geometry = await Task.Run(() => BuildGeometry(mesh));
        var buildMs = stopwatch.Elapsed.TotalMilliseconds;

        // The build is a straight array copy and is not worth making
        // interruptible, but its result still has to be thrown away if the
        // selection moved on while it ran.
        if (generation != _generation)
        {
            return;
        }

        // A pivot picked on the previous model is a point in space that need not
        // even be on this one, so it goes before the new geometry does. Clearing
        // it also re-frames what the marker's scale is derived from.
        PivotPoint = null;

        Mesh = geometry;
        FrameModel(bounds);

        var size = bounds.Size;
        TriangleCountText = $"{triangles:N0} triangles";
        TimingText = $"parse {parseTime.TotalMilliseconds:N0} ms · build {buildMs:N0} ms";
        StatusMessage = triangles == 0
            ? $"{ModelName} contains no geometry"
            : $"{ModelName}  ·  {size.X:N1} × {size.Y:N1} × {size.Z:N1} mm";
    }

    /// <summary>
    /// Clears the viewport and states the reason. A corrupt file gets an explicit
    /// panel rather than a silently empty viewport, because "nothing is drawn" is
    /// otherwise indistinguishable from "this model is still loading".
    /// </summary>
    private void ShowFailure(string? message)
    {
        Mesh = null;
        PivotPoint = null;
        HasError = true;
        ErrorMessage = message ?? "The file could not be read.";
        StatusMessage = $"Could not open {ModelName}";
    }

    [RelayCommand]
    private void Reveal(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            StatusMessage = ShellIntegration.Reveal(path);
        }
    }

    [RelayCommand]
    private void CopyPath(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            StatusMessage = ShellIntegration.CopyText(path, Path.GetFileName(path));
        }
    }

    /// <summary>
    /// Positions the camera so the whole model is visible.
    /// </summary>
    /// <remarks>
    /// Computed from the parsed bounds rather than calling
    /// <c>Viewport3DX.ZoomExtents()</c>. ZoomExtents reads the scene's bounds,
    /// which are only refreshed once the new geometry has been through a render
    /// pass — so calling it right after assignment frames the *previous* model.
    /// Deriving it here is deterministic and has no ordering dependency.
    /// </remarks>
    private void FrameModel(MxBounds bounds)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        var centre = bounds.Center;

        // Bounding-sphere radius, so the framing holds at any orientation.
        var radius = bounds.Size.Length() * 0.5f;
        if (radius <= float.Epsilon)
        {
            radius = 1f;
        }

        _modelCentre = centre;
        _modelRadius = radius;

        var halfFov = double.DegreesToRadians(Camera.FieldOfView) * 0.5;

        // 1.35 leaves breathing room and absorbs the fact that FieldOfView is
        // the horizontal angle while the vertical extent may be the tighter fit.
        var distance = radius / Math.Sin(halfFov) * 1.35;

        // Front-left-above: reads as three-quarter view, so depth is legible
        // immediately instead of looking like a flat silhouette.
        var direction = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0.55f, 0.75f, -0.42f));
        var eye = centre - (direction * (float)distance);

        Camera.Position = new Point3D(eye.X, eye.Y, eye.Z);
        Camera.LookDirection = new Vector3D(direction.X * distance, direction.Y * distance, direction.Z * distance);
        Camera.UpDirection = new Vector3D(0, 0, 1);

        UpdateClipPlanes();
    }

    /// <summary>
    /// Recomputes the near and far planes from the camera's current distance to
    /// the model.
    /// </summary>
    /// <remarks>
    /// Must run on every camera change, not just on load. Fixed clip planes
    /// derived from the initial framing distance stay put while the wheel moves
    /// the camera closer, so zooming in eventually pushes surfaces through the
    /// near plane and slices the model open.
    ///
    /// The near plane is a fraction of the distance to the model's near surface
    /// and collapses to a small fraction of the model's own size once the camera
    /// is inside the bounding sphere. Far is kept within ~1000x of near so depth
    /// precision stays sufficient to avoid z-fighting.
    /// </remarks>
    public void UpdateClipPlanes()
    {
        if (_modelRadius <= 0)
        {
            return;
        }

        var position = Camera.Position;
        var dx = position.X - _modelCentre.X;
        var dy = position.Y - _modelCentre.Y;
        var dz = position.Z - _modelCentre.Z;
        var distance = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));

        var near = Math.Max((distance - _modelRadius) * 0.25, _modelRadius * 0.001);
        var far = Math.Max(distance + (_modelRadius * 3.0), near * 1000.0);

        // Ignore imperceptible changes; otherwise every orbit frame rewrites the
        // projection matrix, and writing the camera back here would re-enter
        // through CameraChanged.
        if (Math.Abs(Camera.NearPlaneDistance - near) <= near * 0.02 &&
            Math.Abs(Camera.FarPlaneDistance - far) <= far * 0.02)
        {
            return;
        }

        Camera.NearPlaneDistance = near;
        Camera.FarPlaneDistance = far;
    }

    /// <summary>
    /// Places the orbit pivot at a point picked off the model surface.
    /// </summary>
    public void SetPivot(double x, double y, double z)
    {
        PivotPoint = new Point3D(x, y, z);
        StatusMessage = $"Rotation centre set to {x:N1}, {y:N1}, {z:N1} mm — right-click empty space to clear";
    }

    /// <summary>Reverts the orbit pivot to the model centre.</summary>
    public void ClearPivot()
    {
        if (PivotPoint is null)
        {
            return;
        }

        PivotPoint = null;
        StatusMessage = "Rotation centre reset to model centre";
    }

    /// <summary>
    /// Unit octahedron. Small enough to be cheap, and its silhouette reads as a
    /// deliberate marker rather than as part of the model.
    /// </summary>
    private static HxMesh BuildOctahedron()
    {
        var px = new System.Numerics.Vector3(1, 0, 0);
        var nx = new System.Numerics.Vector3(-1, 0, 0);
        var py = new System.Numerics.Vector3(0, 1, 0);
        var ny = new System.Numerics.Vector3(0, -1, 0);
        var pz = new System.Numerics.Vector3(0, 0, 1);
        var nz = new System.Numerics.Vector3(0, 0, -1);

        (System.Numerics.Vector3 A, System.Numerics.Vector3 B, System.Numerics.Vector3 C)[] faces =
        [
            (px, py, pz), (py, nx, pz), (nx, ny, pz), (ny, px, pz),
            (py, px, nz), (px, ny, nz), (ny, nx, nz), (nx, py, nz),
        ];

        var positions = new Vector3Collection(faces.Length * 3);
        var normals = new Vector3Collection(faces.Length * 3);
        var indices = new IntCollection(faces.Length * 3);

        for (var i = 0; i < faces.Length; i++)
        {
            var (a, b, c) = faces[i];
            var normal = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(b - a, c - a));

            positions.Add(a);
            positions.Add(b);
            positions.Add(c);
            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);
            indices.Add(i * 3);
            indices.Add((i * 3) + 1);
            indices.Add((i * 3) + 2);
        }

        var mesh = new HxMesh { Positions = positions, Normals = normals, Indices = indices };
        mesh.UpdateBounds();
        return mesh;
    }

    /// <summary>
    /// Converts the renderer-agnostic mesh into HelixToolkit's buffers. Runs off
    /// the UI thread because it copies every vertex.
    /// </summary>
    private static HxMesh BuildGeometry(MeshData data)
    {
        var geometry = new HxMesh
        {
            Positions = new Vector3Collection(data.Positions),
            Normals = new Vector3Collection(data.Normals),
            Indices = new IntCollection(data.Indices),
        };

        geometry.UpdateBounds();
        return geometry;
    }
}
