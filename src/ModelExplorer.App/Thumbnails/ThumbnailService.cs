using System.Collections.Concurrent;
using ModelExplorer.App.Render;
using ModelExplorer.Core.Thumbnails;
using ModelExplorer.Geometry;
using ModelExplorer.Geometry.Rendering;
using ModelExplorer.Indexing;

namespace ModelExplorer.App.Thumbnails;

/// <summary>
/// Supplies the thumbnails the model grid draws.
/// </summary>
/// <remarks>
/// <para>The grid asks for each thumbnail it shows, every frame, with a priority:
/// what is on screen first, then what is about to scroll in. Whatever is not in
/// the atlas yet is produced on low-priority workers — from the PNG cache on disk,
/// or by parsing the file and rendering it in software — and uploaded on the UI
/// thread a few per frame. Requests the grid stops asking for are cancelled, so a
/// fast scroll never leaves a queue of work behind it.</para>
/// <para>Rendered at <see cref="RenderSize"/> and cached at that size; shrunk on the
/// worker to the size the grid shows, so the atlas holds one texel per screen pixel.</para>
/// </remarks>
internal sealed class ThumbnailService : IDisposable
{
    /// <summary>Rendered and cached size. The grid shows them at this size or smaller.</summary>
    public const int RenderSize = 256;

    /// <summary>Decoded full-size thumbnails kept in memory, so a size change re-shrinks without disk reads (about 50 MB).</summary>
    private const int MemoryEntries = 192;

    private readonly GeometryLoaderRegistry _loaders;
    private readonly ThumbnailCache _cache;
    private readonly Action _wake;
    private readonly WorkQueue _queue;
    private readonly ConcurrentQueue<Result> _results = new();
    private readonly Dictionary<long, (WorkQueue.Job Job, int Frame)> _requests = [];
    private readonly HashSet<long> _failed = [];
    private readonly Lock _memoryGate = new();
    private readonly Dictionary<long, LinkedListNode<(long Id, RgbaImage Image)>> _memoryIndex = [];
    private readonly LinkedList<(long Id, RgbaImage Image)> _memoryOrder = new();
    private ThumbnailAtlas? _atlas;
    private int _frame;
    private int _generation;

    public ThumbnailService(GeometryLoaderRegistry loaders, ThumbnailCache cache, Action wake)
    {
        _loaders = loaders;
        _cache = cache;
        _wake = wake;

        // Half the cores, capped. The workers parse files, which is as much disk as
        // CPU, and the viewport must keep its share of the machine meanwhile.
        _queue = new WorkQueue(Math.Clamp(Environment.ProcessorCount / 2, 1, 4), "Thumbnail worker");
    }

    public int CellSize => _atlas?.CellSize ?? 0;

    /// <summary>Sets the size thumbnails are shown at, in pixels. A change empties the atlas.</summary>
    public void SetCellSize(int pixels, GraphicsDevice gpu)
    {
        pixels = Math.Clamp(pixels, 32, RenderSize);
        if (_atlas is not null && _atlas.CellSize == pixels)
        {
            return;
        }

        CancelAll();
        if (_atlas is null)
        {
            _atlas = new ThumbnailAtlas(gpu, pixels);
        }
        else
        {
            _atlas.Reset(pixels);
        }
    }

    public void BeginFrame()
    {
        _frame++;
        _atlas?.BeginFrame();
    }

    /// <summary>
    /// The thumbnail of <paramref name="file"/> if it is in the atlas; if not, it is
    /// requested. Lower <paramref name="priority"/> values are produced first.
    /// </summary>
    public bool TryGet(ModelFile file, int priority, out AtlasSlot slot, out bool failed)
    {
        slot = default;
        failed = false;
        if (_atlas is null)
        {
            return false;
        }

        if (_atlas.TryGet(file.Id, out slot))
        {
            return true;
        }

        if (_failed.Contains(file.Id))
        {
            failed = true;
            return false;
        }

        if (_requests.TryGetValue(file.Id, out var request))
        {
            _requests[file.Id] = (request.Job, _frame);
            _queue.SetPriority(request.Job, priority);
            return false;
        }

        var cell = _atlas.CellSize;
        var generation = _generation;
        var job = _queue.Enqueue(priority, token => Produce(file, cell, generation, token));
        _requests[file.Id] = (job, _frame);
        return false;
    }

    /// <summary>Requests that were asked for neither in this frame nor the last are no longer wanted.</summary>
    public void EndFrame()
    {
        List<long>? stale = null;
        foreach (var (id, request) in _requests)
        {
            if (request.Frame < _frame - 1)
            {
                (stale ??= []).Add(id);
            }
        }

        foreach (var id in stale ?? [])
        {
            _queue.Cancel(_requests[id].Job);
            _requests.Remove(id);
        }
    }

    /// <summary>
    /// Moves finished thumbnails into the atlas, at most <paramref name="budget"/>
    /// per frame, so a burst of them never costs a dropped frame. True if any arrived.
    /// </summary>
    public bool Upload(int budget = 24)
    {
        var any = false;
        while (budget-- > 0 && _results.TryDequeue(out var result))
        {
            if (result.Generation != _generation || _atlas is null)
            {
                continue;
            }

            _requests.Remove(result.Id);
            if (result.Image is null)
            {
                _failed.Add(result.Id);
            }
            else if (result.CellSize == _atlas.CellSize)
            {
                _atlas.Put(result.Id, result.Image);
            }

            any = true;
        }

        if (!_results.IsEmpty)
        {
            _wake(); // more next frame
        }

        return any;
    }

