using System.Collections.Immutable;
using System.Text.Json;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class HostSimulationTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Process_frames_and_console_drive_the_contract_session_on_the_main_thread()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new HostSetup();
        setup.QueueSteps(200);
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            var owner = System.Environment.CurrentManagedThreadId;
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await FramesUntil(() => app.Host!.GetStatus() is not null);
            setup.AssertPrepared();
            Require(setup.PreparationThread != owner && setup.Candidate.ActivationThread == owner,
                "候选未在后台准备并由 Godot 主线程激活。");
            var host = app.Host!;
            Require(host.GetStatus()!.PlayerPaused, "读档候选激活后没有暂停。");
            var console = app.GetChildren().OfType<GameConsole>().Single();
            console.SubmitLine("sim resume");
            await FramesUntil(() => host.GetStatus()!.View.Position.CompletedSteps > 0);
            console.SubmitLine("sim pause");
            var before = host.GetStatus()!.View.Position.CompletedSteps;
            console.SubmitLine("sim step 2");
            Require(host.GetStatus()!.View.Position.CompletedSteps == before + 2 && setup.Session.AdvanceThread == owner,
                "控制台与真实帧未在主线程驱动同一契约会话。");
            console.SubmitLine("sim status");
            var checkpoint = new CoreCheckpoint(host.GetStatus()!.View.Position, "fixed", "engine checkpoint sha");
            setup.Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(new FixedCoreCapture(
                checkpoint, HostCoreScenario.References, "opaque checkpoint", HostCoreScenario.Input, new(1))));
            console.SubmitLine("sim checkpoint");
            Require(Logs(app, "simulation_status").Length == 1 && Logs(app, "simulation_stepped").Length == 1,
                "迁移后的控制台结果没有进入 JSONL。");
            Require(Logs(app, "simulation_checkpoint").Single().GetProperty("attributes").GetProperty("sha256").GetString() == checkpoint.Sha256,
                "契约检查点没有经控制台写入真实日志。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Process_frame_stops_at_the_first_notification_pause()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new HostSetup();
        setup.Session.QueueAdvance(new(1, new(1, 0),
            [new(new(1, 0), 0, new CoreEvent.TrainArrived(new(1), new(1)))], null, null),
            EmptyView(new(1, 0)));
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger, _ => NotificationAction.Pause);
            await fixture.AddToRoot(app);
            await FramesUntil(() => app.Host!.GetStatus() is not null);
            setup.AssertPrepared();
            var host = app.Host!;
            var boundaries = 0;
            host.BoundaryCompleted += (_, _, _, events) => { if (events.Length > 0) boundaries++; };
            host.SetSpeed(Train.App.SimulationSpeed.Eight);
            host.SetPaused(false);
            await FramesUntil(() => host.GetStatus()!.PlayerPaused);
            for (var frame = 0; frame < 3; frame++) await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(host.GetStatus()!.View.Position.CompletedSteps == 1 && setup.Session.AdvanceCount == 1 && boundaries == 1,
                "真实帧跨过通知暂停边界或重复分发事件。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task App_focus_notifications_preserve_other_pause_sources()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new HostSetup();
        setup.QueueSteps(200);
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await FramesUntil(() => app.Host!.GetStatus() is not null);
            setup.AssertPrepared();
            var host = app.Host!;
            app.Notification((int)Node.NotificationApplicationFocusIn);
            host.SetPaused(false);
            app.Notification((int)Node.NotificationApplicationFocusOut);
            Require(!host.GetStatus()!.IsPaused, "默认失焦自动暂停未关闭。");
            host.AutoPauseOnFocusLoss = true;
            Require(host.GetStatus()!.FocusPaused, "已失焦时开启自动暂停没有记账。");
            var before = host.GetStatus()!.View.Position;
            for (var frame = 0; frame < 3; frame++) await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(host.GetStatus()!.View.Position == before, "失焦暂停期间仍驱动核心。");
            host.SetPaused(true);
            app.Notification((int)Node.NotificationApplicationFocusIn);
            Require(!host.GetStatus()!.FocusPaused && host.GetStatus()!.PlayerPaused,
                "聚焦通知清除了玩家暂停。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Contract_fault_is_logged_once_and_remains_frozen_across_frames()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new HostSetup();
        var fault = new CoreFault("engine_fixture_fault", new(0, 0), "step", "fixture", new WorldObjectId.Train(new(1)));
        setup.Session.QueueAdvance(new(0, new(0, 0), [], fault, new("fixed message", "fixed stack")),
            EmptyView(new(0, 0), fault));
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await FramesUntil(() => app.Host!.GetStatus() is not null);
            setup.AssertPrepared();
            app.Host!.SetPaused(false);
            await FramesUntil(() => app.Host.GetStatus()!.IsFrozen);
            var console = app.GetChildren().OfType<GameConsole>().Single();
            console.SubmitLine("sim resume");
            console.SubmitLine("sim step");
            for (var frame = 0; frame < 3; frame++) await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
            var logs = Logs(app, "simulation_fault");
            Require(logs.Length == 1 && logs[0].GetProperty("attributes").GetProperty("diagnostic_stack_trace").GetString() == "fixed stack",
                "核心契约诊断未记录一次或丢失诊断栈。");
            Require(app.Host.GetStatus()!.IsFrozen && setup.Session.AdvanceCount == 1,
                "冻结帧或控制台再次驱动了核心。");
        }
        finally { await fixture.Cleanup(); }
    }

    private async Task FramesUntil(Func<bool> completed)
    {
        for (var frame = 0; frame < 180 && !completed(); frame++)
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
        Require(completed(), "真实 Godot 帧未接入预期结果。");
    }

    private static JsonElement[] Logs(Train.App.App app, string eventId)
    {
        using var stream = new FileStream(app.Logger.JsonlPath!, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
            .Where(log => log.GetProperty("event_id").GetString() == eventId).ToArray();
    }

    private static FixedCoreReadView EmptyView(SimulationPosition position, CoreFault? fault = null) =>
        new(position, HostCoreScenario.Input, fault)
        { World = new(0, new("fixture:railway"), 100, 100, [], [], [], [], []) };

    internal sealed class HostSetup
    {
        internal ScriptedContentFactory? ContentFactory { get; private set; }
        internal Func<IContentView, ImmutableArray<OwnedSettlementTemplateReference>, IContentView>? Copies { get; set; }
        internal ScriptedCoreSession Session { get; }
        internal ScriptedCoreCandidate Candidate { get; }
        internal ScriptedCoreCandidate? ReplacementCandidate { get; set; }
        internal int PreparationThread { get; private set; }
        private CoreContentProjection? _restoreProjection;
        private CoreSessionOptions? _restoreOptions;
        private readonly CoreSessionOptions _options = new(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1);
        private readonly IContentView? _contentView;
        internal HostSetup(ICoreReadView? initial = null, IContentView? content = null)
        {
            _contentView = content;
            Session = new(initial ?? EmptyView(new(0, 0)));
            Candidate = new(Session, HostCoreScenario.References);
        }
        internal void QueueSteps(int count)
        {
            for (var step = 1u; step <= count; step++)
                Session.QueueAdvance(new(1, new(step, 0), [], null, null), EmptyView(new(step, 0)));
        }
        internal HostCoordinator Create(TrainLogger logger, Func<CoreEvent, NotificationAction>? policy = null)
        {
            var view = _contentView ?? new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1));
            var capture = new FixedCoreCapture(new(new(0, 0), "fixed", "fixed"), HostCoreScenario.References,
                "opaque", HostCoreScenario.Input, new(1));
            Session.ReadRecordingCall = () => new(new(0, 0), [new(new(1), capture)], [], [capture.Checkpoint], [], [], capture.References);
            Session.ConfirmRecordingStartCall = (point, _) => point.Value == 1 ? new([], [])
                : throw new InvalidOperationException("UNCONFIGURED: recording point");
            var core = new ScriptedCoreFactory
            {
                DecodeStateCall = payload =>
                {
                    PreparationThread = System.Environment.CurrentManagedThreadId;
                    if (payload != "opaque core") throw new InvalidOperationException("UNCONFIGURED: payload");
                    return new CoreResult<ICoreCapture>.Success(capture);
                },
                PrepareRestoreCall = (_, projection, options) =>
                {
                    _restoreProjection = projection;
                    _restoreOptions = options;
                    return new CoreResult<ICoreSessionCandidate>.Success(Candidate.ActivationCount == 0
                        ? Candidate : ReplacementCandidate ?? throw new InvalidOperationException("UNCONFIGURED: replacement restore"));
                }
            };
            var content = ContentFactory = new ScriptedContentFactory
            {
                WithSettlementCopiesCall = Copies,
                CaptureReplayCall = (history, _) => new ContentResult<IContentReplayCapture>.Success(new FixedContentReplayCapture(history, "engine recording content")),
                DecodeCaptureCall = payload => payload == "opaque content"
                    ? new ContentResult<IContentCapture>.Success(new FixedContentCapture(payload)) : throw new InvalidOperationException("UNCONFIGURED: payload"),
                PrepareLoadCall = (request, _, _) => request.Purpose is ContentLoadPurpose.Restore
                    ? new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(view)) : throw new InvalidOperationException("UNCONFIGURED: purpose")
            };
            var host = new HostCoordinator(core, content, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
                new(new("builtin"), new("mods"), null, "fixture", []), logger, policy, sessionOptions: _options);
            host.BeginRestore("opaque core", "opaque content", _options);
            return host;
        }

        internal void AssertPrepared()
        {
            Require(_restoreProjection?.Identity == HostCoreScenario.Input && _restoreOptions == _options,
                "读档准备没有收到预期内容投影与核心选项。");
        }
    }
}
