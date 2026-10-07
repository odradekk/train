// 属于控制台模块。
// 解析输入并分发基础命令，通过日志和宿主回调提供结果，不访问世界或界面。

using Train.Core.Logging;

namespace Train.Core.Console;

/// <summary>表示按空白分词得到的命令名与有序参数，不解释引号或参数的领域含义。</summary>
/// <remarks>命令名保留大小写；参数按输入顺序保存，创建后不可修改。</remarks>
/// <example><c>say "hello world"</c> 得到命令名 say 和两个带引号字符的参数；见 <c>ConsoleCommandParseTests.Parse_takes_the_first_whitespace_token_without_quote_escaping</c>。</example>
public sealed class ParsedConsoleCommand
{
    /// <summary>获取命令名；空字符串表示没有命令，Parse 保留原输入的大小写。</summary>
    public string Name { get; }
    /// <summary>获取按输入顺序保留的不可变参数列表；没有参数时为空列表。</summary>
    public IReadOnlyList<string> Arguments { get; }
    /// <summary>获取命令名是否为空；不检查参数列表。</summary>
    public bool IsEmpty => Name.Length == 0;

    /// <summary>保存分词得到的命令名，并将内部参数数组包装为只读列表。</summary>
    private ParsedConsoleCommand(string name, string[]? arguments = null)
    {
        Name = name;
        Arguments = Array.AsReadOnly(arguments ?? Array.Empty<string>());
    }

    /// <summary>按空白分隔输入，取首个词为命令名，其余词为参数。</summary>
    /// <param name="line">非 null 的一行输入；空行或只有空白时得到空命令。</param>
    /// <returns>命令名与有序参数；连续空白被忽略，引号、分号和注释符号作为普通字符保留。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> 为 null。</exception>
    /// <example><c>  reload   core&#9;mods  </c>（core 与 mods 之间为制表符）得到 reload 与 core、mods；见 <c>ConsoleCommandParseTests.Parse_takes_the_first_whitespace_token_without_quote_escaping</c>。</example>
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

/// <summary>分发 help、clear、quit、reload 和 loglevel，将日志输出与宿主动作分开。</summary>
/// <remarks>命令名不区分大小写；reload 的可用性由宿主在构造时指定。所有宿主动作同步执行，分发器不捕获回调异常，也不持有世界会话。</remarks>
/// <example>clear 先记录回显，再调用宿主清空缓冲；持久日志中的回显仍保留，见 <c>ConsoleCommandDispatchTests.Clear_echoes_then_empties_the_ring_buffer_but_keeps_persistent_echo</c>。</example>
public sealed class ConsoleCommandDispatcher
{
    /// <summary>基础控制台日志的分类，用于从共享日志中识别命令事件。</summary>
    public const string Category = "console";
    /// <summary>非空输入在分发前记录的回显事件标识。</summary>
    public const string EchoEventId = "command_echo";
    /// <summary>help 输出可用命令列表的事件标识。</summary>
    public const string HelpEventId = "help";
    /// <summary>未知命令警告的事件标识。</summary>
    public const string UnknownEventId = "unknown_command";
    /// <summary>宿主禁止 reload 时记录的警告事件标识。</summary>
    public const string ReloadDeniedEventId = "reload_denied";
    /// <summary>查询或设置日志等级时记录提示的事件标识。</summary>
    public const string LogLevelEventId = "log_level";
    /// <summary>loglevel 参数无效时记录用法警告的事件标识。</summary>
    public const string LogLevelUsageEventId = "log_level_usage";

    private readonly TrainLogger _logger;
    private readonly Action _clear;
    private readonly Action _quit;
    private readonly Action _reload;
    private readonly bool _allowReload;

    /// <summary>将基础命令绑定到共享日志和宿主提供的动作。</summary>
    /// <param name="logger">非 null 的共享日志；过滤等级决定回显和结果是否可见。</param>
    /// <param name="clear">清空控制台内存缓冲的非 null 回调；不由分发器删除日志文件。</param>
    /// <param name="quit">处理退出请求的非 null 回调；退出或存档确认由宿主负责。</param>
    /// <param name="reload">重载内容的非 null 回调；只在 allowReload 为 true 时执行。</param>
    /// <param name="allowReload">是否允许 reload 并将其列入 help；Godot 宿主使用构建类型决定此值。</param>
    /// <exception cref="ArgumentNullException">任一日志或回调参数为 null。</exception>
    /// <example>禁止 reload 时只记录警告，不调用回调；见 <c>ConsoleCommandDispatchTests.Reload_is_denied_outside_debug_and_unknown_commands_warn</c>。</example>
    public ConsoleCommandDispatcher(TrainLogger logger, Action clear, Action quit, Action reload, bool allowReload)
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

    /// <summary>解析一行输入，记录非空命令回显，再执行对应基础命令。</summary>
    /// <param name="line">非 null 的输入；空白行无日志或动作。</param>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> 为 null。</exception>
    /// <remarks>help、clear、quit 和 reload 忽略附带参数；loglevel 只接受零个参数或一个合法等级。未知命令记录 Warning，不调用宿主动作；宿主回调异常原样传播。</remarks>
    /// <example><c>LOGLEVEL Warning</c> 先按旧过滤等级记录提示，再将阈值改为 Warning；见 <c>ConsoleCommandDispatchTests.Loglevel_with_named_level_updates_the_session_threshold</c>。</example>
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

    /// <summary>查询当前过滤等级，或在一个合法等级参数下更新共享日志阈值；无效参数只记录用法警告。</summary>
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

    /// <summary>生成基础命令列表；只有允许重载时才列出 reload。</summary>
    private string HelpMessage()
    {
        var commands = _allowReload ? "help, clear, quit, reload, loglevel" : "help, clear, quit, loglevel";
        return $"Available commands: {commands}.";
    }
}
