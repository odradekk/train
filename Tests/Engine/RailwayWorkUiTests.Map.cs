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
    public async Task Map_click_opens_line_inspector_without_entering_and_double_click_keeps_it()
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
            var panel = Find<PanelContainer>(ui, "LineInspector");
            Require(panel.IsVisibleInTree() && scenes.OpenCalls.Count == 0 &&
                Find<GameShell>(ui, "World").SelectedObject == new WorldObjectId.Edge(edge.Id),
                "单击线路必须直接打开线路检查器，不能进入场景。");
            var summary = Find<Label>(panel, "Summary").Text;
            Require(summary.Contains($"长度 {RailwayWorkUiFixture.ActiveLengthMeters} 米") && !summary.Contains('\r'), summary);
            RequireUnixWorkDescriptions(panel);
            await _input.DoubleClick(point);
            Require(opened?.Target is WorldSceneTarget.Location
            {
                Value: WorldLocation.Edge { Id.Value: RailwayWorkUiFixture.ActiveEdge, DistanceMeters: var distance }
            } && distance == RailwayWorkUiFixture.ActiveLengthMeters / 2,
                "双击线路没有进入该线路中点。实际目标：" + opened?.Target);
            Require(ui.GetParent().FindChild("WorldSceneView", true, false) is Node3D { Visible: true },
                "双击线路后没有进入线路场景。");
            var panels = ui.GetNode<WorldPanels>("Root/World/WorldPanels");
            Require(panels.IsObjectOpen(new WorldObjectId.Edge(new(RailwayWorkUiFixture.ActiveEdge))), "点线路后没有打开线路详情。");
            Require(ReferenceEquals(Find<GameShell>(ui, "World").InspectorContent, panel) && panel.IsVisibleInTree(),
                "双击进入线路场景不能替换或关闭已选中的线路检查器。");
        }, scenes);
        setup.Session.AssertComplete();
    }
}
