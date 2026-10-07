// 属于日志模块，定义诊断日志事件数据及其人类可读表示。
// 同时集中定义日志生产方与接收器共用的验证和规范化规则。

using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Train.Core.Logging;

/// <summary>
/// 标识诊断日志事件的日志级别，按从诊断细节到失败的顺序排列。
/// </summary>
/// <example><see cref="LogLevel.Warning"/> 表示需要关注但尚未报告错误的情况。</example>
public enum LogLevel
{
    /// <summary>调查行为时使用的诊断细节。</summary>
    Debug = 0,

    /// <summary>正常运行信息。</summary>
    Info = 1,

    /// <summary>需要关注且可恢复的情况。</summary>
    Warning = 2,

    /// <summary>操作失败或其他错误情况。</summary>
    Error = 3
}

/// <summary>
/// 表示日志会话期间发出的一条结构化诊断日志事件。
/// </summary>
/// <example>
/// 一条事件可以记录类别 <c>content</c>、事件 ID <c>ready</c>、序号 1 和消息 <c>Ready.</c>。
/// </example>
public sealed class LogEvent
{
    /// <summary>获取事件创建时刻，包括其 UTC 偏移量。</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// 获取传入的日志会话标识符；直接构造事件时，<see cref="Guid.Empty"/> 没有特殊含义。
    /// </summary>
    public Guid SessionId { get; }

    /// <summary>
    /// 获取传入的事件在会话中的位置。<see cref="TrainLogger"/> 从 1 开始分配正值；
    /// 调用方直接构造事件时，零和负值没有特殊含义。
    /// </summary>
    public long Sequence { get; }

    /// <summary>获取传入的事件日志级别；直接构造不会拒绝未定义的枚举值。</summary>
    public LogLevel Level { get; }

    /// <summary>获取传入的子系统或功能名称；直接构造事件的调用方负责保证其有效。</summary>
    public string Category { get; }

    /// <summary>获取传入的机器可读事件标识符；直接构造事件的调用方负责保证其有效。</summary>
    public string EventId { get; }

    /// <summary>获取传入的人类可读事件描述；直接构造事件的调用方负责提供非空值。</summary>
    public string Message { get; }

    /// <summary>
    /// 获取传入的结构化值；空字典表示没有结构化值。直接构造会保留原始字典引用。
    /// </summary>
    public IReadOnlyDictionary<string, object?> Attributes { get; }

    /// <summary>获取捕获的异常详情；事件未报告异常时为 <see langword="null"/>。</summary>
    public LogExceptionData? Exception { get; }

    /// <summary>
    /// 不经验证或防御性复制，直接赋入调用方准备的值以创建诊断日志事件。
    /// </summary>
    /// <param name="timestamp">事件创建时刻，包括其 UTC 偏移量。</param>
    /// <param name="sessionId">同一日志会话中各事件共享的标识符。</param>
    /// <param name="sequence">事件在会话中的位置；任何 <see cref="long"/> 值都会原样赋入。</param>
    /// <param name="level">事件的日志级别。</param>
    /// <param name="category">发出事件的子系统或功能。</param>
    /// <param name="eventId">事件种类的稳定机器可读标识符。</param>
    /// <param name="message">人类可读的事件描述。</param>
    /// <param name="attributes">事件附带的结构化值；保留原始引用，空字典表示没有结构化值。</param>
    /// <param name="exception">捕获的异常详情；事件未报告异常时为 <see langword="null"/>。</param>
    /// <example>
    /// <c>new LogEvent(timestamp, Guid.Empty, 1, LogLevel.Info, "content", "ready", "Ready.", attributes, null)</c>
    /// 创建 <c>HumanLogFormatTests</c> 使用的事件。
    /// </example>
    public LogEvent(
        DateTimeOffset timestamp,
        Guid sessionId,
        long sequence,
        LogLevel level,
        string category,
        string eventId,
        string message,
        IReadOnlyDictionary<string, object?> attributes,
        LogExceptionData? exception)
    {
        Timestamp = timestamp;
        SessionId = sessionId;
        Sequence = sequence;
        Level = level;
        Category = category;
        EventId = eventId;
        Message = message;
        Attributes = attributes;
        Exception = exception;
    }
}

