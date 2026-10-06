// Train.Core.Logging 模块将结构化诊断日志事件存入内存或持久化 JSONL 文件。
// 接收器提供线程安全的集合快照，并使文件保留清理不影响正在进行的日志记录。

using System.Text.Json;

namespace Train.Core.Logging;

/// <summary>
/// 表示从日志管线接收结构化诊断日志事件的接收器。
/// </summary>
/// <example><see cref="MemoryLogSink"/> 保存收到的诊断日志事件，供之后检查。</example>
public interface ILogSink
{
    /// <summary>
    /// 按接收器自身的失败与生命周期语义接收一个诊断日志事件。
    /// </summary>
    /// <param name="logEvent">要接收的完整结构化诊断日志事件。</param>
    /// <exception cref="ArgumentNullException">实现拒绝 <see langword="null"/> 事件。</exception>
    void Emit(LogEvent logEvent);
}

/// <summary>
/// 表示由宿主提供、用于向 Godot 发布结构化诊断日志事件的宿主输出边界，无需引用 Godot 程序集。
/// </summary>
/// <example>宿主实现可以格式化事件，并通过引擎的日志 API 写出。</example>
public interface IGodotLogOutput
{
    /// <summary>
    /// 向宿主输出发布一个结构化诊断日志事件。
    /// </summary>
    /// <param name="logEvent">要发布的完整结构化诊断日志事件。</param>
    /// <exception cref="ArgumentNullException"><paramref name="logEvent"/> 为 <see langword="null"/>。</exception>
    void Write(LogEvent logEvent);
}

/// <summary>
/// 在进程内存中保存每个收到的诊断日志事件，供诊断和测试使用。
/// </summary>
/// <example>接收 ID 为 <c>worker_ready</c> 的事件后，<see cref="Snapshot"/> 包含该事件。</example>
public sealed class MemoryLogSink : ILogSink
{
    private readonly List<LogEvent> _events = new();
    private readonly object _gate = new();

    /// <summary>
    /// 按接收顺序返回目前保存的诊断日志事件。
    /// </summary>
    /// <returns>一个新的集合快照，其中包含对已保存事件的引用；事件本身不会被复制。</returns>
    public IReadOnlyList<LogEvent> Snapshot()
    {
        lock (_gate)
            return _events.ToArray();
    }

    /// <summary>
    /// 向内存历史记录追加一个诊断日志事件。
    /// </summary>
    /// <param name="logEvent">要保留的事件；接收器保留传入的对象引用。</param>
    /// <exception cref="ArgumentNullException"><paramref name="logEvent"/> 为 <see langword="null"/>。</exception>
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        lock (_gate)
            _events.Add(logEvent);
    }
}

/// <summary>
/// 在有界环形缓冲中按最旧优先的顺序保存近期结构化诊断日志事件，供运行时诊断使用。
/// </summary>
/// <example>参见 <c>RingBufferLogSinkTests.Snapshot_keeps_insertion_order_until_capacity_then_drops_oldest</c>。</example>
public sealed class RingBufferLogSink : ILogSink
{
    /// <summary>
    /// 获取近期事件窗口最多保留的事件引用数。
    /// </summary>
    /// <remarks>这是控制台契约选定的固定诊断窗口，不是玩法调优值。</remarks>
    /// <seealso href="README.md#限制"/>
    public const int Capacity = 500;

    private readonly Queue<LogEvent> _events = new(Capacity);
    private readonly object _gate = new();

    /// <summary>
    /// 按从最旧到最新的顺序返回保留的事件。
    /// </summary>
    /// <returns>一个新的集合快照，其中包含最多 <see cref="Capacity"/> 个事件的引用；事件本身不会被复制。</returns>
    public IReadOnlyList<LogEvent> Snapshot()
    {
        lock (_gate)
            return _events.ToArray();
    }

