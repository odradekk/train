// Core 控制台模块测试。
// 验证空白分词、基础分发、宿主回调与共享日志的真实持久回显。

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Logging;
using Train.Core.Console;
using Train.Tests.Core.Logging;

namespace Train.Tests.Core.Console;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:console")]
public sealed class ConsoleCommandParseTests
{
    [TestMethod]
    public void Parse_takes_the_first_whitespace_token_without_quote_escaping()
    {
        var help = ParsedConsoleCommand.Parse("help");
        Assert.AreEqual("help", help.Name);
        Assert.IsFalse(help.IsEmpty);

        var reload = ParsedConsoleCommand.Parse("  reload   core\tmods  ");
        Assert.AreEqual("reload", reload.Name);
        CollectionAssert.AreEqual(new[] { "core", "mods" }, reload.Arguments.ToArray());
        var quoted = ParsedConsoleCommand.Parse("say \"hello world\"");
        Assert.AreEqual("say", quoted.Name);
        CollectionAssert.AreEqual(new[] { "\"hello", "world\"" }, quoted.Arguments.ToArray());
    }

    [TestMethod]
    public void Parse_treats_blank_input_as_empty_and_rejects_null()
    {
        Assert.IsTrue(ParsedConsoleCommand.Parse("").IsEmpty);
        Assert.IsTrue(ParsedConsoleCommand.Parse(" \t ").IsEmpty);
        Assert.AreEqual("", ParsedConsoleCommand.Parse("").Name);
        Assert.ThrowsException<ArgumentNullException>(() => ParsedConsoleCommand.Parse(null!));
    }
}

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:console")]
public sealed class ConsoleCommandDispatchTests
{
    [TestMethod]
    public void Blank_input_is_ignored()
    {
        using var harness = DispatchHarness.Create();
        harness.Dispatcher.Execute("  ");
        Assert.AreEqual(0, harness.ConsoleEvents.Count);
        Assert.IsFalse(harness.Cleared);
        Assert.IsFalse(harness.Quit);
        Assert.IsFalse(harness.Reloaded);
    }

    [TestMethod]
    public void Commands_echo_through_logger_as_console_category()
    {
        using var harness = DispatchHarness.Create();
        harness.Dispatcher.Execute("  help extra  ");

        var echo = harness.ConsoleEvents.Single(e => e.EventId == ConsoleCommandDispatcher.EchoEventId);
        Assert.AreEqual(ConsoleCommandDispatcher.Category, echo.Category);
        Assert.AreEqual(LogLevel.Info, echo.Level);
        Assert.AreEqual("help extra", echo.Message);
        Assert.IsTrue(harness.ConsoleEvents.Any(e => e.EventId == ConsoleCommandDispatcher.HelpEventId));
        Assert.IsFalse(harness.Quit);
        Assert.IsFalse(harness.Reloaded);
        Assert.IsFalse(harness.Cleared);
    }

