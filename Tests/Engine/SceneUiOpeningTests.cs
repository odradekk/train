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
    public async Task Single_click_inspects_and_double_click_enters_without_rebuilding_the_inspector()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup();
        Train.App.App? app = null;
        try
        {
            app = await Ready(fixture, setup);
            var ui = app.GetNode<GameUi>("GameUi");
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            var inspections = 0;
            ui.GetNode<WorldPanels>("Root/World/WorldPanels").InspectorOpened += (id, _) =>
            {
                if (id == new WorldObjectId.Node(new(1)))
                    inspections++;
            };
            var point = map.GlobalPosition + map.MapToScreen(new(250, 300));
            await _input.Click(point);
            var shell = (GameShell)ui.World;
            Require(setup.Scenes.Count == 0 && !app.WorldScenes!.View.Visible &&
                shell.SelectedObject == new WorldObjectId.Node(new(1)) && inspections == 1,
                "单击节点必须只显示检查器，不请求场景。");
            var inspector = shell.InspectorContent;
            await _input.MouseButton(point, MouseButton.Left, true, doubleClick: true);
            await _input.MouseButton(point, MouseButton.Left, false);
            await _input.WaitUntil(() => app.WorldScenes!.View.Interactive);
            Require(setup.Scenes.Single().RequestCalls.SequenceEqual([NodeTarget]) && inspections == 1 &&
                shell.InspectorContent == inspector, "双击应进入节点场景，并保留已经构建的检查器。");
        }
        finally { await Cleanup(fixture, app); }
    }

    [Test]
    public async Task Unopenable_scenes_preserve_the_same_object_selection()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup { RejectNext = true };
        Train.App.App? app = null;
        try
        {
            app = await Ready(fixture, setup);
            var ui = app.GetNode<GameUi>("GameUi");
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            var shell = (GameShell)ui.World;
            var panels = ui.GetNode<WorldPanels>("Root/World/WorldPanels");
            var node = new WorldObjectId.Node(new(1));
            map.RaiseObjectClicked(node);
            Require(!app.WorldScenes!.View.Visible && ui.ErrorText.Contains("fixture_node_failed") &&
                shell.SelectedObject == node && panels.IsObjectOpen(node),
                "场景请求失败仍须选中节点并显示检查器。");
            var instance = new WorldObjectId.Instance(Vehicle);
            map.RaiseObjectClicked(instance);
            Require(shell.SelectedObject == instance && panels.IsObjectOpen(instance) &&
                ui.ErrorText.Contains("对象没有可观察的场景目标"),
                "有效对象没有场景目标时，仍须切换到该对象的检查器。");
            map.RaiseObjectClicked(new WorldObjectId.Node(new(999)));
            Require(shell.SelectedObject == instance && panels.IsObjectOpen(instance) &&
                ui.ErrorText.Contains("对象已不存在或所属对局已更换"),
                "目标不可用时须保留原选择并报告失效，不能清空或替换检查器。");
        }
        finally { await Cleanup(fixture, app); }
    }
}
