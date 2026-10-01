using Train.Logging;

namespace Train;

public sealed class ParsedConsoleCommand
{
    public string Name { get; }
    public IReadOnlyList<string> Arguments { get; }
    public bool IsEmpty => Name.Length == 0;

    public ParsedConsoleCommand(string name, IReadOnlyList<string>? arguments = null)
    {
        Name = name;
        Arguments = arguments ?? Array.Empty<string>();
    }

    public static ParsedConsoleCommand Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return new ParsedConsoleCommand("");
        return new ParsedConsoleCommand(
            parts[0],
            parts.Length == 1 ? Array.Empty<string>() : parts[1..]);
    }
}

public sealed class ConsoleCommandDispatcher
{
    public const string Category = "console";
    public const string EchoEventId = "command_echo";
    public const string HelpEventId = "help";
    public const string UnknownEventId = "unknown_command";
    public const string ReloadDeniedEventId = "reload_denied";
    public const string LogLevelEventId = "log_level";
    public const string LogLevelUsageEventId = "log_level_usage";
    public const string VoxelEventId = "voxel";
    public const string VoxelUnavailableEventId = "voxel_unavailable";
    public const string CompositeEventId = "composite";
    public const string CompositeUnavailableEventId = "composite_unavailable";
    public const string RailEventId = "rail";
    public const string RailUnavailableEventId = "rail_unavailable";
    public const string ObserveEventId = "observe";
    public const string ObserveUnavailableEventId = "observe_unavailable";

    private readonly TrainLogger _logger;
    private readonly Action _clear;
    private readonly Action _quit;
    private readonly Action _reload;
    private readonly bool _allowReload;
    private readonly VoxelConsoleCommands? _voxelCommands;
    private readonly CompositeConsoleCommands? _compositeCommands;
    private readonly RailConsoleCommands? _railCommands;
    private readonly ObserveConsoleCommands? _observeCommands;

    public ConsoleCommandDispatcher(
        TrainLogger logger,
        Action clear,
        Action quit,
        Action reload,
        bool allowReload,
        VoxelConsoleCommands? voxelCommands = null,
        CompositeConsoleCommands? compositeCommands = null,
        RailConsoleCommands? railCommands = null,
        ObserveConsoleCommands? observeCommands = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clear);
        ArgumentNullException.ThrowIfNull(quit);
        ArgumentNullException.ThrowIfNull(reload);
        _logger = logger;
        _clear = clear;
        _quit = quit;
        _reload = reload;
        _allowReload = allowReload;
        _voxelCommands = voxelCommands;
        _compositeCommands = compositeCommands;
        _railCommands = railCommands;
        _observeCommands = observeCommands;
    }

    public void Execute(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var parsed = ParsedConsoleCommand.Parse(line);
        if (parsed.IsEmpty)
            return;

        _logger.Info(Category, EchoEventId, line.Trim());
        switch (parsed.Name.ToLowerInvariant())
        {
            case "help":
                _logger.Info(Category, HelpEventId, HelpMessage());
                return;
            case "clear":
                _clear();
                return;
            case "quit":
                _quit();
                return;
            case "reload":
                if (_allowReload)
                    _reload();
                else
                    _logger.Warning(Category, ReloadDeniedEventId, "reload is only available in debug builds.");
                return;
            case "loglevel":
                ApplyLogLevel(parsed.Arguments);
                return;
            case "voxel":
                if (_voxelCommands is null)
                    _logger.Warning(Category, VoxelUnavailableEventId, "Enter the voxel development scene before using voxel commands.");
                else
                    WriteResult(VoxelEventId, _voxelCommands.Execute(parsed.Arguments));
                return;
            case "composite":
                if (_compositeCommands is null)
                    _logger.Warning(Category, CompositeUnavailableEventId, "Enter the voxel development scene before using composite commands.");
                else
                    WriteResult(CompositeEventId, _compositeCommands.Execute(parsed.Arguments));
                return;
            case "rail":
                if (_railCommands is null)
                    _logger.Warning(Category, RailUnavailableEventId, "Enter the voxel development scene before using rail commands.");
                else
                    WriteResult(RailEventId, _railCommands.Execute(parsed.Arguments));
                return;
            case "observe":
                if (_observeCommands is null)
                    _logger.Warning(Category, ObserveUnavailableEventId, "请先进入世界编辑器再使用 observe 命令。");
                else
                    WriteResult(ObserveEventId, _observeCommands.Execute(parsed.Arguments));
                return;
            default:
                _logger.Warning(Category, UnknownEventId, $"Unknown command: {parsed.Name}");
                return;
        }
    }

    private void WriteResult(string eventId, string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            _logger.Info(Category, eventId, line);
    }

    private void ApplyLogLevel(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            _logger.Info(
                Category,
                LogLevelEventId,
                $"Current log level: {LogLevelText.ToSerialized(_logger.Threshold)}.");
            return;
        }

        if (arguments.Count == 1 && LogLevelText.TryParse(arguments[0], out var level))
        {
            _logger.Info(
                Category,
                LogLevelEventId,
                $"Log level set to {LogLevelText.ToSerialized(level)}.");
            _logger.Threshold = level;
            return;
        }

        _logger.Warning(
            Category,
            LogLevelUsageEventId,
            "Usage: loglevel [debug|info|warning|error]");
    }

    private string HelpMessage()
    {
        var commands = _allowReload
            ? "help, clear, quit, reload, loglevel, voxel, composite, rail, observe"
            : "help, clear, quit, loglevel, voxel, composite, rail, observe";
        return $"Available commands: {commands}.";
    }
}
