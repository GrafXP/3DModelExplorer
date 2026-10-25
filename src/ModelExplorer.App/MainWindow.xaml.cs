using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ModelExplorer.App.Controls;
using ModelExplorer.App.ViewModels;

namespace ModelExplorer.App;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private readonly Dictionary<ModelViewModel, ModelViewer> _viewers = [];
    private GridLength _rootsWidth = new(240);
    private GridLength _resultsWidth = new(360);
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += OnWorkspaceChanged;
        Loaded += OnWindowLoaded;
        CompositionTarget.Rendering += OnRendering;
        ArrangeViewers();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
            foreach (var pane in ViewModel.VisiblePanes) pane.UpdateRotatingLight(frame.RenderingTime);
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.VisiblePanes)) ArrangeViewers();
        if (e.PropertyName == nameof(MainViewModel.ShowLibrary))
        {
            var visible = ViewModel.ShowLibrary;
            if (!visible) { _rootsWidth = RootsColumn.Width; _resultsWidth = ResultsColumn.Width; }
            RootsColumn.MinWidth = visible ? 160 : 0;
            ResultsColumn.MinWidth = visible ? 220 : 0;
            RootsColumn.Width = visible ? _rootsWidth : new GridLength(0);
            ResultsColumn.Width = visible ? _resultsWidth : new GridLength(0);
            var body = (Grid)((FrameworkElement)ViewerGrid.Parent).Parent;
            foreach (UIElement child in body.Children)
                if (Grid.GetColumn(child) < 4) child.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ArrangeViewers()
    {
        ViewerGrid.Children.Clear();
        ViewerGrid.RowDefinitions.Clear();
        ViewerGrid.ColumnDefinitions.Clear();
        var panes = ViewModel.VisiblePanes;
        var columns = panes.Count == 1 ? 1 : panes.Count == 3 ? 3 : 2;
        for (var i = 0; i < columns; i++) ViewerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < (panes.Count + columns - 1) / columns; i++) ViewerGrid.RowDefinitions.Add(new RowDefinition());
        for (var i = 0; i < panes.Count; i++)
        {
            var pane = panes[i];
            if (!_viewers.TryGetValue(pane, out var viewer))
            {
                viewer = new ModelViewer(ViewModel, pane);
                _viewers.Add(pane, viewer);
            }
            Grid.SetColumn(viewer, i % columns);
            Grid.SetRow(viewer, i / columns);
            ViewerGrid.Children.Add(viewer);
        }
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.Library.InitializeAsync();
        if (App.StartupFile is { } path) await ViewModel.LoadAsync(path);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkTitleBar();
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        ViewModel.PropertyChanged -= OnWorkspaceChanged;
        ViewModel.StopWork();
        foreach (var viewer in _viewers.Values) viewer.Detach();
        ViewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Any(ViewModel.IsSupported)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDropped(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await ViewModel.LoadFilesAsync(paths);
    }

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
