using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class RailwayWorkUiTests
{
    [Test]
    public async Task Map_click_selects_line_double_click_enters_scene_and_detail_button_opens_work()
    {
        var setup = new HostSimulationTests.HostSetup(RailwayWorkUiFixture.Create(), RailwayWorkUiFixture.Content());
        LineScene? opened = null;
        var scenes = new FixedDisplayFactory
        {
            OpenWorldCall = (binding, _) => new SceneResult<IWorldSceneSession>.Success(opened = new(binding))
        };
        await Run(setup, async ui =>
        {
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            var edge = ui.View!.World.Edges.Single(item => item.Id.Value == RailwayWorkUiFixture.ActiveEdge);
            var start = ui.View.World.Nodes.Single(item => item.Id == edge.Start).Position;
            var end = ui.View.World.Nodes.Single(item => item.Id == edge.End).Position;
            var point = map.GlobalPosition + (map.MapToScreen(start) + map.MapToScreen(end)) / 2;
            var screen = TestScene.GetViewport().GetScreenTransform() * point;
            using (var motion = new InputEventMouseMotion { Position = screen, GlobalPosition = screen })
                Input.ParseInputEvent(motion);
            await _input.Frames();
            Require(TestScene.GetTree().Root.GuiGetHoveredControl() == map, "线路中点的点击被其他界面遮挡。");
            await _input.Click(point);
            await _input.Frames(2);
            var panel = Find<PanelContainer>(ui, "RailwayWork");
            Require(!panel.Visible && scenes.OpenCalls.Count == 0 &&
                Find<GameShell>(ui, "World").SelectedObject == new WorldObjectId.Edge(edge.Id),
                "单击线路必须只选中并打开详情，不能进入场景或打开施工面板。");
            await _input.MouseButton(point, MouseButton.Left, true, doubleClick: true);
            await _input.MouseButton(point, MouseButton.Left, false);
            Require(opened?.Target is WorldSceneTarget.Location
            {
                Value: WorldLocation.Edge { Id.Value: RailwayWorkUiFixture.ActiveEdge, DistanceMeters: var distance }
            } && distance == RailwayWorkUiFixture.ActiveLengthMeters / 2,
                "双击线路没有进入该线路中点。实际目标：" + opened?.Target);
            Require(ui.GetParent().FindChild("WorldSceneView", true, false) is Node3D { Visible: true },
                "双击线路后没有进入线路场景。");
            var panels = ui.GetNode<WorldPanels>("Root/World/WorldPanels");
            Require(panels.IsObjectOpen(new WorldObjectId.Edge(new(RailwayWorkUiFixture.ActiveEdge))), "点线路后没有打开线路详情。");
            var description = Find<Label>(LatestDetail(ui), "Description").Text;
            Require(description.Contains("线路长度") && !description.Contains('\r'), description);
            Require(description.Contains("1 号轨：空闲"), $"没有列车的轨道冒号后为空：{description}");
            await DismissError(ui);
            await _input.Activate(Find<Button>(LatestDetail(ui), "RailwayWork"));
            Require(panel.Visible && Find<Label>(panel, "Works").Text.Contains("任务 3"),
                "线路详情的施工按钮没有打开施工面板。");
        }, scenes);
        setup.Session.AssertComplete();
    }
}
