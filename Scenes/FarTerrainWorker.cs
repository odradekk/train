namespace Train.Scenes;

internal sealed class FarTerrainWorker : IAsyncDisposable
{
    private readonly Func<int, CancellationToken, bool> advance;
    private readonly Action? slotAvailable;
    private readonly FarTerrainPermission? permission;
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

    internal FarTerrainWorker(Func<int, CancellationToken, bool> advance, Action? slotAvailable = null,
        FarTerrainPermission? permission = null)
    {
        this.advance = advance;
        this.slotAvailable = slotAvailable;
        this.permission = permission;
        permission?.Register(this);
    }

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
            allowed = enabled && activeL0Builds == 0 && hasRunnableWork;
            Start();
        }
    }
    internal void Resume() { lock (gate) Start(); }
    private void Start()
    {
        if (!allowed || permission?.Allowed == false || running || notifying || failure is not null || disposal is not null) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = completion.Task;
        running = true;
        _ = Task.Run(() => Run(completion));
    }
    private void Run(TaskCompletionSource completion)
    {
        var complete = false;
        try
        {
            while (true)
            {
                lock (gate) if (!allowed || permission?.Allowed == false || disposal is not null) break;
                if (advance(128, cancellation.Token)) { complete = true; break; }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { lock (gate) failure = error; }
        finally
        {
            lock (gate) { if (complete) allowed = false; running = false; notifying = true; }
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
            permission?.Remove(this);
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
