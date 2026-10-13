using Train.App.Logging;

namespace Train.Godot.Tests.Logging;

internal sealed class LoggerSession : IDisposable
{
    private readonly string? _directory;
    private bool _loggerDisposed;

    public TrainLogger Logger { get; private set; }
    public MemoryLogSink Memory { get; }
    public RecordingGodotOutput Godot { get; }
    public StringWriter Overflow { get; }
    public string? JsonlPath => Logger.JsonlPath;

    private LoggerSession(
        string? directory,
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

    public static LoggerSession CreateMemory()
    {
        var memory = new MemoryLogSink();
        var godot = new RecordingGodotOutput();
        var overflow = new StringWriter();
        var logger = new TrainLogger("", LogLevel.Debug, godot, memory, null, overflow, false,
            TrainLogger.GodotQueueCapacity, new JsonlLogSink(Stream.Null));
        return new LoggerSession(null, logger, memory, godot, overflow);
    }

    public void DisposeLogger()
    {
        if (_loggerDisposed)
            return;
        Logger.Dispose();
        _loggerDisposed = true;
    }

    public void Dispose()
    {
        DisposeLogger();
        if (_directory is null)
            return;
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

internal sealed class RecordingGodotOutput : IGodotLogOutput
{
    public List<LogEvent> Events { get; } = new();

    public void Write(LogEvent logEvent) => Events.Add(logEvent);
}
