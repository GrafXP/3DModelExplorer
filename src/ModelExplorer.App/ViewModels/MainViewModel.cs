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
    private readonly GeometryLoaderRegistry _loaders = GeometryLoaderRegistry.CreateDefault();
    private CancellationTokenSource? _loadCancellation;
    public DefaultEffectsManager EffectsManager { get; } = new();

    /// <summary>
    /// Library roots, scanning and the indexed list. Selecting a row does not
    /// load it into the viewer yet — that seam, with its cancellation semantics,
    /// is step 5.
    /// </summary>
    public LibraryViewModel Library { get; }

    public MainViewModel()
    {
        Library = new LibraryViewModel(_loaders.SupportedExtensions);
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPivot))]
    private Transform3D? _pivotTransform;

    public bool HasPivot => PivotTransform is not null;

    /// <summary>Bounding sphere of the current model: drives pivot scale and clip planes.</summary>
    private System.Numerics.Vector3 _modelCentre;
    private float _modelRadius = 1f;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModel))]
    private HxMesh? _mesh;

    public bool HasModel => Mesh is not null;

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
    private bool _hasError;

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

    public async Task LoadAsync(string path)
    {
        // Supersede any load still in flight. Step 5 leans on this heavily when
        // arrow-keying through a list, but it already matters for a slow file
        // opened twice in quick succession.
        //
        // Only cancelled here, never disposed: the superseded load is still using
        // its token on a worker thread, and each load disposes its own source in
        // its finally block.
        var previous = Interlocked.Exchange(ref _loadCancellation, null);
        if (previous is not null)
        {
            await previous.CancelAsync();
        }

        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var token = cancellation.Token;

        IsLoading = true;
        HasError = false;
        ModelName = Path.GetFileName(path);
        StatusMessage = $"Loading {ModelName}…";
        TriangleCountText = string.Empty;
        TimingText = string.Empty;

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var data = await Task.Run(() => _loaders.Load(path, token), token);
            var parseMs = stopwatch.Elapsed.TotalMilliseconds;

            stopwatch.Restart();
            var geometry = await Task.Run(() => BuildGeometry(data), token);
            var uploadMs = stopwatch.Elapsed.TotalMilliseconds;

            token.ThrowIfCancellationRequested();

            var triangles = data.TriangleCount;
            var bounds = data.Bounds;
            var size = bounds.Size;

            // Dropped before assignment so the intermediate arrays become
            // collectable rather than being pinned alongside the GPU copy.
            data = null!;

            Mesh = geometry;

            // A pivot picked on the previous model means nothing on this one.
            PivotTransform = null;
            FrameModel(bounds);
            TriangleCountText = $"{triangles:N0} triangles";
            TimingText = $"parse {parseMs:N0} ms · build {uploadMs:N0} ms";
            StatusMessage = triangles == 0
                ? $"{ModelName} contains no geometry"
                : $"{ModelName}  ·  {size.X:N1} × {size.Y:N1} × {size.Z:N1} mm";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer load; the newer one owns the status text.
        }
        catch (Exception ex) when (ex is GeometryFormatException or IOException or UnauthorizedAccessException)
        {
            HasError = true;
            Mesh = null;
            StatusMessage = $"Could not open {ModelName}: {ex.Message}";
        }
        finally
        {
            // Only clear the busy flag if a newer load has not already taken over.
            if (Interlocked.CompareExchange(ref _loadCancellation, null, cancellation) == cancellation)
            {
                IsLoading = false;
            }

            cancellation.Dispose();
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
        // Scaled to the model so the marker is neither a speck on a large print
        // nor larger than a small one.
        var scale = Math.Max(_modelRadius * 0.012, 0.01);

        var transform = new Transform3DGroup();
        transform.Children.Add(new ScaleTransform3D(scale, scale, scale));
        transform.Children.Add(new TranslateTransform3D(x, y, z));
        transform.Freeze();

        PivotTransform = transform;
        StatusMessage = $"Rotation centre set to {x:N1}, {y:N1}, {z:N1} mm — right-click empty space to clear";
    }

    /// <summary>Reverts the orbit pivot to the model centre.</summary>
    public void ClearPivot()
    {
        if (PivotTransform is null)
        {
            return;
        }

        PivotTransform = null;
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
