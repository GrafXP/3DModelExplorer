using System.Collections.Concurrent;

namespace ModelExplorer.App;

/// <summary>
/// The UI thread's synchronization context. An await on the UI thread resumes on
/// it: the continuation is queued and the window is woken with a message, which
/// it answers by running the queue and drawing a frame. This is what lets the
/// library, the model cache and the workspace be written as plain async code that
/// only ever touches its state from one thread.
/// </summary>
internal sealed class UiDispatcher(Action wake) : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private int _woken;

    public int ThreadId { get; } = Environment.CurrentManagedThreadId;

    public override void Post(SendOrPostCallback d, object? state)
    {
        _queue.Enqueue((d, state));

        // One wake-up message per batch: a burst of completions is drained by
        // one message, not one each.
        if (Interlocked.Exchange(ref _woken, 1) == 0)
        {
            wake();
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId == ThreadId)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim();
        Exception? error = null;
        Post(_ =>
        {
            try
            {
                d(state);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                done.Set();
            }
        }, null);
        done.Wait();
        if (error is not null)
        {
            throw new InvalidOperationException("A callback sent to the UI thread failed.", error);
        }
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Runs what was posted. UI thread. Returns whether anything ran.</summary>
    public bool Drain()
    {
        Volatile.Write(ref _woken, 0);
        var ran = false;

        // Bounded, so work that keeps posting more work cannot starve input.
        for (var i = 0; i < 4096 && _queue.TryDequeue(out var item); i++)
        {
            item.Callback(item.State);
            ran = true;
        }

        if (!_queue.IsEmpty && Interlocked.Exchange(ref _woken, 1) == 0)
        {
            wake();
        }

        return ran;
    }
}
