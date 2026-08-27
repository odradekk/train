namespace Train.Logging;

public sealed class TrainLogger : IDisposable
{
    public const int GodotQueueCapacity = 4096;
    public const string LevelEnvironmentVariable = "TRAIN_LOG_LEVEL";
    public const string ApplicationCategory = "application";
    public const string ApplicationReadyEventId = "application_ready";
    public const string LoggingCategory = "logging";
    public const string GodotEventsDroppedEventId = "godot_events_dropped";
    public const string SinkFaultedEventId = "sink_faulted";

    private readonly IGodotLogOutput _godotOutput;
    private readonly TextWriter _overflowWriter;
    private readonly JsonlLogSink _jsonl;
    private readonly ILogSink? _additionalSink;
    private readonly Thread _mainThread;
    private readonly object _gate = new();
    private readonly LinkedList<LogEvent> _godotQueue = new();

    private long _sequence;
    private int _disposed;
    private int _godotDropped;
    private bool _jsonlFaulted;
    private bool _additionalFaulted;
    private bool _sinkFaultReported;
    private bool _draining;

    public Guid SessionId { get; }
    public LogLevel Threshold { get; }
    public string? JsonlPath => _jsonl.FilePath;

    public TrainLogger(
        string logDirectory,
        LogLevel buildDefaultThreshold,
        IGodotLogOutput godotOutput,
        ILogSink? additionalSink = null,
        string? levelOverride = null,
        TextWriter? overflowWriter = null,
        bool readEnvironmentVariable = true)
    {
        ArgumentNullException.ThrowIfNull(logDirectory);
        ArgumentNullException.ThrowIfNull(godotOutput);

        _godotOutput = godotOutput;
        _overflowWriter = overflowWriter ?? Console.Error;
        _additionalSink = additionalSink;
        _mainThread = Thread.CurrentThread;
        SessionId = Guid.NewGuid();

        var overrideValue = levelOverride;
        if (overrideValue is null && readEnvironmentVariable)
            overrideValue = Environment.GetEnvironmentVariable(LevelEnvironmentVariable);

        Threshold = ResolveThreshold(buildDefaultThreshold, overrideValue, out var invalidOverride);
        if (invalidOverride)
            WriteDirectWarning("Invalid TRAIN_LOG_LEVEL value; using the build default.");

        _jsonl = new JsonlLogSink(logDirectory, SessionId);
        if (!_jsonl.IsEnabled)
        {
            _jsonlFaulted = true;
            ReportSinkFault();
        }
    }

    public static TrainLogger StartSession(
        string logDirectory,
        LogLevel buildDefaultThreshold,
        IGodotLogOutput godotOutput,
        ILogSink? additionalSink = null,
        string? levelOverride = null,
        TextWriter? overflowWriter = null,
        bool readEnvironmentVariable = true)
    {
        var logger = new TrainLogger(
            logDirectory,
            buildDefaultThreshold,
            godotOutput,
            additionalSink,
            levelOverride,
            overflowWriter,
            readEnvironmentVariable);
        logger.LogApplicationReady();
        return logger;
    }

    public static LogLevel ResolveThreshold(
        LogLevel buildDefaultThreshold,
        string? overrideValue,
        out bool invalidOverride)
    {
        if (string.IsNullOrWhiteSpace(overrideValue))
        {
            invalidOverride = false;
            return buildDefaultThreshold;
        }

        if (LogLevelText.TryParse(overrideValue, out var parsed))
        {
            invalidOverride = false;
            return parsed;
        }

        invalidOverride = true;
        return buildDefaultThreshold;
    }

    public void LogApplicationReady()
    {
        IReadOnlyDictionary<string, object?>? attributes = JsonlPath is null
            ? null
            : new Dictionary<string, object?> { ["log_path"] = JsonlPath };
        Info(ApplicationCategory, ApplicationReadyEventId, "Application ready.", attributes);
    }

    public void Debug(
        string category,
        string eventId,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null) =>
        Log(LogLevel.Debug, category, eventId, message, attributes, exception);

    public void Info(
        string category,
        string eventId,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null) =>
        Log(LogLevel.Info, category, eventId, message, attributes, exception);

    public void Warning(
        string category,
        string eventId,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null) =>
        Log(LogLevel.Warning, category, eventId, message, attributes, exception);

    public void Error(
        string category,
        string eventId,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null) =>
        Log(LogLevel.Error, category, eventId, message, attributes, exception);

    public void Log(
        LogLevel level,
        string category,
        string eventId,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            WriteDisposedFallback(level, category, eventId, message);
            return;
        }

