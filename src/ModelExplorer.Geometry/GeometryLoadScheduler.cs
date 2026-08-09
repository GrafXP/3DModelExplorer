using System.Diagnostics;

namespace ModelExplorer.Geometry;

/// <summary>
/// Serialises viewer load requests: at most one parse is ever in flight, and only
/// the most recent request is allowed to produce a result.
/// </summary>
/// <remarks>
/// Holding arrow-down through a list raises a selection change per keypress. Two
/// separate mechanisms keep that from turning into a backlog:
///
/// <list type="bullet">
/// <item>A debounce, so rows passed through on the way somewhere else are never
/// opened at all.</item>
/// <item>Supersession, so a parse that is already running is cancelled the moment
/// a newer request arrives and can no longer publish its mesh.</item>
/// </list>
///
/// Deliberately free of any UI dependency. Everything the viewer needs to decide
/// what to display comes back in the outcome, and the caller applies it on
/// whatever thread it awaited from.
/// </remarks>
public sealed class GeometryLoadScheduler
{
    private readonly Func<string, CancellationToken, MeshData> _load;
    private readonly Lock _gate = new();

    /// <summary>
    /// The request allowed to publish. Ownership of disposal travels with it:
    /// whoever takes it out of this field is the one that cancels and disposes
    /// it, so a superseded request never disposes a source another thread is
    /// about to cancel.
    /// </summary>
    private CancellationTokenSource? _current;

    public GeometryLoadScheduler(GeometryLoaderRegistry registry)
        : this(registry.Load)
    {
    }

    public GeometryLoadScheduler(Func<string, CancellationToken, MeshData> load) => _load = load;

    /// <summary>
    /// Requests <paramref name="path"/>, cancelling whatever was loading before.
    /// </summary>
    /// <param name="debounce">
    /// How long the request must stand unchallenged before any file is touched.
    /// </param>
    /// <returns>
    /// Never throws for a bad file or for being superseded — both are outcomes
    /// the viewer has to render, not exceptions it has to handle.
    /// </returns>
    public async Task<GeometryLoadOutcome> RequestAsync(string path, TimeSpan debounce = default)
    {
        var cancellation = new CancellationTokenSource();
        var superseded = Swap(cancellation);
        if (superseded is not null)
        {
            superseded.Cancel();
            superseded.Dispose();
        }

        var token = cancellation.Token;

        try
        {
            if (debounce > TimeSpan.Zero)
            {
                await Task.Delay(debounce, token).ConfigureAwait(false);
            }

            var stopwatch = Stopwatch.StartNew();
            var mesh = await Task.Run(() => _load(path, token), token).ConfigureAwait(false);
            var parseTime = stopwatch.Elapsed;

            // Cancellation is the usual way a superseded request finds out, but a
            // parse that completed in the same instant the newer request arrived
            // can still get here. Checking ownership as well is what makes
            // "only the newest request publishes" true unconditionally.
            token.ThrowIfCancellationRequested();
            return IsCurrent(cancellation)
                ? GeometryLoadOutcome.Loaded(path, mesh, parseTime)
                : GeometryLoadOutcome.Superseded(path);
        }
        catch (OperationCanceledException)
        {
            return GeometryLoadOutcome.Superseded(path);
        }
        catch (Exception ex) when (ex is GeometryFormatException or IOException or UnauthorizedAccessException)
        {
            // A stale failure must not raise an error banner over the model the
            // user has since moved on to.
            return IsCurrent(cancellation)
                ? GeometryLoadOutcome.Failed(path, ex.Message)
                : GeometryLoadOutcome.Superseded(path);
        }
        finally
        {
            // Only release what is still ours. A newer request has already taken
            // this source over — and disposed it — otherwise.
            if (TryRelease(cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    /// <summary>Cancels the in-flight load, if any, without starting another.</summary>
    public void Cancel()
    {
        var superseded = Swap(null);
        if (superseded is not null)
        {
            superseded.Cancel();
            superseded.Dispose();
        }
    }

    private CancellationTokenSource? Swap(CancellationTokenSource? replacement)
    {
        lock (_gate)
        {
            var previous = _current;
            _current = replacement;
            return previous;
        }
    }

    private bool TryRelease(CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, cancellation))
            {
                return false;
            }

            _current = null;
            return true;
        }
    }

    private bool IsCurrent(CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            return ReferenceEquals(_current, cancellation);
        }
    }
}

public enum GeometryLoadStatus
{
    /// <summary>The mesh parsed and is the one that should be displayed.</summary>
    Loaded,

    /// <summary>A newer request arrived; this one produced nothing and must be ignored.</summary>
    Superseded,

    /// <summary>The file could not be read or is not valid for its format.</summary>
    Failed,
}

/// <param name="Mesh">Non-null only when <paramref name="Status"/> is Loaded.</param>
/// <param name="ErrorMessage">Non-null only when <paramref name="Status"/> is Failed.</param>
public readonly record struct GeometryLoadOutcome(
    GeometryLoadStatus Status,
    string Path,
    MeshData? Mesh,
    TimeSpan ParseTime,
    string? ErrorMessage)
{
    public static GeometryLoadOutcome Loaded(string path, MeshData mesh, TimeSpan parseTime) =>
        new(GeometryLoadStatus.Loaded, path, mesh, parseTime, null);

    public static GeometryLoadOutcome Superseded(string path) =>
        new(GeometryLoadStatus.Superseded, path, null, TimeSpan.Zero, null);

    public static GeometryLoadOutcome Failed(string path, string message) =>
        new(GeometryLoadStatus.Failed, path, null, TimeSpan.Zero, message);
}
