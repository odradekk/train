using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SceneUiTests
{
    [Test]
    public async Task Double_click_still_enters_when_the_new_inspector_covers_the_marker()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup(nodeX: 950);
        Train.App.App? app = null;
        try
        {
            app = await Ready(fixture, setup);
            var ui = app.GetNode<GameUi>("GameUi");
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            var point = map.GlobalPosition + map.ObjectPoint(new WorldObjectId.Node(new(1)))!.Value;
            await _input.Click(point);
            var shell = (GameShell)ui.World;
            Require(shell.InspectorContent!.GetGlobalRect().HasPoint(point) && setup.Scenes.Count == 0,
                "此用例必须由首次单击后出现的检查器覆盖原标记。");
            await _input.MouseButton(point, MouseButton.Left, true, doubleClick: true);
            await _input.MouseButton(point, MouseButton.Left, false);
            await _input.WaitUntil(() => app.WorldScenes!.View.Interactive);
            Require(setup.Scenes.Single().RequestCalls.SequenceEqual([NodeTarget]),
                "检查器覆盖原标记后，双击仍须进入原对象的场景。");
        }
        finally { await Cleanup(fixture, app); }
    }

    [Test]
    public async Task Node_combat_marker_inspects_before_entering_its_node()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup(inCombat: true);
        Train.App.App? app = null;
        try
        {
            app = await Ready(fixture, setup);
            var ui = app.GetNode<GameUi>("GameUi");
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            var id = new WorldObjectId.Combat(new(1));
            await _input.Click(map.GlobalPosition + map.ObjectPoint(id)!.Value);
            var shell = (GameShell)ui.World;
            Require(setup.Scenes.Count == 0 && shell.SelectedObject == id && shell.InspectorContent is CombatPanel,
                "单击节点战斗必须只显示现有的战斗面板。");
            var inspector = shell.InspectorContent;
            await _input.Activate(Find<Button>(ui, "EnterScene"));
            await _input.WaitUntil(() => app.WorldScenes!.View.Interactive);
            Require(setup.Scenes.Single().RequestCalls.SequenceEqual([NodeTarget]) && shell.InspectorContent == inspector,
                "战斗检查器必须进入战斗所在节点并保留检查器。");
        }
        finally { await Cleanup(fixture, app); }
    }

    [Test]
    public async Task Inspector_navigation_centers_in_the_visible_map_and_enters_without_reselection()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup();
        Train.App.App? app = null;
        try
        {
            app = await Ready(fixture, setup);
            var ui = app.GetNode<GameUi>("GameUi");
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            var shell = (GameShell)ui.World;
            var node = new WorldObjectId.Node(new(1));
            await _input.Click(map.GlobalPosition + map.ObjectPoint(node)!.Value);
            var inspector = shell.InspectorContent;
            var inspections = 0;
            ui.GetNode<WorldPanels>("Root/World/WorldPanels").InspectorOpened += (_, _) => inspections++;
            await _input.Activate(Find<Button>(ui, "EnterScene"));
            await _input.WaitUntil(() => app.WorldScenes!.View.Interactive);
            Require(setup.Scenes.Single().RequestCalls.SequenceEqual([NodeTarget]) && inspections == 0 &&
                shell.InspectorContent == inspector, "检查器进入场景不能重新构建检查器。");
            await _input.Activate(Find<Button>(ui, "LocateObject"));
            await _input.WaitUntil(map.IsVisibleInTree);
            Require(map.ObjectPoint(node)!.Value.DistanceTo(shell.SceneArea.GetCenter()) < .01f &&
                shell.SelectedObject == node && shell.InspectorContent == inspector, "局部场景定位应回到地图可见区域中央并保留检查器。");
            var offset = map.Offset;
            var zoom = map.Zoom;
            await _input.Activate(Find<Button>(ui, "Fleet"));
            Require(map.Offset == offset && map.Zoom == zoom, "打开列表不能移动地图。");
            await _input.Activate(Find<Button>(ui, "LocateObject"));
            Require(map.ObjectPoint(node)!.Value.DistanceTo(shell.SceneArea.GetCenter()) < .01f,
                "定位必须扣除同时打开的列表和检查器区域。");
            await _input.Activate(Find<Button>(map, "FitMap"));
            Require(map.MapToScreen(new(500, 300)).DistanceTo(shell.SceneArea.GetCenter()) < .01f,
                "全图必须按可见地图区域居中世界。");
            var before = map.Zoom;
            await _input.Activate(Find<Button>(map, "ZoomIn"));
            Require(map.Zoom > before, "放大按钮必须放大地图。");
            await _input.Activate(Find<Button>(map, "ZoomOut"));
            Require(Mathf.IsEqualApprox(map.Zoom, before), "缩小按钮必须恢复同一级缩放。");
        }
        finally { await Cleanup(fixture, app); }
    }

    [Test]
    public async Task Empty_train_has_a_visible_reason_for_the_disabled_scene_action()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup(emptyTrain: true);
        Train.App.App? app = null;
        try
        {
            app = await Ready(fixture, setup);
            var ui = app.GetNode<GameUi>("GameUi");
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            await _input.Click(map.GlobalPosition + map.ObjectPoint(new WorldObjectId.Train(new(7)))!.Value);
            var reason = Find<Label>(ui, "NavigationReason");
            Require(Find<Button>(ui, "EnterScene").Disabled && !Find<Button>(ui, "LocateObject").Disabled &&
                reason.IsVisibleInTree() && reason.Text.Contains("列车没有车辆") && setup.Scenes.Count == 0,
                "空列车仍可定位，进入场景必须置灰并在旁边说明原因。");
        }
        finally { await Cleanup(fixture, app); }
    }

    [Test]
    public async Task Layers_popup_closes_before_inspector_and_preserves_its_switches()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup();
        Train.App.App? app = null;
        try
        {
            app = await Ready(fixture, setup);
            var ui = app.GetNode<GameUi>("GameUi");
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            var shell = (GameShell)ui.World;
            await _input.Click(map.GlobalPosition + map.ObjectPoint(new WorldObjectId.Node(new(1)))!.Value);
            var inspector = shell.InspectorContent;
            var popup = map.GetNode<PopupPanel>("InformationLayers");
            var resources = map.GetNode<CheckBox>("InformationLayers/Body/Resources");
            await _input.Activate(Find<Button>(map, "Layers"));
            await _input.Activate(resources, popup);
            await _input.PressKey(Key.Escape, window: popup);
            Require(!popup.Visible && shell.InspectorContent == inspector && resources.ButtonPressed,
                "Esc 必须先关闭图层弹窗并保留检查器及图层开关。");
            await _input.Activate(Find<Button>(map, "Layers"));
            var outside = map.GlobalPosition + shell.SceneArea.GetCenter();
            await _input.Click(outside);
            Require(!popup.Visible && shell.InspectorContent == inspector && resources.ButtonPressed,
                "点击弹窗外面应关闭图层，并保留检查器及开关。");
            await _input.Activate(Find<Button>(map, "Layers"));
            await _input.Click(Find<Button>(map, "Layers").GetGlobalRect().GetCenter());
            Require(!popup.Visible && resources.ButtonPressed, "再次点击图层应关闭弹窗。");
        }
        finally { await Cleanup(fixture, app); }
    }
}