        category = LogEventKey.Validate(category, nameof(category), LogEventKey.CategoryMaxLength);
        eventId = LogEventKey.Validate(eventId, nameof(eventId), LogEventKey.EventIdMaxLength);
        ArgumentNullException.ThrowIfNull(message);
        var normalized = LogAttributes.Normalize(attributes);
        var exceptionData = exception is null ? null : LogExceptionData.From(exception);

        var reportFault = false;
        lock (_gate)
        {
            if (_disposed != 0)
            {
                WriteDisposedFallback(level, category, eventId, message);
                return;
            }

            if (level < Threshold)
                return;

            var logEvent = new LogEvent(
                DateTimeOffset.UtcNow,
                SessionId,
                ++_sequence,
                level,
                category,
                eventId,
                message,
                normalized,
                exceptionData);

            reportFault = EmitPersistent(logEvent);
            DeliverGodot(logEvent);
        }

        if (reportFault)
            ReportSinkFault();
    }

    public void DrainGodotQueue()
    {
        List<LogEvent> batch;
        int dropped;
        lock (_gate)
        {
            if (_draining)
                return;
            _draining = true;
            batch = _godotQueue.ToList();
            _godotQueue.Clear();
            dropped = _godotDropped;
            _godotDropped = 0;
        }

        try
        {
            foreach (var logEvent in batch)
                WriteGodotSafe(logEvent);

            if (dropped > 0)
            {
                Warning(
                    LoggingCategory,
                    GodotEventsDroppedEventId,
                    "Godot log events were dropped.",
                    new Dictionary<string, object?> { ["count"] = dropped });
            }
        }
        finally
        {
            lock (_gate)
                _draining = false;
        }
    }

    public void Dispose()
    {
        try
        {
            DrainGodotQueue();
        }
        catch
        {
            // Shutdown must not throw.
        }

        lock (_gate)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
            try
            {
                _jsonl.Dispose();
            }
            catch
            {
                // Shutdown must not throw.
            }
        }
    }

    private bool EmitPersistent(LogEvent logEvent)
    {
        var faulted = false;
        if (!_jsonlFaulted)
        {
            try
            {
                _jsonl.Emit(logEvent);
            }
            catch
            {
                _jsonlFaulted = true;
                faulted = true;
            }
        }

        if (_additionalSink is not null && !_additionalFaulted)
        {
            try
            {
                _additionalSink.Emit(logEvent);
            }
            catch
            {
                _additionalFaulted = true;
                faulted = true;
            }
        }

        return faulted;
    }

    private void DeliverGodot(LogEvent logEvent)
    {
        if (Thread.CurrentThread == _mainThread)
        {
            WriteGodotSafe(logEvent);
            return;
        }

        if (_godotQueue.Count < GodotQueueCapacity)
        {
            _godotQueue.AddLast(logEvent);
            return;
        }

        if (TryEvictOldest(LogLevel.Debug) || TryEvictOldest(LogLevel.Info))
        {
            _godotDropped++;
            _godotQueue.AddLast(logEvent);
            return;
        }

        _godotDropped++;
        if (logEvent.Level >= LogLevel.Warning)
            WriteOverflow(logEvent);
    }

    private bool TryEvictOldest(LogLevel level)
    {
        for (var node = _godotQueue.First; node is not null; node = node.Next)
        {
            if (node.Value.Level == level)
            {
                _godotQueue.Remove(node);
                return true;
            }
        }

        return false;
    }

    private void WriteGodotSafe(LogEvent logEvent)
    {
        try
        {
            _godotOutput.Write(logEvent);
        }
        catch
        {
            // Godot output faults must not break logging or gameplay.
        }
    }

    private void ReportSinkFault()
    {
        if (_sinkFaultReported)
            return;
        _sinkFaultReported = true;
        Warning(LoggingCategory, SinkFaultedEventId, "A log sink failed and was disabled.");
    }

    private void WriteDirectWarning(string message)
    {
        try
        {
            _overflowWriter.WriteLine(message);
        }
        catch
        {
            // Direct warnings must not prevent logger construction.
        }
    }

    private void WriteDisposedFallback(LogLevel level, string? category, string? eventId, string? message)
    {
        try
        {
            _overflowWriter.WriteLine($"disposed {LogLevelText.FormatBounded(level, category, eventId, message)}");
        }
        catch
        {
            // Late logs after dispose must not throw.
        }
    }

    private void WriteOverflow(LogEvent logEvent)
    {
        try
        {
            _overflowWriter.WriteLine(LogLevelText.FormatBounded(
                logEvent.Level,
                logEvent.Category,
                logEvent.EventId,
                logEvent.Message));
        }
        catch
        {
            // Overflow fallback must never block a producer.
        }
    }
}
