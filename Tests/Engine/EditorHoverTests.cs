using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EditorUiTests
{
    [Test]
    public async Task Hover_rejections_show_local_reasons_without_operation_errors_and_group_repeated_cells()
    {
        UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
        var fixture = await Ready(DirectEditingFixture());
        var input = new UiTestInput(TestScene);
        try
        {
            var panel = EditorPanel(fixture);
            var session = fixture.Factory.Session!;
            var console = fixture.App.FindChildren("*", "", true, false).OfType<GameConsole>().Single();
            var failures = console.LogEvents.Count(log => log.EventId == "editor_operation_failed");
            session.PreviewCall = _ => new SceneResult<IEditPreview>.Success(new FixedEditPreview(session.Capture().Stamp)
            {
                CanApply = false, CoveredCells = 3, AffectedCells = 0,
                HighlightCells = [new(1, 0, 2), new(2, 0, 2), new(3, 0, 2)],
                Conflicts =
                [
                    new(new(1, 0, 2), null, new("outside_envelope", SceneStage.Edit, null, null, null, null, "first outside")),
                    new(new(2, 0, 2), null, new("outside_envelope", SceneStage.Edit, null, null, null, null, "second outside")),
                    new(new(3, 0, 2), null, new("outside_envelope", SceneStage.Edit, null, null, null, null, "third outside"))
                ]
            });
            await input.Choose(DirectAction(panel), 1);
            await DirectHit(fixture, new(1, 0, 2));
            var point = fixture.Controller.VisibleArea.GetCenter();
            await DirectHover(point);
            Require(Displayed(panel, "超出外形范围") && Displayed(panel, "(1, 0, 2)")
                && (Displayed(panel, "3 格") || Displayed(panel, "3格")),
                "同类冲突必须汇总受阻格数、原因和首个坐标。");
            Require(!Displayed(panel, "(2, 0, 2)") && !Displayed(panel, "(3, 0, 2)"),
                "同一原因不能逐格展开，淹没预览说明。");
            Require(!fixture.Editor.HasError && console.LogEvents.Count(log => log.EventId == "editor_operation_failed") == failures,
                "冲突悬停只显示局部说明，不能污染操作错误状态和日志。");

            session.PreviewCall = _ => new SceneResult<IEditPreview>.Failure(
                [new("region_limit", SceneStage.Edit, null, null, null, null, "fixture region limit")]);
            await DirectHit(fixture, new(4, 0, 2));
            await DirectHover(point + new Vector2(8, 0));
            Require(Displayed(panel, "选区超过上限，请缩小范围。"), "被契约拒绝的悬停也必须说明原因。");
            Require(!fixture.Editor.HasError && console.LogEvents.Count(log => log.EventId == "editor_operation_failed") == failures
                && session.ApplyCalls.Count == 0,
                "超限悬停不得应用编辑、设置操作错误或新增失败日志。");
        }
        finally { fixture.QueueFree(); await input.Frames(3); }
    }

    [Test]
    public async Task Hover_reuses_preview_and_mesh_for_equal_intent_but_refreshes_brush_action_and_values()
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
                { HighlightCells = [new(2, 3, 4)] });
            await input.Choose(DirectAction(panel), 1);
            await input.Choose(DirectTool(panel), 1);
            await DirectHit(fixture, new(2, 3, 4));
            var point = fixture.Controller.VisibleArea.GetCenter();
            await DirectHover(point);
            var queries = session.PreviewCalls.Count;
            var mesh = DirectHighlights(fixture).Multimesh;
            Require(mesh.InstanceCount == 1 && session.PreviewCalls.Last() is EditOperation.Cells
                { Footprint: EditFootprint.Stroke { BrushWidth: 1, Samples.Length: 1 } }, "笔刷悬停必须先显示单点预览。");
            await DirectHover(point + new Vector2(8, 0));
            Require(session.PreviewCalls.Count == queries && ReferenceEquals(DirectHighlights(fixture).Multimesh, mesh),
                "同格移动会重新分配 Stroke.Samples；内容相同不得重查或重建 MultiMesh。");
            var camera = fixture.Controller.View.CameraRig.Camera.GlobalTransform;
            await input.Drag(point, new Vector2(12, 0));
            Require(fixture.Controller.View.CameraRig.Camera.GlobalTransform != camera,
                "相机回归必须通过真实输入实际改变相机变换。");
            Require(session.PreviewCalls.Count == queries && ReferenceEquals(DirectHighlights(fixture).Multimesh, mesh),
                "相机移动后仍命中同一目标时必须复用查询和 MultiMesh。");

            await input.MouseButton(point, MouseButton.Left, true);
            queries = session.PreviewCalls.Count;
            await DirectHit(fixture, new(3, 3, 4));
            await LeftMotion(point + new Vector2(16, 0));
            Require(session.PreviewCalls.Count > queries && session.PreviewCalls.Last() is EditOperation.Cells
                { Footprint: EditFootprint.Stroke stroke } && stroke.Samples.SequenceEqual(new[] { new GridPosition(2, 3, 4), new(3, 3, 4) }),
                "拖动累积新采样必须刷新整笔预览。");
            queries = session.PreviewCalls.Count;
            mesh = DirectHighlights(fixture).Multimesh;
            await LeftMotion(point + new Vector2(24, 0));
            Require(session.PreviewCalls.Count == queries && ReferenceEquals(DirectHighlights(fixture).Multimesh, mesh),
                "累积笔画按内容相等时，新分配的采样数组也必须复用预览。");
            await input.MouseButton(point, MouseButton.Left, false);

            queries = session.PreviewCalls.Count;
            var width = panel.FindChildren("*", "HBoxContainer", true, false).OfType<HBoxContainer>()
                .Single(row => row.GetChildren().OfType<Label>().Any(label => label.Text == "笔刷宽度"))
                .GetChildren().OfType<SpinBox>().Single();
            await EnsureVisible(width);
            await input.Enter(width.GetLineEdit(), "3");
            await input.PressKey(Key.Enter);
            Require(session.PreviewCalls.Count > queries && session.PreviewCalls.Last() is EditOperation.Cells
                { Footprint: EditFootprint.Stroke { BrushWidth: 3 } }, "笔刷宽度改变必须刷新预览。");
            queries = session.PreviewCalls.Count;
            await EnsureVisible(DirectAction(panel));
            await input.Choose(DirectAction(panel), 3);
            Require(session.PreviewCalls.Count > queries && session.PreviewCalls.Last() is EditOperation.Cells { Action: CellEdit.Paint },
                "动作改变必须重新查询，即使目标格不变。");
            queries = session.PreviewCalls.Count;
            var swatch = panel.FindChildren("*", "Button", true, false).OfType<Button>().Single(button => button.Text == "工程黄");
            await EnsureVisible(swatch);
            await input.Activate(swatch);
            Require(session.PreviewCalls.Count > queries && session.PreviewCalls.Last() is EditOperation.Cells
                { Action: CellEdit.Paint { Color: { } color } } && color.Value == "#d9a62b",
                "相同涂装动作的颜色改变必须刷新预览。");
        }
        finally { fixture.QueueFree(); await input.Frames(3); }
    }

    [Test]
    public async Task Stationary_hover_refreshes_after_apply_evaluation_acceptance_and_entering_an_object()
    {
        UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
        var fixture = await Ready(DirectEditingFixture());
        var input = new UiTestInput(TestScene);
        var evaluationGate = new TaskCompletionSource<DesignEvaluation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = EditorPreviewSession.Evaluate(EditorPreviewSession.Draft);
        try
        {
            var panel = EditorPanel(fixture);
            var session = fixture.Factory.Session!;
            ((ScriptedContentFactory)fixture.App.Host!.ContentFactory).EvaluateDesignCall = (_, _) =>
                new ContentResult<DesignEvaluation>.Success(evaluationGate.Task.GetAwaiter().GetResult());
            session.PreviewCall = _ => new SceneResult<IEditPreview>.Success(new FixedEditPreview(session.Capture().Stamp)
            {
                AffectedCells = ReferenceEquals(session.Read().CurrentEvaluation, replacement) ? 2 : 1,
                HighlightCells = ReferenceEquals(session.Read().CurrentEvaluation, replacement)
                    ? [new(9, 0, 0), new(10, 0, 0)] : [new(0, 0, 0)]
            });
            await input.Choose(DirectAction(panel), 1);
            var point = fixture.Controller.VisibleArea.GetCenter();
            await DirectHover(point);
            Require(DirectHighlights(fixture).Multimesh.InstanceCount == 1, "点击前必须存在真实悬停预览。");
            var initialStamp = session.Capture().Stamp;
            var queries = session.PreviewCalls.Count;
            await input.Click(point);
            await FramesUntil(TestScene, () => session.PreviewCalls.Count > queries && DirectHighlights(fixture).Multimesh.InstanceCount == 1);
            Require(session.ApplyCalls.Count == 1 && session.Capture().Stamp != initialStamp && session.Read().CurrentEvaluation is null,
                "点击必须提交新修订，并在尚未获得评估时重新显示悬停。");

            queries = session.PreviewCalls.Count;
            Require(session.AcceptEvaluation(session.Capture().Stamp, replacement), "用例必须让当前修订接受新的评估引用。");
            fixture.Editor.Process();
            await input.Frames(3);
            Require(ReferenceEquals(fixture.Editor.State!.CurrentEvaluation, replacement)
                && session.PreviewCalls.Count > queries && DirectHighlights(fixture).Multimesh.InstanceCount == 2
                && Displayed(panel, "影响 2"),
                "评估到达经 Service.ReadState 和 Controller.OnChanged 后，静止鼠标也必须重查并显示新评估的预览。");
            queries = session.PreviewCalls.Count;
            fixture.Editor.Enter(new(10));
            await input.Frames(3);
            Require(session.Read().EnteredObject == new EditElementId(10) && session.PreviewCalls.Count > queries
                && DirectHighlights(fixture).Multimesh.InstanceCount == 2,
                "进入对象改变局部编辑上下文后，静止鼠标也必须刷新预览。");
        }
        finally
        {
            evaluationGate.TrySetResult(replacement);
            fixture.QueueFree();
            await input.Frames(3);
        }
    }
}
