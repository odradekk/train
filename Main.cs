using Godot;
using Train.Logging;

namespace Train;

public partial class Main : Node
{
    private const string LoggingSmokeEnvironmentVariable = "TRAIN_LOG_SMOKE";
    private const string LoggingSmokeWorkerEventId = "smoke_worker_event";
    private const string LoggingSmokeProcessMarker = "SMOKE_PROCESS_DRAINED";
    private const string LoggingSmokeDisposedMarker = "SMOKE_LOGGER_DISPOSED";

    private TrainLogger? _logger;
    private bool _smoke;
    private volatile bool _smokeWorkerLogged;
    private bool _smokeDrainedFromProcess;
    private bool _smokeQuitRequested;

    public override void _Ready()
    {
        var logDirectory = Path.Combine(OS.GetUserDataDir(), "logs");
        var buildDefault = OS.IsDebugBuild() || Engine.IsEditorHint()
            ? LogLevel.Debug
            : LogLevel.Info;
        _logger = TrainLogger.StartSession(logDirectory, buildDefault, new GodotLogOutput());
        if (IsLoggingSmokeEnabled())
            StartLoggingSmoke();
    }

    public override void _Process(double delta)
    {
        var drained = _logger?.DrainGodotQueue() ?? 0;
        if (_smoke && drained > 0)
            _smokeDrainedFromProcess = true;
        if (_smoke && _smokeWorkerLogged && !_smokeQuitRequested)
        {
            if (_smokeDrainedFromProcess)
                GD.Print(LoggingSmokeProcessMarker);
            _smokeQuitRequested = true;
            GetTree().Quit();
        }
    }

    public override void _ExitTree()
    {
        _logger?.Dispose();
        _logger = null;
        if (_smoke)
            GD.Print(LoggingSmokeDisposedMarker);
    }

    private static bool IsLoggingSmokeEnabled() =>
        string.Equals(System.Environment.GetEnvironmentVariable(LoggingSmokeEnvironmentVariable), "1", StringComparison.Ordinal);

    private void StartLoggingSmoke()
    {
        _smoke = true;
        var logger = _logger!;
        new Thread(() =>
        {
            logger.Info("test", LoggingSmokeWorkerEventId, "queued from background");
            _smokeWorkerLogged = true;
        }).Start();
    }
}
