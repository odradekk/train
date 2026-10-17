using System.Collections.Immutable;
using Train.Scenes.ContractSupport;
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
            var initial = controller.Display!.DisplayedFrame!;
            session.PresentCall = (_, _) => new SceneResult<ISceneFrame>.Success(new FixedSceneFrame
            {
                Update = initial.Update with { Upserts = [VisibleObject()] },
                DebugLines = initial.DebugLines,
                PickResult = ((FixedSceneFrame)initial).PickResult,
                TerrainHeightResult = new Train.Core.Contracts.Lookup<double>.Missing()
            });
            controller.Display.Timestamp = () => 0;
            controller.Process(0);
            session.PresentCall = null;
            var displayed = controller.Display.DisplayedFrame!;
            var geometry = controller.Display.Objects[new(1)].Visuals[0].Node;
            Require(controller.Display.Root.GetNodeOrNull<MeshInstance3D>("DebugLines") is not null, "线框必须来自Present帧。");
            var hit = controller.Pick(TestScene.GetViewport().GetVisibleRect().Size / 2);
            Require(hit is ScenePick.Hit picked && picked.Value.Frame == displayed.Update.Frame && ((Train.Scenes.ContractSupport.FixedSceneFrame)displayed).LastPickRay is not null,
                "拾取必须由已显示的同一帧处理。");
            fixture.Editor.Select(new EditSelection.Elements([new(1)]));
            Require(controller.Display.Root.Visible && controller.Display.Root.GetNodeOrNull<MeshInstance3D>("DebugLines") is null && ReferenceEquals(controller.Display.DisplayedFrame, displayed) && geometry.IsInsideTree() && geometry.IsVisibleInTree(), "选区变化必须立即清除旧线框并保留已提交几何。");
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
            Require(controller.Display.Root.Visible && controller.Display.Root.GetNodeOrNull<MeshInstance3D>("DebugLines") is null && controller.Pick(Vector2.Zero) is null, "草稿变化必须保留已提交几何、清除旧线框并拒绝旧修订拾取。");
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
            controller.ShowPreview(new Train.Scenes.ContractSupport.FixedEditPreview(fixture.Editor.State.Snapshot.Stamp,
                new EditOperation.Properties(new("高亮", ""), EditorPreviewSession.Draft.Specification))
                { HighlightCells = [new(0, 0, 0)] });
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
    [Test]
    public async Task Retry_keeps_full_replacement_request_through_loading_and_resource_budget()
    {
        var fixture = await Ready();
        try
        {
            var controller = fixture.Controller;
            var session = fixture.Factory.Session!;
            session.FailPresent = true;
            controller.Process(0);
            session.FailPresent = false;
            var stamp = controller.Display!.DisplayedFrame!.Update.Frame;
            var loading = true;
            var replacement = new Train.Scenes.ContractSupport.FixedSceneFrame
            {
                Update = new(null, stamp with { Revision = stamp.Revision + 1 }, default,
                    [new(new(1), null, new(default, System.Numerics.Quaternion.Identity), null,
                        [new SceneVisual.Missing(new ContentReference.Design(new("missing-one")), true),
                         new SceneVisual.Missing(new ContentReference.Design(new("missing-two")), true)], [], [])],
                    [], new([], []), []),
                TerrainHeightResult = new Train.Core.Contracts.Lookup<double>.Missing()
            };
            session.PresentCall = (_, _) => new SceneResult<ISceneFrame>.Success(loading
                ? new Train.Scenes.ContractSupport.FixedSceneFrame { Update = replacement.Update, Readiness = SceneReadiness.Loading }
                : replacement);
            controller.Process(0, true);
            Require(fixture.Editor.SceneUnavailable && session.Baselines[^1] is null && !controller.View.Interactive,
                "Loading不能恢复故障场景或改回增量请求。");
            Require(controller.Pick(Vector2.Zero) is ScenePick.NotReady && controller.PickPlane(Vector2.Zero) is null,
                "手动重试的Loading期间不得产生选择或工作平面回落。");
            loading = false;
            long timestamp = 0;
            controller.Display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
            controller.Process(0);
            Require(fixture.Editor.SceneUnavailable && controller.Display.IsPreparing && session.Baselines[^1] is null,
                "预算用尽时保留完整替换候选，不能提前标记恢复。");
            controller.Display.Timestamp = () => 0;
            controller.Process(0);
            Require(!fixture.Editor.SceneUnavailable && ReferenceEquals(controller.Display.DisplayedFrame, replacement) &&
                session.Baselines[^1] is null && controller.Display.Root.Visible,
                "完整替换真正提交后才恢复同一会话。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    [Test]
    public async Task Resource_integration_exception_hides_scene_and_stops_interaction()
    {
        var fixture = await Ready();
        try
        {
            var session = fixture.Factory.Session!;
            var display = fixture.Controller.Display!;
            var displayed = display.DisplayedFrame;
            session.PresentCall = (_, _) => new SceneResult<ISceneFrame>.Success(new Train.Scenes.ContractSupport.FixedSceneFrame
            {
                Update = new(null, displayed!.Update.Frame, default,
                    [new(new(1), new(999), new(default, System.Numerics.Quaternion.Identity), null, [], [], [])], [], new([], []), [])
            });
            fixture.Controller.Process(0);
            Require(fixture.Editor.SceneUnavailable && !display.Root.Visible && !fixture.Controller.View.Interactive &&
                ReferenceEquals(display.DisplayedFrame, displayed),
                "资源接入异常必须隐藏旧画面并停止交互，失败候选不能成为显示基准。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    [Test]
    public async Task Successful_frame_missing_content_problem_is_visible_in_editor_and_console()
    {
        var fixture = await Ready();
        try
        {
            var session = fixture.Factory.Session!;
            var frame = fixture.Controller.Display!.DisplayedFrame!;
            session.PresentCall = (_, _) => new SceneResult<ISceneFrame>.Success(new Train.Scenes.ContractSupport.FixedSceneFrame
            {
                Update = frame.Update,
                TerrainHeightResult = new Train.Core.Contracts.Lookup<double>.Missing(),
                Diagnostics = [new("content_missing", SceneStage.Present, null,
                    new ContentReference.Design(new("missing-editor-device")), new("fixture-package"), null,
                    "缺失定义，设备编辑受阻。")]
            });
            fixture.Controller.Process(0);
            await new UiTestInput(TestScene).Frames(2);
            var texts = new[] { "content_missing", "missing-editor-device", "fixture-package", "设备编辑受阻" };
            foreach (var text in texts)
                Require(Displayed(EditorPanel(fixture), text), $"成功帧诊断必须在编辑器显示：{text}");
            await new UiTestInput(TestScene).PressKey(Key.Quoteleft);
            foreach (var text in texts)
                Require(fixture.App.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>()
                    .Any(label => label.IsVisibleInTree() && label.GetParsedText().Contains(text, StringComparison.Ordinal)),
                    $"成功帧诊断必须在控制台显示：{text}");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }

    [Test]
    public async Task Budget_preparation_keeps_committed_geometry_without_outdated_overlay()
    {
        var fixture = await Ready();
        try
        {
            var session = fixture.Factory.Session!;
            var controller = fixture.Controller;
            var display = controller.Display!;
            var first = new FixedSceneFrame
            {
                Update = display.DisplayedFrame!.Update with { Upserts = [VisibleObject()] },
                DebugLines = [new(default, System.Numerics.Vector3.One, System.Numerics.Vector4.One)],
                TerrainHeightResult = new Train.Core.Contracts.Lookup<double>.Missing()
            };
            session.PresentCall = (_, _) => new SceneResult<ISceneFrame>.Success(first);
            display.Timestamp = () => 0;
            controller.Process(0);
            var geometry = display.Objects[new(1)].Visuals[0].Node;
            fixture.Editor.Select(new EditSelection.Elements([new(1)]));
            var pending = new FixedSceneFrame
            {
                Update = first.Update with { Upserts = [VisibleObject() with { Id = new(2) }, VisibleObject() with { Id = new(3) }] },
                DebugLines = [new(default, System.Numerics.Vector3.UnitX, System.Numerics.Vector4.One)]
            };
            session.PresentCall = (_, _) => new SceneResult<ISceneFrame>.Success(pending);
            long time = 0;
            display.Timestamp = () => time += System.Diagnostics.Stopwatch.Frequency;
            controller.Process(0);
            Require(display.IsPreparing && ReferenceEquals(display.DisplayedFrame, first) &&
                geometry.IsInsideTree() && geometry.IsVisibleInTree() && display.Root.GetNodeOrNull<MeshInstance3D>("DebugLines") is null,
                "预算准备期间必须保留已提交几何，旧线框立即清除，新线框尚不能接入。");
            Require(controller.Pick(Vector2.Zero) is ScenePick.NotReady && controller.PickPlane(Vector2.Zero) is null,
                "完整替换准备期间不得选择旧显示对象，也不得穿透到工作平面。");
            session.PresentCall = null;
            display.Timestamp = () => 0;
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    [Test]
    public async Task Unknown_rotation_coverage_and_conflicts_are_not_reported_as_empty()
    {
        var fixture = await Ready();
        try
        {
            var session = fixture.Factory.Session!;
            var evaluation = (DesignEvaluation.Complete)session.Read().CurrentEvaluation!;
            session.SetState(state => state with { CurrentEvaluation = evaluation with
            {
                Scene = evaluation.Scene with { RotationSweeps = [new([],
                    new ComputedValue<ImmutableArray<GridPosition>>.Unknown([]),
                    new ComputedValue<ImmutableArray<GridPosition>>.Unknown([]))] }
            } });
            await new UiTestInput(TestScene).Frames(2);
            var panel = EditorPanel(fixture);
            Require(Displayed(panel, "转动范围 未知") && Displayed(panel, "冲突 未知") && !Displayed(panel, "冲突 无"),
                "未知范围和未知冲突必须明确显示未知，不能冒充没有冲突。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    [Test]
    public async Task Preview_conflict_preserves_code_definition_package_and_blocked_operation_in_both_outputs()
    {
        var fixture = await Ready();
        try
        {
            var session = fixture.Factory.Session!;
            session.PreviewCall = operation => new FixedEditPreview(session.Capture().Stamp, operation)
            {
                CanApply = false,
                Conflicts = [new(null, null, new("edit_locked", SceneStage.Edit, null,
                    new ContentReference.Design(new("locked-device")), new("missing-package"), null, "缺失定义禁止旋转。"))]
            };
            var panel = EditorPanel(fixture);
            await ActivateEditorButton(panel, "Properties");
            var texts = new[] { "edit_locked", "locked-device", "missing-package", "禁止旋转" };
            foreach (var text in texts) Require(Displayed(panel, text), $"预览冲突界面缺少：{text}");
            await new UiTestInput(TestScene).PressKey(Key.Quoteleft);
            foreach (var text in texts)
                Require(fixture.App.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>()
                    .Any(label => label.IsVisibleInTree() && label.GetParsedText().Contains(text, StringComparison.Ordinal)),
                    $"预览冲突控制台缺少：{text}");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    [Test]
    public async Task Text_field_consumes_undo_before_editor_shortcuts()
    {
        var fixture = await Ready();
        try
        {
            var session = fixture.Factory.Session!;
            var undoCalls = 0;
            session.UndoCall = () => { undoCalls++; return new SceneResult<EditState>.Success(session.Read()); };
            var panel = EditorPanel(fixture);
            var name = panel.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Single(edit => edit.PlaceholderText == "名称");
            var input = new UiTestInput(TestScene);
            await input.Enter(name, "123");
            await input.PressKey(Key.Z, true);
            Require(undoCalls == 0 && name.Text != "123", "编辑名称时Ctrl+Z必须撤销文字输入，不能撤销设计。");
            Find<Button>(panel, "Properties").GrabFocus();
            await input.PressKey(Key.Z, true);
            Require(undoCalls == 1, "GUI未消费Ctrl+Z时编辑器快捷键仍须工作。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    [Test]
    public async Task Escape_routes_once_to_editor_close_protection_with_scene_view_active()
    {
        var fixture = await Ready();
        try
        {
            fixture.Factory.Session!.SetState(state => state with { IsDirty = true });
            var panel = EditorPanel(fixture);
            Find<Button>(panel, "Properties").GrabFocus();
            var closeNotifications = 0;
            void ClosingChanged()
            {
                if (fixture.Editor.Leaving) closeNotifications++;
            }
            fixture.Editor.Changed += ClosingChanged;
            try
            {
                Require(fixture.Controller.View.IsInsideTree() && fixture.Controller.View.Interactive,
                    "Esc路由用例必须包含活动的场景输入视图。");
                await new UiTestInput(TestScene).PressKey(Key.Escape);
                await new UiTestInput(TestScene).Frames(2);
                Require(fixture.Editor.IsOpen && fixture.Editor.Leaving && Find<Control>(panel, "LeaveEditor").Visible &&
                    closeNotifications == 1,
                    "Esc必须恰好触发一次编辑器关闭保护，不能被场景视图吞掉或重复请求。");
            }
            finally { fixture.Editor.Changed -= ClosingChanged; }
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    private static SceneObject VisibleObject() => new(new(1), null,
        new(default, System.Numerics.Quaternion.Identity), null,
        [new SceneVisual.Geometry(new(new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper),
            [new(System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitY, default, default, 0, 0, 0),
             new(System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitY, default, default, 0, 0, 0),
             new(System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY, default, default, 0, 0, 0)], [0, 1, 2]),
            new(false, 0, 0, false))], [], []);

}