    /// <summary>
    /// 清除所有保留的事件，但不影响此前已返回给调用方的集合快照。
    /// </summary>
    /// <example>参见 <c>RingBufferLogSinkTests.Clear_empties_the_buffer_and_later_emits_start_fresh</c>。</example>
    public void Clear()
    {
        lock (_gate)
            _events.Clear();
    }

    /// <summary>
    /// 保留一个事件；环形缓冲达到 <see cref="Capacity"/> 时，先丢弃其中最旧的事件。
    /// </summary>
    /// <param name="logEvent">要保留的事件；接收器保留传入的对象引用。</param>
    /// <exception cref="ArgumentNullException"><paramref name="logEvent"/> 为 <see langword="null"/>。</exception>
    /// <example>参见 <c>RingBufferLogSinkTests.Snapshot_keeps_insertion_order_until_capacity_then_drops_oldest</c>。</example>
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        lock (_gate)
        {
            if (_events.Count == Capacity)
                _events.Dequeue();
            _events.Enqueue(logEvent);
        }
    }
}

/// <summary>
/// 将结构化诊断日志事件写为换行分隔的 UTF-8 JSON，并保留数量有界的会话文件。
/// </summary>
/// <example>参见 <c>StructuredLoggingTests.Session_files_retain_ten_matches_and_leave_unrelated_files</c>。</example>
public sealed class JsonlLogSink : ILogSink, IDisposable
{
    /// <summary>
    /// 获取此接收器所拥有日志文件的前缀。
    /// </summary>
    /// <seealso href="README.md#固定标识符"/>
    public const string FilePrefix = "train-session-";
    /// <summary>
    /// 获取保留清理时用于识别此接收器会话文件的搜索模式。
    /// </summary>
    /// <seealso href="README.md#固定标识符"/>
    public const string FileSearchPattern = "train-session-*.jsonl";
    /// <summary>
    /// 获取尽力执行保留清理后，匹配会话文件的目标最大数量。
    /// </summary>
    /// <remarks>这是固定的诊断保留策略；无法访问的文件可能使清理无法达到此目标。</remarks>
    /// <seealso href="README.md#限制"/>
    public const int RetainedSessionFiles = 10;

    private readonly object _gate = new();
    private Stream? _stream;
    private bool _disposed;

    /// <summary>
    /// 获取已创建的会话文件路径；初始化失败时为 <see langword="null"/>。
    /// </summary>
    public string? FilePath { get; }
    /// <summary>
    /// 获取接收器当前是否有打开的流，因而可以尝试写入事件。
    /// </summary>
    public bool IsEnabled => _stream is not null;

