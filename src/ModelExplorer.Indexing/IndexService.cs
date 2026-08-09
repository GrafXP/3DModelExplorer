using System.Diagnostics;
using System.Threading.Channels;

namespace ModelExplorer.Indexing;

/// <summary>
/// Owns the index: library roots, scanning, and reading the whole thing back.
/// </summary>
/// <remarks>
/// The scan is a producer/consumer pipeline. Each root is walked on its own,
/// pushing into a bounded channel; a single consumer drains it into
/// transaction-sized batches. One consumer because SQLite has one writer anyway,
/// and bounded because an unbounded channel would let a fast local disk queue a
/// million records in memory while the writer catches up.
///
/// Roots are split into local and network groups with separate degrees of
/// parallelism, so a NAS that answers in its own time can never hold up the
/// local disks or the UI.
/// </remarks>
public sealed class IndexService : IDisposable
{
    /// <summary>
    /// Two concurrent walks per share. More does not make a NAS answer faster —
    /// it is latency-bound, not throughput-bound — and it does crowd out the
    /// local roots' threads.
    /// </summary>
    private const int NetworkParallelism = 2;

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(150);

    private readonly ModelIndexStore _store;
    private readonly string[] _extensions;

    public IndexService(ModelIndexStore store, IReadOnlyList<string> extensions)
    {
        _store = store;
        _extensions = [.. extensions];
    }

    public Task<IReadOnlyList<LibraryRoot>> GetRootsAsync() => Task.Run(_store.GetRoots);

