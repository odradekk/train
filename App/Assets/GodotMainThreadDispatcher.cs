namespace Train.App.Assets;

internal sealed class GodotMainThreadDispatcher : IDisposable
{
    private readonly int _mainThread = Environment.CurrentManagedThreadId;
    private readonly Queue<(Action Execute, Action Cancel)> _pending = new();
    private int _directCalls;
    private bool _disposed;

    internal T InvokeOnCallingThread<T>(Func<T> action)
    {
        lock (_pending)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _directCalls++;
        }
        try { return action(); }
        finally
        {
            lock (_pending)
            {
                _directCalls--;
                if (_disposed && _directCalls == 0) Monitor.PulseAll(_pending);
            }
        }
    }

    internal T Invoke<T>(Func<T> action)
    {
        if (Environment.CurrentManagedThreadId == _mainThread)
        {
            lock (_pending) ObjectDisposedException.ThrowIf(_disposed, this);
            return action();
        }
        return Enqueue(action).GetAwaiter().GetResult();
    }

    internal Task<T> Enqueue<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending.Enqueue((() =>
            {
                try { completion.SetResult(action()); }
                catch (Exception exception) { completion.SetException(exception); }
            }, () => completion.SetCanceled()));
        }
        return completion.Task;
    }

    internal void Pump()
    {
        RequireMainThread();
        while (true)
        {
            (Action Execute, Action Cancel) request;
            lock (_pending)
            {
                if (!_pending.TryDequeue(out request)) return;
            }
            request.Execute();
        }
    }

    internal void RequireMainThread()
    {
        if (Environment.CurrentManagedThreadId != _mainThread)
            throw new InvalidOperationException("Godot 资源操作必须在主线程执行。");
    }

    public void Dispose()
    {
        lock (_pending)
        {
            _disposed = true;
            while (_pending.TryDequeue(out var request)) request.Cancel();
            // 这些调用不依赖主线程；返回前结束它们，防止引擎清理仍在使用的对象。
            while (_directCalls != 0) Monitor.Wait(_pending);
        }
    }
}
