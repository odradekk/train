
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
    public const string LogLevelEventId = "log_level";
    public const string LogLevelUsageEventId = "log_level_usage";

    private readonly TrainLogger _logger;
    private const string HelpUsage = "用法：help [命令]；省略参数查看全部命令，或输入命令名查看用法。";
    private const string LogLevelUsage = "用法：loglevel [debug|info|warning|error]；省略参数查询，或选择一种日志级别。";
    private readonly Dictionary<string, ConsoleCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    public ConsoleCommandDispatcher(
        TrainLogger logger,
        Action clear,
        Action quit)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clear);
        ArgumentNullException.ThrowIfNull(quit);
        _logger = logger;
        Register(new("help", "列出可用命令，或查看一条命令的用法", HelpUsage, [], Help));
        Register(new("clear", "清空控制台输出，保留磁盘日志", "用法：clear；输入 clear 清空输出。", [], _ => clear()));
        Register(new("quit", "退出游戏，未保存时先选择如何处理进度", "用法：quit；按保存提示选择保存、放弃或取消退出。", [], _ => quit()));
        Register(new("loglevel", "查看或设置日志级别，同时影响磁盘日志", LogLevelUsage,
            [new("debug", "记录全部级别"), new("info", "记录信息、警告和错误"),
             new("warning", "只记录警告和错误"), new("error", "只记录错误")], ApplyLogLevel));
    }

    internal void Register(ConsoleCommand command) => _commands.Add(command.Name, command);

    internal ConsoleInputGuide DescribeInput(string line, int caret)
    {
        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(line[start - 1])) start--;
        var end = caret;
        while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
        var before = ParsedConsoleCommand.Parse(line[..start]);
        var name = ParsedConsoleCommand.Parse(line).Name;
        _commands.TryGetValue(name, out var current);
        IEnumerable<ConsoleArgument> candidates = [];
        if (before.IsEmpty)
            candidates = _commands.Values.Select(command => new ConsoleArgument(command.Name, command.Description));
        else if (before.Arguments.Count == 0 && _commands.TryGetValue(before.Name, out var command))
            candidates = command.Name == "help"
                ? _commands.Values.Select(item => new ConsoleArgument(item.Name, item.Description)) : command.FirstArguments;
        var prefix = line[start..caret];
        return new(current?.Usage, candidates.Where(item => item.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray(),
            start, end - start);
    }

    public void Execute(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var parsed = ParsedConsoleCommand.Parse(line);
        if (parsed.IsEmpty)
            return;

        _logger.Info(Category, EchoEventId, line.Trim());
        if (_commands.TryGetValue(parsed.Name, out var command)) command.Execute(parsed.Arguments);
        else Unknown(parsed.Name);
    }

    private void Help(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
            _logger.Info(Category, HelpEventId, string.Join("\n", _commands.Values.Select(command => $"{command.Name}：{command.Description}")));
        else if (arguments.Count == 1 && _commands.TryGetValue(arguments[0], out var command))
            _logger.Info(Category, HelpEventId, $"{command.Description}\n{command.Usage}");
        else if (arguments.Count == 1) Unknown(arguments[0]);
        else _logger.Warning(Category, "help_usage", HelpUsage);
    }

    private void Unknown(string name)
    {
        var nearest = _commands.Keys.MinBy(command => EditDistance(name.ToLowerInvariant(), command));
        _logger.Warning(Category, UnknownEventId,
            $"没有「{name}」这条命令。最接近的命令是 {nearest}；输入 help 查看可用命令，或输入 help {nearest} 查看用法。");
    }

    private static int EditDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var row = 1; row <= left.Length; row++)
        {
            var current = new int[right.Length + 1];
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
                current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1),
                    previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1));
            previous = current;
        }
        return previous[^1];
    }

    private void ApplyLogLevel(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            _logger.Info(Category, LogLevelEventId, $"当前日志级别：{LogLevelText.ToSerialized(_logger.Threshold)}。{LogLevelUsage}");
            return;
        }

        if (arguments.Count == 1 && LogLevelText.TryParse(arguments[0], out var level))
        {
            // 提示按旧阈值记录；即使旧阈值隐藏了 Info，新的过滤等级仍会生效。
            _logger.Info(Category, LogLevelEventId, $"日志级别已设为 {LogLevelText.ToSerialized(level)}，磁盘日志同样生效。再次输入 loglevel 可查询当前级别。");
            _logger.Threshold = level;
            return;
        }

        _logger.Warning(Category, LogLevelUsageEventId, LogLevelUsage);
    }

}

internal sealed record ConsoleArgument(string Text, string Description);
internal sealed record ConsoleCommand(string Name, string Description, string Usage,
    IReadOnlyList<ConsoleArgument> FirstArguments, Action<IReadOnlyList<string>> Execute);
internal sealed record ConsoleInputGuide(string? Usage, IReadOnlyList<ConsoleArgument> Suggestions, int Start, int Length);
