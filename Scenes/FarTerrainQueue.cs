namespace Train.Scenes;

internal sealed class FarTerrainQueue : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly int operationBudget;
    private readonly Action? slotAvailable;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Queue<Work> waiting = new();
    private readonly HashSet<Task> pending = new();
    private readonly HashSet<string> keys = new(StringComparer.Ordinal);
    private Task? disposal;
    private Exception? failure;
    private bool paused;
    private int runningCount;
    private int capacity;
    private int notifying;

    internal Exception? Failure { get { lock (gate) return failure; } }
    internal IReadOnlyList<Task> PendingWork { get { lock (gate) return pending.ToArray(); } }
    internal bool HasPendingWork { get { lock (gate) return keys.Count != 0; } }
    internal int RunningCount { get { lock (gate) return runningCount; } }

    internal FarTerrainQueue(int operationBudget, Action? slotAvailable = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operationBudget);
        this.operationBudget = operationBudget;
        this.slotAvailable = slotAvailable;
    }

    internal bool Enqueue(string key, Func<int, CancellationToken, bool> advance)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (!keys.Add(key)) return false;
            waiting.Enqueue(new(key, advance));
            StartWorkers();
            return true;
        }
    }

    internal void PauseForL0(bool hasWaitingL0)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            paused = hasWaitingL0;
            StartWorkers();
        }
    }

    // availableSlots excludes slots already occupied by L0 and far workers.
    internal void Pump(int availableSlots, bool hasWaitingL0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(availableSlots);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            capacity = runningCount + availableSlots;
            paused = hasWaitingL0;
            StartWorkers();
        }
    }

    // All scheduling state is guarded by gate; delegates and notifications run outside it.
    private void StartWorkers()
    {
        while (disposal is null && failure is null && !paused && notifying == 0 &&
               runningCount < capacity && waiting.TryDequeue(out var work))
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(completion.Task);
            runningCount++;
            _ = Task.Run(() => Run(work, completion));
        }
    }

    private void Run(Work work, TaskCompletionSource completion)
    {
        while (true)
        {
            lock (gate)
            {
                if (disposal is not null || failure is not null || paused)
                {
                    if (disposal is null) waiting.Enqueue(work);
                    runningCount--;
                    notifying++;
                    break;
                }
            }
            bool finished = false;
            Exception? error = null;
            try { finished = work.Advance(operationBudget, cancellation.Token); }
            catch (Exception caught) { error = caught; }
            lock (gate)
            {
                if (error is not null && !(error is OperationCanceledException && cancellation.IsCancellationRequested))
                    failure ??= error;
                if (disposal is null)
                {
                    if (finished || error is not null) keys.Remove(work.Key);
                    if (failure is null && !paused)
                    {
                        if (!finished && error is null) continue;
                        if (waiting.TryDequeue(out var next))
                        {
                            work = next;
                            continue;
                        }
                    }
                    if (!finished && error is null) waiting.Enqueue(work);
                }
                runningCount--;
                notifying++;
                break;
            }
        }

        try
        {
            // Give L0 its released slot before starting more far work. Keep this task
            // pending through the callback so disposal also waits for scene scheduling.
            slotAvailable?.Invoke();
        }
        catch (Exception error)
        {
            lock (gate) failure ??= error;
        }
        finally
        {
            lock (gate)
            {
                notifying--;
                pending.Remove(completion.Task);
                completion.SetResult();
                StartWorkers();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task[] tasks;
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
            waiting.Clear();
            keys.Clear();
            tasks = pending.ToArray();
        }
        cancellation.Cancel();
        _ = StopAsync(tasks, completion);
        return new(completion.Task);
    }

    private async Task StopAsync(Task[] tasks, TaskCompletionSource completion)
    {
        await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellation.Dispose();
        completion.SetResult();
    }

    private sealed record Work(string Key, Func<int, CancellationToken, bool> Advance);
}
