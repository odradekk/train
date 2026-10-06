// Train.Core.Logging 协调会话中的诊断日志，将结构化事件交给接收器和宿主输出。
// 日志器为接受的事件排序，并缓冲工作线程的宿主输出，等待宿主线程取出。

namespace Train.Core.Logging;

/// <summary>管理一个诊断日志会话，统一过滤级别并按顺序生成结构化事件。</summary>
/// <remarks>
/// 在宿主输出线程创建日志器，并在同一线程调用 <see cref="DrainGodotQueue"/> 和 <see cref="Dispose"/>；
/// 这两个方法不会检查调用线程。持久输出接收器在日志器锁内同步写入，工作线程的宿主输出则进入队列。
/// 日志器负责释放自身的 JSONL 接收器；传入的宿主输出、额外接收器和备用写入器由调用方管理。
/// </remarks>
/// <example>应用就绪与工作线程输出的用例见 <c>StructuredLoggingTests.Application_lifecycle_logs_ready_and_drains_queued_godot_events</c>。</example>
public sealed class TrainLogger : IDisposable
{
    /// <summary>将等待输出的工作线程事件限制为 4096 个引用。</summary>
    /// <remarks>结构化日志规格规定的固定诊断上限，不代表实测的吞吐量目标。</remarks>
    /// <seealso href="README.md#限制"/>
    public const int GodotQueueCapacity = 4096;
    /// <summary>指定用于覆盖会话初始阈值的环境变量名。</summary>
    /// <seealso href="README.md#固定标识符"/>
    public const string LevelEnvironmentVariable = "TRAIN_LOG_LEVEL";
    /// <summary>标识应用生命周期诊断的类别。</summary>
    /// <seealso href="README.md#固定标识符"/>
    public const string ApplicationCategory = "application";
    /// <summary>标识会话中的应用就绪事件。</summary>
    /// <seealso href="README.md#固定标识符"/>
    public const string ApplicationReadyEventId = "application_ready";
    /// <summary>标识日志管线自身的诊断类别。</summary>
    /// <seealso href="README.md#固定标识符"/>
    public const string LoggingCategory = "logging";
    /// <summary>标识自上次取出队列以来丢弃的宿主输出副本的汇总事件。</summary>
    /// <seealso href="README.md#固定标识符"/>
    public const string GodotEventsDroppedEventId = "godot_events_dropped";
    /// <summary>标识持久输出接收器因故障被禁用的诊断事件。</summary>
    /// <seealso href="README.md#固定标识符"/>
    public const string SinkFaultedEventId = "sink_faulted";

    private readonly IGodotLogOutput _godotOutput;
    private readonly TextWriter _overflowWriter;
    private readonly JsonlLogSink _jsonl;
    private readonly ILogSink? _additionalSink;
    // 创建时记录宿主线程，使 Core 无需引用引擎即可判断输出是否需要排队。
    private readonly Thread _mainThread;
    // 统一保护阈值修改、事件序号分配、持久输出和队列修改，保证它们的顺序。
    private readonly object _gate = new();
    private readonly LinkedList<LogEvent> _godotQueue = new();

    private long _sequence;
    private int _disposed;
    // 统计自上次 TakeGodotBatch 以来丢弃的宿主输出副本，不影响持久输出副本。
    private int _godotDropped;
    private bool _jsonlFaulted;
    private bool _additionalFaulted;
    private bool _sinkFaultReported;
    private bool _draining;
    private LogLevel _threshold;

    /// <summary>获取为本日志器新生成的会话标识，供其创建的所有事件共用。</summary>
    public Guid SessionId { get; }

    /// <summary>获取或修改各正常输出通道接受的最低日志级别。</summary>
    /// <remarks>
    /// 修改经过同步，只影响后续接受的事件，不改变已经排队或保存的事件。
    /// 应传入已定义的 <see cref="LogLevel"/> 值；设置时不校验枚举值。
    /// </remarks>
    /// <example>运行时调整阈值的用例见 <c>StructuredLoggingTests.Runtime_threshold_change_drops_then_restores_lower_events</c>。</example>
    public LogLevel Threshold
    {
        get
        {
            lock (_gate)
                return _threshold;
        }
        set
        {
            lock (_gate)
                _threshold = value;
        }
    }

    /// <summary>获取会话日志文件路径；创建失败时为 <see langword="null"/>。</summary>
    /// <remarks>路径非空不保证接收器仍可用，也不保证文件仍然存在。</remarks>
    public string? JsonlPath => _jsonl.FilePath;

