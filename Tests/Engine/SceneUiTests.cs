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

public partial class SceneUiTests(Node testScene) : TestClass(testScene)
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
            var layers = map.GetNode<PopupPanel>("InformationLayers");
            await _input.Activate(Find<Button>(map, "Layers"));
            await _input.Activate(resources, layers);
            await _input.Activate(platforms, layers);
            await _input.PressKey(Key.Escape);
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
            await _input.Activate(Find<Button>(ui, "Nodes"));
            var list = NodeObjects(ui);
            await _input.ClickItem(list, FirstSelectableItem(list));
            Require(session.ResolveCalls.Count == 1 && ReferenceEquals(frame.LastPickRay, pickedRay), "顶栏或列表点击穿透场景拾取。");
            Find<PanelContainer>(ui, "Details").Hide();
            Find<PanelContainer>(ui, "NodesList").Hide();
            await UiCapture.Capture(TestScene, "world-scene");
            await _input.PressKey(Key.Escape);
            await _input.WaitUntil(() => map.IsVisibleInTree());
            Require(map.Offset == offset && map.Zoom == zoom && session.IsDisposed &&
                !ui.GetNode<Control>("Root/ErrorPanel").Visible, "Escape返回地图丢失视角、未释放场景或将正常返回显示成错误。");
            await Open(app, setup, map.GlobalPosition + map.MapToScreen(new(500, 300)), EdgeTarget);
            await _input.Activate(Find<Button>(ui, "ReturnToMap"));
            await _input.WaitUntil(() => map.IsVisibleInTree());
            Require(map.IsVisibleInTree() && map.Offset == offset && map.Zoom == zoom, "返回地图按钮未恢复原地图视角。");
            Require(resources.ButtonPressed && platforms.ButtonPressed,
                "场景往返重置了地图信息层开关。");
            await Open(app, setup, map.GlobalPosition + map.ObjectPoint(new WorldObjectId.Train(new(7)))!.Value, TrainTarget);
            await _input.PressKey(Key.Escape);
            Require(app.WorldScenes!.View.Visible && ((GameShell)ui.World).InspectorContent is null,
                "首个 Escape 应只关闭检查器。");
            await _input.PressKey(Key.Escape);
            setup.RejectNext = true;
            await _input.WaitUntil(() => map.IsVisibleInTree());
            await _input.Click(map.GlobalPosition + map.MapToScreen(new(250, 300)));
            await _input.DoubleClick(map.GlobalPosition + map.MapToScreen(new(250, 300)));
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
            var layers = map.GetNode<PopupPanel>("InformationLayers");
            await _input.Activate(Find<Button>(map, "Layers"));
            await _input.Activate(resources, layers);
            await _input.PressKey(Key.Escape);
            var oldCore = setup.Session;
            var panel = app.GetNode<SavePanel>("GameUi/Root/SavePanel");
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "存档与读档");
            await _input.Activate(UiTestInput.SaveButton(panel, "保存"));
            await _input.WaitUntil(() => !app.Saves!.HasPendingWork);
            var identity = app.Saves!.Status!.Identity;
            panel.Hide();
            await Open(app, setup, map.GlobalPosition + map.MapToScreen(new(250, 300)), NodeTarget);
            var scene = setup.Scenes[^1];
            var minimap = ui.GetNode<NodeMinimap>("Root/World/NodeMinimap");
            await _input.WaitUntil(minimap.IsVisibleInTree);
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "存档与读档");
            var list = panel.FindChildren("*", "ItemList", true, false).Cast<ItemList>().Single(item => item.GetWindow() == panel.GetWindow());
            var row = Enumerable.Range(0, list.ItemCount).Single(index => list.GetItemText(index).StartsWith(identity.Name + " · ", StringComparison.Ordinal));
            await _input.ClickItem(list, row);
            await _input.Activate(UiTestInput.SaveButton(panel, "读取"));
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
            var layers = map.GetNode<PopupPanel>("InformationLayers");
            await _input.Activate(Find<Button>(map, "Layers"));
            await _input.Activate(platforms, layers);
            await _input.PressKey(Key.Escape);
            await Open(app, setup, map.GlobalPosition + map.MapToScreen(new(250, 300)), NodeTarget);
            var scene = setup.Scenes[^1];
            var returned = 0;
            app.WorldScenes!.ReturnedToMap += _ => returned++;
            var panel = app.GetNode<SavePanel>("GameUi/Root/SavePanel");
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "存档与读档");
            await _input.Activate(UiTestInput.SaveButton(panel, "关闭（Esc）"));
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "返回主菜单");
            await _input.WaitUntil(() => UiTestInput.SavePromptVisible(panel, "未保存的更改"));
            Require(!scene.IsDisposed && app.Host!.GetStatus() is not null, "未保存确认前离开了场景对局。");
            await _input.Activate(UiTestInput.SavePromptButton(panel, "Discard"));
            await _input.WaitUntil(() => menu.IsVisibleInTree() && app.Host!.GetStatus() is null);
            Require(scene.IsDisposed && returned == 0 && !app.WorldScenes.View.Visible &&
                !map.IsVisibleInTree() && !ui.GetNode<Control>("Root/World/Bar").IsVisibleInTree() &&
                !ui.GetNode<WorldPanels>("Root/World/WorldPanels").IsVisibleInTree(),
                "没有ReturnedToMap事件的离开未重置场景和游戏界面。");
            panel.Hide();
            await _input.Activate(menu.GetNode<Button>("%NewGame"));
            await _input.Activate(Find<Button>(ui.GetNode<Control>("Root/NewGame"), "Create"));
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
                IsDownward(frame.LastPickRay) && session.ResolveCalls.Count == 0,
                "小地图左键拖动未定位，或穿透为场景拾取。");
            var focus = camera.Focus;
            var distance = camera.Camera.Position.DistanceTo(focus);
            await _input.MouseButton(end, MouseButton.WheelUp, true);
            await _input.MouseButton(end, MouseButton.WheelUp, false);
            await _input.Drag(end, new(5, 5));
            Require(camera.Focus.DistanceTo(focus) < .001f &&
                Math.Abs(camera.Camera.Position.DistanceTo(focus) - distance) < .001f,
                "小地图滚轮或中键穿透到世界相机。");
            await _input.Activate(Find<Button>(ui, "Nodes"));
            var list = NodeObjects(ui);
            var clearanceRay = frame.LastPickRay;
            await _input.ClickItem(list, FirstSelectableItem(list));
            Require(camera.Focus.DistanceTo(focus) < .001f && ReferenceEquals(frame.LastPickRay, clearanceRay),
                "停靠列表点击穿透到小地图或场景拾取。");
            await ShellGeometry.CheckBothScales(TestScene, ui);
            Find<PanelContainer>(ui, "NodesList").Hide();
            await _input.PressKey(Key.Escape);
            await _input.PressKey(Key.Escape);
            await _input.WaitUntil(() => map.IsVisibleInTree() && !minimap.IsVisibleInTree());
            await Open(app, setup, map.GlobalPosition + map.MapToScreen(new(500, 300)), EdgeTarget);
            Require(!minimap.IsVisibleInTree(), "线路场景不应显示节点小地图。");
            await _input.PressKey(Key.Escape);
            await _input.PressKey(Key.Escape);
            await _input.WaitUntil(map.IsVisibleInTree);
            await Open(app, setup, map.GlobalPosition + map.ObjectPoint(new WorldObjectId.Train(new(7)))!.Value, TrainTarget);
            await _input.WaitUntil(minimap.IsVisibleInTree);
            session = setup.Scenes[^1];
            await _input.Click(minimap.GlobalPosition + minimap.NodeToMap(target));
            await _input.WaitUntil(() => app.WorldScenes.View.Interactive && session.RequestCalls.Count == 2);
            Require(session.RequestCalls.SequenceEqual(new[] { TrainTarget, NodeTarget }) &&
                app.WorldScenes.View.CameraRig.Focus.DistanceTo(new(target.X, 0, target.Y)) < .001f &&
                minimap.IsVisibleInTree() && IsDownward(setup.Frames[^1].LastPickRay),
                "跟随节点内列车时，小地图未复用会话切换自由相机并定位。");
        }
        finally { await Cleanup(fixture, app); }
    }

    [Test]
    public async Task Node_minimap_click_raises_focus_to_the_displayed_building_top()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup(minimap: true, buildingTop: 40);
        Train.App.App? app = null;
        try
        {
            app = await Ready(fixture, setup);
            var map = app.GetNode<MacroMap>("GameUi/Root/World/MacroMap");
            await Open(app, setup, map.GlobalPosition + map.MapToScreen(new(250, 300)), NodeTarget);
            var minimap = app.GetNode<NodeMinimap>("GameUi/Root/World/NodeMinimap");
            await _input.WaitUntil(minimap.IsVisibleInTree);
            var camera = app.WorldScenes!.View.CameraRig;
            var offset = camera.Camera.Position - camera.Focus;
            var target = new Vector2(80, 25);
            await _input.Click(minimap.GlobalPosition + minimap.NodeToMap(target));
            Require(camera.Focus.DistanceTo(new(target.X, 40, target.Y)) < .001f &&
                (camera.Camera.Position - camera.Focus).DistanceTo(offset) < .001f &&
                setup.Scenes[^1].ResolveCalls.Count == 0,
                "经控制器点到建筑后，焦点没有抬到已显示网格的顶面。");
        }
        finally { await Cleanup(fixture, app); }
    }

}