    /// <summary>
    /// 创建会话文件，并按保留策略尽力清理匹配的旧文件。
    /// </summary>
    /// <param name="directory">创建会话文件的目录。</param>
    /// <param name="sessionId">嵌入文件名的会话标识符。</param>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> 为 <see langword="null"/>。</exception>
    /// <remarks>文件系统失败会禁用接收器，并使 <see cref="FilePath"/> 保持为空；保留清理失败不会禁用已成功打开的会话文件。</remarks>
    public JsonlLogSink(string directory, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(directory);
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{FilePrefix}{sessionId:D}.jsonl");
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            FilePath = path;
            TryPruneRetention(directory, path);
        }
        catch
        {
            _stream?.Dispose();
            _stream = null;
            FilePath = null;
        }
    }

    /// <summary>
    /// 写入一个末尾带换行符的 UTF-8 JSON 对象，并将该记录持久刷新到文件。
    /// </summary>
    /// <param name="logEvent">要按日志 JSON 字段契约序列化的结构化诊断日志事件。</param>
    /// <exception cref="ArgumentNullException"><paramref name="logEvent"/> 为 <see langword="null"/>；即使接收器已禁用也会拒绝。</exception>
    /// <exception cref="IOException">活动流无法写入或刷新该事件。</exception>
    /// <exception cref="ArgumentOutOfRangeException">活动接收器收到的事件具有未定义的 LogLevel 值。</exception>
    /// <remarks>
    /// 序列化或 I/O 失败时，会先尝试释放流以关闭并禁用接收器，再重新抛出异常；
    /// 清理本身也可能失败。没有流保持打开时，非空事件会被忽略。并发调用会串行执行。
    /// </remarks>
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        lock (_gate)
        {
            if (_stream is null)
                return;

            try
            {
                WriteLine(_stream, logEvent);
            }
            catch
            {
                _stream.Dispose();
                _stream = null;
                throw;
            }
        }
    }

    /// <summary>
    /// 关闭会话流，并禁用后续事件接收。
    /// </summary>
    /// <remarks>释放操作线程安全且幂等。</remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _stream?.Dispose();
            _stream = null;
        }
    }

    /// <summary>将一个事件序列化为完整的 UTF-8 JSONL 记录，并刷新传入的流；文件流使用持久刷新。</summary>
    internal static void WriteLine(Stream stream, LogEvent logEvent)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteEvent(writer, logEvent);
        buffer.WriteByte((byte)'\n');
        stream.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
        if (stream is FileStream file)
            file.Flush(true);
        else
            stream.Flush();
    }

    /// <summary>写入字段稳定、末尾不带换行符的结构化诊断日志事件 JSON 对象。</summary>
    internal static void WriteEvent(Utf8JsonWriter writer, LogEvent logEvent)
    {
        writer.WriteStartObject();
        writer.WriteString("timestamp", logEvent.Timestamp.UtcDateTime.ToString("O"));
        writer.WriteString("session_id", logEvent.SessionId);
        writer.WriteNumber("sequence", logEvent.Sequence);
        writer.WriteString("level", LogLevelText.ToSerialized(logEvent.Level));
        writer.WriteString("category", logEvent.Category);
        writer.WriteString("event_id", logEvent.EventId);
        writer.WriteString("message", logEvent.Message);
        writer.WritePropertyName("attributes");
        WriteValue(writer, logEvent.Attributes);
        writer.WritePropertyName("exception");
        if (logEvent.Exception is null)
            writer.WriteNullValue();
        else
            WriteException(writer, logEvent.Exception);
        writer.WriteEndObject();
    }

    /// <summary>按日志 JSON 字段契约递归写入传入的异常数据。</summary>
    private static void WriteException(Utf8JsonWriter writer, LogExceptionData exception)
    {
        writer.WriteStartObject();
        writer.WriteString("type", exception.Type);
        writer.WriteString("message", exception.Message);
        if (exception.StackTrace is null)
            writer.WriteNull("stack_trace");
        else
            writer.WriteString("stack_trace", exception.StackTrace);
        writer.WritePropertyName("inner_exceptions");
        writer.WriteStartArray();
        foreach (var inner in exception.InnerExceptions)
            WriteException(writer, inner);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>写入受支持的结构化属性形态，同时保留 JSON 标量类型。</summary>
    private static void WriteValue(Utf8JsonWriter writer, object? value)
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
                    WriteValue(writer, item);
                }

                writer.WriteEndObject();
                return;
            case IReadOnlyList<object?> list:
                writer.WriteStartArray();
                foreach (var item in list)
                    WriteValue(writer, item);
                writer.WriteEndArray();
                return;
            default:
                writer.WriteStringValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                return;
        }
    }

    /// <summary>删除最旧的匹配会话文件，并防止清理失败中断正在进行的日志记录。</summary>
    private static void TryPruneRetention(string directory, string currentPath)
    {
        try
        {
            var files = Directory.GetFiles(directory, FileSearchPattern);
            if (files.Length <= RetainedSessionFiles)
                return;

            var stale = files
                .OrderBy(path => File.GetCreationTimeUtc(path))
                .ThenBy(path => path, StringComparer.Ordinal)
                .Where(path => !string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
                .Take(files.Length - RetainedSessionFiles);

            foreach (var path in stale)
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // 保留策略仅尽力执行，不得删除无关文件，也不得使会话失败。
                }
            }
        }
        catch
        {
            // 尽力执行的清理不得妨碍当前会话记录日志。
        }
    }
}
