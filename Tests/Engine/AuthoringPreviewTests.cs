using System.Text.Json;
using Chickensoft.GoDotTest;
using Godot;
using Train.App.Authoring;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class AuthoringPreviewTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Real_template_frame_picks_repeated_plot_indices_and_refresh_keeps_camera()
    {
        UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
        using var fixture = new RealAuthoringFixture(TestScene);
        WriteGround(fixture.ModDirectory);
        await fixture.Start();
        await fixture.Open();
        var controller = new TemplateSceneController(fixture.Service, fixture.Editor);
        TestScene.AddChild(controller.View);
        var form = new AuthoringDefinitionForm { Name = "RealTemplateForm", Position = new(12, 12), CustomMinimumSize = new(320, 0) };
        var overlay = new CanvasLayer(); TestScene.AddChild(overlay); overlay.AddChild(form);
        try
        {
            var draft = new DefinitionDraft.SettlementTemplate(new("repeated-plots", new("", ""), new("core:agricultural-town"), 1,
                new(fixture.PackageId.Value + ":preview-ground"),
                [new(7, PlotSize.Small, new(-32, 1, 0), VoxelOrientation.PxPy, null),
                 new(7, PlotSize.Small, new(32, 1, 0), VoxelOrientation.PxPy, null)], [], [], []));
            fixture.Service.NewDefinition(draft); form.Load(draft);
            form.Edited += fixture.Service.MarkFormDirty;
            controller.PlotSelected += form.SelectPlot;
            await fixture.Service.PreviewTemplate(form.Capture());
            await fixture.Pump(() => { controller.Process(0); return controller.View.Interactive; });
            var preview = fixture.Service.TemplatePreview!;
            Require(preview.Frame.Readiness == SceneReadiness.Ready &&
                preview.Frame.Update.Upserts.Any(item => item.Visuals.Any(visual => visual is SceneVisual.Geometry)),
                "真实模板必须显示已加载 Mod 地面几何，不能用空帧代替。");
            Require(preview.Frame.DebugLines.Length == 8 && preview.Plots.Select(plot => plot.DraftIndex).SequenceEqual([0, 1]),
                "真实重复编号地块必须各自提供轮廓和数组下标。");
            Require(preview.Diagnostics.Count(problem => problem.Code == "duplicate_plot_number") == 2,
                "重复编号必须保留两条真实诊断，不能阻止可读预览。");
            foreach (var plot in preview.Plots)
            {
                var ray = new SceneRay(plot.Pose.Position + new System.Numerics.Vector3(0, 200, 0), -System.Numerics.Vector3.UnitY, 500);
                Require(fixture.Service.PickPlot(ray) is Lookup<int>.Found found && found.Value == plot.DraftIndex,
                    "真实 PickPlot 必须返回数组下标而非重复的地块编号。");
            }

            var camera = controller.View.CameraRig;
            camera.PlaceAbsolute(new(12, 1, 0), 150, 0.12f, 0.8f);
            var input = new UiTestInput(TestScene);
            await input.Frames(3);
            await UiCapture.Capture(TestScene, "authoring-real-template-ground-outlines");
            var second = preview.Plots[1].Pose.Position;
            var screen = camera.Camera.UnprojectPosition(new(second.X, second.Y, second.Z));
            await input.MouseButton(screen, MouseButton.Left, true);
            await input.MouseButton(screen, MouseButton.Left, false);
            var fields = form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().ToArray();
            var plots = fields.Where(field => field.TooltipText == "地块编号").ToArray();
            Require(TestScene.GetViewport().GuiGetFocusOwner() == plots[1].GetParent(),
                "真实场景视口点击第二个重复编号地块必须定位表单第二行。");

            var focus = camera.Focus; var transform = camera.Camera.Transform;
            await input.Enter(fields.Single(field => field.TooltipText == "权重（可空）"), "2");
            Require(fixture.Service.TemplatePreview is null && !controller.View.Interactive,
                "表单修改必须立即阻止旧真实帧拾取。");
            controller.Process(0);
            await fixture.Service.PreviewTemplate(form.Capture());
            await fixture.Pump(() => { controller.Process(0); return controller.View.Interactive; });
            Require(!ReferenceEquals(preview, fixture.Service.TemplatePreview), "刷新必须生成新的真实预览。");
            Require(camera.Focus.IsEqualApprox(focus) && camera.Camera.Transform.IsEqualApprox(transform),
                "表单修改后的真实预览刷新必须保留焦点、距离和观察角度。");
            await UiCapture.Capture(TestScene, "authoring-real-template-refreshed-camera");
        }
        finally
        {
            controller.Dispose(); controller.View.QueueFree(); overlay.QueueFree();
            await new UiTestInput(TestScene).Frames(3);
        }
    }

    private static void WriteGround(string directory)
    {
        var blueprints = Path.Combine(directory, "resources", "blueprints"); Directory.CreateDirectory(blueprints);
        var cells = Enumerable.Range(-64, 128).SelectMany(x => Enumerable.Range(-32, 64).Select(z => new[] { x, 0, z, 0 })).ToArray();
        File.WriteAllText(Path.Combine(blueprints, "preview-ground.blueprint.json"), JsonSerializer.Serialize(new
        {
            schema_version = 3, id = "preview-ground", category = "scene_model",
            layout = new { palette = new[] { new { voxel = "core:ground" } }, cells },
            display = new { name = "真实预览测试地面", description = "覆盖地块轮廓，检查地表深度遮挡。" }
        }));
    }
}