/// <summary>
/// 表示从异常及其有界内部异常链中捕获的可序列化诊断详情。
/// </summary>
/// <example>
/// 捕获消息为 <c>root</c> 的 <see cref="ApplicationException"/> 时，会记录该类型和消息，但不记录其 <see cref="Exception.Data"/> 条目。
/// </example>
public sealed class LogExceptionData
{
    /// <summary>从单条异常链中最多保留的内部异常数。</summary>
    /// <seealso href="README.md#限制">日志限制及其决策依据。</seealso>
    public const int MaxInnerExceptions = 8;

    /// <summary>
    /// 获取传入的异常类型名称。<see cref="From(Exception)"/> 会在可用时采用完全限定的运行时名称；
    /// 直接构造不执行验证。
    /// </summary>
    public string Type { get; }

    /// <summary>获取传入的异常消息；空字符串表示没有消息文本，直接构造不执行验证。</summary>
    public string Message { get; }

    /// <summary>获取捕获的堆栈跟踪；异常没有堆栈跟踪时为 <see langword="null"/>。</summary>
    public string? StackTrace { get; }

    /// <summary>
    /// 获取传入的内部异常，按从近到远的顺序排列；空列表表示没有内部异常。
    /// 直接构造会保留原始列表引用，且不强制执行 <see cref="MaxInnerExceptions"/>。
    /// </summary>
    public IReadOnlyList<LogExceptionData> InnerExceptions { get; }

    /// <summary>不经验证或防御性复制，直接赋入调用方准备的值以创建异常数据。</summary>
    /// <param name="type">异常的运行时类型名称。</param>
    /// <param name="message">异常消息。</param>
    /// <param name="stackTrace">堆栈跟踪；没有可用值时为 <see langword="null"/>。</param>
    /// <param name="innerExceptions">捕获的内部异常，按从近到远的顺序排列；保留原始引用且不应用捕获上限。</param>
    /// <example>
    /// <c>new LogExceptionData("System.ApplicationException", "root", null, Array.Empty&lt;LogExceptionData&gt;())</c>
    /// 表示未捕获堆栈跟踪或内部异常的异常。
    /// </example>
    public LogExceptionData(
        string type,
        string message,
        string? stackTrace,
        IReadOnlyList<LogExceptionData> innerExceptions)
    {
        Type = type;
        Message = message;
        StackTrace = stackTrace;
        InnerExceptions = innerExceptions;
    }

    /// <summary>
    /// 捕获可序列化的异常详情，并从单条内部异常链中最多捕获 <see cref="MaxInnerExceptions"/> 个条目。
    /// </summary>
    /// <param name="exception">要捕获其类型、消息、堆栈跟踪和内部异常链的异常。</param>
    /// <returns>不包含任意 <see cref="Exception.Data"/> 条目的异常数据。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> 为 <see langword="null"/>。</exception>
    /// <example>
    /// 有关省略第九个内部异常的异常链，请参见 <c>StructuredLoggingTests.Exception_shape_is_bounded_and_omits_arbitrary_data</c>。
    /// </example>
    public static LogExceptionData From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var inners = new List<LogExceptionData>();
        var inner = exception.InnerException;
        for (var i = 0; i < MaxInnerExceptions && inner is not null; i++)
        {
            inners.Add(Capture(inner, Array.Empty<LogExceptionData>()));
            inner = inner.InnerException;
        }

        return Capture(exception, inners);
    }

    /// <summary>复制选定的异常字段，并有意排除无界的异常数据。</summary>
    private static LogExceptionData Capture(Exception exception, IReadOnlyList<LogExceptionData> inners) =>
        new(
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            exception.StackTrace,
            inners);
}

/// <summary>
/// 验证日志类别和事件种类使用的小写标识符。
/// </summary>
/// <example><c>ready</c> 有效；<c>Ready</c> 因以大写字母开头而被拒绝。</example>
internal static class LogEventKey
{
    /// <summary>类别的最大长度，以 UTF-16 代码单元计。</summary>
    /// <seealso href="README.md#限制">日志限制及其决策依据。</seealso>
    public const int CategoryMaxLength = 64;

    /// <summary>事件 ID 的最大长度，以 UTF-16 代码单元计。</summary>
    /// <seealso href="README.md#限制">日志限制及其决策依据。</seealso>
    public const int EventIdMaxLength = 100;

