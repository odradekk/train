using Godot;
using Train.Logging;

namespace Train;

public partial class Main : Node
{
    private TrainLogger? _logger;

    public override void _Ready()
    {
        var logDirectory = Path.Combine(OS.GetUserDataDir(), "logs");
        var buildDefault = OS.IsDebugBuild() || Engine.IsEditorHint()
            ? LogLevel.Debug
            : LogLevel.Info;
        _logger = TrainLogger.StartSession(logDirectory, buildDefault, new GodotLogOutput());
    }

    public override void _Process(double delta)
    {
        _logger?.DrainGodotQueue();
    }

    public override void _ExitTree()
    {
        _logger?.Dispose();
        _logger = null;
    }
}
