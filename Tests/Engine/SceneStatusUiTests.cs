using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Logging;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;
using NVector3 = System.Numerics.Vector3;

namespace Train.Engine.Tests;

public class SceneStatusUiTests(Node testScene) : TestClass(testScene)
{
    private static readonly SceneSessionId SceneId = new(Guid.Parse("00000000-0000-0000-0000-000000000686"));
    private static readonly InstanceId Building = new InstanceId.Building(new(13));
    private static readonly InstanceId Vehicle = new InstanceId.Vehicle(new(11));
    private static readonly InstanceId Behind = new InstanceId.Vehicle(new(12));
    private static readonly InstanceId Missing = new InstanceId.Vehicle(new(99));
    private static readonly WorldLocation NodeLocation = new WorldLocation.Node(new(1));
    private static readonly WorldLocation EdgeLocation = new WorldLocation.Edge(new(1), 120);
    private static readonly WorldSceneTarget NodeTarget = new WorldSceneTarget.Location(NodeLocation);
    private static readonly WorldSceneTarget EdgeTarget = new WorldSceneTarget.Location(EdgeLocation);

    [Test]
    public async Task Scene_markers_follow_hover_selection_and_the_viewed_location()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup();
        var scale = TestScene.GetTree().Root.ContentScaleFactor;
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = setup.Create;
            app.CreateScenes = () => setup.Factory;
            await fixture.AddToRoot(app);
            await Until(() => app.Host!.GetStatus() is not null);
            app.SetProcess(false);
            TestScene.GetTree().Root.ContentScaleFactor = 1;
            var ui = app.GetNode<GameUi>("GameUi");
            var overlay = ui.World.GetNode<SceneStatusOverlay>("SceneStatusOverlay");
            var world = app.WorldScenes!;
            world.Open(NodeTarget);
            var display = world.Display;
            Require(display is not null, $"场景没有创建显示：{ui.ErrorText}");
            display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            world.ProcessFrame(0, false);
            Require(world.InteractionFrame is not null && world.View.Interactive, "场景两帧后仍不能悬停拾取。");
            await Frame();
            Require(!overlay.Always && overlay.Markers.Count == 0, "默认关闭且指针不在场景上时显示了标记。");

            var viewport = TestScene.GetViewport();
            var size = viewport.GetVisibleRect().Size;
            var point = new Vector2(size.X / 2f, Math.Max(120, size.Y - 80));
            var screen = viewport.GetScreenTransform() * point;
            setup.Frame.Current = new ScenePick.Hit(setup.BuildingHit);
            using (var motion = new InputEventMouseMotion { Position = screen, GlobalPosition = screen, Relative = Vector2.One })
                Input.ParseInputEvent(motion);
            await Frame();
            var hovered = viewport.GuiGetHoveredControl();
            Require(world.View.Pointer is { } recorded && recorded.IsEqualApprox(point),
                $"场景没有记下悬停指针。pointer={world.View.Pointer}，point={point}，screen={screen}。");
            Require(hovered is null, $"指针被控件挡住：{hovered?.GetPath()}。");
            // 输入可能晚于本帧的 _Process；下一帧才用记下的指针拾取。
            await Frame();
            RequireIds(overlay, Building);
            RequireMarker(overlay, Building, 0.4, UiStyle.Warning, new SceneStatus.Icon.Relation(SettlementRelation.Hostile));

            setup.Frame.Current = new ScenePick.Miss();
            await Frame();
            Require(overlay.Markers.Count == 0, "指针离开可拾取实例后标记仍在。");

            var panels = ui.GetNode<WorldPanels>("Root/World/WorldPanels");
            panels.OpenObject(new WorldObjectId.Instance(Building));
            await Frame();
            RequireIds(overlay, Building);
            panels.OpenObject(new WorldObjectId.Instance(Vehicle));
            await Frame();
            RequireIds(overlay, Building, Vehicle);
            RequireMarker(overlay, Vehicle, 1, UiStyle.Success, new SceneStatus.Icon.Train(TrainActivity.Running));
            foreach (var button in panels.FindChildren("Close", "Button", true, false).Cast<Button>())
                button.EmitSignal(BaseButton.SignalName.Pressed);
            await Frame();
            Require(overlay.Markers.Count == 0, "关闭详情后面板选中的标记仍在。");

            overlay.Always = true;
            await Frame();
            RequireIds(overlay, Building, Vehicle);
            Require(!overlay.Markers.Any(marker => marker.Id == Missing || marker.Id == Behind),
                $"已删除或相机背后的实例仍被标记。相机 {world.View.CameraRig.Camera.GlobalPosition}，" +
                $"朝向 {-world.View.CameraRig.Camera.GlobalBasis.Z}，标记 {Format(overlay)}。");

