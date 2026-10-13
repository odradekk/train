using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using static Train.Engine.Tests.Assertions;
using NVector3 = System.Numerics.Vector3;

namespace Train.Engine.Tests;

public class WorldSceneTests(Node testScene) : TestClass(testScene)
{
    private static readonly WorldSceneTarget Target = new WorldSceneTarget.Location(new WorldLocation.Node(new(1)));
    private static readonly SceneSessionId SceneId = new(Guid.Parse("00000000-0000-0000-0000-000000000319"));
    private static readonly SceneMesh Mesh = new(new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper),
        [new(NVector3.Zero, NVector3.UnitY, default, default, 0, 0, 0),
         new(NVector3.UnitZ, NVector3.UnitY, default, default, 0, 0, 0),
         new(NVector3.UnitX, NVector3.UnitY, default, default, 0, 0, 0)], [0, 1, 2]);

    [Test]
    public async Task App_injects_the_scene_factory_and_presents_on_real_process_frames()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup();
        var scene = Session(default, Enumerable.Range(1, 12).Select(i => Success(Frame((ulong)i))).ToImmutableArray());
        var factory = Factory(binding => Session(binding, scene.PresentResults));
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = setup.Create;
            app.CreateScenes = () => factory;
            await fixture.AddToRoot(app);
            await Until(() => app.Host!.GetStatus() is not null);
            app.WorldScenes!.Open(Target);
            Require(app.WorldScenes.View.Loading && !app.WorldScenes.View.Interactive, "首帧接入之前必须加载且禁止交互。");
            Require(app.IsProcessing(), "App未启用_Process。");
            await Until(() => app.WorldScenes.Display?.DisplayedFrame is not null);
            app.SetProcess(false);
            Require(!app.WorldScenes.View.Loading && app.WorldScenes.View.Interactive, "实际_Process没有完成场景接入。");
            Require(factory.OpenCalls.Count == 1 && factory.OpenCalls[0].Snapshot.Core == setup.Session.Read(), "场景未使用活动固定读视图。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Loading_base_mismatch_and_minimization_keep_the_last_display_boundary()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var first = Frame(1);
        var loading = Frame(2, readiness: SceneReadiness.Loading);
        var badBase = Frame(3, baseline: Stamp(90));
        var replacement = Frame(4);
        FixedDisplaySession? session = null;
        try
        {
            var app = await Ready(fixture, new Setup(), Factory(binding => session = Session(binding,
                [Success(first), Success(loading), Success(badBase), Success(replacement)])));
            var world = app.WorldScenes!;
            world.Open(Target);
            world.ProcessFrame(0, false);
            world.ProcessFrame(0, true);
            Require(session!.PresentCalls.Count == 1, "最小化仍调用了Present。");
            world.ProcessFrame(0, false);
            Require(ReferenceEquals(world.Display!.DisplayedFrame, first), "Loading替换了旧显示帧。");
            world.ProcessFrame(0, false);
            Require(ReferenceEquals(world.Display.DisplayedFrame, first), "Base不符部分接入了更新。");
            world.ProcessFrame(0, false);
            Require(session.PresentCalls[^1].DisplayedFrame is null && world.Display.DisplayedFrame == replacement,
                "Base不符后没有用null请求完整替换。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Failed_faulted_failure_and_resource_errors_close_the_scene_and_allow_reopening()
    {
        var problem = new SceneProblem("build_failed", SceneStage.Build, null,
            new ContentReference.Design(new(new string('D', 64))), new("fixture-package"), null, "原始诊断文本，不作为协议解析。");
        var unavailable = new AssetRevision(new string('E', 64));
        foreach (var (failure, expectedCode) in new (SceneResult<ISceneFrame>, string)[]
        {
            (Success(Frame(2, readiness: SceneReadiness.Failed, diagnostics: [problem])), "build_failed"),
            (new SceneResult<ISceneFrame>.Faulted(problem), "build_failed"),
            (new SceneResult<ISceneFrame>.Failure([problem]), "build_failed"),
            (Success(Frame(2, objects: [Object(1, new SceneVisual.Model(unavailable, [], null,
                new(false, 0, 0, false), default))])), "scene_adapter_failed")
        })
        {
            var fixture = new Fixture(TestScene.GetTree());
            var sessions = new List<FixedDisplaySession>();
            var closeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = Factory(binding =>
            {
                var id = sessions.Count == 0 ? SceneId : new SceneSessionId(Guid.NewGuid());
                var next = Session(binding, sessions.Count == 0 ? [Success(Frame(1)), failure] : [Success(Frame(3, session: id))],
                    sessions.Count == 0 ? closeGate.Task : null, id: id);
                sessions.Add(next);
                return next;
            });
            try
            {
                var app = await Ready(fixture, new Setup
                {
                    Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>
                        { [unavailable] = new ContentLookup<ContentAsset>.NotFound() }
                }, factory);
                SceneProblem? reason = null;
                var world = app.WorldScenes!;
                world.ReturnedToMap += value => reason = value;
                world.Open(Target);
                world.ProcessFrame(0, false);
                var oldRoot = world.Display!.Root;
                world.ProcessFrame(0, false);
                Require(world.Display is null && !world.View.Visible && !world.View.Interactive && sessions[0].IsDisposed,
                    "故障没有隐藏场景、停止交互和关闭会话。");
                Require(!GodotObject.IsInstanceValid(oldRoot) && reason?.Code == expectedCode,
                    $"故障必须报告{expectedCode}并退役旧组，不能用其他异常替代。");
                var consoleLogs = app.GetChildren().OfType<Train.Ui.GameConsole>().Single().LogEvents;
                Require(!consoleLogs.Any(log => log.Exception?.Message.Contains("UNCONFIGURED", StringComparison.Ordinal) == true),
                    "未配置的替身调用不能充当预期故障。");
                if (expectedCode == problem.Code)
                {
                    var logged = consoleLogs.Single(log =>
                        log.EventId == "scene_problem" && Equals(log.Attributes["code"], problem.Code));
                    Require(logged.Message == problem.Detail && Equals(logged.Attributes["package"], problem.Package!.Value.Value) &&
                        Equals(logged.Attributes["content"], problem.Content!.ToString()), "诊断没有保留Code、Content、Package和原始Detail。");
                }
                world.Open(Target);
                world.ProcessFrame(0, false);
                Require(world.Display?.DisplayedFrame is not null && !closeGate.Task.IsCompleted,
                    "重开被旧会话关闭等待阻塞。");
                closeGate.SetResult();
                world.ProcessFrame(0, true);
            }
            finally { closeGate.TrySetResult(); await fixture.Cleanup(); }
        }
    }

    [Test]
    public async Task Picking_uses_the_displayed_camera_frame_after_origin_shift_and_resolves_before_selection()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var hit = new SceneHit(Stamp(1), new(1), new PickSubject.Terrain(new WorldLocation.Node(new(1))),
            default, GridFace.PositiveY, default, NVector3.UnitY, 1, []);
        var first = Frame(1, pick: new ScenePick.Hit(hit));
        var shifted = Frame(2, baseline: first.Update.Frame, origin: new(100, 0, 0), shift: new(-100, 0, 0),
            pick: new ScenePick.Hit(hit with { Frame = Stamp(2) }));
        var secondHit = ((ScenePick.Hit)shifted.PickResult!).Value;
        FixedDisplaySession? session = null;
        var factory = Factory(binding => session = Session(binding, [Success(first), Success(shifted),
            Success(Frame(3, pick: new ScenePick.NotReady()))], resolve:
            new Dictionary<SceneHit, SceneResult<PickSubject>> { [hit] = new SceneResult<PickSubject>.Success(hit.Subject),
                [secondHit] = new SceneResult<PickSubject>.Success(secondHit.Subject) }.ToImmutableDictionary()));
        try
        {
            var app = await Ready(fixture, new Setup(), factory);
            var world = app.WorldScenes!;
            var selected = 0;
            world.Selected += _ => selected++;
            world.Open(Target);
            world.ProcessFrame(0, false);
            var center = TestScene.GetViewport().GetVisibleRect().Size / 2;
            world.Pick(center);
            var oldRay = first.LastPickRay!;
            world.ProcessFrame(0, false);
            world.Pick(center);
            Require(selected == 2 && session!.ResolveCalls.SequenceEqual(new[] { hit, secondHit }), "命中没有经当前会话Resolve交给宿主事件。");
            Require(NVector3.Distance(shifted.LastPickRay!.Origin, oldRay.Origin + new NVector3(-100, 0, 0)) < 0.01f &&
                NVector3.Distance(shifted.LastPickRay.Direction, oldRay.Direction) < 0.001f, "原点移动后射线基准漂移。");
            world.ProcessFrame(0, false);
            world.Pick(center);
            Require(selected == 2 && session!.ResolveCalls.Count == 2, "NotReady被当作可选择空地。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Accepted_reload_reopens_with_loading_and_rejected_reload_keeps_the_old_picture()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup();
        var sessions = new List<FixedDisplaySession>();
        var factory = Factory(binding =>
        {
            var id = sessions.Count == 0 ? SceneId : new SceneSessionId(Guid.NewGuid());
            var session = Session(binding, [Success(Frame((ulong)sessions.Count + 1, session: id))],
                observe: [new SceneResult<SimulationPosition>.Success(new(0, 1))], id: id);
            sessions.Add(session);
            return session;
        });
        try
        {
            var app = await Ready(fixture, setup, factory);
            var world = app.WorldScenes!;
            world.Open(Target);
            world.ProcessFrame(0, false);
            var old = world.Display!.DisplayedFrame;
            var changedInput = new CoreInputId(new string('B', 64));
            var rejectedContent = new FixedContentView(HostContentScenario.CreateProjection(changedInput), new(2));
            setup.Reload = new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(rejectedContent, changes: ContentChange.CoreInput));
            var rejectedView = new FixedCoreReadView(new(0, 1), HostCoreScenario.Input);
            setup.Session.QueueSubmit(new CoreCommand.PublishContent(rejectedContent.Core), CommandSource.Console,
                new SubmissionResult.Completed(new(1, new(0, 0), new(0, 1), new CommandDecision.Rejected([new("rejected", "field", null, ["reason"])])), [], null), rejectedView);
            app.Host!.BeginReload(CommandSource.Console);
            await Pump(app.Host);
            Require(world.Display.DisplayedFrame == old && sessions.Count == 1 && !sessions[0].IsDisposed,
                "拒绝重载更换了显示组。");
            var nextContent = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(2));
            setup.Reload = new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(nextContent, changes: ContentChange.Presentation));
            app.Host.BeginReload(CommandSource.Console);
            await Pump(app.Host);
            Require(sessions[0].IsDisposed && sessions.Count == 2 && world.View.Loading && !world.View.Interactive,
                "有效重载没有更换绑定、关闭旧场景和加载。");
            world.Pick(Vector2.Zero);
            Require(sessions[0].ResolveCalls.Count == 0 && factory.OpenCalls[0].Binding != factory.OpenCalls[1].Binding,
                "旧绑定仍可拾取或新会话沿用了旧绑定。");
            world.ProcessFrame(0, false);
            Require(world.Display!.DisplayedFrame != old && world.View.Interactive, "重载后的新帧未接入。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Unhandled_mouse_input_respects_a_control_covering_the_world()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await Ready(fixture, new Setup(), Factory(binding => Session(binding, [Success(Frame(1, pick: new ScenePick.Miss()))])));
            var world = app.WorldScenes!;
            world.Open(Target);
            world.ProcessFrame(0, false);
            var picks = 0;
            world.View.PickRequested += _ => picks++;
            var cover = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
            TestScene.AddChild(cover);
            cover.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            try
            {
                var viewport = TestScene.GetViewport();
                var position = viewport.GetVisibleRect().Size / 2;
                viewport.PushInput(new InputEventMouseButton { Position = position, GlobalPosition = position,
                    ButtonIndex = MouseButton.Left, Pressed = true }, true);
                Require(picks == 0, "UI遮挡鼠标仍进入了世界_UnhandledInput。");
                cover.Free();
                world.View._UnhandledInput(new InputEventMouseButton { Position = position, ButtonIndex = MouseButton.Left, Pressed = true });
                Require(picks == 1, "未遮挡点击没有交给拾取入口。");
                world.ReturnToMap();
                var exits = 0;
                world.ReturnedToMap += _ => exits++;
                world.View._UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
                Require(exits == 0, "隐藏的世界视图仍消费地图的Escape。");
            }
            finally { if (GodotObject.IsInstanceValid(cover)) cover.Free(); }
        }
        finally { await fixture.Cleanup(); }
    }

    private async Task<Train.App.App> Ready(Fixture fixture, Setup setup, FixedDisplayFactory factory)
    {
        var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
        app.CreateHost = setup.Create;
        app.CreateScenes = () => factory;
        await fixture.AddToRoot(app);
        await Until(() => app.Host!.GetStatus() is not null);
        app.SetProcess(false);
        return app;
    }
    private async Task Until(Func<bool> complete)
    {
        var limit = System.Diagnostics.Stopwatch.StartNew();
        while (!complete() && limit.Elapsed < TimeSpan.FromSeconds(30))
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
        Require(complete(), "挂起保护超时：宿主准备未完成。");
    }
    private async Task Pump(HostCoordinator host)
    {
        await Until(() => { host.ProcessFrame(0); return !host.IsBusy; });
    }
    private static FixedDisplayFactory Factory(Func<SceneBinding, FixedDisplaySession> open) => new()
    { OpenWorldCall = (binding, _) => new SceneResult<IWorldSceneSession>.Success(open(binding)) };
    private static FixedDisplaySession Session(SceneBinding binding, ImmutableArray<SceneResult<ISceneFrame>> frames,
        Task? close = null, ImmutableDictionary<SceneHit, SceneResult<PickSubject>>? resolve = null,
        ImmutableArray<SceneResult<SimulationPosition>> observe = default, SceneSessionId? id = null) => new()
    {
        Id = id ?? SceneId, Binding = binding, PresentResults = frames, DisposeGate = close,
        RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty.Add(Target,
            new SceneResult<SceneRequestId>.Success(new(1))),
        ResolveResults = resolve ?? ImmutableDictionary<SceneHit, SceneResult<PickSubject>>.Empty,
        ObserveResults = observe.IsDefault ? [] : observe
    };
    private static SceneResult<ISceneFrame> Success(ISceneFrame frame) => new SceneResult<ISceneFrame>.Success(frame);
    private static SceneFrameStamp Stamp(ulong revision, SceneSessionId? session = null) => new(session ?? SceneId, new(1), new(1), revision);
    private static SceneObject Object(ulong id, SceneVisual? visual = null) => new(new(id), null,
        new(NVector3.Zero, System.Numerics.Quaternion.Identity), null,
        [visual ?? new SceneVisual.Geometry(Mesh, new(false, 0, 0, false))], [], []);
    private static FixedSceneFrame Frame(ulong revision, SceneFrameStamp? baseline = null,
        SceneReadiness readiness = SceneReadiness.Ready, ImmutableArray<SceneObject> objects = default,
        ScenePoint origin = default, NVector3 shift = default, ScenePick? pick = null,
        ImmutableArray<SceneProblem> diagnostics = default, SceneSessionId? session = null) => new()
    {
        Update = new(baseline, Stamp(revision, session), shift, objects.IsDefault ? [Object(1)] : objects, [], new([], []), []),
        Origin = origin, Readiness = readiness, PickResult = pick,
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics,
        TerrainHeightResult = new Lookup<double>.Missing()
    };

    private sealed class Setup
    {
        internal ScriptedCoreSession Session { get; } = new(new FixedCoreReadView(new(0, 0), HostCoreScenario.Input));
        internal ContentResult<IContentCandidate>? Reload { get; set; }
        internal IReadOnlyDictionary<AssetRevision, ContentLookup<ContentAsset>> Assets { get; init; } =
            ImmutableDictionary<AssetRevision, ContentLookup<ContentAsset>>.Empty;
        internal HostCoordinator Create(TrainLogger logger)
        {
            var content = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1)) { Assets = Assets };
            var capture = new FixedCoreCapture(new(new(0, 0), "fixed", "fixed"), HostCoreScenario.References,
                "opaque", HostCoreScenario.Input, new(1));
            Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(capture));
            var core = new ScriptedCoreFactory
            {
                DecodeStateCall = _ => new CoreResult<ICoreCapture>.Success(capture),
                PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(new ScriptedCoreCandidate(Session, HostCoreScenario.References))
            };
            var factory = new ScriptedContentFactory
            {
                DecodeCaptureCall = _ => new ContentResult<IContentCapture>.Success(new FixedContentCapture("opaque")),
                PrepareLoadCall = (request, _, _) => request.Purpose is ContentLoadPurpose.Restore
                    ? new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(content))
                    : Reload ?? throw new InvalidOperationException("UNCONFIGURED: 重载结果。")
            };
            var host = new HostCoordinator(core, factory, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
                new(new("builtin"), new("mods"), null, "fixture", []), logger);
            host.BeginRestore("opaque", "opaque", new(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1));
            return host;
        }
    }
}
