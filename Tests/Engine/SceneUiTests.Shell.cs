using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Scenes.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SceneUiTests
{
    private static bool IsDownward(SceneRay? ray) => ray is not null &&
        Math.Abs(ray.Direction.X) < 0.001f && ray.Direction.Y < -0.99f && Math.Abs(ray.Direction.Z) < 0.001f;

    [Test]
    public async Task Workspace_mask_blocks_minimap_navigation_and_scene_picking()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup(minimap: true);
        Train.App.App? app = null;
        try
        {
            app = await Ready(fixture, setup);
            var ui = app.GetNode<GameUi>("GameUi");
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            await Open(app, setup, map.GlobalPosition + map.MapToScreen(new(250, 300)), NodeTarget);
            var minimap = Find<NodeMinimap>(ui, "NodeMinimap");
            await _input.WaitUntil(minimap.IsVisibleInTree);
            minimap.Size += new Vector2(16, 12);
            await _input.Frames(2);
            var shell = (GameShell)ui.World;
            Require(Mathf.IsEqualApprox(minimap.GetGlobalRect().End.X, shell.SceneArea.End.X) &&
                Mathf.IsEqualApprox(minimap.GlobalPosition.Y, shell.SceneArea.Position.Y),
                "小地图尺寸变化后仍须对齐可用场景区域。");
            await _input.Activate(Find<Button>(ui, "EconomyReports"));
            var workspace = shell.WorkspaceContent!;
            var point = minimap.GlobalPosition + new Vector2(minimap.Size.X / 2, 4);
            Require(Find<Control>(shell, "WorkspaceMask").GetGlobalRect().HasPoint(point) &&
                !workspace.GetGlobalRect().HasPoint(point), "点击位置必须由遮罩覆盖，且位于工作区内容之外的小地图上。");
            var camera = app.WorldScenes!.View.CameraRig;
            var focus = camera.Focus;
            var ray = setup.Frames[^1].LastPickRay;
            var resolutions = setup.Scenes[^1].ResolveCalls.Count;
            await _input.Click(point);
            Require(camera.Focus.DistanceTo(focus) < .001f && ReferenceEquals(setup.Frames[^1].LastPickRay, ray) &&
                setup.Scenes[^1].ResolveCalls.Count == resolutions && shell.WorkspaceContent == workspace,
                "遮罩点击不能定位小地图、触发场景拾取或关闭工作区。");
            await _input.PressKey(Key.Escape);
            Require(shell.WorkspaceContent is null, "Esc 应撤掉工作区及遮罩。");
            await _input.Click(point);
            Require(camera.Focus.DistanceTo(focus) > 1,
                "撤掉遮罩后，同一位置必须能实际改变小地图焦点，证明遮罩前的检查会捕获穿透。");
        }
        finally { await Cleanup(fixture, app); }
    }
}
