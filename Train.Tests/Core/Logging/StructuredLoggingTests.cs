// Core 日志模块测试。
// 验证结构化日志的 JSONL 序列化、保留策略和文件生命周期。

using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Logging;

namespace Train.Tests.Core.Logging;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:logging")]
public sealed class StructuredLoggingTests
{
    [TestMethod]
    public void Application_lifecycle_logs_ready_and_drains_queued_godot_events()
    {
        using var session = LoggerSession.Create(startApplication: true);
        var ready = session.Memory.Snapshot().Single(e => e.EventId == TrainLogger.ApplicationReadyEventId);

        Assert.AreEqual(TrainLogger.ApplicationCategory, ready.Category);
        Assert.AreEqual(LogLevel.Info, ready.Level);
        Assert.AreEqual(session.Logger.SessionId, ready.SessionId);
        Assert.AreEqual(1L, ready.Sequence);
        Assert.AreEqual(TimeSpan.Zero, ready.Timestamp.Offset);
        Assert.AreEqual(session.Logger.JsonlPath, ready.Attributes["log_path"]);
        Assert.IsTrue(session.Godot.Events.Any(e => e.EventId == TrainLogger.ApplicationReadyEventId));

        var started = new ManualResetEventSlim(false);
        var written = new ManualResetEventSlim(false);
        var worker = new Thread(() =>
        {
            started.Wait();
            session.Logger.Info("test", "worker_ready", "queued");
            written.Set();
        });
        worker.Start();
        started.Set();
        Assert.IsTrue(written.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(session.Godot.Events.Any(e => e.EventId == "worker_ready"));

        session.Logger.DrainGodotQueue();
        Assert.IsTrue(session.Godot.Events.Any(e => e.EventId == "worker_ready"));
        Assert.AreEqual(2L, session.Memory.Snapshot().Single(e => e.EventId == "worker_ready").Sequence);
    }

    [TestMethod]
    public void Event_keys_reject_invalid_developer_input()
    {
        using var session = LoggerSession.Create();
        Assert.ThrowsException<ArgumentNullException>(() =>
            session.Logger.Info(null!, "ready", "x"));
        Assert.ThrowsException<ArgumentException>(() =>
            session.Logger.Info("", "ready", "x"));
        Assert.ThrowsException<ArgumentException>(() =>
            session.Logger.Info("Application", "ready", "x"));
        Assert.ThrowsException<ArgumentException>(() =>
            session.Logger.Info("1start", "ready", "x"));
        Assert.ThrowsException<ArgumentException>(() =>
            session.Logger.Info("bad-key", "ready", "x"));
        Assert.ThrowsException<ArgumentException>(() =>
            session.Logger.Info(new string('a', 65), "ready", "x"));
        Assert.ThrowsException<ArgumentException>(() =>
            session.Logger.Info("test", new string('e', 101), "x"));
        Assert.ThrowsException<ArgumentNullException>(() =>
            session.Logger.Info("test", "ready", null!));
        Assert.AreEqual(0, session.Memory.Snapshot().Count);
    }

    [TestMethod]
    public void Attributes_keep_json_scalars_and_reject_object_graphs()
    {
        using var session = LoggerSession.Create();
        session.Logger.Info(
            "test",
            "typed_attributes",
            "values",
            new Dictionary<string, object?>
            {
                ["none"] = null,
                ["name"] = "oak",
                ["ok"] = true,
                ["count"] = 4,
                ["ratio"] = 1.5d,
                ["flags"] = new[] { true, false },
                ["nested"] = new Dictionary<string, int> { ["units"] = 2 }
            });

        var recorded = session.Memory.Snapshot().Single();
        Assert.IsNull(recorded.Attributes["none"]);
        Assert.AreEqual("oak", recorded.Attributes["name"]);
        Assert.AreEqual(true, recorded.Attributes["ok"]);
        Assert.AreEqual(4, recorded.Attributes["count"]);
        Assert.AreEqual(1.5d, recorded.Attributes["ratio"]);
        CollectionAssert.AreEqual(new object?[] { true, false }, ((IReadOnlyList<object?>)recorded.Attributes["flags"]!).ToArray());
        Assert.AreEqual(2, ((IReadOnlyDictionary<string, object?>)recorded.Attributes["nested"]!)["units"]);

        Assert.ThrowsException<ArgumentException>(() =>
            session.Logger.Info("test", "rejects_object_graph", "x", new Dictionary<string, object?>
            {
                ["graph"] = new { Label = "unsupported", Values = new[] { 1, 2 } }
            }));
        Assert.ThrowsException<ArgumentException>(() =>
            session.Logger.Info("test", "rejects_nan", "x", new Dictionary<string, object?> { ["bad"] = double.NaN }));
        Assert.ThrowsException<ArgumentException>(() =>
            session.Logger.Info("test", "rejects_infinity", "x", new Dictionary<string, object?> { ["bad"] = float.PositiveInfinity }));
        Assert.ThrowsException<ArgumentException>(() =>
            session.Logger.Info("test", "rejects_object", "x", new Dictionary<string, object?> { ["bad"] = new object() }));

        var jsonlPath = session.JsonlPath!;
        session.DisposeLogger();
        using var document = ReadSingle(jsonlPath);
        var attributes = document.RootElement.GetProperty("attributes");
        Assert.AreEqual(JsonValueKind.Null, attributes.GetProperty("none").ValueKind);
        Assert.AreEqual(JsonValueKind.String, attributes.GetProperty("name").ValueKind);
        Assert.AreEqual(JsonValueKind.True, attributes.GetProperty("ok").ValueKind);
        Assert.AreEqual(JsonValueKind.Number, attributes.GetProperty("count").ValueKind);
        Assert.AreEqual(4, attributes.GetProperty("count").GetInt32());
        Assert.AreEqual(JsonValueKind.Number, attributes.GetProperty("ratio").ValueKind);
        Assert.AreEqual(JsonValueKind.Array, attributes.GetProperty("flags").ValueKind);
        Assert.AreEqual(2, attributes.GetProperty("nested").GetProperty("units").GetInt32());
    }

    [TestMethod]
    public void Threshold_filters_below_cutoff_and_invalid_override_falls_back()
    {
        using var info = LoggerSession.Create(buildDefault: LogLevel.Info, levelOverride: "info");
        info.Logger.Debug("test", "hidden_debug", "quiet");
        info.Logger.Info("test", "visible_info", "shown");
        var events = info.Memory.Snapshot();
        Assert.IsFalse(events.Any(e => e.EventId == "hidden_debug"));
        Assert.AreEqual(1L, events.Single(e => e.EventId == "visible_info").Sequence);

        using var invalid = LoggerSession.Create(buildDefault: LogLevel.Warning, levelOverride: "loud");
        Assert.AreEqual(LogLevel.Warning, invalid.Logger.Threshold);
        Assert.IsTrue(invalid.Overflow.ToString()!.Length > 0);
        invalid.Logger.Info("test", "filtered_info", "quiet");
        invalid.Logger.Warning("test", "visible_warning", "shown");
        Assert.IsFalse(invalid.Memory.Snapshot().Any(e => e.EventId == "filtered_info"));
        Assert.AreEqual(LogLevel.Warning, invalid.Memory.Snapshot().Single().Level);
    }

    [TestMethod]
    public void Runtime_threshold_change_drops_then_restores_lower_events()
    {
        using var session = LoggerSession.Create(buildDefault: LogLevel.Debug);
        session.Logger.Debug("test", "before_switch", "shown");
        session.Logger.Threshold = LogLevel.Warning;
        session.Logger.Debug("test", "hidden_after_raise", "quiet");
        session.Logger.Warning("test", "visible_warning", "shown");
        session.Logger.Threshold = LogLevel.Debug;
        session.Logger.Debug("test", "restored_debug", "shown");

        var ids = session.Memory.Snapshot().Select(e => e.EventId).ToArray();
        CollectionAssert.Contains(ids, "before_switch");
        CollectionAssert.DoesNotContain(ids, "hidden_after_raise");
        CollectionAssert.Contains(ids, "visible_warning");
        CollectionAssert.Contains(ids, "restored_debug");
    }

    [TestMethod]
    public void Exception_shape_is_bounded_and_omits_arbitrary_data()
    {
        using var session = LoggerSession.Create();
        var innerMost = new InvalidOperationException("inner-8");
        Exception chain = innerMost;
        for (var i = 7; i >= 1; i--)
            chain = new InvalidOperationException($"inner-{i}", chain);
        var root = new ApplicationException("root", new InvalidOperationException("inner-0", chain));
        root.Data["secret"] = "do-not-serialize";

        session.Logger.Error("test", "failed_operation", "boom", exception: root);
        var recorded = session.Memory.Snapshot().Single().Exception;
        Assert.IsNotNull(recorded);
        Assert.AreEqual(typeof(ApplicationException).FullName, recorded.Type);
        Assert.AreEqual("root", recorded.Message);
        Assert.AreEqual(LogExceptionData.MaxInnerExceptions, recorded.InnerExceptions.Count);
        Assert.AreEqual("inner-0", recorded.InnerExceptions[0].Message);
        Assert.AreEqual("inner-7", recorded.InnerExceptions[^1].Message);
        Assert.IsFalse(recorded.InnerExceptions.Any(inner => inner.Message == "inner-8"));

        var jsonlPath = session.JsonlPath!;
        session.DisposeLogger();
        using var document = ReadSingle(jsonlPath);
        var exception = document.RootElement.GetProperty("exception");
        Assert.AreEqual(typeof(ApplicationException).FullName, exception.GetProperty("type").GetString());
        Assert.IsTrue(exception.TryGetProperty("message", out _));
        Assert.IsTrue(exception.TryGetProperty("stack_trace", out _));
        Assert.AreEqual(8, exception.GetProperty("inner_exceptions").GetArrayLength());
        Assert.IsFalse(exception.TryGetProperty("data", out _));
        Assert.IsFalse(exception.TryGetProperty("Data", out _));
    }

    [TestMethod]
    public void Session_files_retain_ten_matches_and_leave_unrelated_files()
    {
        var directory = Directory.CreateTempSubdirectory("train-log-retain-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "notes.txt"), "keep");
            File.WriteAllText(Path.Combine(directory, "other.jsonl"), "{}");
            for (var i = 0; i < 12; i++)
            {
                var godot = new RecordingGodotOutput();
                using var logger = new TrainLogger(
                    directory,
                    LogLevel.Debug,
                    godot,
                    readEnvironmentVariable: false);
                logger.Info("test", "session_event", "x");
            }

            var sessions = Directory.GetFiles(directory, JsonlLogSink.FileSearchPattern);
            Assert.AreEqual(JsonlLogSink.RetainedSessionFiles, sessions.Length);
            Assert.IsTrue(File.Exists(Path.Combine(directory, "notes.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(directory, "other.jsonl")));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public void Sink_faults_are_isolated_and_do_not_prevent_later_events()
    {
        var blockedRoot = Directory.CreateTempSubdirectory("train-log-block-").FullName;
        try
        {
            var blocked = Path.Combine(blockedRoot, "logs");
            File.WriteAllText(blocked, "not-a-directory");
            var memory = new MemoryLogSink();
            var godot = new RecordingGodotOutput();
            using var logger = new TrainLogger(blocked, LogLevel.Debug, godot, memory, readEnvironmentVariable: false);
            Assert.IsNull(logger.JsonlPath);
            logger.Info("test", "still_emitted", "ok");
            var events = memory.Snapshot();
            Assert.IsTrue(events.Any(e => e.EventId == TrainLogger.SinkFaultedEventId));
            Assert.IsTrue(events.Any(e => e.EventId == "still_emitted"));
            Assert.IsTrue(godot.Events.Any(e => e.EventId == "still_emitted"));
        }
        finally
        {
            TryDeleteDirectory(blockedRoot);
        }

        var throwingDirectory = Directory.CreateTempSubdirectory("train-log-throw-").FullName;
        try
        {
            var throwingGodot = new RecordingGodotOutput();
            using var logger = new TrainLogger(
                throwingDirectory,
                LogLevel.Debug,
                throwingGodot,
                new ThrowingSink(),
                readEnvironmentVariable: false);
            logger.Info("test", "first_event", "one");
            logger.Info("test", "second_event", "two");
            Assert.AreEqual(1, throwingGodot.Events.Count(e => e.EventId == TrainLogger.SinkFaultedEventId));
            Assert.IsTrue(throwingGodot.Events.Any(e => e.EventId == "first_event"));
            Assert.IsTrue(throwingGodot.Events.Any(e => e.EventId == "second_event"));
            var path = logger.JsonlPath;
            logger.Dispose();
            var jsonl = File.ReadAllText(path!);
            Assert.IsTrue(jsonl.Contains("\"event_id\":\"first_event\"", StringComparison.Ordinal));
            Assert.IsTrue(jsonl.Contains("\"event_id\":\"second_event\"", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(throwingDirectory);
        }
    }

    [TestMethod]
    public void Dispose_closes_entry_and_does_not_orphan_godot_copies()
    {
        var directory = Directory.CreateTempSubdirectory("train-log-dispose-").FullName;
        var memory = new MemoryLogSink();
        var godot = new BlockingGodotOutput { BlockEventId = "hold_event" };
        var overflow = new StringWriter();
        var logger = new TrainLogger(directory, LogLevel.Debug, godot, memory, overflowWriter: overflow, readEnvironmentVariable: false);
        try
        {
            var queued = new ManualResetEventSlim(false);
            var holdWorker = new Thread(() =>
            {
                logger.Info("test", "hold_event", "hold");
                queued.Set();
            });
            holdWorker.Start();
            Assert.IsTrue(queued.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(holdWorker.Join(TimeSpan.FromSeconds(5)));

            var disposed = new ManualResetEventSlim(false);
            var disposer = new Thread(() =>
            {
                logger.Dispose();
                disposed.Set();
            });
            disposer.Start();
            Assert.IsTrue(godot.Entered.Wait(TimeSpan.FromSeconds(5)));

            var lateLogged = new ManualResetEventSlim(false);
            var lateWorker = new Thread(() =>
            {
                logger.Info("test", "dispose_late", "late");
                lateLogged.Set();
            });
            lateWorker.Start();
            Assert.IsTrue(lateLogged.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(lateWorker.Join(TimeSpan.FromSeconds(5)));

            godot.Release.Set();
            Assert.IsTrue(disposed.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(disposer.Join(TimeSpan.FromSeconds(5)));

            var lateInMemory = memory.Snapshot().Any(e => e.EventId == "dispose_late");
            var lateInGodot = godot.Events.Any(e => e.EventId == "dispose_late");
            var lateDegraded = overflow.ToString()!.Contains("dispose_late", StringComparison.Ordinal);
            Assert.IsTrue(lateInGodot || lateDegraded);
            if (lateInMemory)
                Assert.IsTrue(lateInGodot);
        }
        finally
        {
            godot.Release.Set();
            logger.Dispose();
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public void Dispose_is_idempotent_and_late_logs_do_not_throw()
    {
        using var session = LoggerSession.Create();
        session.Logger.Info("test", "before_dispose", "ok");
        session.Logger.Dispose();
        session.Logger.Dispose();
        session.Logger.Info("test", "after_dispose", "late");
        session.Logger.Error("test", "after_dispose_error", "late");
        Assert.AreEqual(1, session.Memory.Snapshot().Count);
        Assert.IsTrue(session.Overflow.ToString()!.Length > 0);
    }

    private static JsonDocument ReadSingle(string path)
    {
        var line = File.ReadAllLines(path).Single(item => !string.IsNullOrWhiteSpace(item));
        return JsonDocument.Parse(line);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            // Windows 仍占用临时目录时，尽力清理但不掩盖测试结果。
        }
    }
}

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:logging")]
public sealed class StructuredLoggingConcurrencyTests
{
    [TestMethod]
    public void Concurrent_logs_assign_unique_monotonic_sequences_and_complete_jsonl_lines()
    {
        using var session = LoggerSession.Create();
        const int workers = 4;
        const int perWorker = 25;
        var start = new ManualResetEventSlim(false);
        var remaining = new CountdownEvent(workers);
        var threads = Enumerable.Range(0, workers).Select(index => new Thread(() =>
        {
            start.Wait();
            for (var i = 0; i < perWorker; i++)
                session.Logger.Info("test", "concurrent_event", "x", new Dictionary<string, object?> { ["worker"] = index, ["i"] = i });
            remaining.Signal();
        })).ToArray();

        foreach (var thread in threads)
            thread.Start();
        start.Set();
        Assert.IsTrue(remaining.Wait(TimeSpan.FromSeconds(10)));
        foreach (var thread in threads)
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));

        session.DisposeLogger();
        var sequences = session.Memory.Snapshot().Select(e => e.Sequence).OrderBy(v => v).ToArray();
        Assert.AreEqual(workers * perWorker, sequences.Length);
        CollectionAssert.AreEqual(Enumerable.Range(1, workers * perWorker).Select(v => (long)v).ToArray(), sequences);
        Assert.AreEqual(1, session.Memory.Snapshot().Select(e => e.SessionId).Distinct().Count());

        var lines = File.ReadAllLines(session.JsonlPath!).Where(line => line.Length > 0).ToArray();
        Assert.AreEqual(workers * perWorker, lines.Length);
        var parsed = new List<long>();
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            parsed.Add(document.RootElement.GetProperty("sequence").GetInt64());
        }

        parsed.Sort();
        CollectionAssert.AreEqual(sequences, parsed);
    }

    // 测试注入的队列容量：足够小，使每个用例的真实持久刷盘次数可以忽略，同时仍能观察淘汰与溢出。
    private const int TestQueueCapacity = 4;

    [TestMethod]
    public void Full_info_queue_rejects_incoming_debug_without_evicting_info()
    {
        using var session = LoggerSession.Create(godotQueueCapacity: TestQueueCapacity);
        RunOnWorker(session.Logger, TestQueueCapacity, LogLevel.Info, "queued_info");
        RunOnWorker(session.Logger, 1, LogLevel.Debug, "incoming_debug");
        session.Logger.DrainGodotQueue();

        Assert.AreEqual(TestQueueCapacity, session.Godot.Events.Count(e => e.EventId == "queued_info"));
        Assert.IsFalse(session.Godot.Events.Any(e => e.EventId == "incoming_debug"));
        Assert.IsTrue(session.Memory.Snapshot().Any(e => e.EventId == "incoming_debug"));
        Assert.AreEqual(1, session.Memory.Snapshot().Single(e => e.EventId == TrainLogger.GodotEventsDroppedEventId).Attributes["count"]);
        Assert.AreEqual(1, session.Godot.Events.Count(e => e.EventId == TrainLogger.GodotEventsDroppedEventId));
    }

    [TestMethod]
    public void Full_godot_queue_drops_low_severity_first_and_summarizes_once()
    {
        using var session = LoggerSession.Create(godotQueueCapacity: TestQueueCapacity);
        RunOnWorker(session.Logger, TestQueueCapacity, LogLevel.Debug, "queued_debug");
        var overflowBefore = session.Overflow.ToString()!.Length;
        RunOnWorker(session.Logger, 3, LogLevel.Debug, "extra_debug");
        session.Logger.DrainGodotQueue();

        var dropped = session.Memory.Snapshot().Single(e => e.EventId == TrainLogger.GodotEventsDroppedEventId);
        Assert.AreEqual(LogLevel.Warning, dropped.Level);
        Assert.AreEqual(3, dropped.Attributes["count"]);
        Assert.AreEqual(1, session.Godot.Events.Count(e => e.EventId == TrainLogger.GodotEventsDroppedEventId));
        Assert.AreEqual(TestQueueCapacity, session.Godot.Events.Count(e => e.EventId == "queued_debug"));
        Assert.AreEqual(0, session.Godot.Events.Count(e => e.EventId == "extra_debug"));
        Assert.AreEqual(3, session.Memory.Snapshot().Count(e => e.EventId == "extra_debug"));
        Assert.AreEqual(overflowBefore, session.Overflow.ToString()!.Length);
    }

    [TestMethod]
    public void Warning_evicts_oldest_debug_from_full_queue()
    {
        using var session = LoggerSession.Create(godotQueueCapacity: TestQueueCapacity);
        RunOnWorker(session.Logger, TestQueueCapacity, LogLevel.Debug, "queued_debug");
        RunOnWorker(session.Logger, 1, LogLevel.Warning, "evicting_warning");
        session.Logger.DrainGodotQueue();

        var afterEviction = session.Memory.Snapshot().Single(e => e.EventId == TrainLogger.GodotEventsDroppedEventId);
        Assert.AreEqual(1, afterEviction.Attributes["count"]);
        // 期望被淘汰的是排在最前的一个 Debug，送达的是其余几个；序号从内存快照推导，不依赖构造阶段不产生事件。
        var queuedDebug = session.Memory.Snapshot()
            .Where(e => e.EventId == "queued_debug")
            .Select(e => e.Sequence)
            .OrderBy(sequence => sequence)
            .ToArray();
        var deliveredDebug = session.Godot.Events
            .Where(e => e.EventId == "queued_debug")
            .Select(e => e.Sequence)
            .OrderBy(sequence => sequence)
            .ToArray();
        CollectionAssert.AreEqual(queuedDebug.Skip(1).ToArray(), deliveredDebug);
        Assert.IsTrue(session.Godot.Events.Any(e => e.EventId == "evicting_warning"));
    }

    [TestMethod]
    public void Full_warning_queue_keeps_existing_and_overflows_incoming_warning()
    {
        using var session = LoggerSession.Create(godotQueueCapacity: TestQueueCapacity);
        RunOnWorker(session.Logger, TestQueueCapacity, LogLevel.Warning, "queued_warning");
        RunOnWorker(session.Logger, 1, LogLevel.Warning, "overflow_warning");

        Assert.IsTrue(session.Overflow.ToString()!.Length > 0);
        session.Logger.DrainGodotQueue();

        Assert.AreEqual(TestQueueCapacity, session.Godot.Events.Count(e => e.EventId == "queued_warning"));
        Assert.IsFalse(session.Godot.Events.Any(e => e.EventId == "overflow_warning"));
        Assert.IsTrue(session.Memory.Snapshot().Any(e => e.EventId == "overflow_warning"));
        Assert.AreEqual(1, session.Memory.Snapshot().Single(e => e.EventId == TrainLogger.GodotEventsDroppedEventId).Attributes["count"]);
    }

    [TestMethod]
    public void Public_constructor_uses_the_documented_queue_capacity()
    {
        // 公开构造函数必须把规格容量传给队列。把日志目录指向一个文件以禁用 JSONL 持久输出，从而不产生真实刷盘。
        var root = Directory.CreateTempSubdirectory("train-log-capacity-").FullName;
        try
        {
            var blocked = Path.Combine(root, "logs");
            File.WriteAllText(blocked, "not-a-directory");
            var memory = new MemoryLogSink();
            var godot = new RecordingGodotOutput();
            var overflow = new StringWriter();
            using var logger = new TrainLogger(
                blocked,
                LogLevel.Warning,
                godot,
                memory,
                overflowWriter: overflow,
                readEnvironmentVariable: false);
            Assert.IsNull(logger.JsonlPath);

            RunOnWorker(logger, TrainLogger.GodotQueueCapacity, LogLevel.Warning, "queued_warning");
            RunOnWorker(logger, 1, LogLevel.Warning, "overflow_warning");
            Assert.IsTrue(overflow.ToString()!.Length > 0);
            logger.DrainGodotQueue();

            Assert.AreEqual(TrainLogger.GodotQueueCapacity, godot.Events.Count(e => e.EventId == "queued_warning"));
            Assert.IsFalse(godot.Events.Any(e => e.EventId == "overflow_warning"));
            Assert.AreEqual(1, memory.Snapshot().Single(e => e.EventId == TrainLogger.GodotEventsDroppedEventId).Attributes["count"]);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // Windows 仍占用临时目录时，尽力清理但不掩盖测试结果。
            }
        }
    }

    [TestMethod]
    public void Events_dropped_from_godot_queue_keep_complete_jsonl_records()
    {
        // 淘汰路径：满队列的 Debug 被 Warning 挤掉。被挤掉的事件不应出现在宿主输出，但必须保留完整 JSONL 记录。
        using (var evicted = LoggerSession.Create(godotQueueCapacity: TestQueueCapacity))
        {
            RunOnWorker(evicted.Logger, TestQueueCapacity, LogLevel.Debug, "queued_debug");
            RunOnWorker(evicted.Logger, 1, LogLevel.Warning, "evicting_warning");
            evicted.DisposeLogger();

            Assert.IsFalse(evicted.Godot.Events.Any(e => e.EventId == "queued_debug" && e.Sequence == 1));
            AssertJsonlHasCompleteRecords(evicted, "queued_debug", "evicting_warning");
        }

        // 溢出路径：满队列的 Warning 无法淘汰同级事件，新的 Warning 进入备用输出，但同样必须保留完整 JSONL 记录。
        using (var overflowed = LoggerSession.Create(godotQueueCapacity: TestQueueCapacity))
        {
            RunOnWorker(overflowed.Logger, TestQueueCapacity, LogLevel.Warning, "queued_warning");
            RunOnWorker(overflowed.Logger, 1, LogLevel.Warning, "overflow_warning");
            Assert.IsTrue(overflowed.Overflow.ToString()!.Length > 0);
            overflowed.DisposeLogger();

            Assert.IsFalse(overflowed.Godot.Events.Any(e => e.EventId == "overflow_warning"));
            AssertJsonlHasCompleteRecords(overflowed, "queued_warning", "overflow_warning");
        }
    }

    /// <summary>断言会话的 JSONL 文件与内存快照事件数一致、每行都是完整记录、序号连续，且包含指定事件标识。</summary>
    private static void AssertJsonlHasCompleteRecords(LoggerSession session, params string[] expectedEventIds)
    {
        var lines = File.ReadAllLines(session.JsonlPath!);
        Assert.AreEqual(session.Memory.Snapshot().Count, lines.Length);

        var sequences = new List<long>();
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            Assert.AreNotEqual(string.Empty, root.GetProperty("event_id").GetString());
            Assert.AreNotEqual(string.Empty, root.GetProperty("message").GetString());
            sequences.Add(root.GetProperty("sequence").GetInt64());
        }

        sequences.Sort();
        CollectionAssert.AreEqual(Enumerable.Range(1, lines.Length).Select(v => (long)v).ToArray(), sequences);
        foreach (var eventId in expectedEventIds)
        {
            Assert.IsTrue(
                lines.Any(line => line.Contains($"\"event_id\":\"{eventId}\"", StringComparison.Ordinal)),
                $"JSONL should keep a record for {eventId}.");
        }
    }

    /// <summary>在专用工作线程上按顺序记录若干事件，等线程结束后再把其中的异常重抛到测试线程。</summary>
    private static void RunOnWorker(TrainLogger logger, int count, LogLevel level, string eventId)
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < count; i++)
                    logger.Log(level, "test", eventId, "queued");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        worker.Start();
        worker.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }
}

internal sealed class BlockingGodotOutput : IGodotLogOutput
{
    public string? BlockEventId { get; init; }
    public ManualResetEventSlim Entered { get; } = new(false);
    public ManualResetEventSlim Release { get; } = new(false);
    public List<LogEvent> Events { get; } = new();

    public void Write(LogEvent logEvent)
    {
        if (BlockEventId is null || logEvent.EventId == BlockEventId)
        {
            Entered.Set();
            Release.Wait();
        }

        Events.Add(logEvent);
    }
}

internal sealed class ThrowingSink : ILogSink
{
    public void Emit(LogEvent logEvent) => throw new IOException("sink failed");
}