    /// <summary>检查标识符长度和小写 ASCII 键语法后返回该标识符。</summary>
    /// <param name="value">要验证的标识符。</param>
    /// <param name="paramName">验证异常中包含的参数名称。</param>
    /// <param name="maxLength">允许的最大长度，以 UTF-16 代码单元计；最小长度为一。</param>
    /// <returns>有效时返回原始标识符。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> 为空、超过 <paramref name="maxLength"/>、未以小写 ASCII 字母开头，
    /// 或包含小写 ASCII 字母、数字和下划线以外的字符。
    /// </exception>
    public static string Validate(string value, string paramName, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (value.Length is < 1 || value.Length > maxLength)
            throw new ArgumentException($"Must be 1-{maxLength} characters.", paramName);
        if (value[0] is < 'a' or > 'z')
            throw new ArgumentException("Must start with a lowercase ASCII letter.", paramName);

        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
                continue;
            throw new ArgumentException("Must contain only lowercase letters, digits, or underscores.", paramName);
        }

        return value;
    }
}

/// <summary>
/// 将调用方提供的日志属性转换为结构化接收器接受的有界值图。
/// </summary>
/// <example>
/// 会保留 <c>4</c>、<c>1.5</c>、<c>true</c> 和嵌套的字符串键字典等值；非有限数会被拒绝。
/// </example>
internal static class LogAttributes
{
    /// <summary>事件没有属性时使用的共享只读表示。</summary>
    internal static readonly IReadOnlyDictionary<string, object?> Empty =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    /// <summary>规范化属性图时接受的最大集合嵌套深度。</summary>
    /// <remarks>此实现保护可防止不受信任的值图引发无界递归规范化。</remarks>
    /// <seealso href="README.md#限制">日志限制及其决策依据。</seealso>
    private const int MaxDepth = 16;

    /// <summary>返回独立的只读属性图，其中仅包含日志格式支持的值。</summary>
    /// <param name="attributes">要规范化的属性；未提供属性时为 <see langword="null"/>。</param>
    /// <returns>只读副本；输入为 <see langword="null"/> 或空时返回共享空字典。</returns>
    /// <exception cref="ArgumentNullException">值图中的字典包含 <see langword="null"/> 键。</exception>
    /// <exception cref="ArgumentException">
    /// 值不受支持、浮点数不是有限数、字典键不是字符串，
    /// 或值图超过支持的嵌套深度。
    /// </exception>
    /// <example>
    /// <c>StructuredLoggingTests.Attributes_keep_json_scalars_and_reject_object_graphs</c> 测试会规范化 <c>count=4</c>、
    /// <c>ratio=1.5</c> 和 <c>flags=[true, false]</c>，同时拒绝任意对象。
    /// </example>
    public static IReadOnlyDictionary<string, object?> Normalize(
        IReadOnlyDictionary<string, object?>? attributes)
    {
        if (attributes is null || attributes.Count == 0)
            return Empty;

        var copy = new Dictionary<string, object?>(attributes.Count, StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            ArgumentNullException.ThrowIfNull(key, nameof(attributes));
            copy[key] = NormalizeValue(value, nameof(attributes), 0);
        }

        return new ReadOnlyDictionary<string, object?>(copy);
    }

    /// <summary>递归复制一个受支持的值，同时跟踪深度以限制恶意值图。</summary>
    private static object? NormalizeValue(object? value, string paramName, int depth)
    {
        switch (value)
        {
            case null:
            case string:
            case bool:
            case sbyte:
            case byte:
            case short:
            case ushort:
            case int:
            case uint:
            case long:
            case ulong:
                return value;
            case float f:
                if (!float.IsFinite(f))
                    throw new ArgumentException("Floating-point attributes must be finite.", paramName);
                return value;
            case double d:
                if (!double.IsFinite(d))
                    throw new ArgumentException("Floating-point attributes must be finite.", paramName);
                return value;
        }

        if (depth >= MaxDepth)
            throw new ArgumentException("Attribute graphs exceed the supported nesting depth.", paramName);

        if (TryAsReadOnlyDictionary(value, out var dictionary))
        {
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, item) in dictionary)
            {
                ArgumentNullException.ThrowIfNull(key, paramName);
                copy[key] = NormalizeValue(item, paramName, depth + 1);
            }

