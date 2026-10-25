using System.Numerics;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using ModelExplorer.App;
using ModelExplorer.App.ViewModels;
using ModelExplorer.Geometry;
using ModelExplorer.Indexing;

namespace ModelExplorer.Tests;

public class ComparisonWorkspaceTests
{
    private static Task OnUiThread(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public Task FourPanesSynchronizeCameraAndPivotInBothDirections() => OnUiThread(() =>
    {
        using var workspace = new MainViewModel();
        workspace.SelectedLayout = workspace.LayoutOptions[2];
        var a = workspace.Panes[0];
        var d = workspace.Panes[3];
        d.Camera.Position = new(4, 5, 6);
        d.Camera.LookDirection = new(-2, 3, 4);
        d.Camera.UpDirection = new(0, 1, 0);
        Assert.All(workspace.VisiblePanes, pane =>
        {
            Assert.Equal(d.Camera.Position, pane.Camera.Position);
            Assert.Equal(d.Camera.LookDirection, pane.Camera.LookDirection);
            Assert.Equal(d.Camera.UpDirection, pane.Camera.UpDirection);
        });
        a.PivotPoint = new(2, 3, 4);
        Assert.Equal(a.PivotPoint, d.PivotPoint);
        workspace.SyncCameras = false;
        d.Camera.Position = new(90, 0, 0);
        Assert.NotEqual(d.Camera.Position, a.Camera.Position);
        workspace.ActivatePane(d);
        workspace.SyncCameras = true;
        Assert.Equal(d.Camera.Position, a.Camera.Position);
        return Task.CompletedTask;
    });

    [Fact]
    public Task LoadingLayoutsComparisonAndReplacementPreserveIndependentModels() => OnUiThread(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "ModelExplorerComparison-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var paths = Enumerable.Range(0, 4).Select(i => Path.Combine(directory, $"model{i}.stl")).ToArray();
            foreach (var path in paths) await File.WriteAllBytesAsync(path, StlFixtures.BinaryCube());
            using var workspace = new MainViewModel();
            await workspace.LoadFilesAsync(paths);
            Assert.Equal(4, workspace.VisiblePanes.Count);
            Assert.Equal(paths, workspace.Panes.Select(p => p.CurrentPath));
            var preserved = workspace.Panes.Select(p => p.SourceMesh).ToArray();
            workspace.SelectedLayout = workspace.LayoutOptions[0];
            workspace.SelectedLayout = workspace.LayoutOptions[1];
            Assert.Equal(preserved, workspace.Panes.Select(p => p.SourceMesh));
            Assert.True(workspace.CompareCommand.CanExecute(null));
            Assert.Equal(ComparisonDisplayKind.BlendMeshes, workspace.SelectedComparisonMode.Kind);
            workspace.Panes[0].Camera.LookDirection = new(6, 7, 8);
            var cameraBeforeComparison = workspace.Panes[0].Camera.Position;
            await workspace.CompareCommand.ExecuteAsync(null);
            Assert.True(workspace.HasComparison, workspace.ComparisonStatus);
            Assert.Equal(cameraBeforeComparison, workspace.Panes[0].Camera.Position);
            Assert.Equal(new Vector3D(6, 7, 8), workspace.ComparisonPane.Camera.LookDirection);
            Assert.Equal(3, workspace.VisiblePanes.Count);
            Assert.Same(workspace.ComparisonPane, workspace.VisiblePanes[2]);
            Assert.Null(workspace.ComparisonPane.Mesh);
            Assert.Same(workspace.Panes[0].OriginalMesh, workspace.ComparisonPane.OverlayMeshA);
            Assert.Same(workspace.Panes[1].OriginalMesh, workspace.ComparisonPane.OverlayMeshB);
            Assert.True(workspace.ComparisonPane.ShowsOverlayComparison);
            workspace.ComparisonPane.OverlayOpacityA = 0.2;
            workspace.ComparisonPane.OverlayOpacityB = 0.8;
            Assert.Equal(0.2f, workspace.ComparisonPane.OverlayMaterialA.DiffuseColor.Alpha, 4);
            Assert.Equal(0.8f, workspace.ComparisonPane.OverlayMaterialB.DiffuseColor.Alpha, 4);
            workspace.ComparisonPane.Camera.LookDirection = new(6, 7, 8);
            Assert.Equal(workspace.ComparisonPane.Camera.LookDirection, workspace.Panes[0].Camera.LookDirection);

            workspace.SelectedComparisonMode = workspace.ComparisonModes.Single(mode => mode.Kind == ComparisonDisplayKind.Heatmap);
            while (workspace.IsComparing || !workspace.HasComparison) await Task.Delay(10);
            Assert.True(workspace.HasComparison, workspace.ComparisonStatus);
            Assert.NotNull(workspace.ComparisonPane.Mesh?.Colors);
            Assert.False(workspace.ComparisonPane.ShowsOverlayComparison);

            workspace.CloseComparisonCommand.Execute(null);
            await workspace.CompareCommand.ExecuteAsync(null);
            Assert.Equal(ComparisonDisplayKind.BlendMeshes, workspace.SelectedComparisonMode.Kind);
            Assert.True(workspace.ComparisonPane.ShowsOverlayComparison);

            workspace.ActivatePane(workspace.Panes[1]);
            await workspace.LoadAsync(Path.Combine(directory, "missing.stl"));
            Assert.False(workspace.HasComparison);
            Assert.Null(workspace.ComparisonPane.Mesh);
            Assert.True(workspace.Panes[1].HasError);
            Assert.Same(preserved[0], workspace.Panes[0].SourceMesh);
            Assert.False(workspace.CompareCommand.CanExecute(null));
            workspace.CloseComparisonCommand.Execute(null);
            Assert.Equal(2, workspace.VisiblePanes.Count);
            workspace.SelectedLayout = workspace.LayoutOptions[2];
            Assert.Same(preserved[3], workspace.Panes[3].SourceMesh);
        }
        finally { Directory.Delete(directory, true); }
    });

    [Fact]
    public Task CanceledComparisonCannotPublishStaleResult() => OnUiThread(async () =>
    {
        using var workspace = new MainViewModel();
        workspace.SelectedLayout = workspace.LayoutOptions[1];
        var mesh = MeshComparisonTests.Mesh([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [0, 1, 2]);
        workspace.Panes[0].SourceMesh = mesh;
        workspace.Panes[1].SourceMesh = mesh;
        await workspace.CompareCommand.ExecuteAsync(null);
        workspace.SelectedComparisonMode = workspace.ComparisonModes.Single(mode => mode.Kind == ComparisonDisplayKind.Heatmap);
        workspace.CancelComparisonCommand.Execute(null);
        Assert.False(workspace.HasComparison);
        Assert.Null(workspace.ComparisonPane.Mesh);
        Assert.Contains("canceled", workspace.ComparisonStatus);
    });

    [Fact]
    public Task ReplacingASourceWhileComparingCannotPublishThePreviousMeshes() => OnUiThread(async () =>
    {
        using var workspace = new MainViewModel();
        workspace.SelectedLayout = workspace.LayoutOptions[1];
        var mesh = MeshComparisonTests.Mesh([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [0, 1, 2]);
        workspace.Panes[0].SourceMesh = mesh;
        workspace.Panes[1].SourceMesh = mesh;
        await workspace.CompareCommand.ExecuteAsync(null);
        workspace.SelectedComparisonMode = workspace.ComparisonModes.Single(mode => mode.Kind == ComparisonDisplayKind.Heatmap);
        workspace.Panes[1].SourceMesh = null;
        Assert.False(workspace.HasComparison);
        Assert.Null(workspace.ComparisonPane.Mesh);
        Assert.False(workspace.CompareCommand.CanExecute(null));
    });

    [Theory]
    [InlineData(0.35)]
    [InlineData(1)]
    [InlineData(3)]
    public Task FitKeepsTheBoundingSphereInsideBothProjectionDimensions(double aspect) => OnUiThread(() =>
    {
        using var workspace = new MainViewModel();
        var pane = workspace.Panes[0];
        pane.ViewportAspectRatio = aspect;
        var bounds = new BoundingBox(new(-10), new(10));
        pane.FrameModel(bounds);
        var projection = pane.Camera.CreateProjectionMatrix(aspect);
        var radius = bounds.Size.Length() / 2;
        var distance = pane.Camera.LookDirection.Length;
        var angularRadius = Math.Asin(radius / distance);
        Assert.True(Math.Tan(angularRadius) * projection.M11 < 0.8);
        Assert.True(Math.Tan(angularRadius) * projection.M22 < 0.8);
        return Task.CompletedTask;
    });

    [Fact]
    public Task CenterAlignmentMapsCameraPositionsAndPivotsWithoutScaling() => OnUiThread(() =>
    {
        using var workspace = new MainViewModel();
        workspace.SelectedLayout = workspace.LayoutOptions[1];
        var a = workspace.Panes[0];
        var b = workspace.Panes[1];
        a.SourceMesh = MeshComparisonTests.Mesh([new(0, 0, 0), new(10, 0, 0), new(0, 10, 0)], [0, 1, 2]);
        b.SourceMesh = MeshComparisonTests.Mesh(a.SourceMesh.Positions.Select(p => p + new Vector3(100, 20, -5)).ToArray(), [0, 1, 2]);
        workspace.AlignCenters = true;
        a.Camera.Position = new(4, 5, 6);
        Assert.Equal(new Point3D(104, 25, 1), b.Camera.Position);
        a.Camera.LookDirection = new(3, 4, 5);
        Assert.Equal(a.Camera.LookDirection, b.Camera.LookDirection);
        b.PivotPoint = new(102, 23, 0);
        Assert.Equal(new Point3D(2, 3, 5), a.PivotPoint);
        return Task.CompletedTask;
    });

    [Fact]
    public void HeatmapKeepsSeparateFaceColorsAndDoesNotMutateTheSource()
    {
        var mesh = MeshComparisonTests.Mesh([new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)], [0, 1, 2, 0, 2, 3]);
        var result = new MeshDistanceResult([0, 2], 1, 2);
        var rendered = ComparisonHeatmap.Build(mesh, result, 0.1f, 2);
        Assert.NotNull(rendered.Positions);
        Assert.NotNull(rendered.Colors);
        Assert.Equal(6, rendered.Positions.Count);
        Assert.NotEqual(rendered.Colors[0], rendered.Colors[3]);
        Assert.Equal(rendered.Positions[0], rendered.Positions[3]);
        Assert.Equal(4, mesh.Positions.Length);
        Assert.Equal(ComparisonHeatmap.Color(0, 0.1f, 2), ComparisonHeatmap.Color(0.1f, 0.1f, 2));
    }

    [Fact]
    public void BlendComparisonAlignsCentersAndReusesBothRenderMeshes()
    {
        using var effects = new HelixToolkit.SharpDX.DefaultEffectsManager();
        var pane = new ModelViewModel(effects, "Comparison", true);
        var a = MeshComparisonTests.Mesh([new(0, 0, 0), new(10, 0, 0), new(0, 10, 0)], [0, 1, 2]);
        var b = MeshComparisonTests.Mesh(
            a.Positions.Select(point => point + new Vector3(100, -20, 7)).ToArray(), [0, 1, 2]);
        var geometryA = new HelixToolkit.SharpDX.MeshGeometry3D();
        var geometryB = new HelixToolkit.SharpDX.MeshGeometry3D();

        pane.SetBlendComparison(a, geometryA, b, geometryB, true, "a.stl", "b.stl");

        Assert.Same(geometryA, pane.OverlayMeshA);
        Assert.Same(geometryB, pane.OverlayMeshB);
        var translation = Assert.IsType<TranslateTransform3D>(pane.OverlayTransformB);
        Assert.Equal(-100, translation.OffsetX, 5);
        Assert.Equal(20, translation.OffsetY, 5);
        Assert.Equal(-7, translation.OffsetZ, 5);
        Assert.True(pane.HasModel);
        Assert.Null(pane.Mesh);
    }
}
