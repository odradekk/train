using Godot;

namespace Train.Logging;

public sealed class GodotLogOutput : IGodotLogOutput
{
    public void Write(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var line = LogLevelText.FormatBounded(
            logEvent.Level,
            logEvent.Category,
            logEvent.EventId,
            logEvent.Message);
        switch (logEvent.Level)
        {
            case LogLevel.Warning:
                GD.PushWarning(line);
                break;
            case LogLevel.Error:
                GD.PushError(line);
                break;
            default:
                GD.Print(line);
                break;
        }
    }
}
