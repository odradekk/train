
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Logging;
using Train.App.Console;
using Train.Godot.Tests.Logging;
using Train.App;
using Train.Ui;

namespace Train.Godot.Tests.Console;

[TestClass]
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
        Assert.ThrowsExactly<ArgumentNullException>(() => ParsedConsoleCommand.Parse(null!));
    }
}

[TestClass]
public sealed class DevelopmentConsoleCommandTests
{
    [TestMethod]
    public void DevelopmentIsListedAndDispatchedOnlyWhenRegistered()
    {
        IReadOnlyList<string>? forwarded = null;
        using var registered = DispatchHarness.CreateMemory(development: args => forwarded = args);
        registered.Dispatcher.Execute("help");
        StringAssert.Contains(registered.ConsoleEvents.Single(x => x.EventId == ConsoleCommandDispatcher.HelpEventId).Message, "dev");
        registered.Dispatcher.Execute("DeV open");
        CollectionAssert.AreEqual(new[] { "open" }, forwarded!.ToArray());

        using var unregistered = DispatchHarness.CreateMemory();
        unregistered.Dispatcher.Execute("help");
        Assert.IsFalse(unregistered.ConsoleEvents.Single(x => x.EventId == ConsoleCommandDispatcher.HelpEventId)
            .Message.Contains("dev", StringComparison.Ordinal));
        unregistered.Dispatcher.Execute("dev open");
        Assert.AreEqual(ConsoleCommandDispatcher.UnknownEventId, unregistered.ConsoleEvents.Last().EventId);
    }
}

[TestClass]
public sealed class LightingConsoleCommandTests
{
    [TestMethod]
    public void Lighting_is_listed_and_forwarded_in_debug_and_explicitly_rejected_in_player_builds()
    {
        IReadOnlyList<string>? forwarded = null;
        using var debug = DispatchHarness.CreateMemory(lighting: arguments => forwarded = arguments);
        debug.Dispatcher.Execute("help");
        StringAssert.Contains(debug.ConsoleEvents.Single(e => e.EventId == ConsoleCommandDispatcher.HelpEventId).Message, "lighting");
        debug.Dispatcher.Execute("LiGhTiNg night");
        CollectionAssert.AreEqual(new[] { "night" }, forwarded!.ToArray());

        using var player = DispatchHarness.CreateMemory(allowReload: false);
        player.Dispatcher.Execute("help");
        Assert.IsFalse(player.ConsoleEvents.Single(e => e.EventId == ConsoleCommandDispatcher.HelpEventId)
            .Message.Contains("lighting", StringComparison.Ordinal));
        player.Dispatcher.Execute("lighting night");
        var denial = player.ConsoleEvents.Last();
        Assert.AreEqual(ConsoleCommandDispatcher.LightingDeniedEventId, denial.EventId);
        Assert.AreEqual(LogLevel.Warning, denial.Level);
        StringAssert.Contains(denial.Message, "Debug");
    }

    [TestMethod]
    public void Lighting_queries_without_applying_and_selects_all_presets_and_restore()
    {
        using var harness = DispatchHarness.CreateMemory();
        var mode = InspectionLighting.Game;
        var applications = 0;
        var commands = new LightingConsoleCommands(harness.Logger, () => mode,
            selected => { mode = selected; applications++; });
        foreach (var (argument, expected) in new[]
        {
            ("DAYLIGHT", InspectionLighting.Daylight), ("overcast", InspectionLighting.Overcast),
            ("night", InspectionLighting.Night), ("restore", InspectionLighting.Game)
        })
        {
            commands.Execute([argument]);
            Assert.AreEqual(expected, mode);
            var applied = applications;
            commands.Execute([]);
            Assert.AreEqual(applied, applications, "查询不应重新应用光照。");
            var query = harness.ConsoleEvents.Last();
            Assert.AreEqual(LightingConsoleCommands.ModeEventId, query.EventId);
            Assert.AreEqual(expected.ToString().ToLowerInvariant(), query.Attributes["mode"]);
        }
        Assert.AreEqual(4, applications);
    }

