namespace Train.Scenes;

internal sealed class FarTerrainWorker(Func<int, CancellationToken, bool> advance, Action? slotAvailable = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private Task pending = Task.CompletedTask;
    private Task? disposal;
    private bool enabled = true;
    private bool allowed, running, notifying;
    private Exception? failure;
    internal Exception? Failure { get { lock (gate) return failure; } }
    internal Task PendingWork { get { lock (gate) return pending; } }
    internal int RunningCount { get { lock (gate) return running ? 1 : 0; } }

    internal void SetEnabled(bool value)
    {
        lock (gate) { enabled = value; if (!value) allowed = false; }
    }
    internal void Pause() { lock (gate) allowed = false; }
    internal void Pump(int activeL0Builds, bool hasRunnableWork)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            allowed = enabled && activeL0Builds < RailwayCoordinates.MaxConcurrentBuilds && hasRunnableWork;
            Start();
        }
    }
    private void Start()
    {
        if (!allowed || running || notifying || failure is not null || disposal is not null) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = completion.Task;
        running = true;
        _ = Task.Run(() => Run(completion));
    }
    private void Run(TaskCompletionSource completion)
    {
        try
        {
            while (true)
            {
                lock (gate) if (!allowed || disposal is not null) break;
                if (advance(128, cancellation.Token)) break;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { lock (gate) failure = error; }
        finally
        {
            lock (gate) { allowed = false; running = false; notifying = true; }
            try { slotAvailable?.Invoke(); }
            catch (Exception error) { lock (gate) failure ??= error; }
            finally
            {
                lock (gate)
                {
                    notifying = false;
                    completion.SetResult();
                    Start();
                }
            }
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            allowed = false;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
            cancellation.Cancel();
            _ = Stop(completion);
            return new(disposal);
        }
    }
    private async Task Stop(TaskCompletionSource completion)
    {
        await pending.ConfigureAwait(false);
        cancellation.Dispose();
        completion.SetResult();
    }
}
