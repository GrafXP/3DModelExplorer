using System.Collections.Concurrent;
using ModelExplorer.Core.Models;
using ModelExplorer.Geometry;

namespace ModelExplorer.Tests;

/// <summary>A loader whose files are meshes in memory, optionally held back until released.</summary>
internal sealed class FakeLoader
{
    private readonly ConcurrentDictionary<string, MeshData> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.OrdinalIgnoreCase);
    private int _running;

    public int MaxConcurrent { get; private set; }

    public ConcurrentBag<string> Cancelled { get; } = [];

    public int Loads;

    public void Add(string path, MeshData mesh) => _meshes[path] = mesh;

    /// <summary>Loads of <paramref name="path"/> wait until <see cref="Release"/>.</summary>
    public void Hold(string path) => _gates[path] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release(string path)
    {
        if (_gates.TryRemove(path, out var gate))
        {
            gate.SetResult();
        }
    }

    public MeshData Load(string path, CancellationToken token)
    {
        Interlocked.Increment(ref Loads);
        var running = Interlocked.Increment(ref _running);
        lock (this)
        {
            MaxConcurrent = Math.Max(MaxConcurrent, running);
        }

        try
        {
            if (_gates.TryGetValue(path, out var gate))
            {
                try
                {
                    gate.Task.Wait(token);
                }
                catch (OperationCanceledException)
                {
                    Cancelled.Add(path);
                    throw;
                }
            }
            else
            {
                Thread.Sleep(5);
            }

            return _meshes.TryGetValue(path, out var mesh) ? mesh : throw new GeometryFormatException($"{Path.GetFileName(path)} is not a model.");
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

/// <summary>Stands in for GPU buffers; records being released.</summary>
internal sealed class FakeGpu : IDisposable
{
    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}

public class ModelCacheTests
{
    [Fact]
    public void WantedModelsLoadAndArePreparedForTheRenderer() => UiThread.Run(async () =>
    {
        var loader = new FakeLoader();
        loader.Add("a.stl", TestMeshes.Cube(0, 1));
        using var cache = new ModelCache(loader.Load, (_, _) => new FakeGpu(), 1L << 30, 2);
        var completed = new List<string>();
        cache.Completed += model => completed.Add(model.Name);

        var a = cache.SetWanted(["a.stl"])[0];
        Assert.Equal(ModelState.Loading, a.State);
        await UiThread.Until(() => a.State != ModelState.Loading);

        Assert.Equal(ModelState.Loaded, a.State);
        Assert.Equal(12, a.Mesh!.TriangleCount);
        Assert.IsType<FakeGpu>(a.Gpu);
        Assert.Equal(["a.stl"], completed);
        Assert.Same(a, cache.SetWanted(["a.stl"])[0]);
    });

    [Fact]
    public void AFileThatCannotBeReadSaysWhy() => UiThread.Run(async () =>
    {
        var loader = new FakeLoader();
        using var cache = new ModelCache(loader.Load, null, 1L << 30, 2);

        var model = cache.SetWanted(["broken.stl"])[0];
        await UiThread.Until(() => model.State != ModelState.Loading);

        Assert.Equal(ModelState.Failed, model.State);
        Assert.Contains("not a model", model.Error);
    });

    [Fact]
    public void ALoadNobodyWantsAnyMoreIsCancelled() => UiThread.Run(async () =>
    {
        var loader = new FakeLoader();
        loader.Add("a.stl", TestMeshes.Cube(0, 1));
        loader.Add("b.stl", TestMeshes.Cube(0, 2));
        loader.Hold("a.stl");
        using var cache = new ModelCache(loader.Load, null, 1L << 30, 1);

        cache.SetWanted(["a.stl"]);
        await UiThread.Until(() => loader.Loads == 1);
        var b = cache.SetWanted(["b.stl"])[0];
        await UiThread.Until(() => b.IsLoaded);

        Assert.Contains("a.stl", loader.Cancelled);
        Assert.Null(cache.Find("a.stl"));
    });

    [Fact]
    public void PrefetchedModelsLoadAndStayUntilMemoryIsNeeded() => UiThread.Run(async () =>
    {
        var loader = new FakeLoader();
        loader.Add("a.stl", TestMeshes.Cube(0, 1));
        loader.Add("next.stl", TestMeshes.Cube(0, 2));
        using var cache = new ModelCache(loader.Load, null, 1L << 30, 2);

        cache.SetWanted(["a.stl"], ["next.stl"]);
        await UiThread.Until(() => cache.Find("next.stl")?.IsLoaded == true);

        // Stepping on to the prefetched model shows it without loading it again.
        var loads = loader.Loads;
        var next = cache.SetWanted(["next.stl"])[0];
        Assert.True(next.IsLoaded);
        Assert.Equal(loads, loader.Loads);
    });

    [Fact]
    public void OverBudgetTheOldestUnwantedModelIsReleasedButNeverAWantedOne() => UiThread.Run(async () =>
    {
        var loader = new FakeLoader();
        loader.Add("a.stl", TestMeshes.Cube(0, 1));
        loader.Add("b.stl", TestMeshes.Cube(0, 2));
        var gpus = new List<FakeGpu>();
        using var cache = new ModelCache(loader.Load, (_, _) => { var gpu = new FakeGpu(); lock (gpus) { gpus.Add(gpu); } return gpu; }, budgetBytes: 1, 2);

        var a = cache.SetWanted(["a.stl"])[0];
        await UiThread.Until(() => a.IsLoaded);
        Assert.Same(a, cache.Find("a.stl")); // over budget, but wanted

        var b = cache.SetWanted(["b.stl"])[0];
        await UiThread.Until(() => b.IsLoaded);

        Assert.Null(cache.Find("a.stl"));
        Assert.Null(a.Gpu);
        Assert.True(gpus[0].Disposed, "the released model's GPU buffers were not given back");
        Assert.False(gpus[1].Disposed);
        Assert.Same(b, cache.Find("b.stl"));
    });

    [Fact]
    public void AFileSavedAgainIsReadAgain() => UiThread.Run(async () =>
    {
        using var directory = new TempDirectory();
        var path = directory.Write("part.stl", StlFixtures.BinaryCube());
        var loader = new FakeLoader();
        loader.Add(path, TestMeshes.Cube(0, 1));
        using var cache = new ModelCache(loader.Load, null, 1L << 30, 2);

        var first = cache.SetWanted([path])[0];
        await UiThread.Until(() => first.IsLoaded);
        File.WriteAllBytes(path, StlFixtures.BinaryStrip(4));

        var second = cache.SetWanted([path])[0];

        Assert.NotSame(first, second);
        await UiThread.Until(() => second.IsLoaded);
    });

    [Fact]
    public void OnlyAFewModelsLoadAtOnce() => UiThread.Run(async () =>
    {
        var loader = new FakeLoader();
        var paths = Enumerable.Range(0, 8).Select(i => $"m{i}.stl").ToArray();
        foreach (var path in paths)
        {
            loader.Add(path, TestMeshes.Cube(0, 1));
        }

        using var cache = new ModelCache(loader.Load, null, 1L << 30, 2);
        var models = cache.SetWanted(paths);
        await UiThread.Until(() => models.All(m => m.IsLoaded));

        Assert.InRange(loader.MaxConcurrent, 1, 2);
    });
}