    /// <summary>创建诊断日志会话，并将调用线程记录为宿主输出线程。</summary>
    /// <param name="logDirectory">存放会话 JSONL 文件并清理匹配旧文件的目录。</param>
    /// <param name="buildDefaultThreshold">未提供有效启动覆盖值时使用的最低日志级别。</param>
    /// <param name="godotOutput">在创建日志器的线程或显式取出队列时调用的宿主输出。</param>
    /// <param name="additionalSink">可选的同步接收器；null 表示持久输出仅使用 JSONL 接收器。</param>
    /// <param name="levelOverride">显式启动级别；只要非 null 就优先于环境变量，包括空字符串和空白字符串。</param>
    /// <param name="overflowWriter">用于无法排队的警告或错误、无效覆盖值和释放后日志的备用写入器；null 时使用 System.Console.Error。</param>
    /// <param name="readEnvironmentVariable">显式覆盖值为 null 时，是否读取 TRAIN_LOG_LEVEL。</param>
    /// <exception cref="ArgumentNullException"><paramref name="logDirectory"/> 或 <paramref name="godotOutput"/> 为 null。</exception>
    /// <remarks>
    /// 文件创建失败时禁用 JSONL 接收器，不中断日志器的创建。非空白覆盖值无效时使用构建默认级别，并尝试直接写入警告。
    /// 传入的输出对象仍由调用方管理。
    /// </remarks>
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
        _overflowWriter = overflowWriter ?? System.Console.Error;
        _additionalSink = additionalSink;
        _mainThread = Thread.CurrentThread;
        SessionId = Guid.NewGuid();

        var overrideValue = levelOverride;
        if (overrideValue is null && readEnvironmentVariable)
            overrideValue = Environment.GetEnvironmentVariable(LevelEnvironmentVariable);

        _threshold = ResolveThreshold(buildDefaultThreshold, overrideValue, out var invalidOverride);
        if (invalidOverride)
            WriteDirectWarning("Invalid TRAIN_LOG_LEVEL value; using the build default.");

