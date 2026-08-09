using System.Numerics;
using ModelExplorer.Geometry;

namespace ModelExplorer.Tests;

/// <summary>
/// Covers the guarantee the viewer depends on when a list is scrubbed with the
/// arrow keys: whatever else happens, the newest request is the only one that
/// gets to publish a mesh.
/// </summary>
public sealed class GeometryLoadSchedulerTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(60);

    /// <summary>Generous, because it only bounds a wait that normally returns at once.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Loads_the_requested_file()
    {
        var scheduler = new GeometryLoadScheduler((path, _) => MeshFor(path));

        var outcome = await scheduler.RequestAsync("cube.stl");

        Assert.Equal(GeometryLoadStatus.Loaded, outcome.Status);
        Assert.Equal("cube.stl", outcome.Path);
        Assert.Equal(1, outcome.Mesh!.TriangleCount);
    }

    [Fact]
    public async Task A_newer_request_supersedes_the_one_in_flight()
    {
        var firstStarted = new SemaphoreSlim(0);
        var releaseFirst = new SemaphoreSlim(0);

        var scheduler = new GeometryLoadScheduler((path, token) =>
        {
            if (path == "slow.stl")
            {
                firstStarted.Release();
                releaseFirst.Wait(token);
            }

            return MeshFor(path);
        });

        var slow = scheduler.RequestAsync("slow.stl");
        Assert.True(await firstStarted.WaitAsync(Timeout));

        var fast = scheduler.RequestAsync("fast.stl");

        // The slow load observes cancellation through its token; unblocking it
        // here mirrors a parser that has already passed its last cancellation
        // check and is on its way to returning a mesh regardless.
        releaseFirst.Release();

        Assert.Equal(GeometryLoadStatus.Superseded, (await slow).Status);

        var winner = await fast;
        Assert.Equal(GeometryLoadStatus.Loaded, winner.Status);
        Assert.Equal("fast.stl", winner.Path);
    }

    /// <summary>
    /// The specific failure the gate looks for: a load that finishes after a newer
    /// one has already been displayed must not paint over it.
    /// </summary>
    [Fact]
    public async Task A_slow_load_that_finishes_last_still_loses()
    {
        var releaseSlow = new SemaphoreSlim(0);
        var slowStarted = new SemaphoreSlim(0);

        var scheduler = new GeometryLoadScheduler((path, _) =>
        {
            if (path == "slow.stl")
            {
                slowStarted.Release();

                // Deliberately ignores the token: some of the parse loop runs
                // between cancellation checks, and the result still must not land.
                releaseSlow.Wait();
            }

            return MeshFor(path);
        });

        var slow = scheduler.RequestAsync("slow.stl");
        Assert.True(await slowStarted.WaitAsync(Timeout));

        var fast = scheduler.RequestAsync("fast.stl");
        Assert.Equal(GeometryLoadStatus.Loaded, (await fast).Status);

        releaseSlow.Release();
        Assert.Equal(GeometryLoadStatus.Superseded, (await slow).Status);
    }

    [Fact]
    public async Task Debounced_requests_that_are_replaced_never_touch_the_file()
    {
        var opened = new List<string>();
        var gate = new Lock();

        var scheduler = new GeometryLoadScheduler((path, _) =>
        {
            lock (gate)
            {
                opened.Add(path);
            }

            return MeshFor(path);
        });

        // Stands in for holding arrow-down: a run of selections, then a pause.
        var abandoned = new List<Task<GeometryLoadOutcome>>();
        for (var i = 0; i < 20; i++)
        {
            abandoned.Add(scheduler.RequestAsync($"row{i}.stl", Debounce));
        }

        var settled = await scheduler.RequestAsync("row20.stl", Debounce);
        foreach (var task in abandoned)
        {
            Assert.Equal(GeometryLoadStatus.Superseded, (await task).Status);
        }

        Assert.Equal(GeometryLoadStatus.Loaded, settled.Status);

        lock (gate)
        {
            Assert.Equal(["row20.stl"], opened);
        }
    }

    [Fact]
    public async Task A_debounced_request_still_loads_once_it_stands()
    {
        var scheduler = new GeometryLoadScheduler((path, _) => MeshFor(path));

        var outcome = await scheduler.RequestAsync("only.stl", Debounce);

        Assert.Equal(GeometryLoadStatus.Loaded, outcome.Status);
    }

    [Theory]
    [MemberData(nameof(ReportableFailures))]
    public async Task A_bad_file_is_reported_rather_than_thrown(Exception failure)
    {
        var scheduler = new GeometryLoadScheduler((_, _) => throw failure);

        var outcome = await scheduler.RequestAsync("broken.stl");

        Assert.Equal(GeometryLoadStatus.Failed, outcome.Status);
        Assert.Equal(failure.Message, outcome.ErrorMessage);
        Assert.Null(outcome.Mesh);
    }

    public static TheoryData<Exception> ReportableFailures() =>
    [
        new GeometryFormatException("Malformed vertex on line 3."),
        new FileNotFoundException("It moved."),
        new IOException("The network path was not found."),
        new UnauthorizedAccessException("Access to the path is denied."),
    ];

    /// <summary>
    /// A bug in a parser is not a corrupt file, and hiding it behind the same
    /// error panel would make it invisible.
    /// </summary>
    [Fact]
    public async Task An_unexpected_exception_is_not_swallowed()
    {
        var scheduler = new GeometryLoadScheduler((_, _) => throw new InvalidOperationException("bug"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.RequestAsync("broken.stl"));
    }

    [Fact]
    public async Task A_failure_from_a_superseded_load_is_not_reported()
    {
        var brokenStarted = new SemaphoreSlim(0);
        var releaseBroken = new SemaphoreSlim(0);

        var scheduler = new GeometryLoadScheduler((path, _) =>
        {
            if (path == "broken.stl")
            {
                brokenStarted.Release();
                releaseBroken.Wait();
                throw new GeometryFormatException("Corrupt.");
            }

            return MeshFor(path);
        });

        var broken = scheduler.RequestAsync("broken.stl");
        Assert.True(await brokenStarted.WaitAsync(Timeout));

        var good = scheduler.RequestAsync("good.stl");
        Assert.Equal(GeometryLoadStatus.Loaded, (await good).Status);

        releaseBroken.Release();

        // Failed here would put an error panel over a model that loaded fine.
        Assert.Equal(GeometryLoadStatus.Superseded, (await broken).Status);
    }

    [Fact]
    public async Task Cancel_stops_the_load_in_flight()
    {
        var started = new SemaphoreSlim(0);
        var blocked = new SemaphoreSlim(0);

        var scheduler = new GeometryLoadScheduler((_, token) =>
        {
            started.Release();
            blocked.Wait(token);
            return MeshData.Empty;
        });

        var request = scheduler.RequestAsync("slow.stl");
        Assert.True(await started.WaitAsync(Timeout));

        scheduler.Cancel();

        Assert.Equal(GeometryLoadStatus.Superseded, (await request).Status);
    }

    /// <summary>
    /// The undebounced burst — a run of clicks rather than a held arrow key. Every
    /// request is answered, and exactly one of them is allowed to have loaded.
    /// </summary>
    [Fact]
    public async Task Only_the_last_of_a_burst_loads()
    {
        var scheduler = new GeometryLoadScheduler((path, _) =>
        {
            Thread.Sleep(1);
            return MeshFor(path);
        });

        var requests = new List<Task<GeometryLoadOutcome>>();
        for (var i = 0; i < 50; i++)
        {
            requests.Add(scheduler.RequestAsync($"row{i}.stl"));
        }

        var outcomes = await Task.WhenAll(requests);

        Assert.Equal(GeometryLoadStatus.Loaded, outcomes[^1].Status);
        Assert.Equal("row49.stl", outcomes[^1].Path);
        Assert.All(outcomes[..^1], outcome => Assert.Equal(GeometryLoadStatus.Superseded, outcome.Status));
    }

    private static MeshData MeshFor(string path) => new()
    {
        Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
        Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
        Indices = [0, 1, 2],
        Bounds = BoundingBox.Empty.Union(Vector3.Zero).Union(Vector3.UnitX).Union(Vector3.UnitY),
    };
}
