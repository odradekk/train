
using Godot;
using Train.App.Logging;

namespace Train.App.Checks;

internal sealed class LoggingCheck
{
    private readonly Thread _worker;
    private volatile bool _workerLogged;
    private bool _drained;

    public LoggingCheck(TrainLogger logger)
    {
        _worker = new Thread(() =>
        {
            logger.Info("test", "smoke_worker_event", "queued from background");
            _workerLogged = true;
        });
        _worker.Start();
    }

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
