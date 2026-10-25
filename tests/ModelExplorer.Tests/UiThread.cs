using System.Collections.Concurrent;

namespace ModelExplorer.Tests;

/// <summary>
/// Runs a test the way the app runs its UI thread: one thread, with a
/// synchronization context that queues every continuation back onto it. The
/// library, the model cache and the workspace rely on exactly that.
/// </summary>
internal static class UiThread
{
    public static void Run(Func<Task> test, int timeoutSeconds = 30)
    {
        var context = new QueueContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var task = test();
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("The test did not finish on its UI thread.");
                }

                context.RunOne(TimeSpan.FromMilliseconds(50));
            }

            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Waits, on the UI thread, until <paramref name="condition"/> holds.</summary>
    public static async Task Until(Func<bool> condition, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition never held.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class QueueContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public void RunOne(TimeSpan wait)
        {
            if (_queue.TryTake(out var item, wait))
            {
                item.Callback(item.State);
            }
        }
    }
}
