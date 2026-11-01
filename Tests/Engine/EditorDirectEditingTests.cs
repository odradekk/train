using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.ContractSupport;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EditorUiTests
{
    [Test]
    public async Task Single_cell_press_applies_each_action_once_before_release()
    {
        UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
        var fixture = await Ready(DirectEditingFixture());
        var input = new UiTestInput(TestScene);
        try
        {
            var panel = EditorPanel(fixture);
            var session = fixture.Factory.Session!;
            session.ApplyCall = _ => new SceneResult<EditState>.Success(session.Read());
            for (var action = 0; action < 4; action++)
            {
                await EnsureVisible(DirectAction(panel));
                await input.Choose(DirectAction(panel), action);
                await DirectHit(fixture, new(2, 3, 4));
                session.ApplyCalls.Clear();
                var point = fixture.Controller.VisibleArea.GetCenter();
                await input.MouseButton(point, MouseButton.Left, true);
                Require(session.ApplyCalls.Count == 1, "单格按下必须立即提交一次，不能等待松开或确认。");
                var operation = session.ApplyCalls.Single() as EditOperation.Cells;
                var expected = action == 0 ? new GridPosition(2, 4, 4) : new GridPosition(2, 3, 4);
                Require(operation?.Footprint is EditFootprint.Cell cell && cell.At == expected && DirectActionIs(operation.Action, action),
                    $"单格动作 {action} 必须使用命中格；只有添加向命中面外移一格。");
                await input.MouseButton(point, MouseButton.Left, false);
                Require(session.ApplyCalls.Count == 1, "松开单格不能重复提交。");
            }
        }
        finally { fixture.QueueFree(); await input.Frames(3); }
    }

    [Test]
    public async Task Box_uses_two_clicks_normalizes_both_corners_and_extends_only_add_height()
    {
        UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
        var fixture = await Ready(DirectEditingFixture());
        var input = new UiTestInput(TestScene);
        try
        {
            var panel = EditorPanel(fixture);
            var session = fixture.Factory.Session!;
            session.ApplyCall = _ => new SceneResult<EditState>.Success(session.Read());
            session.PreviewCall = _ => new SceneResult<IEditPreview>.Success(new FixedEditPreview(session.Capture().Stamp)
                { HighlightCells = [new(0, 0, 0), new(1, 0, 0)] });
            await input.Choose(DirectTool(panel), 2);
            var height = Find<SpinBox>(panel, "BoxHeight");
            Require(Displayed(panel, "长方体高度"), "长方体高度必须有玩家可见的标签。");
            await EnsureVisible(height);
            await input.Enter(height.GetLineEdit(), "3");
            await input.PressKey(Key.Enter);
            foreach (var reverse in new[] { false, true })
            for (var action = 0; action < 4; action++)
            {
                await EnsureVisible(DirectAction(panel));
                await input.Choose(DirectAction(panel), action);
                var first = reverse ? new GridPosition(-2, 1, -3) : new GridPosition(5, 4, 7);
                var second = reverse ? new GridPosition(5, 4, 7) : new GridPosition(-2, 1, -3);
                var expected = action == 0
                    ? new GridBounds(new(-2, 2, -3), new(5, 7, 7))
                    : new GridBounds(new(-2, 1, -3), new(5, 4, 7));
                await DirectHit(fixture, first);
                session.ApplyCalls.Clear();
                var point = fixture.Controller.VisibleArea.GetCenter();
                await input.Click(point);
                Require(session.ApplyCalls.Count == 0, "长方体首击只能选定首角。");
                Require(Displayed(panel, "再点一下确定对角，Esc 取消。"), "首角与悬停范围相同也必须显示下一步提示。");
                await DirectHit(fixture, second);
                await DirectHover(point + new Vector2(8, 0));
                Require(session.ApplyCalls.Count == 0, "长方体悬停不得写入。");
                Require(session.PreviewCalls.LastOrDefault() is EditOperation.Cells { Footprint: EditFootprint.Box box } operation
                    && box.Bounds == expected && DirectActionIs(operation.Action, action),
                    $"长方体动作 {action} 的悬停范围必须按两角各轴排序，只有添加向上延伸高度。");
                Require(DirectHighlights(fixture).Multimesh.InstanceCount == 2, "悬停必须显示契约返回的范围高亮。");
                if (!reverse && action == 0) await UiCapture.Capture(TestScene, "direct-edit-box");
                await input.MouseButton(point, MouseButton.Left, true);
                Require(session.ApplyCalls.Count == 1 && session.ApplyCalls.Single() is EditOperation.Cells
                    { Footprint: EditFootprint.Box applied } committed && applied.Bounds == expected && DirectActionIs(committed.Action, action),
                    "长方体第二次按下必须一次提交当前范围与动作。");
                await input.MouseButton(point, MouseButton.Left, false);
                Require(session.ApplyCalls.Count == 1, "长方体第二次松开不能重复提交。");
            }
        }
        finally { fixture.QueueFree(); await input.Frames(3); }
    }

    [Test]
    public async Task Escape_and_switching_tools_cancel_the_pending_box_corner()
    {
        UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
        var fixture = await Ready(DirectEditingFixture());
        var input = new UiTestInput(TestScene);
        try
        {
            var panel = EditorPanel(fixture);
            var session = fixture.Factory.Session!;
            session.ApplyCall = _ => new SceneResult<EditState>.Success(session.Read());
            await input.Choose(DirectAction(panel), 1);
            foreach (var escape in new[] { true, false })
            {
                await input.Choose(DirectTool(panel), 2);
                await DirectHit(fixture, new(5, 4, 7));
                var point = fixture.Controller.VisibleArea.GetCenter();
                await input.Click(point);
                if (escape)
                {
                    await input.PressKey(Key.Escape);
                    Require(fixture.Editor.IsOpen && !fixture.Editor.Leaving, "已有首角时Escape必须取消范围，不能退出编辑器。");
                }
                else
                {
                    await input.Choose(DirectTool(panel), 0);
                    await input.Choose(DirectTool(panel), 2);
                }
                await DirectHit(fixture, new(-2, 1, -3));
                await input.Click(point);
                Require(session.ApplyCalls.Count == 0, "取消首角后下一次点击必须重新选首角，不能沿用旧范围。");
                await DirectHit(fixture, new(1, 2, 3));
                await input.Click(point);
                Require(session.ApplyCalls.Count == 1 && session.ApplyCalls.Single() is EditOperation.Cells
                    { Footprint: EditFootprint.Box { Bounds: var bounds }, Action: CellEdit.Remove }
                    && bounds == new GridBounds(new(-2, 1, -3), new(1, 2, 3)),
                    "取消后必须以新的两个角提交范围。");
                session.ApplyCalls.Clear();
            }
        }
        finally { fixture.QueueFree(); await input.Frames(3); }
    }

    [Test]
    public async Task Hover_is_read_only_shows_transparent_green_or_red_and_explains_zero_affected_cells()
    {
        UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
        var fixture = await Ready(DirectEditingFixture());
        var input = new UiTestInput(TestScene);
        try
        {
            var panel = EditorPanel(fixture);
            var session = fixture.Factory.Session!;
            await input.Choose(DirectAction(panel), 1);
            var targetX = 2;
            foreach (var state in new[] { (Allowed: true, Affected: 1), (Allowed: false, Affected: 1), (Allowed: true, Affected: 0) })
            {
                session.PreviewCall = _ => new SceneResult<IEditPreview>.Success(new FixedEditPreview(session.Capture().Stamp)
                {
                    CanApply = state.Allowed, AffectedCells = state.Affected,
                    HighlightCells = [new(2, 3, 4)],
                    Conflicts = state.Allowed ? [] :
                    [new(new(2, 3, 4), null, new("occupancy_conflict", SceneStage.Edit, null, null, null, null, "fixture conflict"))]
                });
                await DirectHit(fixture, new(targetX++, 3, 4));
                await DirectHover(fixture.Controller.VisibleArea.GetCenter() + new Vector2(state.Affected * 8, state.Allowed ? 0 : 8));
                var mesh = DirectHighlights(fixture);
                var material = (StandardMaterial3D)mesh.MaterialOverride;
                var color = material.AlbedoColor;
                Require(mesh.Multimesh.InstanceCount == 1 && material.Transparency == BaseMaterial3D.TransparencyEnum.Alpha
                    && color.A > 0 && color.A < 1, "悬停高亮必须以透明材质显示契约返回的格。");
                Require(state.Allowed && state.Affected > 0 ? color.G > color.R : color.R > color.G,
                    "有实际修改且可应用时必须显示绿色；冲突或零改动必须显示红色。");
                Require(session.ApplyCalls.Count == 0, "所有悬停查询均不得应用编辑。");
                await UiCapture.Capture(TestScene, state.Affected == 0 ? "direct-edit-empty" : state.Allowed ? "direct-edit-green" : "direct-edit-conflict");
                if (!state.Allowed) Require(Displayed(panel, "与其他部分的占格重叠"), "红色冲突预览必须说明冲突原因。");
                if (state.Affected == 0)
                    Require(Displayed(panel, "没有可移除的体素"),
                        "零改动悬停必须向玩家解释没有可修改的格。");
            }
            var reasons = new[] { "已有体素", "没有可移除", "没有可替换", "没有可涂装" };
            for (var action = 0; action < reasons.Length; action++)
            {
                await input.Choose(DirectAction(panel), action);
                await DirectHover(fixture.Controller.VisibleArea.GetCenter() + new Vector2(action * 8, 0));
                Require(Displayed(panel, reasons[action]), "零改动原因必须对应当前动作。");
            }
        }
        finally { fixture.QueueFree(); await input.Frames(3); }
    }

    [Test]
    public async Task Oversized_box_hover_shows_one_transparent_red_bounds_and_the_limit_reason_without_applying()
    {
        UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
        var fixture = await Ready(DirectEditingFixture());
        var input = new UiTestInput(TestScene);
        try
        {
            var panel = EditorPanel(fixture);
            var session = fixture.Factory.Session!;
            session.PreviewCall = _ => new SceneResult<IEditPreview>.Failure(
                [new("region_limit", SceneStage.Edit, null, null, null, null, "fixture region limit")]);
            await input.Choose(DirectAction(panel), 1);
            await input.Choose(DirectTool(panel), 2);
            await DirectHit(fixture, new(0, 0, 0));
            var point = fixture.Controller.VisibleArea.GetCenter();
            await input.Click(point);
            await DirectHit(fixture, new(5000, 0, 0));
            await DirectHover(point + new Vector2(8, 0));
            Require(session.PreviewCalls.LastOrDefault() is EditOperation.Cells
                { Footprint: EditFootprint.Box { Bounds: var bounds }, Action: CellEdit.Remove }
                && bounds == new GridBounds(new(0, 0, 0), new(5000, 0, 0)),
                "超限悬停必须把可表示的5,001格范围交给契约预览。");
            var mesh = DirectHighlights(fixture);
            var material = (StandardMaterial3D)mesh.MaterialOverride;
            var color = material.AlbedoColor;
            Require(mesh.Multimesh.InstanceCount == 1 && material.Transparency == BaseMaterial3D.TransparencyEnum.Alpha
                && color.R > color.G && color.A > 0 && color.A < 1,
                "预览被拒绝时必须用一个红色半透明包围盒显示原始范围。");
            Require(Displayed(panel, "选区超过上限，请缩小范围。") && session.ApplyCalls.Count == 0,
                "超限悬停必须说明缩小范围，不能提交编辑。");
            await UiCapture.Capture(TestScene, "direct-edit-limit");
        }
        finally { fixture.QueueFree(); await input.Frames(3); }
    }

    [Test]
    public async Task Hover_discards_a_result_based_on_another_revision()
    {
        var fixture = await Ready(DirectEditingFixture());
        var input = new UiTestInput(TestScene);
        try
        {
            var session = fixture.Factory.Session!;
            await input.Choose(DirectAction(EditorPanel(fixture)), 1);
            session.PreviewCall = _ => new SceneResult<IEditPreview>.Success(new FixedEditPreview(
                session.Capture().Stamp with { Revision = new(999) }) { HighlightCells = [new(0, 0, 0)] });
            await DirectHover(fixture.Controller.VisibleArea.GetCenter());
            Require(DirectHighlights(fixture).Multimesh.InstanceCount == 0 && session.ApplyCalls.Count == 0,
                "其他修订的悬停结果不能显示或改变草稿。");
        }
        finally { fixture.QueueFree(); await input.Frames(3); }
    }

    private static EditorPreview DirectEditingFixture()
    {
        var core = HostContentScenario.CreateProjection(HostCoreScenario.Input);
        var voxel = new VoxelDefinitionId("fixture:direct-edit");
        return new()
        {
            Name = "DirectEditingFixture",
            Content = new FixedContentView(core, new(1))
            {
                Blueprints = [], LocalDrafts = [], GenerationProfiles = [], Voxels = [new(voxel, core.Goods[0])],
                VoxelDisplays = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelDisplayProjection>>
                {
                    [voxel] = new ContentLookup<VoxelDisplayProjection>.Found(new(voxel, new("编辑体素", ""), new("icon")))
                },
                VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
                {
                    [voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(new(voxel, AllowedVoxelOrientations.None,
                        new VoxelCollision.FullBox(), new VoxelPresentation.Mesh(new("model"), []), new(false, null)))
                }
            }
        };
    }

    private async Task DirectHit(EditorPreview fixture, GridPosition cell)
    {
        var session = fixture.Factory.Session!;
        var displayed = fixture.Controller.Display!.DisplayedFrame!;
        var frame = new FixedSceneFrame
        {
            Update = displayed.Update,
            TerrainHeightResult = new Train.Core.Contracts.Lookup<double>.Missing(),
            PickResult = new ScenePick.Hit(new(displayed.Update.Frame, null,
                new PickSubject.Editing(session.Id, session.Capture().Stamp.Revision, new(1)), cell, GridFace.PositiveY,
                default, System.Numerics.Vector3.UnitY, 1, []))
        };
        session.PresentCall = (_, _) => new SceneResult<ISceneFrame>.Success(frame);
        await FramesUntil(TestScene, () => ReferenceEquals(fixture.Controller.Display.DisplayedFrame, frame));
    }

    private async Task DirectHover(Vector2 point)
    {
        var screen = TestScene.GetViewport().GetScreenTransform() * point;
        using var motion = new InputEventMouseMotion { Position = screen, GlobalPosition = screen };
        Input.ParseInputEvent(motion);
        await new UiTestInput(TestScene).Frames(2);
    }

    private static MultiMeshInstance3D DirectHighlights(EditorPreview fixture) => fixture.Controller.View
        .GetNode<Node3D>("EditPreviewHighlights").GetChildren().OfType<MultiMeshInstance3D>().Single();
    private static OptionButton DirectTool(DesignEditorPanel panel) => panel.FindChildren("*", "OptionButton", true, false)
        .OfType<OptionButton>().Single(choice => choice.ItemCount == 4 && choice.GetItemText(0) == "单格");
    private static OptionButton DirectAction(DesignEditorPanel panel) => panel.FindChildren("*", "OptionButton", true, false)
        .OfType<OptionButton>().Single(choice => choice.ItemCount > 1 && choice.GetItemText(0) == "添加");
    private static bool DirectActionIs(CellEdit actual, int index) => index switch
    {
        0 => actual is CellEdit.Add { Value.Orientation: VoxelOrientation.PxPy },
        1 => actual is CellEdit.Remove,
        2 => actual is CellEdit.Replace { Value.Orientation: VoxelOrientation.PxPy },
        _ => actual is CellEdit.Paint
    };
}