        _jsonl = new JsonlLogSink(logDirectory, SessionId);
        if (!_jsonl.IsEnabled)
        {
            _jsonlFaulted = true;
            ReportSinkFault();
        }
    }

    /// <summary>创建会话，并按当前阈值尝试记录应用就绪事件。</summary>
    /// <param name="logDirectory">存放会话 JSONL 文件的目录。</param>
    /// <param name="buildDefaultThreshold">未提供有效覆盖值时使用的初始最低日志级别。</param>
    /// <param name="godotOutput">在当前调用线程使用的宿主输出。</param>
    /// <param name="additionalSink">由调用方管理的可选同步接收器。</param>
    /// <param name="levelOverride">显式启动级别；非 null 时优先于环境变量。</param>
    /// <param name="overflowWriter">备用写入器；null 时使用 System.Console.Error。</param>
    /// <param name="readEnvironmentVariable">显式覆盖值为 null 时，是否读取 TRAIN_LOG_LEVEL。</param>
    /// <returns>新会话；调用方应在创建它的线程上取出输出队列并释放会话。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="logDirectory"/> 或 <paramref name="godotOutput"/> 为 null。</exception>
    /// <example>应用就绪与工作线程输出的用例见 <c>StructuredLoggingTests.Application_lifecycle_logs_ready_and_drains_queued_godot_events</c>。</example>
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

    /// <summary>忽略首尾空白和大小写解析启动级别，无法解析时使用构建默认值。</summary>
    /// <param name="buildDefaultThreshold">覆盖值缺失、为空白或无效时使用的日志级别。</param>
    /// <param name="overrideValue">可选的 debug、info、warning 或 error 文本。</param>
    /// <param name="invalidOverride">仅在文本非空白且不是支持的级别名时为 true。</param>
    /// <returns>解析出的覆盖级别，或原样返回的构建默认级别。</returns>
    /// <example>默认级别为 Warning、覆盖值为 <c>loud</c> 时，返回 Warning 且 invalidOverride 为 true；见 <c>StructuredLoggingTests.Threshold_filters_below_cutoff_and_invalid_override_falls_back</c>。</example>
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

    /// <summary>尝试记录 Info 级应用就绪事件；会话文件创建成功时附带 log_path 属性。</summary>
    /// <remarks>当前阈值可能过滤此事件，与其他 Info 级诊断一致。</remarks>
    /// <example>应用就绪事件的用例见 <c>StructuredLoggingTests.Application_lifecycle_logs_ready_and_drains_queued_godot_events</c>。</example>
    public void LogApplicationReady()
    {
        IReadOnlyDictionary<string, object?>? attributes = JsonlPath is null
            ? null
            : new Dictionary<string, object?> { ["log_path"] = JsonlPath };
        Info(ApplicationCategory, ApplicationReadyEventId, "Application ready.", attributes);
    }

    /// <summary>按 <see cref="Log"/> 的校验和输出契约尝试记录 Debug 级事件。</summary>
    /// <inheritdoc cref="Log"/>
    public void Debug(
        string category,
        string eventId,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null) =>
        Log(LogLevel.Debug, category, eventId, message, attributes, exception);

    /// <summary>按 <see cref="Log"/> 的校验和输出契约尝试记录 Info 级事件。</summary>
    /// <inheritdoc cref="Log"/>
    public void Info(
        string category,
        string eventId,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null) =>
        Log(LogLevel.Info, category, eventId, message, attributes, exception);

    /// <summary>按 <see cref="Log"/> 的校验和输出契约尝试记录 Warning 级事件。</summary>
    /// <inheritdoc cref="Log"/>
    public void Warning(
        string category,
        string eventId,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null) =>
        Log(LogLevel.Warning, category, eventId, message, attributes, exception);

    /// <summary>按 <see cref="Log"/> 的校验和输出契约尝试记录 Error 级事件。</summary>
    /// <inheritdoc cref="Log"/>
    public void Error(
        string category,
        string eventId,
        string message,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null) =>
        Log(LogLevel.Error, category, eventId, message, attributes, exception);

    /// <summary>校验诊断输入并制作快照，级别达到阈值时将事件交给所有输出通道。</summary>
    /// <param name="level">用于统一过滤和宿主队列优先级的日志级别；应传入已定义的 LogLevel 值。</param>
    /// <param name="category">以小写 ASCII 字母开头、由小写字母、数字和下划线组成的类别名，长度为 1–64 个字符。</param>
    /// <param name="eventId">格式同类别名的稳定事件标识，长度为 1–100 个字符。</param>
    /// <param name="message">完整的诊断文本；只有供人阅读的格式化输出限制显示长度。</param>
    /// <param name="attributes">可选属性树，包含 null、string、bool、整数、有限的 float/double、只读列表和字符串键字典；输出前复制。</param>
    /// <param name="exception">可选异常，捕获其类型、消息、堆栈及数量有上限的内部异常链。</param>
    /// <exception cref="ArgumentNullException">尚未释放的日志器收到为 null 的 category、eventId、message 或属性字典键。</exception>
    /// <exception cref="ArgumentException">尚未释放的日志器收到无效标识符、不支持的属性值、非有限浮点数或过深的属性嵌套。</exception>
    /// <remarks>
    /// 先校验输入，再按阈值过滤。接受的事件使用 UTC 时间戳和递增的会话序号。
    /// 持久输出先于宿主输出，宿主队列已满时仍保留持久输出副本。各输出对象的故障彼此隔离。
    /// 释放后只尝试写入限制消息长度的备用文本，不再校验输入、创建结构化事件或重新打开输出对象。
    /// </remarks>
    /// <example>属性保留和拒绝规则的用例见 <c>StructuredLoggingTests.Attributes_keep_json_scalars_and_reject_object_graphs</c>。</example>
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

            if (level < _threshold)
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

            // 先写持久输出，避免宿主队列溢出时丢失已接受事件的结构化副本。
            reportFault = EmitPersistent(logEvent);
            DeliverGodot(logEvent);
        }

        if (reportFault)
            ReportSinkFault();
    }

    /// <summary>输出当前批次的工作线程事件，并尝试汇总一次已丢弃的宿主输出副本。</summary>
    /// <returns>从队列取出的普通事件数量，不含汇总事件，也不取决于输出是否成功；已经释放或正在取出队列时为零。</returns>
    /// <remarks>
    /// 应在创建日志器的线程调用。宿主输出在队列锁外进行，其他线程可在输出期间为下一批入队。
    /// 丢弃汇总按正常的 Warning 级阈值过滤。
    /// </remarks>
    /// <example>满队列和丢弃汇总的用例见 <c>StructuredLoggingConcurrencyTests.Full_godot_queue_drops_low_severity_first_and_summarizes_once</c>。</example>
    public int DrainGodotQueue()
    {
        List<LogEvent> batch;
        int dropped;
        lock (_gate)
        {
            if (_disposed != 0 || _draining)
                return 0;
            _draining = true;
            batch = TakeGodotBatch(out dropped);
        }

        try
        {
            DeliverGodotBatch(batch, dropped, allowStructuredSummary: true);
            return batch.Count;
        }
        finally
        {
            lock (_gate)
                _draining = false;
        }
    }

    /// <summary>停止接受结构化日志，输出宿主队列中的事件，并关闭自身的 JSONL 接收器。</summary>
    /// <remarks>
    /// 应在创建日志器的线程调用，重复调用不生效。关闭时的丢弃汇总不经过正常阈值过滤，因为会话已经停止接受事件。
    /// 不释放调用方传入的输出对象，输出或关闭过程中的异常不会向外传播。
    /// </remarks>
    public void Dispose()
    {
        List<LogEvent> batch;
        int dropped;
        lock (_gate)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
            batch = TakeGodotBatch(out dropped);
        }

        try
        {
            DeliverGodotBatch(batch, dropped, allowStructuredSummary: false);
        }
        catch
        {
            // 关闭过程中的异常不能向外传播。
        }

        lock (_gate)
        {
            try
            {
                _jsonl.Dispose();
            }
            catch
            {
                // 关闭过程中的异常不能向外传播。
            }
        }
    }

    /// <summary>在调用方持有日志器锁时取出并清空宿主队列，同时取出和重置丢弃计数。</summary>
    private List<LogEvent> TakeGodotBatch(out int dropped)
    {
        var batch = _godotQueue.ToList();
        _godotQueue.Clear();
        dropped = _godotDropped;
        _godotDropped = 0;
        return batch;
    }

    /// <summary>输出已取出的宿主事件批次，再按会话是否仍接受事件选择丢弃汇总方式。</summary>
    private void DeliverGodotBatch(List<LogEvent> batch, int dropped, bool allowStructuredSummary)
    {
        foreach (var logEvent in batch)
            WriteGodotSafe(logEvent);

        if (dropped <= 0)
            return;

        if (allowStructuredSummary)
        {
            Warning(
                LoggingCategory,
                GodotEventsDroppedEventId,
                "Godot log events were dropped.",
                new Dictionary<string, object?> { ["count"] = dropped });
            return;
        }

        WriteShutdownDropSummary(dropped);
    }

    /// <summary>在正常日志入口已被释放流程禁用后，仍保存并输出丢弃汇总。</summary>
    private void WriteShutdownDropSummary(int dropped)
    {
        LogEvent summary;
        lock (_gate)
        {
            summary = new LogEvent(
                DateTimeOffset.UtcNow,
                SessionId,
                ++_sequence,
                LogLevel.Warning,
                LoggingCategory,
                GodotEventsDroppedEventId,
                "Godot log events were dropped.",
                new Dictionary<string, object?> { ["count"] = dropped },
                exception: null);
            EmitPersistent(summary);
        }

        WriteGodotSafe(summary);
    }

    /// <summary>在日志器锁内写入仍可用的持久输出接收器，并返回是否新发生了接收器故障。</summary>
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

    /// <summary>立即输出宿主线程事件，或为工作线程事件入队而不等待队列空位。</summary>
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

        if (TryEvictOldestLowerThan(logEvent.Level))
        {
            _godotDropped++;
            _godotQueue.AddLast(logEvent);
            return;
        }

        _godotDropped++;
        if (logEvent.Level >= LogLevel.Warning)
            WriteOverflow(logEvent);
    }

    /// <summary>在低于传入事件的级别中，选择队列中的最低级别并移除该级别最旧的事件。</summary>
    private bool TryEvictOldestLowerThan(LogLevel incoming)
    {
        // 先按级别再按时间选择：较新的 Debug 副本也应先于较旧的 Info 或 Warning 副本被丢弃。
        for (var level = LogLevel.Debug; level < incoming; level++)
        {
            if (TryEvictOldest(level))
                return true;
        }

        return false;
    }

    /// <summary>若队列中存在指定级别的事件，移除该级别最旧的宿主输出副本。</summary>
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

    /// <summary>尝试宿主输出，并阻止其故障影响日志调用方。</summary>
    private void WriteGodotSafe(LogEvent logEvent)
    {
        try
        {
            _godotOutput.Write(logEvent);
        }
        catch
        {
            // Godot 输出故障不能中断日志记录或游戏运行。
        }
    }

    /// <summary>用标记阻止因故障报告再次触发接收器故障报告。</summary>
    private void ReportSinkFault()
    {
        if (_sinkFaultReported)
            return;
        _sinkFaultReported = true;
        Warning(LoggingCategory, SinkFaultedEventId, "A log sink failed and was disabled.");
    }

    /// <summary>尚无结构化事件可用时，通过备用写入器尝试记录启动警告。</summary>
    private void WriteDirectWarning(string message)
    {
        try
        {
            _overflowWriter.WriteLine(message);
        }
        catch
        {
            // 直接写入警告失败不能阻止日志器创建。
        }
    }

    /// <summary>尝试以限制消息长度的可读文本记录释放后的日志，不创建新的结构化事件。</summary>
    private void WriteDisposedFallback(LogLevel level, string? category, string? eventId, string? message)
    {
        try
        {
            _overflowWriter.WriteLine($"disposed {LogLevelText.FormatBounded(level, category, eventId, message)}");
        }
        catch
        {
            // 释放后记录日志不能向外抛出异常。
        }
    }

    /// <summary>为无法进入已满宿主队列的警告或错误事件尝试写入备用文本。</summary>
    private void WriteOverflow(LogEvent logEvent)
    {
        try
        {
            _overflowWriter.WriteLine(LogLevelText.FormatBounded(logEvent));
        }
        catch
        {
            // 备用写入器的故障不能传播给日志调用方。
        }
    }
}
