using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HelixToolkit.Maths;
using HelixToolkit.Wpf.SharpDX;
using ModelExplorer.Geometry;
using System.Windows.Media.Media3D;
using HxMesh = HelixToolkit.SharpDX.MeshGeometry3D;

namespace ModelExplorer.App.ViewModels;

public sealed partial class ModelViewModel
{
    public string PaneLabel { get; }
    public bool IsComparison { get; }
    public bool CanEditModel => !IsComparison;
    public event EventHandler? StopCameraMotion;
    public event EventHandler? FitRequested;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isLoadPending;
    [ObservableProperty] private MeshData? _sourceMesh;
    [ObservableProperty] private bool _cameraInertiaEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModel))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowsCommittedModel))]
    [NotifyPropertyChangedFor(nameof(ShowsOverlayComparison))]
    private HxMesh? _overlayMeshA;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModel))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowsCommittedModel))]
    [NotifyPropertyChangedFor(nameof(ShowsOverlayComparison))]
    private HxMesh? _overlayMeshB;

    [ObservableProperty] private Transform3D _overlayTransformA = Transform3D.Identity;
    [ObservableProperty] private Transform3D _overlayTransformB = Transform3D.Identity;
    [ObservableProperty] private double _overlayOpacityA = 0.55;
    [ObservableProperty] private double _overlayOpacityB = 0.55;

    public PhongMaterial OverlayMaterialA { get; } = CreateOverlayMaterial(0.20f, 0.64f, 1.00f, 0.55f);
    public PhongMaterial OverlayMaterialB { get; } = CreateOverlayMaterial(1.00f, 0.38f, 0.16f, 0.55f);
    public bool ShowsOverlayComparison => IsComparison && OverlayMeshA is not null && OverlayMeshB is not null;
    public double ViewportAspectRatio { get; set; } = 1;
    public string PaneInfo => HasModel ? $"{DimensionsText} · {TriangleCountText}" : StatusMessage;
    public string EmptyMessage => IsComparison ? StatusMessage :
        "Select this pane, then choose a model\n\nOpen, Ctrl+O, or drop an STL / 3MF here";

    partial void OnDimensionsTextChanged(string value) => OnPropertyChanged(nameof(PaneInfo));
    partial void OnTriangleCountTextChanged(string value) => OnPropertyChanged(nameof(PaneInfo));
    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(PaneInfo));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    [RelayCommand]
    private void Fit() => FitRequested?.Invoke(this, EventArgs.Empty);

    public void StopMotion() => StopCameraMotion?.Invoke(this, EventArgs.Empty);

    public void StopWork()
    {
        ++_generation;
        _loads.Cancel();
        CancelCutWork();
        StopMotion();
    }

    public void ClearComparison()
    {
        SourceMesh = null;
        Mesh = null;
        OriginalMesh = null;
        OverlayMeshA = null;
        OverlayMeshB = null;
        OverlayTransformA = Transform3D.Identity;
        OverlayTransformB = Transform3D.Identity;
        PivotPoint = null;
        _modelCentre = System.Numerics.Vector3.Zero;
        _modelRadius = 0;
        ModelName = string.Empty;
        TriangleCountText = string.Empty;
        ApplyBounds(ModelExplorer.Geometry.BoundingBox.Empty);
    }

    public void SetHeatmapComparison(MeshData source, HxMesh geometry, string name)
    {
        OverlayMeshA = null;
        OverlayMeshB = null;
        SourceMesh = source;
        Mesh = geometry;
        ModelName = name;
        _modelCentre = source.Bounds.Center;
        _modelRadius = MathF.Max(source.Bounds.Size.Length() / 2, 0.001f);
        ModelMaterial.DiffuseColor = new(1, 1, 1, 1);
        ModelMaterial.VertexColorBlendingFactor = 1;
        ModelMaterial.SpecularColor = new(0, 0, 0, 1);
        TriangleCountText = $"{source.TriangleCount:N0} triangles · surface deviation";
        ApplyBounds(source.Bounds);
    }

    public void SetBlendComparison(
        MeshData sourceA,
        HxMesh geometryA,
        MeshData sourceB,
        HxMesh geometryB,
        bool alignCenters,
        string nameA,
        string nameB)
    {
        Mesh = null;
        OriginalMesh = null;
        SourceMesh = sourceA;

        var translation = alignCenters ? sourceA.Bounds.Center - sourceB.Bounds.Center : System.Numerics.Vector3.Zero;
        var transformB = new TranslateTransform3D(translation.X, translation.Y, translation.Z);
        transformB.Freeze();
        OverlayTransformA = Transform3D.Identity;
        OverlayTransformB = transformB;
        OverlayMeshA = geometryA;
        OverlayMeshB = geometryB;

        var boundsB = new ModelExplorer.Geometry.BoundingBox(
            sourceB.Bounds.Min + translation,
            sourceB.Bounds.Max + translation);
        var bounds = sourceA.Bounds.Union(boundsB);
        ModelName = $"{nameA} + {nameB}";
        _modelCentre = bounds.Center;
        _modelRadius = MathF.Max(bounds.Size.Length() / 2, 0.001f);
        TriangleCountText = $"A {sourceA.TriangleCount:N0} · B {sourceB.TriangleCount:N0} triangles · blended";
        StatusMessage = "Blend comparison ready";
        ApplyBounds(bounds);
    }

    partial void OnOverlayOpacityAChanged(double value) =>
        SetOverlayOpacity(OverlayMaterialA, value, 0.20f, 0.64f, 1.00f);

    partial void OnOverlayOpacityBChanged(double value) =>
        SetOverlayOpacity(OverlayMaterialB, value, 1.00f, 0.38f, 0.16f);

    private static PhongMaterial CreateOverlayMaterial(float red, float green, float blue, float alpha) => new()
    {
        DiffuseColor = new Color4(red, green, blue, alpha),
        AmbientColor = new Color4(red * 0.20f, green * 0.20f, blue * 0.20f, alpha),
        SpecularColor = new Color4(0.18f, 0.18f, 0.18f, alpha),
        SpecularShininess = 18,
    };

    private static void SetOverlayOpacity(PhongMaterial material, double opacity, float red, float green, float blue)
    {
        var alpha = (float)Math.Clamp(opacity, 0, 1);
        material.DiffuseColor = new Color4(red, green, blue, alpha);
        material.AmbientColor = new Color4(red * 0.20f, green * 0.20f, blue * 0.20f, alpha);
        material.SpecularColor = new Color4(0.18f, 0.18f, 0.18f, alpha);
    }
}
