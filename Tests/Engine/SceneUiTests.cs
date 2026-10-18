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
using Train.Ui;
using static Train.Engine.Tests.Assertions;
using NVector3 = System.Numerics.Vector3;

namespace Train.Engine.Tests;

public class SceneUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);
    private float _previousScale;
    private static readonly SceneSessionId SceneId = new(Guid.Parse("00000000-0000-0000-0000-000000320001"));
    private static readonly InstanceId Vehicle = new InstanceId.Vehicle(new(9));
    private static readonly WorldSceneTarget NodeTarget = new WorldSceneTarget.Location(new WorldLocation.Node(new(1)));
    private static readonly WorldSceneTarget EdgeTarget = new WorldSceneTarget.Location(new WorldLocation.Edge(new(1), 300));
    private static readonly WorldSceneTarget TrainTarget = new WorldSceneTarget.Follow(new(7), new(9));

    [Test]
    public async Task Map_and_scene_inputs_route_targets_preserve_camera_and_block_panel_picking()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup();
        Train.App.App? app = null;
        try
        {
            app = await Ready(fixture, setup);
            var ui = app.GetNode<GameUi>("GameUi");
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            var resources = map.GetNode<CheckBox>("InformationLayers/Body/Resources");
            var platforms = map.GetNode<CheckBox>("InformationLayers/Body/Platforms");
            await _input.Activate(resources);
            await _input.Activate(platforms);
            await _input.Drag(map.GlobalPosition + map.Size / 2, new(20, 15));
            await _input.MouseButton(map.GlobalPosition + map.Size / 2, MouseButton.WheelUp, true);
            var offset = map.Offset;
            var zoom = map.Zoom;
            await Open(app, setup, map.GlobalPosition + map.MapToScreen(new(250, 300)), NodeTarget);
            Require(!map.IsVisibleInTree() && !app.GetNode<MainMenu>("MainMenu").IsVisibleInTree(),
                "场景进入后仍显示地图或主菜单。");
            var frame = setup.Frames[^1];
            var session = setup.Scenes[^1];
            await _input.Click(new(950, 650));
            Require(frame.LastPickRay is not null && session.ResolveCalls.Count == 1 &&
                ui.GetNode<WorldPanels>("Root/World/WorldPanels").IsObjectOpen(new WorldObjectId.Instance(Vehicle)),
                "场景真实点击未经过Pick/Resolve打开实例详情。");
            var pickedRay = frame.LastPickRay;
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Nodes"));
            var list = ui.GetNode<ItemList>("Root/World/WorldPanels/NodesList/Layout/Content/Body/Objects");
            await _input.ClickItem(list, 0);
            Require(session.ResolveCalls.Count == 1 && ReferenceEquals(frame.LastPickRay, pickedRay), "顶栏或列表点击穿透场景拾取。");
            foreach (var panel in ui.GetNode<WorldPanels>("Root/World/WorldPanels").GetChildren().OfType<PanelContainer>()) panel.Hide();
            await UiCapture.Capture(TestScene, "world-scene");
            await _input.PressKey(Key.Escape);
            await _input.WaitUntil(() => map.IsVisibleInTree());
            Require(map.Offset == offset && map.Zoom == zoom && session.IsDisposed &&
                !ui.GetNode<Control>("Root/ErrorPanel").Visible, "Escape返回地图丢失视角、未释放场景或将正常返回显示成错误。");
            await Open(app, setup, map.GlobalPosition + map.MapToScreen(new(500, 300)), EdgeTarget);
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/ReturnToMap"));
            await _input.WaitUntil(() => map.IsVisibleInTree());
            Require(map.IsVisibleInTree() && map.Offset == offset && map.Zoom == zoom, "返回地图按钮未恢复原地图视角。");
            Require(resources.ButtonPressed && platforms.ButtonPressed,
                "场景往返重置了地图信息层开关。");
            await Open(app, setup, map.GlobalPosition + map.MapToScreen(new(250, 300)) + new Vector2(25, -22), TrainTarget);
            await _input.PressKey(Key.Escape);
            setup.RejectNext = true;
            await _input.WaitUntil(() => map.IsVisibleInTree());
            await _input.Click(map.GlobalPosition + map.MapToScreen(new(250, 300)));
            await _input.WaitUntil(() => map.IsVisibleInTree() && ui.ErrorText.Contains("fixture_node_failed"));
            Require(!app.WorldScenes!.View.Visible, "节点构建失败仍保留场景。");
            await UiCapture.Capture(TestScene, "phase5-map");
        }
        finally { await Cleanup(fixture, app); }
    }

    [Test]
    public async Task Save_panel_load_from_scene_disposes_scene_and_enters_paused_map()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup(minimap: true);
        Train.App.App? app = null;
        try
        {
            app = await Ready(fixture, setup);
            var ui = app.GetNode<GameUi>("GameUi");
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            var resources = map.GetNode<CheckBox>("InformationLayers/Body/Resources");
            await _input.Activate(resources);
            var panel = app.GetNode<MainMenu>("MainMenu").GetChildren().OfType<SavePanel>().Single();
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Saves"));
            await _input.Activate(UiTestInput.SaveButton(panel, "保存"), panel);
            await _input.WaitUntil(() => !app.Saves!.HasPendingWork);
            var identity = app.Saves!.Status!.Identity;
            panel.Hide();
            await Open(app, setup, map.GlobalPosition + map.MapToScreen(new(250, 300)), NodeTarget);
            var scene = setup.Scenes[^1];
            var minimap = ui.GetNode<NodeMinimap>("Root/World/NodeMinimap");
            await _input.WaitUntil(minimap.IsVisibleInTree);
            var oldCore = setup.Session;
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Saves"));
            var list = panel.FindChildren("*", "ItemList", true, false).Cast<ItemList>().Single(item => item.GetWindow() == panel);
            var row = Enumerable.Range(0, list.ItemCount).Single(index => list.GetItemText(index).StartsWith(identity.Name + " ·", StringComparison.Ordinal));
            await _input.ClickItem(list, row, window: panel);
            await _input.Activate(UiTestInput.SaveButton(panel, "读取选中存档"), panel);
            await _input.WaitUntil(() => !app.Saves.HasPendingWork && map.IsVisibleInTree());
            Require(app.Host!.GetStatus() is { IsPaused: true, PlayerPaused: true } && scene.IsDisposed &&
                oldCore.DisposeCount == 1 && !app.WorldScenes!.View.Visible && app.Saves.Status!.Identity == identity &&
                !ui.GetNode<Control>("Root/ErrorPanel").Visible,
                "场景中真实读档没有释放旧场景并进入暂停地图。");
            await _input.WaitUntil(() => !minimap.IsVisibleInTree());
            Require(resources.ButtonPressed, "读档重置了信息层开关。");
            panel.Hide();
            await Open(app, setup, map.GlobalPosition + map.MapToScreen(new(250, 300)), NodeTarget);
            await _input.WaitUntil(minimap.IsVisibleInTree);
            await _input.Click(minimap.GlobalPosition + minimap.NodeToMap(new(70, 30)));
            Require(setup.Scenes.Count == 2 && !setup.Scenes[^1].IsDisposed &&
                app.WorldScenes!.View.CameraRig.Focus.DistanceTo(new(70, 0, 30)) < .001f,
                "换绑定后节点小地图未使用新场景定位。");
        }
        finally { await Cleanup(fixture, app); }
    }

    [Test]
    public async Task Protected_scene_leave_and_new_game_reset_view_without_return_event()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup();
        Train.App.App? app = null;
        try
        {
            app = await Ready(fixture, setup);
            var ui = app.GetNode<GameUi>("GameUi");
            var menu = app.GetNode<MainMenu>("MainMenu");
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            var platforms = map.GetNode<CheckBox>("InformationLayers/Body/Platforms");
            await _input.Activate(platforms);
            await Open(app, setup, map.GlobalPosition + map.MapToScreen(new(250, 300)), NodeTarget);
            var scene = setup.Scenes[^1];
            var returned = 0;
            app.WorldScenes!.ReturnedToMap += _ => returned++;
            var panel = menu.GetChildren().OfType<SavePanel>().Single();
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Saves"));
            await _input.Activate(UiTestInput.SaveButton(panel, "返回菜单"), panel);
            await _input.WaitUntil(() => panel.GetChildren().OfType<ConfirmationDialog>().Any(dialog => dialog.Title == "未保存的更改" && dialog.Visible));
            Require(!scene.IsDisposed && app.Host!.GetStatus() is not null, "未保存确认前离开了场景对局。");
            var confirmation = panel.GetChildren().OfType<ConfirmationDialog>().Single(dialog => dialog.Title == "未保存的更改");
            await _input.Activate(confirmation.FindChildren("*", "Button", true, false).Cast<Button>().Single(button => button.Text == "不保存"), confirmation);
            await _input.WaitUntil(() => menu.IsVisibleInTree() && app.Host!.GetStatus() is null);
            Require(scene.IsDisposed && returned == 0 && !app.WorldScenes.View.Visible &&
                !map.IsVisibleInTree() && !ui.GetNode<Control>("Root/World/Bar").IsVisibleInTree() &&
                !ui.GetNode<WorldPanels>("Root/World/WorldPanels").IsVisibleInTree(),
                "没有ReturnedToMap事件的离开未重置场景和游戏界面。");
            panel.Hide();
            await _input.Activate(menu.GetNode<Button>("%NewGame"));
            await _input.Activate(ui.GetNode<Button>("Root/NewGame/Layout/Content/Body/Create"));
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null && map.IsVisibleInTree());
            Require(app.Host!.GetStatus() is { IsPaused: true, PlayerPaused: true } && !menu.IsVisibleInTree() &&
                !app.WorldScenes!.View.Visible && setup.Scenes.Count == 1,
                "菜单新建对局恢复了旧场景而不是暂停地图。");
            Require(platforms.ButtonPressed, "新开局重置了信息层开关。");
        }
        finally { await Cleanup(fixture, app); }
    }

    [Test]
    public async Task Node_minimap_routes_real_clicks_and_drags_without_scene_or_panel_input_leaks()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup(minimap: true);
        Train.App.App? app = null;
        try
        {
            app = await Ready(fixture, setup);
            var ui = app.GetNode<GameUi>("GameUi");
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            var minimap = ui.GetNode<NodeMinimap>("Root/World/NodeMinimap");
            Require(!minimap.IsVisibleInTree(), "宏观地图不应显示节点小地图。");
            await Open(app, setup, map.GlobalPosition + map.MapToScreen(new(250, 300)), NodeTarget);
            await _input.WaitUntil(minimap.IsVisibleInTree);
            var frame = setup.Frames[^1];
            var session = setup.Scenes[^1];
            var camera = app.WorldScenes!.View.CameraRig;
            camera.Orbit(50, 20);
            var offset = camera.Camera.Position - camera.Focus;
            var heading = camera.Camera.Basis.Z;
            var target = new Vector2(80, 25);
            var pixel = minimap.NodeToMap(target);
            Require(minimap.MapToNode(pixel).DistanceTo(target) < .001f,
                "非方形节点坐标不能正确往返小地图。");
            var unitX = minimap.NodeToMap(target + Vector2.Right) - pixel;
            var unitZ = minimap.NodeToMap(target + Vector2.Down) - pixel;
            Require(Math.Abs(unitX.Length() - unitZ.Length()) < .001f, "非方形节点小地图改变了纵横比例。");
            Require(minimap.MapToNode(new(-1000, 1000)).DistanceTo(new(-120, 120)) < .001f,
                "小地图边缘输入没有限制在节点范围内。");
            await _input.Click(minimap.GlobalPosition + pixel);
            Require(camera.Focus.DistanceTo(new(target.X, 0, target.Y)) < .001f &&
                (camera.Camera.Position - camera.Focus).DistanceTo(offset) < .001f &&
                camera.Camera.Basis.Z.DistanceTo(heading) < .001f,
                "小地图点击没有使用帧原点换算定位，或改变了相机环绕和距离。");
            var arrow = minimap.HeadingTipOnMap - minimap.FocusOnMap;
            var forward = new Vector2(-camera.Camera.Basis.Z.X, -camera.Camera.Basis.Z.Z).Normalized();
            Require(arrow.Length() >= 12 && arrow.Normalized().Dot(forward) > .999f,
                "小地图朝向箭头必须至少长12像素，并与3D相机的水平朝向一致。");
            var end = minimap.GlobalPosition + minimap.NodeToMap(new(-70, 70));
            var start = minimap.GlobalPosition + pixel;
            await _input.MouseButton(start, MouseButton.Left, true);
            var transform = TestScene.GetViewport().GetScreenTransform();
            using (var motion = new InputEventMouseMotion { Position = transform * end, GlobalPosition = transform * end,
                Relative = transform * end - transform * start, ButtonMask = MouseButtonMask.Left })
                Input.ParseInputEvent(motion);
            await _input.Frames();
            await _input.MouseButton(end, MouseButton.Left, false);
            Require(camera.Focus.DistanceTo(new(-70, 0, 70)) < .001f &&
                frame.LastPickRay is null && session.ResolveCalls.Count == 0,
                "小地图左键拖动未定位，或穿透为场景拾取。");
            var focus = camera.Focus;
            var distance = camera.Camera.Position.DistanceTo(focus);
            await _input.MouseButton(end, MouseButton.WheelUp, true);
            await _input.MouseButton(end, MouseButton.WheelUp, false);
            await _input.Drag(end, new(5, 5));
            Require(camera.Focus.DistanceTo(focus) < .001f &&
                Math.Abs(camera.Camera.Position.DistanceTo(focus) - distance) < .001f,
                "小地图滚轮或中键穿透到世界相机。");
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Nodes"));
            var panel = ui.GetNode<PanelContainer>("Root/World/WorldPanels/NodesList");
            var list = panel.GetNode<ItemList>("Layout/Content/Body/Objects");
            await _input.WaitUntil(() => list.ItemCount > 0 && list.GetItemRect(0).Size.Y > 0 &&
                list.GetItemAtPosition(list.GetItemRect(0).GetCenter(), true) == 0);
            panel.Position = minimap.Position;
            await _input.Frames();
            var overlap = minimap.GlobalPosition + new Vector2(20, 45);
            Require(panel.GetGlobalRect().HasPoint(overlap) && minimap.GetGlobalRect().HasPoint(overlap),
                $"覆盖夹具没有重叠：panel={panel.GetGlobalRect()}，minimap={minimap.GetGlobalRect()}，click={overlap}。");
            await _input.Click(overlap);
            Require(camera.Focus.DistanceTo(focus) < .001f && frame.LastPickRay is null,
                $"覆盖小地图的面板点击穿透：before={focus}，after={camera.Focus}，pick={frame.LastPickRay}，" +
                $"panel={panel.GetGlobalRect()}，minimap={minimap.GetGlobalRect()}，mask={Input.GetMouseButtonMask()}，" +
                $"hover={TestScene.GetViewport().GuiGetHoveredControl()?.GetPath()}。");
            panel.Hide();
            await _input.PressKey(Key.Escape);
            await _input.WaitUntil(() => map.IsVisibleInTree() && !minimap.IsVisibleInTree());
            await Open(app, setup, map.GlobalPosition + map.MapToScreen(new(500, 300)), EdgeTarget);
            Require(!minimap.IsVisibleInTree(), "线路场景不应显示节点小地图。");
            await _input.PressKey(Key.Escape);
            await _input.WaitUntil(map.IsVisibleInTree);
            await Open(app, setup, map.GlobalPosition + map.MapToScreen(new(250, 300)) + new Vector2(25, -22), TrainTarget);
            await _input.WaitUntil(minimap.IsVisibleInTree);
            session = setup.Scenes[^1];
            await _input.Click(minimap.GlobalPosition + minimap.NodeToMap(target));
            await _input.WaitUntil(() => app.WorldScenes.View.Interactive && session.RequestCalls.Count == 2);
            Require(session.RequestCalls.SequenceEqual(new[] { TrainTarget, NodeTarget }) &&
                app.WorldScenes.View.CameraRig.Focus.DistanceTo(new(target.X, 0, target.Y)) < .001f &&
                minimap.IsVisibleInTree() && setup.Frames[^1].LastPickRay is null,
                "跟随节点内列车时，小地图未复用会话切换自由相机并定位。");
        }
        finally { await Cleanup(fixture, app); }
    }
    private async Task<Train.App.App> Ready(Fixture fixture, Setup setup)
    {
        _previousScale = TestScene.GetTree().Root.ContentScaleFactor;
        UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
        TestScene.GetTree().Root.ContentScaleFactor = 1;
        var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
        app.CreateHost = setup.Create;
        app.CreateScenes = () => setup.Factory;
        await fixture.AddToRoot(app);
        await _input.WaitUntil(() => app.Host!.GetStatus() is not null && app.Saves!.Status is not null);
        await _input.Frames(2);
        return app;
    }

    private async Task Open(Train.App.App app, Setup setup, Vector2 point, WorldSceneTarget target)
    {
        await _input.Click(point);
        await _input.WaitUntil(() => app.WorldScenes!.View.Visible && app.WorldScenes.View.Interactive);
        Require(setup.Scenes[^1].RequestCalls.SequenceEqual(new[] { target }), "地图点击未请求对应世界场景目标。");
    }

    private async Task Cleanup(Fixture fixture, Train.App.App? app)
    {
        var identity = app?.Saves?.Status?.Identity.Id;
        if (app?.Saves is { } saves) { saves.Cancel(); await _input.WaitUntil(() => !saves.HasPendingWork); }
        await fixture.Cleanup();
        TestScene.GetTree().Root.ContentScaleFactor = _previousScale;
        if (identity is { } id)
        {
            UiTestInput.DeleteSaves([id]);
        }
    }

    private sealed class Setup
    {
        internal ScriptedCoreSession Session = null!;
        internal readonly List<FixedDisplaySession> Scenes = [];
        internal readonly List<FixedSceneFrame> Frames = [];
        internal bool RejectNext;
        internal FixedDisplayFactory Factory { get; }
        internal Setup(bool minimap = false)
        {
            Factory = new() { OpenWorldCall = (binding, _) =>
            {
                if (RejectNext) return new SceneResult<IWorldSceneSession>.Failure([new("fixture_node_failed", SceneStage.Build,
                    new WorldObjectId.Node(new(1)), null, null, null, "固定节点构建失败。")]);
                var stamp = new SceneFrameStamp(SceneId, new(1), new(1), 1);
                var subject = new PickSubject.WorldObject(new WorldObjectId.Instance(Vehicle), null, null);
                var hit = new SceneHit(stamp, new(1), subject, new(0, 0, 0), GridFace.PositiveY,
                    NVector3.Zero, NVector3.UnitY, 1, []);
                var frame = new FixedSceneFrame { Update = new(null, stamp, NVector3.Zero, [Geometry()], [], new([], []), []),
                    NodePanBounds = minimap ? new(new(-120, -60), new(240, 120)) : null,
                    NodeOverview = minimap ? new([], [[new(-120, 0), new(240, 0)]]) : null,
                    Origin = minimap ? new(1000, 0, -2000) : default,
                    PickResult = new ScenePick.Hit(hit), TerrainHeightResult = new Lookup<double>.Found(0),
                    Poses = ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty.Add(new WorldObjectId.Instance(Vehicle),
                        new Lookup<ScenePose>.Found(new(NVector3.Zero, System.Numerics.Quaternion.Identity))) };
                Frames.Add(frame);
                var session = new FixedDisplaySession { Id = SceneId, Binding = binding,
                    RequestResults = new[] { NodeTarget, EdgeTarget, TrainTarget }.ToImmutableDictionary(target => target,
                        _ => (SceneResult<SceneRequestId>)new SceneResult<SceneRequestId>.Success(new(1))),
                    PresentResults = Enumerable.Repeat<SceneResult<ISceneFrame>>(new SceneResult<ISceneFrame>.Success(frame), 180).ToImmutableArray(),
                    ResolveResults = ImmutableDictionary<SceneHit, SceneResult<PickSubject>>.Empty.Add(hit, new SceneResult<PickSubject>.Success(subject)) };
                Scenes.Add(session);
                return new SceneResult<IWorldSceneSession>.Success(session);
            } };
        }

        internal HostCoordinator Create(TrainLogger logger)
        {
            var capture = new FixedCoreCapture(new(new(0, 0), "scene ui", "scene ui"), HostCoreScenario.References,
                "scene-ui", HostCoreScenario.Input, new(0));
            ScriptedCoreCandidate Candidate()
            {
                Session = UiWorldFixture.Session(UiWorldFixture.Scene());
                Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(capture));
                return new(Session, HostCoreScenario.References);
            }
            var core = new ScriptedCoreFactory { DecodeStateCall = _ => new CoreResult<ICoreCapture>.Success(capture),
                PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(Candidate()),
                PrepareNewCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(Candidate()) };
            var contentView = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
            { GenerationProfiles = [new(new("fixture:world"), new("固定世界", ""))] };
            var content = new ScriptedContentFactory { CaptureCall = (view, _) => new ContentResult<IContentCapture>.Success(new FixedContentCapture("scene-ui", view.PersistentRevision)),
                CaptureReplayCall = (history, _) => new ContentResult<IContentReplayCapture>.Success(new FixedContentReplayCapture(history, "ui recording content")),
                DecodeCaptureCall = _ => new ContentResult<IContentCapture>.Success(new FixedContentCapture("scene-ui")),
                PrepareLoadCall = (_, _, _) => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(contentView)),
                CreateWorldInputCall = (_, _, _, _) => new ContentResult<WorldGenerationInput>.Success(HostCoreScenario.World) };
            var options = new CoreSessionOptions(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1);
            var host = new HostCoordinator(core, content, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
                new(new("builtin"), new("mods"), null, "scene-ui", []), logger, sessionOptions: options);
            host.BeginRestore("scene-ui", "scene-ui", options);
            return host;
        }

        private static SceneObject Geometry()
        {
            var mesh = new SceneMesh(new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper),
                [new(new(-20, 0, -20), NVector3.UnitY, new(0, 0), new(0, 0), ExposedFaceEdges.None, 0, 0),
                 new(new(-20, 0, 20), NVector3.UnitY, new(0, 1), new(0, 1), ExposedFaceEdges.None, 0, 0),
                 new(new(20, 0, 20), NVector3.UnitY, new(1, 1), new(1, 1), ExposedFaceEdges.None, 0, 0),
                 new(new(20, 0, -20), NVector3.UnitY, new(1, 0), new(1, 0), ExposedFaceEdges.None, 0, 0)], [0, 1, 2, 0, 2, 3]);
            return new(new(1), null, new(NVector3.Zero, System.Numerics.Quaternion.Identity),
                new WorldObjectId.Instance(Vehicle), [new SceneVisual.Geometry(mesh, new(false, 0, 0, false))], [], []);
        }

    }
}

