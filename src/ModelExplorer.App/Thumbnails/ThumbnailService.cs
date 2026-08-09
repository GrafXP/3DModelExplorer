using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ModelExplorer.Geometry;
using ModelExplorer.Geometry.Rendering;
using ModelExplorer.Indexing;

namespace ModelExplorer.App.Thumbnails;

/// <summary>
/// Produces thumbnails for library rows, newest request first.
/// </summary>
/// <remarks>
/// Three tiers, cheapest first: a bounded in-memory cache of decoded images, the
/// PNG cache on disk, and finally a parse plus a software render. Only the last
/// costs anything, and it happens on dedicated worker threads.
///
/// Priority is simply request order, newest wins. In a virtualized grid that is
/// exactly "what is on screen now": scrolling realizes containers as they come
/// into view and recycles the ones leaving it, and a recycled container cancels
/// its request. So the queue naturally drains toward the viewport without the
/// service knowing anything about scroll positions.
/// </remarks>
public sealed class ThumbnailService : IDisposable
{
    /// <summary>Rendered size. The grid shows them smaller and scales down.</summary>
    public const int PixelSize = 256;

    /// <summary>
    /// Roughly two screens of a dense grid. Each decoded image is 256 KB, so this
    /// is the cache's memory budget in disguise — about 50 MB.
    /// </summary>
    private const int MemoryCacheEntries = 192;

    private readonly GeometryLoaderRegistry _loaders;
    private readonly ThumbnailCache _cache;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly Thread[] _workers;

    private readonly Lock _gate = new();
    private readonly PriorityQueue<Job, long> _queue = new();
    private readonly Dictionary<long, LinkedListNode<CacheEntry>> _memoryIndex = [];
    private readonly LinkedList<CacheEntry> _memoryOrder = new();

    /// <summary>
    /// Files that could not be rendered. Kept in memory rather than marked on
    /// disk so a file that was merely locked is retried next session, while
    /// scrolling past a corrupt one does not retry it on every pass.
    /// </summary>
    private readonly HashSet<long> _failed = [];

    private long _sequence;

    public ThumbnailService(GeometryLoaderRegistry loaders, ThumbnailCache cache, int? workerCount = null)
    {
        _loaders = loaders;
        _cache = cache;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Half the cores, capped. These threads spend most of their time parsing,
        // which is as much disk as CPU, and the interactive viewport must keep its
        // share of the machine while the grid fills in.
        var count = workerCount ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        _workers = new Thread[count];
        for (var i = 0; i < count; i++)
        {
            _workers[i] = new Thread(Work)
            {
                IsBackground = true,
                Name = $"Thumbnail worker {i + 1}",

                // Below normal: a thumbnail appearing a frame later is invisible,
                // a dropped frame while scrolling is not.
                Priority = ThreadPriority.BelowNormal,
            };

            _workers[i].Start();
        }
    }

    /// <summary>
    /// Asks for a file's thumbnail. <paramref name="ready"/> runs on the UI
    /// thread — synchronously, before returning, on an in-memory cache hit.
    /// </summary>
    /// <returns>Disposing cancels the request. Required when a row is recycled.</returns>
    public IDisposable Load(ModelFile file, Action<BitmapSource?> ready)
    {
        lock (_gate)
        {
            if (TryTakeFromMemory(file.Id, out var cached))
            {
                ready(cached);
                return Cancellation.None;
            }

            if (_failed.Contains(file.Id))
            {
                ready(null);
                return Cancellation.None;
            }
        }

        var job = new Job(file, ready);

        lock (_gate)
        {
            // Negated so the heap's smallest value is the newest request.
            _queue.Enqueue(job, -(++_sequence));
        }

        _available.Release();
        return job;
    }

    /// <summary>Empties both cache tiers. Callers should then re-realize the visible rows.</summary>
    public int ClearCache()
    {
        lock (_gate)
        {
            _memoryIndex.Clear();
            _memoryOrder.Clear();
            _failed.Clear();
        }

        return _cache.Clear();
    }

    public void Dispose()
    {
        _shutdown.Cancel();

        // One permit per worker so each wakes from its wait and sees the shutdown.
        _available.Release(_workers.Length);
        foreach (var worker in _workers)
        {
            worker.Join(TimeSpan.FromSeconds(2));
        }

        _shutdown.Dispose();
        _available.Dispose();
    }