            world.Open(EdgeTarget);
            world.Display!.Timestamp = () => 0;
            world.ProcessFrame(0, false);
            world.ProcessFrame(0, false);
            await Frame();
            Require(setup.Session.RequestCalls.SequenceEqual([NodeTarget, EdgeTarget]), "线路场景没有切换观察目标。");
            RequireIds(overlay, Vehicle);
            RequireMarker(overlay, Vehicle, 1, UiStyle.Success, new SceneStatus.Icon.Train(TrainActivity.Running));
        }
        finally
        {
            await fixture.Cleanup();
            TestScene.GetTree().Root.ContentScaleFactor = scale;
        }
    }

    private async Task Frame() =>
        await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task Until(Func<bool> complete)
    {
        var limit = System.Diagnostics.Stopwatch.StartNew();
        while (!complete() && limit.Elapsed < TimeSpan.FromSeconds(30)) await Frame();
        Require(complete(), "挂起保护超时：宿主准备未完成。");
    }

    private static void RequireIds(SceneStatusOverlay overlay, params InstanceId[] ids)
    {
        var actual = overlay.Markers.Select(marker => marker.Id).OrderBy(id => id.ToString()).ToArray();
        var expected = ids.OrderBy(id => id.ToString()).ToArray();
        Require(actual.SequenceEqual(expected), $"标记集合不符：{Format(overlay)}。");
    }

    private static void RequireMarker(SceneStatusOverlay overlay, InstanceId id, double ratio, Color bar, SceneStatus.Icon icon)
    {
        var marker = overlay.Markers.Single(item => item.Id == id);
        Require(Math.Abs(marker.Ratio - ratio) < 1e-9 && marker.Bar == bar && marker.Icon.Equals(icon),
            $"实例 {id} 的标记不符合约定：ratio={marker.Ratio}，bar={marker.Bar}，icon={marker.Icon}。");
    }

    private static string Format(SceneStatusOverlay overlay) =>
        string.Join(", ", overlay.Markers.Select(marker => marker.Id.ToString()));

    private sealed class Setup
    {
        internal HoverFrame Frame { get; }
        internal SceneHit BuildingHit { get; }
        internal FixedDisplaySession Session { get; private set; } = null!;
        internal FixedDisplayFactory Factory { get; }

        internal Setup()
        {
            var stamp = new SceneFrameStamp(SceneId, new(1), new(1), 1);
            var subject = new PickSubject.WorldObject(new WorldObjectId.Instance(Building), null, null);
            BuildingHit = new(stamp, new(1), subject, new(0, 0, 0), GridFace.PositiveY, NVector3.Zero, NVector3.UnitY, 1, []);
            var poses = ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty
                .Add(new WorldObjectId.Instance(Building), Pose(NVector3.Zero))
                .Add(new WorldObjectId.Instance(Vehicle), Pose(new(8, 1, -6)))
                .Add(new WorldObjectId.Instance(Behind), Pose(new(0, 0, 500)))
                .Add(new WorldObjectId.Instance(Missing), Pose(NVector3.Zero));
            var inner = new FixedSceneFrame
            {
                Update = new(null, stamp, NVector3.Zero,
                    [Object(1, Building, NVector3.Zero), Object(2, Vehicle, new(8, 1, -6)), Object(3, Behind, new(0, 0, 500))],
                    [], new([], []), []),
                Poses = poses,
                TerrainHeightResult = new Lookup<double>.Missing()
            };
            Frame = new() { Inner = inner };
            Factory = new()
            {
                OpenWorldCall = (binding, _) =>
                {
                    Session = new()
                    {
                        Id = SceneId, Binding = binding,
                        RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty
                            .Add(NodeTarget, new SceneResult<SceneRequestId>.Success(new(1)))
                            .Add(EdgeTarget, new SceneResult<SceneRequestId>.Success(new(1))),
                        PresentResults = Enumerable.Repeat<SceneResult<ISceneFrame>>(new SceneResult<ISceneFrame>.Success(Frame), 40).ToImmutableArray(),
                        ResolveResults = ImmutableDictionary<SceneHit, SceneResult<PickSubject>>.Empty
                            .Add(BuildingHit, new SceneResult<PickSubject>.Success(subject)),
                        ObserveResults = Enumerable.Repeat<SceneResult<SimulationPosition>>(new SceneResult<SimulationPosition>.Success(new(0, 0)), 8).ToImmutableArray()
                    };
                    return new SceneResult<IWorldSceneSession>.Success(Session);
                }
            };
        }

        internal HostCoordinator Create(TrainLogger logger)
        {
            var content = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1));
            return WorldSceneFixture.CreateHost(new ScriptedCoreSession(CoreView()), content, logger);
        }

        private static Lookup<ScenePose> Pose(NVector3 position) =>
            new Lookup<ScenePose>.Found(new(position, System.Numerics.Quaternion.Identity));

        private static SceneObject Object(ulong id, InstanceId subject, NVector3 position)
        {
            var mesh = new SceneMesh(new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper),
                [new(new(-1, 0, -1), NVector3.UnitY, new(0, 0), new(0, 0), ExposedFaceEdges.None, 0, 0),
                 new(new(-1, 0, 1), NVector3.UnitY, new(0, 1), new(0, 1), ExposedFaceEdges.None, 0, 0),
                 new(new(1, 0, 1), NVector3.UnitY, new(1, 1), new(1, 1), ExposedFaceEdges.None, 0, 0),
                 new(new(1, 0, -1), NVector3.UnitY, new(1, 0), new(1, 0), ExposedFaceEdges.None, 0, 0)],
                [0, 1, 2, 0, 2, 3]);
            return new(new(id), null, new(position, System.Numerics.Quaternion.Identity), new WorldObjectId.Instance(subject),
                [new SceneVisual.Geometry(mesh, new(false, 0, 0, false))], [], []);
        }

        private static FixedCoreReadView CoreView()
        {
            var terrain = new TerrainView(new("fixture:plain"), 0, []);
            var first = new NodeMapItem(new(1), new(0, 0), terrain, new NodeOccupant.Empty(), [], 0, 1, null);
            var second = first with { Id = new(2), Position = new(600, 0) };
            var edge = new EdgeMapItem(new(1), first.Id, second.Id, 600, terrain, 1, 1, [], []);
            var trainPosition = new TrainPosition.Node(first.Id, null, null, null, null, NodeTravelPhase.Docked, 0, null);
            var train = new TrainView(new(7), trainPosition, TrainActivity.Running, [new(11)], new(1),
                0, 20, 10, 100, 0, 30, null, null, 0,
                new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0.5), [], null, [],
                TrainOrderFacing.FirstEndLeading, null);
            InstanceView Instance(InstanceId id, SettlementRelation relation, InstanceDetails details, DurabilityView structure) =>
                new(id, new("design"), new("composition"), NodeLocation, relation, details, InstanceLock.Combat, structure, [], []);
            return new(new(0, 0), HostCoreScenario.Input)
            {
                Economy = UiWorldFixture.Economy,
                World = new(1, new("fixture:railway"), 1000, 600, [first, second], [edge],
                    [new(train.Id, trainPosition, train.Activity)], [], []),
                Trains = new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = new Lookup<TrainView>.Found(train) },
                Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
                {
                    [Building] = new Lookup<InstanceView>.Found(Instance(Building, SettlementRelation.Hostile,
                        new InstanceDetails.Building(new(3), PlotSize.Small), new(40, 100))),
                    [Vehicle] = new Lookup<InstanceView>.Found(Instance(Vehicle, SettlementRelation.PlayerOwned,
                        new InstanceDetails.Vehicle(SizeClass.Medium, train.Id), new(100, 100))),
                    [Behind] = new Lookup<InstanceView>.Found(Instance(Behind, SettlementRelation.PlayerOwned,
                        new InstanceDetails.Vehicle(SizeClass.Small, null), new(20, 100))),
                    [Missing] = new Lookup<InstanceView>.Missing()
                },
                Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
                {
                    [NodeLocation] = new Lookup<LocationView>.Found(new(NodeLocation, [train.Id],
                        [Building, Vehicle, Behind, Missing], [], [], new NodeOccupant.Empty())),
                    [EdgeLocation] = new Lookup<LocationView>.Found(new(EdgeLocation, [train.Id],
                        [Vehicle], [], [], null))
                }
            };
        }
    }

    private sealed class HoverFrame : ISceneFrame
    {
        internal required FixedSceneFrame Inner { get; init; }
        internal ScenePick Current { get; set; } = new ScenePick.Miss();
        public ScenePoint Origin => Inner.Origin;
        public ScenePanBounds? NodePanBounds => Inner.NodePanBounds;
        public SceneNodeOverview? NodeOverview => Inner.NodeOverview;
        public SceneReadiness Readiness => Inner.Readiness;
        public double SimulationSeconds => Inner.SimulationSeconds;
        public SceneUpdate Update => Inner.Update;
        public ImmutableArray<SceneProblem> Diagnostics => Inner.Diagnostics;
        public ImmutableArray<SceneDebugLine> DebugLines => Inner.DebugLines;
        public ScenePick Pick(SceneRay ray) => Current;
        public Lookup<ScenePose> FindPose(WorldObjectId subject) =>
            Inner.Poses.TryGetValue(subject, out var pose) ? pose : new Lookup<ScenePose>.Missing();
        public Lookup<WorldLocation> Locate(NVector3 point) => new Lookup<WorldLocation>.Missing();
        public Lookup<double> TerrainHeight(double x, double z) => new Lookup<double>.Missing();
        public Lookup<double> DisplayTerrainHeight(double x, double z) => new Lookup<double>.Missing();
    }
}
