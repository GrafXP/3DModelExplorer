using System.Diagnostics;
using ModelExplorer.Geometry;

namespace ModelExplorer.Core.Models;

public enum ModelState
{
    Loading,
    Loaded,
    Failed,
}

/// <summary>One model file as the cache holds it: loading, loaded with its mesh, or failed.</summary>
public sealed class LoadedModel
{
    internal LoadedModel(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
    }

    public string Path { get; }

    public string Name { get; }

    public ModelState State { get; internal set; }

    public MeshData? Mesh { get; internal set; }

    /// <summary>Why the file could not be shown. Null unless <see cref="State"/> is Failed.</summary>
    public string? Error { get; internal set; }

    public TimeSpan ParseTime { get; internal set; }

    /// <summary>How long the renderer took to take the mesh over (the GPU upload).</summary>
    public TimeSpan PrepareTime { get; internal set; }

    /// <summary>
    /// What the renderer made of the mesh, created on the loading thread by the
    /// cache's prepare callback so that no upload ever runs on the UI thread.
    /// </summary>
    public IDisposable? Gpu { get; internal set; }

    public bool IsLoaded => State == ModelState.Loaded;

    public long Bytes => Mesh?.ApproximateBytes ?? 0;

    internal long FileLength { get; set; }

    internal DateTime FileWriteUtc { get; set; }

    internal long LastUsed { get; set; }

    internal int Priority { get; set; }

    internal CancellationTokenSource? Cancellation { get; set; }

    internal bool Started { get; set; }
}

/// <summary>
/// Parsed models, kept while memory allows, so going back to a model shows it at once.
/// </summary>
/// <remarks>
/// <para>The caller states what it needs with <see cref="SetWanted"/>: the models on
/// screen, in order, and the ones it expects next. Loads run a few at a time, most
/// wanted first; a load nobody wants any more is cancelled before it costs more,
/// so holding an arrow key through the library never builds a backlog.</para>
/// <para>Everything public runs on the UI thread. Parsing and the prepare callback
/// run on the thread pool, and their results come back through the caller's
/// synchronization context.</para>
/// </remarks>
public sealed class ModelCache : IDisposable
{
    private readonly Func<string, CancellationToken, MeshData> _load;
    private readonly Func<MeshData, CancellationToken, IDisposable?>? _prepare;
    private readonly long _budgetBytes;
    private readonly int _parallelism;
    private readonly Dictionary<string, LoadedModel> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _wanted = new(StringComparer.OrdinalIgnoreCase);
    private int _running;
    private long _clock;
    private bool _disposed;

    public ModelCache(
        Func<string, CancellationToken, MeshData> load,
        Func<MeshData, CancellationToken, IDisposable?>? prepare,
        long budgetBytes,
        int parallelism)
    {
        _load = load;
        _prepare = prepare;
        _budgetBytes = budgetBytes;
        _parallelism = Math.Max(1, parallelism);
    }

    /// <summary>Raised on the UI thread when a load has finished, successfully or not.</summary>
    public event Action<LoadedModel>? Completed;

    /// <summary>Models held, loaded or not.</summary>
    public int Count => _entries.Count;

    /// <summary>Memory held by loaded meshes.</summary>
    public long Bytes => _entries.Values.Sum(entry => entry.Bytes);

