using System.Numerics;
using ModelExplorer.Core.Models;
using ModelExplorer.Core.Viewing;
using ModelExplorer.Geometry;

namespace ModelExplorer.Tests;

public class WorkspaceTests
{
    private static readonly PaneRect[] TwoPanes = [new(0, 0, 400, 300), new(404, 0, 400, 300)];

    private static (FakeLoader Loader, ModelCache Cache, Workspace Workspace) Create(Func<MeshData, CancellationToken, IDisposable?>? prepare = null)
    {
        var loader = new FakeLoader();
        var cache = new ModelCache(loader.Load, prepare, 1L << 30, 2);
        return (loader, cache, new Workspace(cache, prepare));
    }

    private static async Task ShowLoaded(Workspace workspace, params string[] paths)
    {
        workspace.Show(paths);
        await UiThread.Until(() => Enumerable.Range(0, workspace.Count).All(i => workspace.Shown(i)?.State is ModelState.Loaded or ModelState.Failed));
    }

    [Fact]
    public void EverySelectedModelGetsAPaneFramedAtOneScale() => UiThread.Run(async () =>
    {
        var (loader, cache, workspace) = Create();
        using var _ = cache;
        using var __ = workspace;
        loader.Add("small.stl", TestMeshes.Cube(0, 1));
        loader.Add("large.stl", TestMeshes.Cube(0, 50));

        await ShowLoaded(workspace, "small.stl", "large.stl");
        workspace.Layout(TwoPanes);

        Assert.Equal(2, workspace.PaneCount);
        Assert.True(workspace.SharedCamera.IsSet);
        Assert.Equal(workspace.Camera(0).Distance, workspace.Camera(1).Distance);
        Assert.True(workspace.SharedCamera.Distance > 50, "the framing must hold the larger model");

        workspace.Mode = CompareMode.Overlay;
        Assert.Equal(1, workspace.PaneCount);
    });

    [Fact]
    public void AModelStillLoadingLeavesThePreviousOneOnScreen() => UiThread.Run(async () =>
    {
        var (loader, cache, workspace) = Create();
        using var _ = cache;
        using var __ = workspace;
        loader.Add("a.stl", TestMeshes.Cube(0, 1));
        loader.Add("b.stl", TestMeshes.Cube(0, 2));
        await ShowLoaded(workspace, "a.stl");
        var a = workspace.Shown(0);

        loader.Hold("b.stl");
        workspace.Show(["b.stl"]);

        Assert.Same(a, workspace.Shown(0));
        Assert.Equal(ModelState.Loading, workspace.Requested(0)!.State);
        loader.Release("b.stl");
        await UiThread.Until(() => workspace.Shown(0)?.Name == "b.stl");
    });

    [Fact]
    public void MoreThanTheLimitIsCappedAndReported() => UiThread.Run(() =>
    {
        var (_, cache, workspace) = Create();
        using var __ = cache;
        using var ___ = workspace;

        workspace.Show([.. Enumerable.Range(0, 20).Select(i => $"m{i}.stl")]);

        Assert.Equal(Workspace.MaxModels, workspace.Count);
        Assert.Equal(4, workspace.Overflow);
        return Task.CompletedTask;
    });

    [Fact]
    public void UnlinkingCopiesTheSharedCameraAndRelinkingAdoptsTheActivePanes() => UiThread.Run(async () =>
    {
        var (loader, cache, workspace) = Create();
        using var _ = cache;
        using var __ = workspace;
        loader.Add("a.stl", TestMeshes.Cube(0, 1));
        loader.Add("b.stl", TestMeshes.Cube(0, 1));
        await ShowLoaded(workspace, "a.stl", "b.stl");
        workspace.Layout(TwoPanes);

        workspace.SyncCameras = false;
        Assert.Equal(workspace.SharedCamera.Target, workspace.Camera(1).Target);
        workspace.Camera(1).Orbit(200, 0);
        Assert.NotEqual(workspace.Camera(0).Yaw, workspace.Camera(1).Yaw);

        var turned = workspace.Camera(1).Yaw;
        workspace.Active = 1;
        workspace.SyncCameras = true;
        Assert.Equal(turned, workspace.Camera(0).Yaw);
    });

    [Fact]
    public void AligningCentresDrawsEveryModelAroundTheOrigin() => UiThread.Run(async () =>
    {
        var (loader, cache, workspace) = Create();
        using var _ = cache;
        using var __ = workspace;
        loader.Add("a.stl", TestMeshes.Cube(100, 110));
        await ShowLoaded(workspace, "a.stl");
        var model = workspace.Shown(0)!;

        Assert.Equal(Vector3.Zero, workspace.Offset(model));
        workspace.AlignCenters = true;

        Assert.Equal(new Vector3(-105), workspace.Offset(model));
        Assert.Equal(Vector3.Zero, workspace.Bounds(model).Center);
    });

