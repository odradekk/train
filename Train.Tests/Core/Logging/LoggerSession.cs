// Core 日志测试辅助：为测试提供独立日志目录和可观察的输出。
// 会话释放时关闭日志器，并尝试清理测试创建的临时文件。
using Train.Core.Logging;

namespace Train.Tests.Core.Logging;

/// <summary>表示测试专用日志会话；收集内存、Godot 队列和溢出输出，不调用真实引擎。</summary>
/// <example>StructuredLoggingTests 用 Create(startApplication: true) 检查应用启动事件和后台日志的队列排空。</example>
internal sealed class LoggerSession : IDisposable
{
    private readonly string _directory;
    private bool _loggerDisposed;

    public TrainLogger Logger { get; private set; }
    public MemoryLogSink Memory { get; }
    public RecordingGodotOutput Godot { get; }
    public StringWriter Overflow { get; }
    public string? JsonlPath => Logger.JsonlPath;

    private LoggerSession(
        string directory,
        TrainLogger logger,
        MemoryLogSink memory,
        RecordingGodotOutput godot,
        StringWriter overflow)
    {
        _directory = directory;
        Logger = logger;
        Memory = memory;
        Godot = godot;
        Overflow = overflow;
    }

    /// <summary>在独立临时目录创建日志器及其输出记录器，供日志、控制台和内容测试使用。</summary>
    /// <param name="buildDefault">未设置有效覆盖值时使用的最低日志级别。</param>
    /// <param name="levelOverride">日志级别覆盖文本；null 使用构建默认级别。</param>
    /// <param name="startApplication">是否通过 StartSession 记录应用启动事件。</param>
    /// <param name="godotQueueCapacity">宿主输出队列引用数上限；null 使用生产规格值 <see cref="TrainLogger.GodotQueueCapacity"/>，测试可传入小容量；不能与 <paramref name="startApplication"/> 同时使用。</param>
    /// <returns>由调用者释放、拥有临时目录的测试会话。</returns>
    public static LoggerSession Create(
        LogLevel buildDefault = LogLevel.Debug,
        string? levelOverride = null,
        bool startApplication = false,
        int? godotQueueCapacity = null)
    {
        if (godotQueueCapacity is not null && startApplication)
            throw new ArgumentException("A session with the start event always uses the production capacity.", nameof(startApplication));

        var directory = Directory.CreateTempSubdirectory("train-log-").FullName;
        var memory = new MemoryLogSink();
        var godot = new RecordingGodotOutput();
        var overflow = new StringWriter();
        TrainLogger logger = godotQueueCapacity is int capacity
            ? new TrainLogger(directory, buildDefault, godot, memory, levelOverride, overflow, false, capacity)
            : startApplication
                ? TrainLogger.StartSession(directory, buildDefault, godot, memory, levelOverride, overflow, false)
                : new TrainLogger(directory, buildDefault, godot, memory, levelOverride, overflow, false);
        return new LoggerSession(directory, logger, memory, godot, overflow);
    }

    /// <summary>仅释放日志器，保留目录供测试读取已关闭的 JSONL 文件；重复调用无作用。</summary>
    public void DisposeLogger()
    {
        if (_loggerDisposed)
            return;
        Logger.Dispose();
        _loggerDisposed = true;
    }

    /// <summary>释放日志器并尝试清理会话目录；Windows 文件仍被占用时保留临时目录。</summary>
    public void Dispose()
    {
        DisposeLogger();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch
        {
            // Windows 可能尚未释放文件句柄，清理失败不遮盖测试的断言结果。
        }
    }
}

/// <summary>按调用顺序记录日志器排出的 Godot 事件，供测试观察主线程输出。</summary>
/// <example>StructuredLoggingTests 取出队列后检查 Events 中保存的事件及其顺序。</example>
internal sealed class RecordingGodotOutput : IGodotLogOutput
{
    public List<LogEvent> Events { get; } = new();

    public void Write(LogEvent logEvent) => Events.Add(logEvent);
}
