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