    [Fact]
    public void DeviationMeasuresEveryModelAgainstTheActiveOne() => UiThread.Run(async () =>
    {
        var (loader, cache, workspace) = Create();
        using var _ = cache;
        using var __ = workspace;
        loader.Add("flat.stl", TestMeshes.Square(10));
        loader.Add("raised.stl", TestMeshes.Square(10, z: 1));
        loader.Add("higher.stl", TestMeshes.Square(10, z: 3));
        await ShowLoaded(workspace, "flat.stl", "raised.stl");

        workspace.Mode = CompareMode.Deviation;
        await UiThread.Until(() => workspace.Deviation(1) is not null);

        Assert.Equal(1, workspace.Deviation(1)!.Distances.MaxDistance, 4);
        // With two models the reference is coloured too, by its distance from the other.
        Assert.Equal(1, workspace.Deviation(0)!.Distances.MaxDistance, 4);
        Assert.StartsWith("Reference", workspace.DeviationText(0));

        // A third: each is measured against the reference, which stays neutral.
        await ShowLoaded(workspace, "flat.stl", "raised.stl", "higher.stl");
        await UiThread.Until(() => workspace.Deviation(2) is not null);
        Assert.Null(workspace.Deviation(0));
        Assert.Equal(3, workspace.Deviation(2)!.Distances.MaxDistance, 4);
        Assert.Equal(3, workspace.DeviationMaximum, 4);

        // Another reference: distances are measured from it. Flat against higher is
        // there at once, the other direction of a pair already measured.
        workspace.Active = 2;
        Assert.NotNull(workspace.Deviation(0));
        await UiThread.Until(() => workspace.Deviation(1) is not null);
        Assert.Equal(3, workspace.Deviation(0)!.Distances.MaxDistance, 4);
        Assert.Equal(2, workspace.Deviation(1)!.Distances.MaxDistance, 4);
    });

    [Fact]
    public void LeavingTheDeviationViewCancelsItsWork() => UiThread.Run(async () =>
    {
        var (loader, cache, workspace) = Create();
        using var _ = cache;
        using var __ = workspace;
        loader.Add("a.stl", TestMeshes.Cube(0, 10));
        loader.Add("b.stl", TestMeshes.Cube(0, 11));
        await ShowLoaded(workspace, "a.stl", "b.stl");

        workspace.Mode = CompareMode.Deviation;
        workspace.Mode = CompareMode.Separate;
        await Task.Delay(200);

        Assert.False(workspace.IsComputingDeviation);
        Assert.Null(workspace.Deviation(1));
        Assert.Equal(string.Empty, workspace.DeviationText(1));
    });

    [Fact]
    public void TheCutFollowsThePlaneAndIsReleasedWhenTurnedOff() => UiThread.Run(async () =>
    {
        var gpus = new List<FakeGpu>();
        var (loader, cache, workspace) = Create((_, _) => { var gpu = new FakeGpu(); lock (gpus) { gpus.Add(gpu); } return gpu; });
        using var _ = cache;
        using var __ = workspace;
        loader.Add("cube.stl", TestMeshes.Cube(0, 10));
        await ShowLoaded(workspace, "cube.stl");

        workspace.CutEnabled = true;
        await UiThread.Until(() => workspace.CutResult(0) is not null);
        var upper = workspace.CutResult(0)!;
        Assert.Equal(5, upper.Mesh.Bounds.Min.Z, 3);
        Assert.StartsWith("Solid cut", workspace.CutText(0));

        // A new plane: the old result no longer stands for it, and the new one replaces it.
        workspace.Cut = CutPlaneSettings.Facing(Vector3.UnitX);
        Assert.Null(workspace.CutResult(0));
        Assert.Equal("Previewing cut…", workspace.CutText(0));
        await UiThread.Until(() => workspace.CutResult(0) is not null);
        Assert.Equal(5, workspace.CutResult(0)!.Mesh.Bounds.Min.X, 3);
        Assert.True(((FakeGpu)upper.Gpu!).Disposed);

        workspace.CutEnabled = false;
        Assert.Null(workspace.CutResult(0));
        Assert.False(((FakeGpu)workspace.Shown(0)!.Gpu!).Disposed);
        Assert.All(gpus.Skip(1), gpu => Assert.True(gpu.Disposed));
    });

    [Fact]
    public void DraggingThePlaneWaitsForTheReleaseBeforeCutting() => UiThread.Run(async () =>
    {
        var (loader, cache, workspace) = Create();
        using var _ = cache;
        using var __ = workspace;
        loader.Add("cube.stl", TestMeshes.Cube(0, 10));
        await ShowLoaded(workspace, "cube.stl");
        workspace.CutEnabled = true;
        await UiThread.Until(() => workspace.CutResult(0) is not null);

        workspace.BeginCutDrag();
        workspace.Cut = workspace.Cut.WithOffset(2);
        await Task.Delay(400);
        Assert.Null(workspace.CutResult(0));

        workspace.EndCutDrag();
        await UiThread.Until(() => workspace.CutResult(0) is not null);
        Assert.Equal(7, workspace.CutResult(0)!.Mesh.Bounds.Min.Z, 3);
    });
}