            return new ReadOnlyDictionary<string, object?>(copy);
        }

        if (TryAsReadOnlyList(value, out var list))
        {
            var copy = new List<object?>(list.Count);
            foreach (var item in list)
                copy.Add(NormalizeValue(item, paramName, depth + 1));
            return new ReadOnlyCollection<object?>(copy);
        }

        throw new ArgumentException(
            $"Attribute values must be null, strings, booleans, integers, finite floats, or read-only lists and string-keyed dictionaries of those types. Rejected: {value.GetType().FullName}.",
            paramName);
    }

    /// <summary>具体化泛型只读列表，且不将字符串视为字符集合。</summary>
    private static bool TryAsReadOnlyList(object value, out IReadOnlyList<object?> list)
    {
        if (value is string)
        {
            list = null!;
            return false;
        }

        if (!ImplementsGeneric(value, typeof(IReadOnlyList<>), out _))
        {
            list = null!;
            return false;
        }

        var copy = new List<object?>();
        foreach (var item in (IEnumerable)value)
            copy.Add(item);
        list = copy;
        return true;
    }

    /// <summary>强制要求字符串键后，具体化泛型只读字典。</summary>
    private static bool TryAsReadOnlyDictionary(object value, out List<KeyValuePair<string, object?>> pairs)
    {
        if (!ImplementsGeneric(value, typeof(IReadOnlyDictionary<,>), out var iface))
        {
            pairs = null!;
            return false;
        }

        var args = iface.GetGenericArguments();
        if (args[0] != typeof(string))
            throw new ArgumentException("Attribute dictionaries must use string keys.", nameof(value));

        pairs = new List<KeyValuePair<string, object?>>();
        foreach (var item in (IEnumerable)value)
        {
            var itemType = item!.GetType();
            var key = (string?)itemType.GetProperty("Key")!.GetValue(item);
            var itemValue = itemType.GetProperty("Value")!.GetValue(item);
            pairs.Add(new KeyValuePair<string, object?>(key!, itemValue));
        }

        return true;
    }

    /// <summary>在值的具体类型或已实现接口中查找所需的泛型协定。</summary>
    private static bool ImplementsGeneric(object value, Type generic, out Type iface)
    {
        var type = value.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == generic)
        {
            iface = type;
            return true;
        }

        foreach (var candidate in type.GetInterfaces())
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == generic)
            {
                iface = candidate;
                return true;
            }
        }

        iface = null!;
        return false;
    }
}

/// <summary>
/// 定义用于区分控制台日志元数据与日志级别的 Godot 富文本颜色。
/// </summary>
/// <remarks>这些固定诊断样式源自人类可读格式协定，而非经测量得出的玩法调优值。</remarks>
/// <seealso href="README.md#限制">日志限制及其决策依据。</seealso>
/// <example>错误消息使用 <see cref="Error"/>，其时间戳则使用 <see cref="Dim"/>。</example>
internal static class LogConsoleColors
{
    /// <summary>应用于时间戳、类别和事件 ID 的颜色。</summary>
    public const string Dim = "#7f7f7f";

    /// <summary>应用于错误日志级别和消息的颜色。</summary>
    public const string Error = "#ff6b68";

    /// <summary>应用于警告日志级别和消息的颜色。</summary>
    public const string Warning = "#e5c07b";

    /// <summary>应用于调试日志级别和消息的颜色。</summary>
    public const string Debug = "#7f7f7f";

    /// <summary>应用于控制台命令回显消息的颜色。</summary>
    public const string CommandEcho = "#56b6c2";
}

/// <summary>
/// 将日志级别和事件转换为稳定的序列化名称与人类可读的控制台文本。
/// </summary>
/// <example><see cref="LogLevel.Warning"/> 序列化为 <c>warning</c>，并显示为固定宽度的 <c>WARN </c>。</example>
public static class LogLevelText
{
    /// <summary>控制台消息采用命令回显颜色呈现的事件 ID。</summary>
    /// <seealso href="README.md#固定标识符"/>
    public const string CommandEchoEventId = "command_echo";

    /// <summary>有界行从消息中保留的最大 UTF-16 代码单元数。</summary>
    /// <seealso href="README.md#限制">日志限制及其决策依据。</seealso>
    private const int MaxFormattedMessageLength = 200;

