namespace ModelExplorer.App;

/// <summary>
/// Continuations for the window's code, which is compiled unsafe throughout and
/// so cannot await: the work runs on, and the continuation comes back to, the
/// UI thread's synchronization context.
/// </summary>
internal static class UiTasks
{
    public static void Then<T>(Task<T> task, Action<T> continuation) => _ = ThenAsync(task, continuation);

    private static async Task ThenAsync<T>(Task<T> task, Action<T> continuation)
    {
        try
        {
            continuation(await task);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Best-effort housekeeping: a file that could not be removed is left for next time.
        }
    }
}
