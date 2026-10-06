// Train.App.Checks 验证真实应用中的工作线程日志输出。
// 检查主线程逐帧取出队列，持久日志的释放仍由应用生命周期完成。

using Godot;
using Train.Core.Logging;

namespace Train.App.Checks;

/// <summary>用一个工作线程事件验证日志队列经过应用主线程输出。</summary>
/// <example>工作线程打印 smoke_worker_event 后，主线程报告 SMOKE_PROCESS_DRAINED。</example>
internal sealed class LoggingCheck
{
    private readonly Thread _worker;
    // volatile 使主线程只在工作线程完成入队后检查取出结果。
    private volatile bool _workerLogged;
    private bool _drained;

    /// <summary>启动仅写入一条检查日志的线程，不在工作线程访问 Godot。</summary>
    public LoggingCheck(TrainLogger logger)
    {
        _worker = new Thread(() =>
        {
            logger.Info("test", "smoke_worker_event", "queued from background");
            _workerLogged = true;
        });
        _worker.Start();
    }

    /// <summary>观察应用本帧取出的队列；工作线程尚未结束时继续等待。</summary>
    /// <param name="drained">应用本帧交给 Godot 输出的事件数量。</param>
    /// <returns>尚未入队时为 null，完成后表示是否确实通过主线程输出。</returns>
    public bool? ObserveDrain(int drained)
    {
        _drained |= drained > 0;
        if (!_workerLogged)
            return null;
        if (_drained)
            GD.Print("SMOKE_PROCESS_DRAINED");
        // 工作线程可能刚在本帧 Drain 之后入队；等下一帧取出，不能提前判失败。
        return _drained ? true : null;
    }

    public void Join() => _worker.Join();
}