    /// <summary>A sensible budget: a quarter of the memory the process can use, at most 4 GB.</summary>
    public static long DefaultBudget =>
        Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4, 512L << 20, 4L << 30);

    public LoadedModel? Find(string path) => _entries.GetValueOrDefault(path);

    /// <summary>
    /// Declares what is needed: <paramref name="wanted"/> in order of importance,
    /// then <paramref name="prefetch"/>. Their loads start, most important first;
    /// loads of anything else are cancelled; memory over budget is given back by
    /// dropping the models used longest ago that are not wanted.
    /// </summary>
    /// <returns>The entries of <paramref name="wanted"/>, in the same order.</returns>
    public LoadedModel[] SetWanted(IReadOnlyList<string> wanted, IReadOnlyList<string>? prefetch = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _wanted.Clear();
        var result = new LoadedModel[wanted.Count];
        var priority = 0;
        for (var i = 0; i < wanted.Count; i++)
        {
            result[i] = Want(wanted[i], priority++, checkFile: true);
            _wanted.Add(wanted[i]);
        }

        var prefetching = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in prefetch ?? [])
        {
            if (!_wanted.Contains(path) && prefetching.Add(path))
            {
                Want(path, 1000 + priority++, checkFile: false);
            }
        }

        // Loads for anything no longer asked for are not worth finishing. A started
        // one keeps its slot until its task notices the cancellation.
        foreach (var entry in _entries.Values.ToList())
        {
            if (entry.State == ModelState.Loading && !_wanted.Contains(entry.Path) && !prefetching.Contains(entry.Path))
            {
                entry.Cancellation?.Cancel();
                _entries.Remove(entry.Path);
            }
        }

        Pump();
        Trim();
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var entry in _entries.Values)
        {
            entry.Cancellation?.Cancel();
            entry.Gpu?.Dispose();
            entry.Gpu = null;
        }

        _entries.Clear();
    }

    private LoadedModel Want(string path, int priority, bool checkFile)
    {
        if (_entries.TryGetValue(path, out var entry))
        {
            // A file saved again since it was read is read again. Asked only when the
            // set of wanted models changes, so it costs one metadata read per change.
            if (checkFile && entry.State != ModelState.Loading && FileChanged(entry))
            {
                Release(entry);
                _entries.Remove(path);
            }
            else
            {
                entry.LastUsed = ++_clock;
                entry.Priority = priority;
                return entry;
            }
        }

        entry = new LoadedModel(path) { Priority = priority, LastUsed = ++_clock };
        (entry.FileLength, entry.FileWriteUtc) = Identify(path);
        _entries[path] = entry;
        return entry;
    }

    private void Pump()
    {
        while (_running < _parallelism)
        {
            LoadedModel? next = null;
            foreach (var entry in _entries.Values)
            {
                if (entry.State == ModelState.Loading && !entry.Started && (next is null || entry.Priority < next.Priority))
                {
                    next = entry;
                }
            }

            if (next is null)
            {
                return;
            }

            next.Started = true;
            next.Cancellation = new CancellationTokenSource();
            _running++;
            _ = RunAsync(next, next.Cancellation);
        }
    }

    private async Task RunAsync(LoadedModel entry, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        MeshData? mesh = null;
        IDisposable? gpu = null;
        string? error = null;
        TimeSpan parse = default, prepare = default;
        try
        {
            (mesh, gpu, parse, prepare) = await Task.Run(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                var parsed = _load(entry.Path, token);
                var parseTime = stopwatch.Elapsed;
                token.ThrowIfCancellationRequested();
                stopwatch.Restart();
                var prepared = parsed.TriangleCount > 0 ? _prepare?.Invoke(parsed, token) : null;
                return (parsed, prepared, parseTime, stopwatch.Elapsed);
            }, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is GeometryFormatException or IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            error = ex is OutOfMemoryException ? "Not enough memory to load this model." : ex.Message;
        }
        catch (Exception ex)
        {
            // The renderer refusing a mesh (e.g. out of video memory) is still only
            // this one model failing, not the application.
            error = $"Could not display this model: {ex.Message}";
        }
        finally
        {
            _running--;
            cancellation.Dispose();
        }

        var current = !_disposed && !token.IsCancellationRequested &&
            _entries.TryGetValue(entry.Path, out var live) && ReferenceEquals(live, entry);
        if (!current)
        {
            gpu?.Dispose();
        }
        else
        {
            entry.Cancellation = null;
            if (mesh is not null && error is null)
            {
                entry.Mesh = mesh;
                entry.Gpu = gpu;
                entry.ParseTime = parse;
                entry.PrepareTime = prepare;
                entry.State = ModelState.Loaded;
            }
            else
            {
                entry.Error = error ?? "The file could not be read.";
                entry.State = ModelState.Failed;
            }

            Completed?.Invoke(entry);
        }

        if (!_disposed)
        {
            Pump();
            Trim();
        }
    }

    /// <summary>Drops models used longest ago until the loaded ones fit the budget. Wanted models always stay.</summary>
    private void Trim()
    {
        var total = Bytes;
        if (total <= _budgetBytes)
        {
            return;
        }

        foreach (var entry in _entries.Values.Where(e => e.State != ModelState.Loading && !_wanted.Contains(e.Path))
                     .OrderBy(e => e.LastUsed).ToList())
        {
            if (total <= _budgetBytes)
            {
                break;
            }

            total -= entry.Bytes;
            Release(entry);
            _entries.Remove(entry.Path);
        }
    }

    private static void Release(LoadedModel entry)
    {
        entry.Cancellation?.Cancel();
        entry.Gpu?.Dispose();
        entry.Gpu = null;
    }

    private static bool FileChanged(LoadedModel entry)
    {
        var (length, written) = Identify(entry.Path);
        return length != entry.FileLength || written != entry.FileWriteUtc;
    }

    private static (long Length, DateTime WriteUtc) Identify(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-1, default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (-1, default);
        }
    }
}