    private void Work()
    {
        var token = _shutdown.Token;

        while (true)
        {
            try
            {
                _available.Wait(token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            Job? job;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out job, out _))
                {
                    continue;
                }
            }

            Process(job);
        }
    }

    private void Process(Job job)
    {
        var running = job.Begin();
        if (running is null)
        {
            // Scrolled away before a worker ever picked it up — the common case
            // during a fast scroll, and the reason the queue drains so quickly.
            return;
        }

        try
        {
            var file = job.File;

            // Re-checked here as well as in Load: another worker may have rendered
            // this very file while this job sat in the queue.
            lock (_gate)
            {
                if (TryTakeFromMemory(file.Id, out var cached))
                {
                    Publish(job, cached);
                    return;
                }
            }

            var image = Produce(file, running.Token);
            if (image is null)
            {
                lock (_gate)
                {
                    _failed.Add(file.Id);
                }

                Publish(job, null);
                return;
            }

            lock (_gate)
            {
                StoreInMemory(file.Id, image);
            }

            Publish(job, image);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the row being recycled, or by shutdown.
        }
        finally
        {
            job.End(running);
        }
    }

    /// <summary>Disk cache, then parse and render. Null means the file cannot be shown.</summary>
    private BitmapSource? Produce(ModelFile file, CancellationToken token)
    {
        try
        {
            var path = file.FullPath;
            var key = ContentKey.Compute(path);
            token.ThrowIfCancellationRequested();

            if (_cache.TryGetPath(key, out var cached))
            {
                return Decode(File.ReadAllBytes(cached));
            }

            var mesh = _loaders.Load(path, token);
            var raster = MeshRasterizer.Render(mesh, PixelSize, token);
            if (raster.IsEmpty)
            {
                // A file that parsed but drew nothing has no thumbnail to cache;
                // it is not a failure worth retrying either.
                return null;
            }

            var png = Encode(raster);
            _cache.Write(key, png);
            return Decode(png);
        }
        catch (Exception ex) when (ex is GeometryFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[] Encode(RasterImage raster)
    {
        var source = BitmapSource.Create(
            raster.Size,
            raster.Size,
            96,
            96,

            // Straight alpha, matching the rasterizer's output. Pbgra32 would
            // require premultiplying and would darken every antialiased edge.
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            ToBgra(raster.Pixels),
            raster.Size * 4);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>The rasterizer emits RGBA; WPF's Bgra32 wants the other order.</summary>
    private static byte[] ToBgra(byte[] rgba)
    {
        var bgra = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = rgba[i + 3];
        }

        return bgra;
    }

    /// <summary>
    /// Decodes on the worker and freezes, so the image can cross to the UI thread
    /// and be bound without any further copying.
    /// </summary>
    private static BitmapSource Decode(byte[] png)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = new MemoryStream(png);

        // OnLoad, or the stream has to stay open for the lifetime of the image.
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void Publish(Job job, BitmapSource? image)
    {
        if (job.IsCancelled)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            // Re-checked on the UI thread: the row may have been recycled while
            // this callback sat in the dispatcher queue.
            if (!job.IsCancelled)
            {
                job.Ready(image);
            }
        });
    }

    /// <summary>Must be called under <see cref="_gate"/>.</summary>
    private bool TryTakeFromMemory(long fileId, out BitmapSource image)
    {
        if (_memoryIndex.TryGetValue(fileId, out var node))
        {
            _memoryOrder.Remove(node);
            _memoryOrder.AddFirst(node);
            image = node.Value.Image;
            return true;
        }

        image = null!;
        return false;
    }

    /// <summary>Must be called under <see cref="_gate"/>.</summary>
    private void StoreInMemory(long fileId, BitmapSource image)
    {
        if (_memoryIndex.ContainsKey(fileId))
        {
            return;
        }

        _memoryIndex[fileId] = _memoryOrder.AddFirst(new CacheEntry(fileId, image));

        while (_memoryOrder.Count > MemoryCacheEntries)
        {
            var last = _memoryOrder.Last!;
            _memoryOrder.RemoveLast();
            _memoryIndex.Remove(last.Value.FileId);
        }
    }

    private readonly record struct CacheEntry(long FileId, BitmapSource Image);

    private sealed class Job(ModelFile file, Action<BitmapSource?> ready) : IDisposable
    {
        private readonly Lock _gate = new();
        private CancellationTokenSource? _running;

        public ModelFile File { get; } = file;

        public Action<BitmapSource?> Ready { get; } = ready;

        public bool IsCancelled { get; private set; }

        public void Dispose() => Cancel();

        public void Cancel()
        {
            lock (_gate)
            {
                IsCancelled = true;

                // Only cancels a render that has actually started. Allocating a
                // token source per queued job would mean thousands of them during
                // a fast scroll; there are never more than one per worker here.
                _running?.Cancel();
            }
        }

        /// <summary>Claims the job for a worker. Null if it was already cancelled.</summary>
        public CancellationTokenSource? Begin()
        {
            lock (_gate)
            {
                if (IsCancelled)
                {
                    return null;
                }

                _running = new CancellationTokenSource();
                return _running;
            }
        }

        public void End(CancellationTokenSource running)
        {
            lock (_gate)
            {
                _running = null;
            }

            running.Dispose();
        }
    }

    /// <summary>Handed back when there is nothing to cancel.</summary>
    private sealed class Cancellation : IDisposable
    {
        public static IDisposable None { get; } = new Cancellation();

        public void Dispose()
        {
        }
    }
}
