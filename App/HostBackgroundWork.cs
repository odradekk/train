namespace Train.App;

// Decision note: 2026-10-26-post-demo-performance-and-memory-gaps
// 读档和存档共用线程，避免共享数组池在多个线程的 TLS 槽里各保留大缓冲。
internal sealed class HostBackgroundWork : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<(Action Run, Action Cancel)> _waiting = new();
    private readonly Thread _thread;
    private bool _closed;

    internal HostBackgroundWork()
    {
        _thread = new(Run) { IsBackground = true, Name = "Host background work" };
        _thread.Start();
    }

    internal Task<T> Enqueue<T>(Func<T> work, CancellationToken cancellation = default)
    {
        if (Thread.CurrentThread == _thread)
            throw new InvalidOperationException("后台工作不能等待同一队列中的工作。");
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _waiting.Enqueue((() =>
            {
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    result.SetResult(work());
                }
                catch (OperationCanceledException error) { result.SetCanceled(error.CancellationToken); }
                catch (Exception error) { result.SetException(error); }
            }, () => result.SetCanceled()));
            Monitor.Pulse(_gate);
        }
        return result.Task;
    }

    private void Run()
    {
        while (true)
        {
            Action work;
            lock (_gate)
            {
                while (_waiting.Count == 0 && !_closed) Monitor.Wait(_gate);
                if (_closed) return;
                work = _waiting.Dequeue().Run;
            }
            work();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            while (_waiting.TryDequeue(out var work)) work.Cancel();
            Monitor.Pulse(_gate);
        }
    }
}