    /// <summary>返回为指定日志级别写入结构化日志的小写名称。</summary>
    /// <param name="level">要序列化的日志级别。</param>
    /// <returns><c>debug</c>、<c>info</c>、<c>warning</c> 或 <c>error</c>。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> 不是已定义的 <see cref="LogLevel"/> 值。</exception>
    /// <example><c>ToSerialized(LogLevel.Warning)</c> 返回 <c>warning</c>。</example>
    public static string ToSerialized(LogLevel level) => level switch
    {
        LogLevel.Debug => "debug",
        LogLevel.Info => "info",
        LogLevel.Warning => "warning",
        LogLevel.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown log level.")
    };

    /// <summary>返回人类可读日志中显示的固定宽度大写标签。</summary>
    /// <param name="level">要显示的日志级别。</param>
    /// <returns>五个 UTF-16 代码单元的标签；较短的名称包含尾随空格。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> 不是已定义的 <see cref="LogLevel"/> 值。</exception>
    /// <example><c>ToDisplayName(LogLevel.Info)</c> 返回 <c>INFO </c>。</example>
    public static string ToDisplayName(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown log level.")
    };

    /// <summary>尝试解析日志级别名称，忽略两端空白和字母大小写。</summary>
    /// <param name="value">候选日志级别名称，或 <see langword="null"/>。</param>
    /// <param name="level">解析得到的日志级别；解析失败时为 <see cref="LogLevel.Debug"/>。</param>
    /// <returns>对于 <c>debug</c>、<c>info</c>、<c>warning</c> 或 <c>error</c> 返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    /// <example><c>TryParse(" warning ", out var level)</c> 返回 <see langword="true"/>，并将 <c>level</c> 设为 <see cref="LogLevel.Warning"/>。</example>
    public static bool TryParse(string? value, out LogLevel level)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "debug":
                level = LogLevel.Debug;
                return true;
            case "info":
                level = LogLevel.Info;
                return true;
            case "warning":
                level = LogLevel.Warning;
                return true;
            case "error":
                level = LogLevel.Error;
                return true;
            default:
                level = default;
                return false;
        }
    }

    /// <summary>将事件格式化为人类可读的一行，并将消息截取至固定上限。</summary>
    /// <param name="logEvent">要格式化其时间戳、日志级别、标识、消息和属性的事件。</param>
    /// <returns>
    /// 包含精确到毫秒的本地时钟时间的一行；仅消息限制为
    /// <see cref="MaxFormattedMessageLength"/> 个 UTF-16 代码单元，因此完整行可能更长。
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="logEvent"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException">事件具有未定义的 <see cref="LogLevel"/> 值。</exception>
    /// <example>
    /// 有关将 201 个 UTF-16 代码单元的消息截取至 200、同时在消息后保留其 <c>kept=1</c> 属性的情况，
    /// 请参见 <c>HumanLogFormatTests.Empty_attributes_do_not_append_and_messages_truncate_at_200</c>。
    /// </example>
    public static string FormatBounded(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        return FormatBounded(
            logEvent.Level,
            logEvent.Category,
            logEvent.EventId,
            logEvent.Message,
            logEvent.Timestamp,
            logEvent.Attributes);
    }

    /// <summary>使用当前本地时间且不附带属性来格式化传入值。</summary>
    /// <param name="level">事件的日志级别。</param>
    /// <param name="category">子系统或功能名称；<see langword="null"/> 会生成空类别段。</param>
    /// <param name="eventId">稳定的事件标识符；<see langword="null"/> 会生成空事件 ID 段。</param>
    /// <param name="message">人类可读描述；<see langword="null"/> 和空字符串会省略消息段。</param>
    /// <returns>使用当前本地时钟时间的人类可读行。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> 不是已定义的 <see cref="LogLevel"/> 值。</exception>
    /// <example><c>FormatBounded(LogLevel.Info, "content", "ready", "Ready.")</c> 以 <c>Ready.</c> 结尾。</example>
    public static string FormatBounded(LogLevel level, string? category, string? eventId, string? message) =>
        FormatBounded(level, category, eventId, message, DateTimeOffset.Now, null);

    /// <summary>将传入的事件值和属性格式化为人类可读的一行。</summary>
    /// <param name="level">事件的日志级别。</param>
    /// <param name="category">子系统或功能名称；<see langword="null"/> 会生成空类别段。</param>
    /// <param name="eventId">稳定的事件标识符；<see langword="null"/> 会生成空事件 ID 段。</param>
    /// <param name="message">人类可读描述；<see langword="null"/> 和空字符串会省略消息段。</param>
    /// <param name="timestamp">事件时间；以本地时钟时间显示到毫秒，不含日期或偏移量。</param>
    /// <param name="attributes">按枚举顺序追加的值；没有值时为 <see langword="null"/>。</param>
    /// <returns>
    /// 消息限制为 <see cref="MaxFormattedMessageLength"/> 个 UTF-16 代码单元的一行；
    /// 其前缀和属性不计入此上限。
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> 不是已定义的 <see cref="LogLevel"/> 值。</exception>
    /// <example>
    /// 消息为 <c>Loaded.</c>，且属性依次插入 <c>count=4</c> 和 <c>name=oak</c> 时，该行会保留此顺序；
    /// 请参见 <c>HumanLogFormatTests.Attributes_follow_the_message_in_insertion_order</c>。
    /// </example>
    public static string FormatBounded(
        LogLevel level,
        string? category,
        string? eventId,
        string? message,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, object?>? attributes)
    {
        var clipped = string.IsNullOrEmpty(message)
            ? ""
            : message.Length <= MaxFormattedMessageLength ? message : message[..MaxFormattedMessageLength];
        var time = timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var head = $"[{ToDisplayName(level)}][{time}][{category}][{eventId}]";
        var tail = FormatAttributes(attributes);
        if (clipped.Length == 0)
            return tail.Length == 0 ? head : $"{head} {tail}";
        return tail.Length == 0 ? $"{head} {clipped}" : $"{head} {clipped} {tail}";
    }

    /// <summary>将事件格式化为 Godot 富文本，为日志级别和消息着色，并淡化元数据。</summary>
    /// <param name="logEvent">要格式化并着色的事件。</param>
    /// <returns>将字面左方括号转义为 <c>[lb]</c> 的 Godot BBCode。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="logEvent"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException">事件具有未定义的 <see cref="LogLevel"/> 值。</exception>
    /// <example>
    /// 错误消息 <c>failed [id]</c> 使用错误颜色，并将其中的字面左方括号呈现为 <c>[lb]</c>；
    /// 请参见 <c>HumanLogFormatTests.Console_coloring_dims_meta_and_tints_level_and_message</c>。
    /// </example>
    public static string FormatConsole(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var plain = FormatBounded(logEvent);
        return Colorize(plain, logEvent.Level, logEvent.EventId == CommandEchoEventId);
    }

    /// <summary>按枚举顺序将属性追加为空格分隔的键值对。</summary>
    private static string FormatAttributes(IReadOnlyDictionary<string, object?>? attributes)
    {
        if (attributes is null || attributes.Count == 0)
            return "";

        var parts = new List<string>(attributes.Count);
        foreach (var (key, value) in attributes)
            parts.Add($"{key}={FormatAttributeValue(value)}");
        return string.Join(" ", parts);
    }

    /// <summary>使用区域性无关的标量文本和递归集合语法格式化一个属性。</summary>
    private static string FormatAttributeValue(object? value)
    {
        switch (value)
        {
            case null:
                return "";
            case string text:
                return text;
            case IReadOnlyDictionary<string, object?> dictionary:
                return ToCompactJson(dictionary);
            case IReadOnlyList<object?> list:
                return "[" + string.Join(", ", list.Select(FormatAttributeValue)) + "]";
            default:
                if (value is IEnumerable enumerable and not string)
                {
                    var items = new List<string>();
                    foreach (var item in enumerable)
                        items.Add(FormatAttributeValue(item));
                    return "[" + string.Join(", ", items) + "]";
                }

                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
    }

    /// <summary>将规范化字典序列化为紧凑 JSON，以嵌入人类可读行。</summary>
    private static string ToCompactJson(IReadOnlyDictionary<string, object?> dictionary)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteJson(writer, dictionary);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>写入规范化的属性值，同时保留字典和列表的枚举顺序。</summary>
    private static void WriteJson(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string text:
                writer.WriteStringValue(text);
                return;
            case bool flag:
                writer.WriteBooleanValue(flag);
                return;
            case sbyte n:
                writer.WriteNumberValue(n);
                return;
            case byte n:
                writer.WriteNumberValue(n);
                return;
            case short n:
                writer.WriteNumberValue(n);
                return;
            case ushort n:
                writer.WriteNumberValue(n);
                return;
            case int n:
                writer.WriteNumberValue(n);
                return;
            case uint n:
                writer.WriteNumberValue(n);
                return;
            case long n:
                writer.WriteNumberValue(n);
                return;
            case ulong n:
                writer.WriteNumberValue(n);
                return;
            case float n:
                writer.WriteNumberValue(n);
                return;
            case double n:
                writer.WriteNumberValue(n);
                return;
            case IReadOnlyDictionary<string, object?> dictionary:
                writer.WriteStartObject();
                foreach (var (key, item) in dictionary)
                {
                    writer.WritePropertyName(key);
                    WriteJson(writer, item);
                }

                writer.WriteEndObject();
                return;
            case IReadOnlyList<object?> list:
                writer.WriteStartArray();
                foreach (var item in list)
                    WriteJson(writer, item);
                writer.WriteEndArray();
                return;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
        }
    }

    /// <summary>对已知行段应用 Godot 富文本颜色；解析失败时回退为单一消息颜色。</summary>
    private static string Colorize(string plain, LogLevel level, bool commandEcho)
    {
        if (!TrySplit(plain, out var levelText, out var time, out var category, out var eventId, out var rest))
            return Wrap(Escape(plain), MessageColor(level, commandEcho));

        var builder = new StringBuilder();
        builder.Append(Wrap(Escape($"[{levelText}]"), LevelColor(level)));
        builder.Append(Wrap(Escape($"[{time}]"), LogConsoleColors.Dim));
        builder.Append(Wrap(Escape($"[{category}]"), LogConsoleColors.Dim));
        builder.Append(Wrap(Escape($"[{eventId}]"), LogConsoleColors.Dim));
        if (rest.Length > 0)
            builder.Append(Wrap(Escape(rest), MessageColor(level, commandEcho)));
        return builder.ToString();
    }

    /// <summary>从格式化行的可选剩余部分中拆出四个带方括号的元数据字段。</summary>
    private static bool TrySplit(
        string plain,
        out string levelText,
        out string time,
        out string category,
        out string eventId,
        out string rest)
    {
        levelText = time = category = eventId = rest = "";
        if (plain.Length < 8 || plain[0] != '[')
            return false;

        var first = plain.IndexOf(']');
        var second = first < 0 ? -1 : plain.IndexOf(']', first + 1);
        var third = second < 0 ? -1 : plain.IndexOf(']', second + 1);
        var fourth = third < 0 ? -1 : plain.IndexOf(']', third + 1);
        if (first < 1 || second < 0 || third < 0 || fourth < 0)
            return false;
        if (plain[first + 1] != '[' || plain[second + 1] != '[' || plain[third + 1] != '[')
            return false;

        levelText = plain[1..first];
        time = plain[(first + 2)..second];
        category = plain[(second + 2)..third];
        eventId = plain[(third + 2)..fourth];
        rest = fourth + 1 < plain.Length ? plain[(fourth + 1)..] : "";
        return true;
    }

    /// <summary>返回配置的日志级别颜色；信息文本不着色时返回 <see langword="null"/>。</summary>
    private static string? LevelColor(LogLevel level) => level switch
    {
        LogLevel.Error => LogConsoleColors.Error,
        LogLevel.Warning => LogConsoleColors.Warning,
        LogLevel.Debug => LogConsoleColors.Debug,
        _ => null
    };

    /// <summary>需要时选择命令回显颜色，否则采用日志级别颜色。</summary>
    private static string? MessageColor(LogLevel level, bool commandEcho) =>
        commandEcho ? LogConsoleColors.CommandEcho : LevelColor(level);

    /// <summary>配置了颜色时，用 Godot 颜色标签包裹文本。</summary>
    private static string Wrap(string text, string? color) =>
        color is null ? text : $"[color={color}]{text}[/color]";

    /// <summary>转义左方括号，因为 Godot BBCode 会将未转义的左方括号解释为标记。</summary>
    private static string Escape(string text) => text.Replace("[", "[lb]");
}
