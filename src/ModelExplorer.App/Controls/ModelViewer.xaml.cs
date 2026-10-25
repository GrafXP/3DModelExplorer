using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HelixToolkit.Wpf.SharpDX;
using ModelExplorer.App.ViewModels;

namespace ModelExplorer.App.Controls;

public partial class ModelViewer : UserControl
{
    private bool _isCutPlanePointerDown;
    private bool _wasViewportInertiaEnabled;
    private ModelViewModel? ViewModel => DataContext as ModelViewModel;
    public MainViewModel Workspace { get; }

    public ModelViewer(MainViewModel workspace, ModelViewModel model)
    {
        Workspace = workspace;
        DataContext = model;
        InitializeComponent();
        Viewport.CameraChanged += (_, _) => model.UpdateClipPlanes();
        model.PropertyChanged += OnModelChanged;
        model.StopCameraMotion += StopCameraMotion;
    }

    private void StopCameraMotion(object? sender, EventArgs e) => Viewport.StopSpin();

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewModel is { } model && e.NewSize.Height > 0)
            model.ViewportAspectRatio = e.NewSize.Width / e.NewSize.Height;
    }

    public void Detach()
    {
        if (ViewModel is { } model)
        {
            model.PropertyChanged -= OnModelChanged;
            model.StopCameraMotion -= StopCameraMotion;
        }
        Viewport.Dispose();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModelViewModel.PivotPoint)) return;
        if (ViewModel?.PivotPoint is { } point)
        {
            Viewport.FixedRotationPoint = point;
            Viewport.FixedRotationPointEnabled = true;
        }
        else Viewport.FixedRotationPointEnabled = false;
    }

    private void OnPaneSelected(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is { } model) Workspace.ActivatePane(model);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ViewModel?.CanEditModel == true && e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
            paths.Any(Workspace.IsSupported) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDropped(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (ViewModel is not { CanEditModel: true } model || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        Workspace.ActivatePane(model);
        await Workspace.LoadFilesAsync(paths);
    }

    private void OnViewportPreviewLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { ShowCutPlane: true } viewModel)
        {
            return;
        }

        var hits = Viewport.FindHits(e.GetPosition(Viewport));
        if (!hits.Any(hit => hit.IsValid && hit.ModelHit is UIManipulator3D))
        {
            return;
        }

        // This preview event runs before the viewport's LeftClick rotate command.
        // The 3D handle still receives the unhandled event, while the camera
        // controller sees rotation disabled for the duration of the drag.
        _isCutPlanePointerDown = true;
        _wasViewportInertiaEnabled = Viewport.IsInertiaEnabled;
        Viewport.StopSpin();
        Viewport.IsInertiaEnabled = false;
        Viewport.IsRotationEnabled = false;
        viewModel.BeginCutPlaneManipulation();
    }

    private void OnViewportPreviewLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isCutPlanePointerDown)
        {
            return;
        }

        _isCutPlanePointerDown = false;
        ViewModel?.EndCutPlaneManipulation();
        Viewport.StopSpin();

        // Keep the camera disabled through the bubbling MouseUp event; restoring
        // it in this preview handler lets the rotate command turn the handle's
        // drag velocity into camera inertia after release.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Viewport.IsRotationEnabled = true;
            Viewport.IsInertiaEnabled = _wasViewportInertiaEnabled;
            Viewport.StopSpin();
        }), DispatcherPriority.Background);
    }

    /// <summary>
    /// Right-click picks a point on the model and makes it the orbit centre.
    /// Right-clicking empty space clears it, returning the pivot to the model
    /// centre. Without this, orbiting a large print always swings around its
    /// bounding-box centre, which is useless when inspecting one corner of it.
    /// </summary>
    private void OnViewportRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var hits = Viewport.FindHits(e.GetPosition(Viewport));

        // FindHits returns front-to-back, but sort defensively rather than
        // trusting the order — picking the far side of a model would be worse
        // than a marginally slower right-click.
        var nearest = hits
            .Where(h => h.IsValid &&
                        (ReferenceEquals(h.ModelHit, CommittedModel) ||
                         ReferenceEquals(h.ModelHit, CutPreviewModel)))
            .OrderBy(h => h.Distance)
            .FirstOrDefault();

        // Only the view model is touched; SyncRotationPoint carries the change
        // through to the viewport.
        if (nearest is null)
        {
            viewModel.ClearPivot();
        }
        else
        {
            var p = nearest.PointHit;
            viewModel.SetPivot(p.X, p.Y, p.Z);
        }

        e.Handled = true;
    }

}
