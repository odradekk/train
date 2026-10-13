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

public class WorldSceneTargetTests(Node testScene) : TestClass(testScene)
{
    private static readonly SceneSessionId SceneId = new(Guid.Parse("00000000-0000-0000-0000-000000319001"));
    private static readonly TrainId Train = new(1);
    private static readonly WorldSceneTarget Follow3 = new WorldSceneTarget.Follow(Train, new(3));
    private static readonly WorldSceneTarget Follow1 = new WorldSceneTarget.Follow(Train, new(1));
    private static readonly SceneProblem Missing = new("target_missing", SceneStage.Present,
        Subject(3), null, null, null, "目标车辆已消失。");

    [Test]
    public async Task Switching_followed_vehicles_reuses_the_session_and_faulted_requests_return_to_map()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var follow2 = new WorldSceneTarget.Follow(Train, new(2));
        var fault = new SceneProblem("scene_faulted", SceneStage.Input, Subject(2), null, null, null, "观察目标请求故障。");
        FixedDisplaySession? session = null;
        var factory = Factory(binding => session = new()
        {
            Id = SceneId, Binding = binding,
            RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty
                .Add(Follow3, new SceneResult<SceneRequestId>.Success(new(1)))
                .Add(Follow1, new SceneResult<SceneRequestId>.Success(new(2)))
                .Add(follow2, new SceneResult<SceneRequestId>.Faulted(fault)),
            PresentResults = [Success(Frame(1, 1, 1, 3, new(10, 0, 20))),
                Success(Frame(2, 2, 1, 1, new(110, 0, 20))), Success(Frame(3, 2, 1, 1, new(110, 0, 20)))]
        });
        try
        {
            var app = await Ready(fixture, new Setup(true), factory);
            var world = app.WorldScenes!;
            world.Open(Follow3);
            world.ProcessFrame(0, false);
            var focus = world.View.CameraRig.Focus;
            var cameraPosition = world.View.CameraRig.Camera.Position;
            world.Open(Follow1);
            Require(factory.OpenCalls.Count == 1 && !session!.IsDisposed && !world.View.Loading && !world.View.Interactive,
                "同列车切换车辆应复用会话，禁用交互且不显示加载罩。");
            world.ProcessFrame(0, false);
            Require(world.View.Interactive && !world.View.Loading && session!.PresentCalls[^1].DisplayedFrame is null,
                "换车辆的Ready首帧没有完整接入，或出现了加载罩。");
            Near(world.View.CameraRig.Focus, focus);
            Near(world.View.CameraRig.Camera.Position, cameraPosition);
            world.ProcessFrame(0.05, false);
            Require(world.View.CameraRig.Focus.X > focus.X && world.View.CameraRig.Focus.X < 110 &&
                factory.OpenCalls.Count == 1 && !world.View.Loading,
                "同空间换车辆没有延续相机焦点并逐帧平滑。");
            SceneProblem? reason = null;
            world.ReturnedToMap += problem => reason = problem;
            world.Open(follow2);
            Require(reason == fault && session!.IsDisposed && world.Display is null && !world.View.Visible,
                "Faulted目标请求没有报告故障并退出场景。");
            Require(session!.RequestCalls.SequenceEqual(new[] { Follow3, Follow1, follow2 }),
                "换车辆或故障请求没有经过当前会话RequestView。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Missing_target_selects_smallest_valid_vehicle_and_changes_space_without_loading()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup(true);
        FixedDisplaySession? session = null;
        var first = Frame(1, 1, 1, 3, new(10, 0, 20));
        var next = Frame(2, 2, 2, 1, new(-500, 0, 700));
        try
        {
            var app = await Ready(fixture, setup, Factory(binding => session = Session(binding,
                [Success(first), new SceneResult<ISceneFrame>.Failure([Missing]), Success(next)])));
            var world = app.WorldScenes!;
            world.Open(Follow3);
            world.ProcessFrame(0, false);
            world.View.CameraRig.Orbit(80, 15);
            world.View.CameraRig.Zoom(1);
            var offset = world.View.CameraRig.Camera.Position - world.View.CameraRig.Focus;
            var forward = world.View.CameraRig.Camera.Basis.Z;
            Require(app.Host!.Step(1).Completed == 1, "未推进目标消失的固定核心边界。");
            Require(session!.ObserveCalls.Count == 1 && session.ObserveCalls[0].Snapshot.Core == setup.Session.Read(),
                "场景没有观察目标消失后的同一固定读视图。");
            world.ProcessFrame(0, false);
            Require(session.RequestCalls.SequenceEqual(new[] { Follow3, Follow1 }),
                "必须按车辆ID最小选择替代，不能按编组数组顺序选择2。");
            Require(app.GetChildren().OfType<Train.Ui.GameConsole>().Single().LogEvents.Any(log =>
                log.EventId == "scene_problem" && Equals(log.Attributes["code"], Missing.Code)),
                "成功回退车辆时也必须记录目标消失的SceneProblem。");
            Require(!world.View.Loading && !world.View.Interactive && world.Display!.DisplayedFrame == first,
                "切换请求期间应保留旧显示帧、禁用交互且不显示加载罩。");
            world.ProcessFrame(0, false);
            Require(session.PresentCalls[^1].DisplayedFrame is null && world.Display!.DisplayedFrame == next,
                "替代目标的新请求未以完整帧接入。");
            Require(!world.View.Loading && world.View.Interactive, "跟随跨空间切换不能出现加载罩。");
            Near(world.View.CameraRig.Focus, new(-500, 0, 700));
            Near(world.View.CameraRig.Camera.Position - world.View.CameraRig.Focus, offset);
            Near(world.View.CameraRig.Camera.Basis.Z, forward);
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Missing_target_without_surviving_vehicles_returns_to_map()
    {
        var fixture = new Fixture(TestScene.GetTree());
        FixedDisplaySession? session = null;
        try
        {
            var app = await Ready(fixture, new Setup(false), Factory(binding => session = Session(binding,
                [Success(Frame(1, 1, 1, 3, NVector3.Zero)), new SceneResult<ISceneFrame>.Failure([Missing])])));
            var world = app.WorldScenes!;
            SceneProblem? reason = null;
            world.ReturnedToMap += problem => reason = problem;
            world.Open(Follow3);
            world.ProcessFrame(0, false);
            Require(app.Host!.Step(1).Completed == 1, "未推进目标消失边界。");
            world.ProcessFrame(0, false);
            Require(reason == Missing && world.Display is null && !world.View.Visible && !world.View.Interactive,
                "无有效车辆时必须返回地图并报告目标消失。");
            Require(session!.IsDisposed && session.RequestCalls.SequenceEqual(new[] { Follow3 }),
                "无有效车辆时不能请求替代目标或保留旧场景会话。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Failed_frame_with_target_missing_diagnostic_closes_instead_of_requesting_replacement()
    {
        var fixture = new Fixture(TestScene.GetTree());
        FixedDisplaySession? session = null;
        try
        {
            var failed = Frame(2, 1, 1, 1, NVector3.Zero, SceneReadiness.Failed, [Missing]);
            var app = await Ready(fixture, new Setup(true), Factory(binding => session = Session(binding,
                [Success(Frame(1, 1, 1, 3, NVector3.Zero)), Success(failed)])));
            var world = app.WorldScenes!;
            SceneProblem? reason = null;
            world.ReturnedToMap += problem => reason = problem;
            world.Open(Follow3);
            world.ProcessFrame(0, false);
            Require(app.Host!.Step(1).Completed == 1, "未推进目标消失边界。");
            world.ProcessFrame(0, false);
            Require(reason == Missing && world.Display is null && !world.View.Interactive && session!.IsDisposed,
                "Failed帧必须关闭场景，不能当作正常目标消失继续观察。");
            Require(session!.RequestCalls.SequenceEqual(new[] { Follow3 }), "Failed帧不能触发替代目标请求。");
        }
        finally { await fixture.Cleanup(); }
    }

    private async Task<Train.App.App> Ready(Fixture fixture, Setup setup, FixedDisplayFactory factory)
    {
        var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
        app.CreateHost = setup.Create;
        app.CreateScenes = () => factory;
        await fixture.AddToRoot(app);
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (app.Host!.GetStatus() is null && timeout.Elapsed < TimeSpan.FromSeconds(30))
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
        Require(app.Host.GetStatus() is not null, "挂起保护超时：宿主准备未完成。");
        app.SetProcess(false);
        return app;
    }

    private static FixedDisplayFactory Factory(Func<SceneBinding, FixedDisplaySession> open) => new()
    { OpenWorldCall = (binding, _) => new SceneResult<IWorldSceneSession>.Success(open(binding)) };

    private static FixedDisplaySession Session(SceneBinding binding,
        ImmutableArray<SceneResult<ISceneFrame>> frames) => new()
    {
        Id = SceneId, Binding = binding, PresentResults = frames,
        RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty
            .Add(Follow3, new SceneResult<SceneRequestId>.Success(new(1)))
            .Add(Follow1, new SceneResult<SceneRequestId>.Success(new(2))),
        ObserveResults = [new SceneResult<SimulationPosition>.Success(new(1, 0))]
    };

    private static SceneResult<ISceneFrame> Success(ISceneFrame frame) => new SceneResult<ISceneFrame>.Success(frame);
    private static WorldObjectId Subject(ulong vehicle) => new WorldObjectId.Instance(new InstanceId.Vehicle(new(vehicle)));
    private static FixedSceneFrame Frame(ulong revision, ulong request, ulong space, ulong vehicle,
        NVector3 position, SceneReadiness readiness = SceneReadiness.Ready,
        ImmutableArray<SceneProblem> diagnostics = default) => new()
    {
        Update = new(null, new(SceneId, new(request), new(space), revision), NVector3.Zero,
            [], [], new([], []), []),
        Readiness = readiness, SimulationSeconds = revision == 1 ? 0 : 1d / 60,
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics,
        Poses = ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty.Add(Subject(vehicle),
            new Lookup<ScenePose>.Found(new(position, System.Numerics.Quaternion.Identity))),
        TerrainHeightResult = new Lookup<double>.Missing()
    };

    private static FixedCoreReadView View(bool alternatives, bool initial) => new(new(initial ? 0UL : 1UL, 0), HostCoreScenario.Input)
    {
        Trains = new Dictionary<TrainId, Lookup<TrainView>>
        {
            [Train] = new Lookup<TrainView>.Found(new(Train, new TrainPosition.Node(new(1)), TrainActivity.Stopped,
                [new(3), new(2), new(1)], new(1), 0, 30, 10, 0, 0, 0, null, null, 0,
                new(TargetPriority.ArmedFirst, TargetScope.ArmedOnly, EncounterAction.Engage, 0.2),
                [], null, [], new(null, null), TrainOrderFacing.FirstEndLeading))
        },
        Instances = Enumerable.Range(1, 3).ToDictionary(index => (InstanceId)new InstanceId.Vehicle(new((ulong)index)),
            index => initial && index == 3 || alternatives && index < 3
                ? (Lookup<InstanceView>)new Lookup<InstanceView>.Found(new(new InstanceId.Vehicle(new((ulong)index)),
                    new("fixture-design"), new("fixture-composition"), new WorldLocation.Node(new(1)),
                    SettlementRelation.PlayerOwned, new InstanceDetails.Vehicle(SizeClass.Small, Train),
                    InstanceLock.None, new(100, 100), [], []))
                : new Lookup<InstanceView>.Missing())
    };

    private static void Near(Vector3 actual, Vector3 expected) => Require(actual.DistanceTo(expected) < 0.001f,
        $"预期 {expected}，实际 {actual}。");

    private sealed class Setup
    {
        internal ScriptedCoreSession Session { get; }
        internal Setup(bool alternatives)
        {
            Session = new(View(alternatives, true));
            Session.QueueAdvance(new(1, new(1, 0), [], null, null), View(alternatives, false));
        }

        internal HostCoordinator Create(TrainLogger logger)
        {
            var content = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1));
            return WorldSceneFixture.CreateHost(Session, content, logger);
        }
    }
}
