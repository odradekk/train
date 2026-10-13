
using Train.App.Logging;

namespace Train.App.Console;

public sealed class ParsedConsoleCommand
{
    public string Name { get; }
    public IReadOnlyList<string> Arguments { get; }
    public bool IsEmpty => Name.Length == 0;

    private ParsedConsoleCommand(string name, string[]? arguments = null)
    {
        Name = name;
        Arguments = Array.AsReadOnly(arguments ?? Array.Empty<string>());
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
    public const string LightingDeniedEventId = "lighting_denied";
    public const string LogLevelEventId = "log_level";
    public const string LogLevelUsageEventId = "log_level_usage";

    private readonly TrainLogger _logger;
    private readonly Action _clear;
    private readonly Action _quit;
    private readonly Action _reload;
    private readonly bool _allowReload;
    private readonly Action<IReadOnlyList<string>>? _simulation;
    private readonly Action<IReadOnlyList<string>>? _lighting;
    private readonly Action<IReadOnlyList<string>>? _development;

    public ConsoleCommandDispatcher(
        TrainLogger logger,
        Action clear,
        Action quit,
        Action reload,
        bool allowReload,
        Action<IReadOnlyList<string>>? simulation = null,
        Action<IReadOnlyList<string>>? lighting = null,
        Action<IReadOnlyList<string>>? development = null)
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
        _simulation = simulation;
        _lighting = lighting;
        _development = development;
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
            case "sim" when _simulation is not null:
                _simulation(parsed.Arguments);
                return;
            case "dev" when _development is not null:
                _development(parsed.Arguments);
                return;
            case "lighting":
                if (_lighting is not null)
                    _lighting(parsed.Arguments);
                else
                    _logger.Warning(Category, LightingDeniedEventId, "lighting 仅在 Debug 构建中可用。");
                return;
            default:
                _logger.Warning(Category, UnknownEventId, $"Unknown command: {parsed.Name}");
                return;
        }
    }

    private void ApplyLogLevel(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            _logger.Info(Category, LogLevelEventId, $"Current log level: {LogLevelText.ToSerialized(_logger.Threshold)}.");
            return;
        }

        if (arguments.Count == 1 && LogLevelText.TryParse(arguments[0], out var level))
        {
            // 提示按旧阈值记录；即使旧阈值隐藏了 Info，新的过滤等级仍会生效。
            _logger.Info(Category, LogLevelEventId, $"Log level set to {LogLevelText.ToSerialized(level)}.");
            _logger.Threshold = level;
            return;
        }

        _logger.Warning(Category, LogLevelUsageEventId, "Usage: loglevel [debug|info|warning|error]");
    }

    private string HelpMessage()
    {
        var commands = _allowReload ? "help, clear, quit, reload, loglevel" : "help, clear, quit, loglevel";
        if (_simulation is not null)
            commands += ", sim";
        if (_lighting is not null)
            commands += ", lighting";
        if (_development is not null)
            commands += ", dev";
        return $"Available commands: {commands}.";
    }
}
