using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class EditorUiTests(Node testScene) : TestClass(testScene)
{
    private async Task<EditorPreview> Ready()
    {
        var fixture = new EditorPreview { Name = "EditorUiFixture" }; TestScene.AddChild(fixture);
        await FramesUntil(TestScene, () => fixture.Editor.Session is not null && fixture.Controller.Display?.DisplayedFrame is not null);
        return fixture;
    }
    [Test]
    public async Task Editor_uses_displayed_frame_for_picking_and_clears_old_sweep_immediately_after_change()
    {
        var root = TestScene.GetTree().Root;
        var scale = root.ContentScaleFactor;
        EditorPreview? fixture = null;
        try
        {
            UiCapture.ConfigureWindowIfNeeded(root);
            root.ContentScaleFactor = 1;
            fixture = await Ready();
            var inspector = Find<Control>(fixture.App, "Inspector");
            var viewport = TestScene.GetViewport().GetVisibleRect();
            var inspectorRect = inspector.GetGlobalRect();
            Require(inspectorRect.Size.X > 0 && inspectorRect.Size.Y > 0 && viewport.Encloses(inspectorRect),
                $"编辑器右侧检查器必须完整显示在窗口内：检查器 {inspectorRect}，视口 {viewport}。");
            await UiCapture.Capture(TestScene, "player-editor");
            var controller = fixture.Controller; var session = fixture.Factory.Session!;
            var displayed = controller.Display!.DisplayedFrame!;
            Require(controller.Display.Root.GetNodeOrNull<MeshInstance3D>("DebugLines") is not null, "线框必须来自Present帧。");
            var hit = controller.Pick(TestScene.GetViewport().GetVisibleRect().Size / 2);
            Require(hit?.Frame == displayed.Update.Frame && ((Train.Scenes.ContractSupport.FixedSceneFrame)displayed).LastPickRay is not null,
                "拾取必须由已显示的同一帧处理。");
            fixture.Editor.Select(new EditSelection.Elements([new(1)]));
            Require(!controller.Display.Root.Visible, "选区变化必须立即清除旧选择对应的线框。");
            await new UiTestInput(TestScene).Frames(2);
            var line = controller.Display.Root.GetNode<MeshInstance3D>("DebugLines");
            using (var arrays = line.Mesh.SurfaceGetArrays(0))
            {
                var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var latest = session.LastFrame!.DebugLines[0];
                Require(vertices[1] == new Vector3(latest.To.X, latest.To.Y, latest.To.Z), "选区变化和逐帧Present的新DebugLines必须原样显示，不能保留旧candidate线框。");
            }
            var preview = (SceneResult<IEditPreview>.Success)fixture.Editor.Preview(new EditOperation.Properties(new("修改后的草稿", ""), EditorPreviewSession.Draft.Specification))!;
            fixture.Editor.Apply(preview.Value, false);
            Require(!controller.Display.Root.Visible && controller.Pick(Vector2.Zero) is null, "草稿变化必须立即隐藏旧扫过线框并拒绝旧帧拾取。");
            await FramesUntil(TestScene, () => controller.Display?.Root.Visible == true && controller.Display.DisplayedFrame?.DebugLines.IsEmpty == true);
            Require(controller.Display!.Root.GetNodeOrNull<MeshInstance3D>("DebugLines") is null, "新帧没有DebugLines时必须删除旧线框。");
            session.StalePick = true;
            await new UiTestInput(TestScene).Frames(2);
            Require(controller.Pick(Vector2.Zero) is null && fixture.Editor.Message?.Contains("过期") == true, "旧编辑修订的命中必须拒绝并解释。");
        }
        finally { fixture?.QueueFree(); await new UiTestInput(TestScene).Frames(3); root.ContentScaleFactor = scale; }
    }
    [Test]
    public async Task Nested_input_uses_fixed_layout_transform_for_cell_faces_and_highlights()
    {
        var fixture = await Ready();
        try
        {
            fixture.Editor.Enter(new(10));
            await new UiTestInput(TestScene).Frames(2);
            var controller = fixture.Controller;
            var hit = new SceneHit(controller.Display!.DisplayedFrame!.Update.Frame, null,
                new PickSubject.Editing(fixture.Editor.Session!.Id, fixture.Editor.State!.Snapshot.Stamp.Revision, new(1)),
                new(0, 0, 0), GridFace.PositiveX, default, System.Numerics.Vector3.UnitX, 1, [0]);
            Require(controller.CellFor(hit, false) == new GridPosition(0, 0, 0) && controller.CellFor(hit, true) == new GridPosition(1, 0, 0),
                "进入旋转嵌套对象后，命中格与Face必须转换到当前局部坐标，不能使用动画Normal。");
            Require(controller.FaceAxis(hit) == GridAxis.X && controller.EnteredDraft!.Display.Name == "嵌套设备", "表面工作平面和对象文字必须属于当前进入的设备。");
            controller.ShowPreview(new HighlightPreview(fixture.Editor.State.Snapshot.Stamp));
            var root = controller.View.GetNode<Node3D>("EditPreviewHighlights");
            var box = root.GetChildren().OfType<MeshInstance3D>().Single();
            Require(box.Position.IsEqualApprox(new(8.5f, 0.5f, 0.5f)), "局部高亮格必须按固定嵌套朝向变回根空间显示。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    [Test]
    public async Task Brush_drag_submits_one_stroke_only_when_left_button_is_released()
    {
        var fixture = await Ready();
        try
        {
            var panel = EditorPanel(fixture); var session = fixture.Factory.Session!;
            var input = new UiTestInput(TestScene);
            var choices = panel.FindChildren("*", "OptionButton", true, false).OfType<OptionButton>();
            await input.Choose(choices.Single(choice => choice.ItemCount > 1 && choice.GetItemText(1).StartsWith("笔刷")), 1);
            await input.Choose(choices.Single(choice => choice.ItemCount > 0 && choice.GetItemText(0) == "添加"), 1);
            session.PickSamples = new([new(0, 0, 0), new(1, 0, 0), new(1, 0, 0), new(2, 0, 0)]);
            await input.Frames(2);
            var center = TestScene.GetViewport().GetVisibleRect().GetCenter();
            await input.MouseButton(center - new Vector2(40, 0), MouseButton.Left, true);
            await LeftMotion(center);
            await LeftMotion(center);
            await LeftMotion(center + new Vector2(40, 0));
            Require(session.ApplyCalls.Count == 0 && session.PreviewCalls.Count == 0, "笔刷按住与移动期间不得写入或提前提交预览。");
            await input.MouseButton(center + new Vector2(40, 0), MouseButton.Left, false);
            Require(session.PreviewCalls.Count == 1 && session.ApplyCalls.Count == 1, "松开笔刷必须只预览并应用一次。");
            var operation = session.PreviewCalls.Single() as EditOperation.Cells;
            Require(operation is { Footprint: EditFootprint.Stroke, Action: CellEdit.Remove }, "鼠标笔刷必须作为一个Stroke交给契约。");
            var stroke = (EditFootprint.Stroke)operation!.Footprint;
            Require(stroke.BrushWidth == 1 && stroke.Samples.SequenceEqual(new[] { new GridPosition(0, 0, 0), new(1, 0, 0), new(2, 0, 0) }),
                "同一笔必须保留采样顺序并去除重复格，不能把每格拆成独立修改。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    [Test]
    public async Task Confirmation_cancel_does_not_apply_and_accept_applies_once_with_confirmation()
    {
        var fixture = await Ready();
        try
        {
            var panel = EditorPanel(fixture); var session = fixture.Factory.Session!;
            var input = new UiTestInput(TestScene); session.ConfirmationRequired = true;
            await input.Enter(panel.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Single(edit => edit.PlaceholderText == "名称"), "123");
            await input.Activate(Find<Button>(panel, "Properties"));
            await ActivateEditorButton(panel, "Apply");
            var confirmation = panel.GetChildren().OfType<ConfirmationDialog>().Single();
            Require(confirmation.Visible && session.ApplyCalls.Count == 0, "RequiresConfirmation预览必须先弹确认，不能提前Apply。");
            await input.Activate(confirmation.GetCancelButton(), confirmation);
            Require(!confirmation.Visible && session.ApplyCalls.Count == 0, "取消确认不得应用修改。");
            await ActivateEditorButton(panel, "Apply");
            await input.Activate(confirmation.GetOkButton(), confirmation);
            Require(session.ApplyCalls.Count == 1 && session.ApplyCalls[0].Confirmed, "确认后必须只应用一次并传confirmed=true。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    [Test]
    public async Task Stale_preview_rejection_preserves_draft_and_displays_the_scene_problem()
    {
        var fixture = await Ready();
        try
        {
            var panel = EditorPanel(fixture); var session = fixture.Factory.Session!;
            var input = new UiTestInput(TestScene);
            await input.Enter(panel.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Single(edit => edit.PlaceholderText == "名称"), "123");
            await input.Activate(Find<Button>(panel, "Properties"));
            var before = fixture.Editor.Session!.Capture();
            // 第二次公开预览使UI仍持有的前一份预览过期；不实现Scenes的修订或占格算法。
            fixture.Editor.Preview(new EditOperation.Properties(new("另一个预览", ""), EditorPreviewSession.Draft.Specification));
            await ActivateEditorButton(panel, "Apply");
            Require(session.ApplyCalls.Count == 1 && fixture.Editor.Session.Capture() == before, "被拒绝的旧预览不得改变草稿或修订。");
            await FramesUntil(TestScene, () => Displayed(panel, "stale_preview") && Displayed(panel, "预览已过期"));
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    [Test]
    public async Task Enter_locked_and_closed_composite_rejections_preserve_level_and_display_reasons()
    {
        var fixture = await Ready();
        try
        {
            var panel = EditorPanel(fixture); var input = new UiTestInput(TestScene);
            var list = panel.FindChildren("*", "ItemList", true, false).OfType<ItemList>().Single();
            foreach (var (id, code, detail) in new[] { (20, "edit_locked", "缺失定义锁定"), (30, "functional_composite_closed", "功能复合体是封闭引用") })
            {
                await EnsureVisible(list);
                var index = Enumerable.Range(0, list.ItemCount).Single(item => list.GetItemText(item).StartsWith($"{id} ·"));
                await input.ClickItem(list, index);
                await ActivateEditorButton(panel, "EnterObject");
                Require(fixture.Editor.State!.EnteredObject is null, "进入被拒绝时必须保留原层级。");
                await FramesUntil(TestScene, () => Displayed(panel, code) && Displayed(panel, detail));
            }
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    private static Control EditorPanel(EditorPreview fixture) => Find<Control>(fixture.App, "DesignEditor");
    private static bool Displayed(Control panel, string text) => panel.FindChildren("*", "Label", true, false).OfType<Label>()
        .Any(label => label.IsVisibleInTree() && label.Text.Contains(text, StringComparison.Ordinal));
    private async Task EnsureVisible(Control control)
    {
        for (var parent = control.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll) scroll.EnsureControlVisible(control);
        await new UiTestInput(TestScene).Frames(2);
    }
    private async Task ActivateEditorButton(Control panel, string name)
    {
        var button = Find<Button>(panel, name); await EnsureVisible(button);
        await new UiTestInput(TestScene).Activate(button);
    }
    private async Task LeftMotion(Vector2 position)
    {
        var screen = TestScene.GetViewport().GetScreenTransform() * position;
        using var motion = new InputEventMouseMotion { Position = screen, GlobalPosition = screen, ButtonMask = MouseButtonMask.Left };
        Input.ParseInputEvent(motion); await new UiTestInput(TestScene).Frames();
    }
    private sealed record HighlightPreview(EditStamp Basis) : IEditPreview
    {
        public int CoveredCells => 1; public int AffectedCells => 1; public int SkippedCells => 0;
        public bool OccupancyComplete => true; public bool CanApply => true; public bool RequiresConfirmation => false;
        public System.Collections.Immutable.ImmutableArray<EditConflict> Conflicts => [];
        public System.Collections.Immutable.ImmutableArray<GridPosition> HighlightCells => [new(0, 0, 0)];
    }
    [Test]
    public async Task Scene_failure_stops_automatic_presentation_and_manual_retry_keeps_same_draft_session()
    {
        var fixture = await Ready();
        try
        {
            var session = fixture.Factory.Session!; session.FailPresent = true;
            await FramesUntil(TestScene, () => fixture.Editor.SceneUnavailable);
            var calls = session.Baselines.Count;
            await new UiTestInput(TestScene).Frames(3);
            Require(session.Baselines.Count == calls && !fixture.Controller.View.Interactive, "故障后不得自动重试或保留场景交互。");
            session.FailPresent = false;
            fixture.Controller.Process(0, true);
            Require(!fixture.Editor.SceneUnavailable && ReferenceEquals(fixture.Editor.Session, session) && session.Disposals == 0,
                "手动重试必须复用同一个编辑会话与草稿。");
            Require(session.Baselines[^1] is null, "重试必须使用Present(options,null)。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
}
