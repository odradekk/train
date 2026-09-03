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

    private readonly TrainLogger _logger;
    private readonly Action _clear;
    private readonly Action _quit;
    private readonly Action _reload;
    private readonly bool _allowReload;

    public ConsoleCommandDispatcher(
        TrainLogger logger,
        Action clear,
        Action quit,
        Action reload,
        bool allowReload)
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
            default:
                _logger.Warning(Category, UnknownEventId, $"Unknown command: {parsed.Name}");
                return;
        }
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
            ? "help, clear, quit, reload, loglevel"
            : "help, clear, quit, loglevel";
        return $"Available commands: {commands}.";
    }
}