    public Task<LibraryRoot> AddRootAsync(string path) => Task.Run(() =>
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return _store.AddRoot(full, LibraryRoot.IsNetworkPath(full));
    });

    public Task RemoveRootAsync(long rootId) => Task.Run(() => _store.RemoveRoot(rootId));

    public Task<IReadOnlyList<ModelFile>> LoadFilesAsync() => Task.Run(_store.LoadFiles);

    /// <summary>
    /// Rebuilds the index for the given roots.
    /// </summary>
    /// <remarks>
    /// A full rescan: each root's rows are dropped and rewritten. Step 7 replaces
    /// that with a (size, mtime) diff so unchanged files are never touched.
    ///
    /// Cancelling keeps every batch already committed and leaves the roots
    /// unmarked, so they are known to be partly indexed rather than silently
    /// wrong. Cancellation is reported through <see cref="ScanSummary.Cancelled"/>
    /// and never thrown: it is a button the user pressed, not a failure, and an
    /// escaping <see cref="OperationCanceledException"/> would surface as a crash
    /// from the command that started the scan.
    /// </remarks>
    public async Task<ScanSummary> ScanAsync(
        IReadOnlyList<LibraryRoot> roots,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var counters = new Counters();
        var stopwatch = Stopwatch.StartNew();

        // Bailing out before the clear matters: dropping every row and then
        // indexing nothing would empty the index instead of leaving it alone.
        if (roots.Count == 0 || cancellationToken.IsCancellationRequested)
        {
            return new ScanSummary(0, 0, TimeSpan.Zero, cancellationToken.IsCancellationRequested);
        }

        // Not cancellable. Once the decision to rescan is made, clearing has to
        // finish or the roots are left half-emptied with no scan to refill them.
        await Task.Run(
            () =>
            {
                foreach (var root in roots)
                {
                    _store.ClearRoot(root.Id);
                }
            },
            CancellationToken.None);

        var channel = Channel.CreateBounded<ScannedFile>(
            new BoundedChannelOptions(ModelIndexStore.BatchSize * 4)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        using var reporting = new CancellationTokenSource();
        var reporter = progress is null
            ? Task.CompletedTask
            : ReportAsync(progress, counters, stopwatch, reporting.Token);

        var consumer = ConsumeAsync(channel.Reader, counters, cancellationToken);

        try
        {
            await Task.WhenAll(
                ScanGroupAsync(
                    [.. roots.Where(r => !r.IsNetwork)],
                    Environment.ProcessorCount,
                    channel.Writer,
                    counters,
                    cancellationToken),
                ScanGroupAsync(
                    [.. roots.Where(r => r.IsNetwork)],
                    NetworkParallelism,
                    channel.Writer,
                    counters,
                    cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // Expected on Cancel. Whatever the consumer has already committed stays.
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        await consumer;
        stopwatch.Stop();

        await reporting.CancelAsync();
        await reporter;

        var cancelled = cancellationToken.IsCancellationRequested;
        if (!cancelled)
        {
            var completed = DateTime.UtcNow;
            await Task.Run(
                () =>
                {
                    foreach (var root in roots)
                    {
                        _store.MarkScanned(root.Id, completed);
                    }
                },
                CancellationToken.None);
        }

        return new ScanSummary(counters.Found, counters.Indexed, stopwatch.Elapsed, cancelled);
    }

    public void Dispose() => _store.Dispose();

    private async Task ScanGroupAsync(
        IReadOnlyList<LibraryRoot> roots,
        int parallelism,
        ChannelWriter<ScannedFile> writer,
        Counters counters,
        CancellationToken cancellationToken)
    {
        if (roots.Count == 0)
        {
            return;
        }

        await Parallel.ForEachAsync(
            roots,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Math.Min(parallelism, roots.Count)),
                CancellationToken = cancellationToken,
            },
            async (root, token) =>
            {
                counters.Current = root.Path;

                foreach (var entry in FileScanner.Enumerate(root.Path, _extensions))
                {
                    // Per file, not per directory: this is what makes Cancel land
                    // within one directory read rather than at the end of a walk.
                    token.ThrowIfCancellationRequested();

                    Interlocked.Increment(ref counters.Found);
                    await writer.WriteAsync(
                        new ScannedFile(root.Id, entry.RelativePath, entry.Size, entry.ModifiedTicks),
                        token);
                }
            });
    }

    private async Task ConsumeAsync(
        ChannelReader<ScannedFile> reader,
        Counters counters,
        CancellationToken cancellationToken)
    {
        var batch = new List<ScannedFile>(ModelIndexStore.BatchSize);

        // Read with no token of its own. Cancellation is handled by breaking out
        // below, which commits the partial batch instead of throwing it away;
        // producers are released by the same token they were given.
        await foreach (var file in reader.ReadAllAsync(CancellationToken.None))
        {
            batch.Add(file);

            if (batch.Count >= ModelIndexStore.BatchSize)
            {
                Flush(batch, counters);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        Flush(batch, counters);
    }

    private void Flush(List<ScannedFile> batch, Counters counters)
    {
        if (batch.Count == 0)
        {
            return;
        }

        _store.WriteBatch(batch);
        Interlocked.Add(ref counters.Indexed, batch.Count);
        batch.Clear();
    }

    /// <summary>
    /// Samples the counters on a timer.
    /// </summary>
    /// <remarks>
    /// Reporting per file would post tens of thousands of callbacks a second at
    /// the UI thread and is the classic way to make a "responsive" scan freeze the
    /// window. A timer decouples the update rate from the scan rate entirely.
    /// </remarks>
    private static async Task ReportAsync(
        IProgress<ScanProgress> progress,
        Counters counters,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(ProgressInterval, cancellationToken);

                progress.Report(new ScanProgress(
                    Interlocked.Read(ref counters.Found),
                    Interlocked.Read(ref counters.Indexed),
                    stopwatch.Elapsed,
                    counters.Current));
            }
        }
        catch (OperationCanceledException)
        {
            // Reporting ends with the scan.
        }
    }

    /// <summary>
    /// Shared counters. A class with plain fields because
    /// <see cref="Interlocked"/> needs a ref to storage, which a property cannot
    /// give.
    /// </summary>
    private sealed class Counters
    {
        public long Found;
        public long Indexed;

        private string _current = string.Empty;

        public string Current
        {
            get => Volatile.Read(ref _current);
            set => Volatile.Write(ref _current, value);
        }
    }
}