    /// <summary>Forgets every thumbnail, in memory, in the atlas and on disk. Returns how many files were removed.</summary>
    public async Task<int> ClearCacheAsync()
    {
        CancelAll();
        _failed.Clear();
        _atlas?.Clear();
        lock (_memoryGate)
        {
            _memoryIndex.Clear();
            _memoryOrder.Clear();
        }

        return await Task.Run(_cache.Clear);
    }

    public void Dispose()
    {
        _queue.Dispose();
        _atlas?.Dispose();
    }

    private void CancelAll()
    {
        _generation++;
        foreach (var request in _requests.Values)
        {
            _queue.Cancel(request.Job);
        }

        _requests.Clear();
        _results.Clear();
    }

    private void Produce(ModelFile file, int cellSize, int generation, CancellationToken token)
    {
        RgbaImage? image = null;
        try
        {
            var full = TakeFromMemory(file.Id) ?? Load(file, token);
            if (full is not null)
            {
                StoreInMemory(file.Id, full);
                image = ImageScaling.Fit(full, cellSize);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is GeometryFormatException or IOException or UnauthorizedAccessException or InvalidDataException or OutOfMemoryException)
        {
            // Remembered for this session only: a file that was merely locked is
            // tried again next time, a corrupt one is not retried on every scroll.
        }

        _results.Enqueue(new Result(file.Id, image, cellSize, generation));
        _wake();
    }

    /// <summary>The disk cache, or a parse and a software render. Null when the file draws nothing.</summary>
    private RgbaImage? Load(ModelFile file, CancellationToken token)
    {
        var path = file.FullPath;
        var key = ContentKey.Compute(path);
        token.ThrowIfCancellationRequested();
        if (_cache.TryGetPath(key, out var cached))
        {
            try
            {
                return PngCodec.Decode(File.ReadAllBytes(cached));
            }
            catch (InvalidDataException)
            {
                // A damaged or foreign cache entry is rendered again and replaced.
            }
        }

        var mesh = _loaders.Load(path, token);
        var raster = MeshRasterizer.Render(mesh, RenderSize, token);
        if (raster.IsEmpty)
        {
            return null;
        }

        var image = new RgbaImage(raster.Size, raster.Size, raster.Pixels);
        _cache.Write(key, PngCodec.Encode(image));
        return image;
    }

    private RgbaImage? TakeFromMemory(long id)
    {
        lock (_memoryGate)
        {
            if (!_memoryIndex.TryGetValue(id, out var node))
            {
                return null;
            }

            _memoryOrder.Remove(node);
            _memoryOrder.AddFirst(node);
            return node.Value.Image;
        }
    }

    private void StoreInMemory(long id, RgbaImage image)
    {
        lock (_memoryGate)
        {
            if (_memoryIndex.ContainsKey(id))
            {
                return;
            }

            _memoryIndex[id] = _memoryOrder.AddFirst((id, image));
            while (_memoryOrder.Count > MemoryEntries)
            {
                _memoryIndex.Remove(_memoryOrder.Last!.Value.Id);
                _memoryOrder.RemoveLast();
            }
        }
    }

    private readonly record struct Result(long Id, RgbaImage? Image, int CellSize, int Generation);
}

/// <summary>
/// A few low-priority threads working through jobs, the most urgent first. A job's
/// priority can change while it waits; a cancelled job that has not started never
/// runs, one that has started sees its token cancelled.
/// </summary>
internal sealed class WorkQueue : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<Job> _pending = [];
    private readonly Thread[] _threads;
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _shutdown = new();

    public WorkQueue(int threads, string name)
    {
        _threads = new Thread[threads];
        for (var i = 0; i < threads; i++)
        {
            // Below normal: a thumbnail a frame later is invisible, a dropped
            // frame while turning a model is not.
            _threads[i] = new Thread(Work) { IsBackground = true, Name = $"{name} {i + 1}", Priority = ThreadPriority.BelowNormal };
            _threads[i].Start();
        }
    }

    public Job Enqueue(int priority, Action<CancellationToken> work)
    {
        var job = new Job(work) { Priority = priority };
        lock (_gate)
        {
            _pending.Add(job);
        }

        _available.Release();
        return job;
    }

    public void SetPriority(Job job, int priority)
    {
        lock (_gate)
        {
            job.Priority = priority;
        }
    }

    public void Cancel(Job job)
    {
        lock (_gate)
        {
            job.Cancelled = true;
            job.Running?.Cancel();
            _pending.Remove(job);
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _available.Release(_threads.Length);
        foreach (var thread in _threads)
        {
            thread.Join(TimeSpan.FromSeconds(1));
        }
    }

    private void Work()
    {
        while (true)
        {
            try
            {
                _available.Wait(_shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Job? next = null;
            CancellationTokenSource running;
            lock (_gate)
            {
                foreach (var job in _pending)
                {
                    if (next is null || job.Priority < next.Priority)
                    {
                        next = job;
                    }
                }

                if (next is null)
                {
                    continue;
                }

                _pending.Remove(next);
                running = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                next.Running = running;
            }

            try
            {
                next.Work(running.Token);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (_gate)
                {
                    next.Running = null;
                }

                running.Dispose();
            }
        }
    }

    internal sealed class Job(Action<CancellationToken> work)
    {
        public Action<CancellationToken> Work { get; } = work;

        public int Priority { get; set; }

        public bool Cancelled { get; set; }

        public CancellationTokenSource? Running { get; set; }
    }
}