    [TestMethod]
    public void Lighting_rejects_invalid_arguments_without_applying_or_querying_the_view()
    {
        using var harness = DispatchHarness.CreateMemory();
        var commands = new LightingConsoleCommands(harness.Logger,
            () => throw new AssertFailedException("非法参数不应查询视图。"),
            _ => throw new AssertFailedException("非法参数不应修改光照。"));
        foreach (var arguments in new[] { new[] { "game" }, new[] { "night", "extra" }, new[] { "unknown" } })
        {
            commands.Execute(arguments);
            Assert.AreEqual(LightingConsoleCommands.UsageEventId, harness.ConsoleEvents.Last().EventId);
        }
    }

    [TestMethod]
    public void Lighting_reports_unavailable_without_a_world_view()
    {
        using var harness = DispatchHarness.CreateMemory();
        var commands = new LightingConsoleCommands(harness.Logger, () => null,
            _ => throw new AssertFailedException("不存在视图时不应修改光照。"));
        commands.Execute([]);
        commands.Execute(["night"]);
        Assert.IsTrue(harness.ConsoleEvents.All(e => e.EventId == LightingConsoleCommands.UnavailableEventId));
        Assert.AreEqual(2, harness.ConsoleEvents.Count);
    }

}

[TestClass]
[TestCategory("integration")]
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
    public void Simulation_command_is_listed_and_forwarded_only_when_host_provides_it()
    {
        using var available = DispatchHarness.Create(simulation: true);
        available.Dispatcher.Execute("help");
        StringAssert.Contains(
            available.ConsoleEvents.Single(e => e.EventId == ConsoleCommandDispatcher.HelpEventId).Message,
            "sim");

        available.Dispatcher.Execute("SiM speed 4");
        CollectionAssert.AreEqual(new[] { "speed", "4" }, available.SimulationArguments!.ToArray());

        using var unavailable = DispatchHarness.Create(simulation: false);
        unavailable.Dispatcher.Execute("help");
        Assert.IsFalse(unavailable.ConsoleEvents.Single(e => e.EventId == ConsoleCommandDispatcher.HelpEventId)
            .Message.Contains("sim", StringComparison.Ordinal));
        unavailable.Dispatcher.Execute("sim status");
        Assert.AreEqual(ConsoleCommandDispatcher.UnknownEventId,
            unavailable.ConsoleEvents.Last().EventId);
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
        Assert.ThrowsExactly<ArgumentNullException>(() => harness.Dispatcher.Execute(null!));
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
    public IReadOnlyList<string>? SimulationArguments { get; private set; }

    public IReadOnlyList<LogEvent> ConsoleEvents =>
        _session.Memory.Snapshot().Where(e => e.Category == ConsoleCommandDispatcher.Category).ToArray();

    private DispatchHarness(LoggerSession session, bool allowReload, bool simulation, Action<IReadOnlyList<string>>? lighting,
        Action<IReadOnlyList<string>>? development = null)
    {
        _session = session;
        Dispatcher = new ConsoleCommandDispatcher(
            session.Logger,
            () => Cleared = true,
            () => Quit = true,
            () => Reloaded = true,
            allowReload,
            simulation ? arguments => SimulationArguments = arguments.ToArray() : null, lighting: lighting, development: development);
    }

    public static DispatchHarness Create(bool allowReload = true, bool simulation = false) =>
        new(LoggerSession.Create(), allowReload, simulation, null);

    public static DispatchHarness CreateMemory(bool allowReload = true, Action<IReadOnlyList<string>>? lighting = null,
        Action<IReadOnlyList<string>>? development = null) =>
        new(LoggerSession.CreateMemory(), allowReload, false, lighting, development);

    public void ResetActions()
    {
        Cleared = false;
        Quit = false;
        Reloaded = false;
        SimulationArguments = null;
    }

    public void Dispose() => _session.Dispose();
}
