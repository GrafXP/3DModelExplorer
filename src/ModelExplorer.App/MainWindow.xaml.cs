using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using HelixToolkit.Wpf.SharpDX;
using ModelExplorer.App.ViewModels;

namespace ModelExplorer.App;

/// <summary>
/// Interaction logic for MainWindow.xaml.
/// </summary>
public partial class MainWindow : Window
{
    // Win10 20H1 and later. Earlier builds used 19 for the same attribute.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();

        Loaded += OnWindowLoaded;

        // Clip planes track the camera, so they have to be recomputed on every
        // camera change rather than only when a model is loaded.
        Viewport.CameraChanged += (_, _) => ViewModel?.UpdateClipPlanes();

        if (ViewModel is { } viewModel)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PivotPoint))
        {
            SyncRotationPoint();
        }
    }

    /// <summary>
    /// Mirrors the view model's pivot onto the camera controller.
    /// </summary>
    /// <remarks>
    /// The fixed rotation point lives on the viewport, not in the view model, and
    /// nothing clears it on its own. Left alone it survives into the next model —
    /// so loading a new one would orbit it around a point picked off the previous
    /// one, which is usually not even inside the new geometry. Driving it from a
    /// single view-model property means every reset path covers it.
    /// </remarks>
    private void SyncRotationPoint()
    {
        if (ViewModel?.PivotPoint is { } point)
        {
            Viewport.FixedRotationPoint = point;
            Viewport.FixedRotationPointEnabled = true;
        }
        else
        {
            Viewport.FixedRotationPointEnabled = false;
        }
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        // The index is opened and read after the window is up, so a slow disk
        // delays the list rather than the first frame.
        await viewModel.Library.InitializeAsync();

        if (App.StartupFile is { } path)
        {
            await viewModel.LoadAsync(path);
        }
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkTitleBar();
    }

    /// <summary>
    /// Stops the thumbnail workers. They are background threads and would not
    /// hold the process open, but one in the middle of a large parse would keep
    /// reading a file for seconds after the window is gone.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        ViewModel?.Thumbnails.Dispose();
        base.OnClosed(e);
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
            .Where(h => h.IsValid)
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

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedModel(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDropped(object sender, DragEventArgs e)
    {
        if (TryGetDroppedModel(e) is { } path && ViewModel is { } viewModel)
        {
            e.Handled = true;
            await viewModel.LoadAsync(path);
        }
    }

    /// <summary>
    /// Accepts whatever the loader registry accepts, so a format added to the
    /// registry works here without a second list to keep in step.
    /// </summary>
    private string? TryGetDroppedModel(DragEventArgs e)
    {
        if (ViewModel is not { } viewModel || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return null;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return null;
        }

        return paths.FirstOrDefault(viewModel.IsSupported);
    }

    /// <summary>
    /// Opts the non-client area into dark mode. WPF does not do this on its own,
    /// so without it the window keeps a light title bar over a dark client area.
    /// Applied during SourceInitialized — before the window is shown — so there
    /// is no visible light-to-dark flash on startup.
    /// </summary>
    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            // Pre-20H1 attribute id. Failure here is cosmetic only, so it is ignored.
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }
    }
}