    [TestMethod]
    public void Help_lists_reload_only_when_debug_reload_is_allowed()
    {
        using var debug = DispatchHarness.Create(allowReload: true);
        debug.Dispatcher.Execute("HELP");
        var debugHelp = debug.ConsoleEvents.Single(e => e.EventId == ConsoleCommandDispatcher.HelpEventId);
        Assert.AreEqual(LogLevel.Info, debugHelp.Level);
        StringAssert.Contains(debugHelp.Message, "help");
        StringAssert.Contains(debugHelp.Message, "clear");
        StringAssert.Contains(debugHelp.Message, "quit");
        StringAssert.Contains(debugHelp.Message, "reload");
        StringAssert.Contains(debugHelp.Message, "loglevel");
        foreach (var retired in new[] { "voxel", "composite", "rail", "observe" })
            Assert.IsFalse(debugHelp.Message.Contains(retired, StringComparison.Ordinal));

        using var release = DispatchHarness.Create(allowReload: false);
        release.Dispatcher.Execute("help");
        var releaseHelp = release.ConsoleEvents.Single(e => e.EventId == ConsoleCommandDispatcher.HelpEventId);
        StringAssert.Contains(releaseHelp.Message, "help");
        StringAssert.Contains(releaseHelp.Message, "clear");
        StringAssert.Contains(releaseHelp.Message, "quit");
        StringAssert.Contains(releaseHelp.Message, "loglevel");
        Assert.IsFalse(releaseHelp.Message.Contains("reload", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Loglevel_without_argument_reports_the_current_threshold()
    {
        using var harness = DispatchHarness.Create();
        Assert.AreEqual(LogLevel.Debug, harness.Logger.Threshold);
        harness.Dispatcher.Execute("loglevel");

        var reply = harness.ConsoleEvents.Single(e => e.EventId == ConsoleCommandDispatcher.LogLevelEventId);
        Assert.AreEqual(LogLevel.Info, reply.Level);
        StringAssert.Contains(reply.Message, LogLevelText.ToSerialized(LogLevel.Debug));
        Assert.AreEqual(LogLevel.Debug, harness.Logger.Threshold);
    }

    [TestMethod]
    public void Loglevel_with_named_level_updates_the_session_threshold()
    {
        using var harness = DispatchHarness.Create();
        harness.Dispatcher.Execute("LOGLEVEL Warning");

        Assert.AreEqual(LogLevel.Warning, harness.Logger.Threshold);
        var reply = harness.ConsoleEvents.Single(e => e.EventId == ConsoleCommandDispatcher.LogLevelEventId);
        Assert.AreEqual(LogLevel.Info, reply.Level);
        StringAssert.Contains(reply.Message, LogLevelText.ToSerialized(LogLevel.Warning));

        harness.Logger.Debug("test", "hidden_after_loglevel", "quiet");
        harness.Logger.Warning("test", "visible_after_loglevel", "shown");
        Assert.IsFalse(harness.Memory.Snapshot().Any(e => e.EventId == "hidden_after_loglevel"));
        Assert.IsTrue(harness.Memory.Snapshot().Any(e => e.EventId == "visible_after_loglevel"));
    }

    [TestMethod]
    public void Loglevel_rejects_unknown_values_and_keeps_the_threshold()
    {
        using var harness = DispatchHarness.Create();
        harness.Dispatcher.Execute("loglevel verbose");

        Assert.AreEqual(LogLevel.Debug, harness.Logger.Threshold);
        var usage = harness.ConsoleEvents.Single(e => e.EventId == ConsoleCommandDispatcher.LogLevelUsageEventId);
        StringAssert.Contains(usage.Message, "loglevel");
        StringAssert.Contains(usage.Message, "debug");
        StringAssert.Contains(usage.Message, "info");
        StringAssert.Contains(usage.Message, "warning");
        StringAssert.Contains(usage.Message, "error");
    }

    [TestMethod]
    public void Clear_echoes_then_empties_the_ring_buffer_but_keeps_persistent_echo()
    {
        var directory = Directory.CreateTempSubdirectory("train-console-clear-").FullName;
        try
        {
            var buffer = new RingBufferLogSink();
            var godot = new RecordingGodotOutput();
            using var logger = new TrainLogger(directory, LogLevel.Debug, godot, buffer, readEnvironmentVariable: false);
            logger.Info("test", "prior_event", "keep");
            Assert.AreEqual(1, buffer.Snapshot().Count);

            var dispatcher = new ConsoleCommandDispatcher(logger, buffer.Clear, static () => { }, static () => { }, allowReload: true);
            dispatcher.Execute("clear");

            Assert.AreEqual(0, buffer.Snapshot().Count);
            Assert.IsTrue(godot.Events.Any(e => e.Category == ConsoleCommandDispatcher.Category && e.EventId == ConsoleCommandDispatcher.EchoEventId));
            logger.Dispose();
            var jsonl = File.ReadAllText(logger.JsonlPath!);
            Assert.IsTrue(jsonl.Contains("\"event_id\":\"command_echo\"", StringComparison.Ordinal));
            Assert.IsTrue(jsonl.Contains("\"category\":\"console\"", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public void Quit_and_reload_invoke_host_actions_after_echo()
    {
        using var harness = DispatchHarness.Create(allowReload: true);
        harness.Dispatcher.Execute("quit");
        Assert.IsTrue(harness.Quit);
        Assert.IsFalse(harness.Reloaded);
        Assert.IsTrue(harness.ConsoleEvents.Any(e => e.EventId == ConsoleCommandDispatcher.EchoEventId && e.Message == "quit"));

        harness.ResetActions();
        harness.Dispatcher.Execute("reload leftover");
        Assert.IsTrue(harness.Reloaded);
        Assert.IsFalse(harness.Quit);
    }

    [TestMethod]
    public void Reload_is_denied_outside_debug_and_unknown_commands_warn()
    {
        using var release = DispatchHarness.Create(allowReload: false);
        release.Dispatcher.Execute("reload");
        Assert.IsFalse(release.Reloaded);
        var denied = release.ConsoleEvents.Single(e => e.EventId == ConsoleCommandDispatcher.ReloadDeniedEventId);
        Assert.AreEqual(LogLevel.Warning, denied.Level);
        Assert.AreEqual(ConsoleCommandDispatcher.Category, denied.Category);

        using var unknown = DispatchHarness.Create();
        unknown.Dispatcher.Execute("teleport");
        Assert.IsFalse(unknown.Quit);
        Assert.IsFalse(unknown.Reloaded);
        Assert.IsFalse(unknown.Cleared);
        var warning = unknown.ConsoleEvents.Single(e => e.EventId == ConsoleCommandDispatcher.UnknownEventId);
        Assert.AreEqual(LogLevel.Warning, warning.Level);
        StringAssert.Contains(warning.Message, "teleport");
    }

    [TestMethod]
    public void Execute_rejects_null()
    {
        using var harness = DispatchHarness.Create();
        Assert.ThrowsException<ArgumentNullException>(() => harness.Dispatcher.Execute(null!));
    }

    [DataTestMethod]
    [DataRow("voxel")]
    [DataRow("composite")]
    [DataRow("rail")]
    [DataRow("observe")]
    public void Retired_commands_warn_without_invoking_host_actions(string name)
    {
        using var harness = DispatchHarness.Create();
        harness.Dispatcher.Execute(name.ToUpperInvariant() + " query");

        Assert.IsFalse(harness.Cleared);
        Assert.IsFalse(harness.Quit);
        Assert.IsFalse(harness.Reloaded);
        var warning = harness.ConsoleEvents.Single(item => item.EventId == ConsoleCommandDispatcher.UnknownEventId);
        Assert.AreEqual(LogLevel.Warning, warning.Level);
        StringAssert.Contains(warning.Message, name.ToUpperInvariant());
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            // Windows 仍占用临时文件时，清理失败不掩盖测试结果。
        }
    }
}

internal sealed class DispatchHarness : IDisposable
{
    private readonly LoggerSession _session;

    public ConsoleCommandDispatcher Dispatcher { get; }
    public TrainLogger Logger => _session.Logger;
    public MemoryLogSink Memory => _session.Memory;
    public bool Cleared { get; private set; }
    public bool Quit { get; private set; }
    public bool Reloaded { get; private set; }

    public IReadOnlyList<LogEvent> ConsoleEvents =>
        _session.Memory.Snapshot().Where(e => e.Category == ConsoleCommandDispatcher.Category).ToArray();

    private DispatchHarness(LoggerSession session, bool allowReload)
    {
        _session = session;
        Dispatcher = new ConsoleCommandDispatcher(
            session.Logger,
            () => Cleared = true,
            () => Quit = true,
            () => Reloaded = true,
            allowReload);
    }

    public static DispatchHarness Create(bool allowReload = true) =>
        new(LoggerSession.Create(), allowReload);

    public void ResetActions()
    {
        Cleared = false;
        Quit = false;
        Reloaded = false;
    }

    public void Dispose() => _session.Dispose();
}
