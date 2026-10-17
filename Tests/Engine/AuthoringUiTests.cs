using Train.App.Authoring;
using Train.Core.ContractSupport;
using Train.App.Saves;
using Train.Content.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class AuthoringUiTests(Node testScene) : TestClass(testScene)
{
    private AuthoringDefinitionForm Mount(DefinitionDraft draft)
    {
        var form = new AuthoringDefinitionForm { Name = "AuthoringFormUnderTest" };
        TestScene.AddChild(form); form.Load(draft); return form;
    }
    private static LineEdit Field(AuthoringDefinitionForm form, string title) =>
        form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Single(edit => edit.TooltipText == title);

    [Test]
    public async Task Raw_invalid_numeric_input_marks_form_edited_before_capture_can_succeed()
    {
        var draft = new DefinitionDraft.SettlementType(new("town", new("城镇", "描述"), null, true, 5, [new("core:plain")], 10, 50));
        var form = new AuthoringDefinitionForm(); var edits = 0; form.Edited += () => edits++;
        TestScene.AddChild(form);
        try
        {
            form.Load(draft);
            Require(edits == 0, "加载表单不得产生未保存修改。");
            await new UiTestInput(TestScene).Frames(2);
            await new UiTestInput(TestScene).Enter(Field(form, "权重（可空）"), "99999999999999999999999");
            Require(edits > 0, "不能解析的原始输入也必须立即报告 Edited。");
            var rejected = false;
            try { form.Capture(); } catch (FormatException error) { rejected = error.Message.Contains("权重"); }
            Require(rejected, "非法原始数值必须以包含字段名称的 FormatException 报告。");
        }
        finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }

    [Test]
    public async Task Typed_behavior_input_changes_parameter_and_preserves_unedited_layout_and_rotation()
    {
        var layout = new VoxelLayout([new(new("core:steel"), VoxelOrientation.PzPy, new("cube"), new("#123456"))], [new(new(1, 2, 3), 0)], []);
        var rotation = new RotationAppearance(RotationAxis.Z, new(3, 4, 5), -15, 70, 25);
        var source = new DefinitionDraft.Blueprint("engine", new(new("动力设备", "保留说明"), new DesignSpecification.FunctionalComposite([new PartCapability.Propulsion(12, 34), new PartCapability.Control()], rotation), layout));
        var form = Mount(source);
        try
        {
            await new UiTestInput(TestScene).Frames(2);
            await new UiTestInput(TestScene).Enter(Field(form, "牵引力 kN"), "42");
            var result = (DefinitionDraft.Blueprint)form.Capture();
            var specification = (DesignSpecification.FunctionalComposite)result.Value.Specification;
            Require(specification.Behaviors[0] is PartCapability.Propulsion { TractionKilonewtons: 42, RatedSpeedMetersPerSecond: 34 } && specification.Behaviors[1] is PartCapability.Control,
                "表单必须只修改用户输入的行为参数，保留其余行为及参数。");
            Require(ReferenceEquals(result.Value.Layout, layout) && specification.Rotation == rotation && result.LocalId == source.LocalId && result.Value.Display == source.Value.Display,
                "修改行为不得重建或丢失网格、转动、身份和文字。");
        }
        finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }

    [Test]
    public async Task Blueprint_form_preserves_grid_owned_properties_without_duplicate_controls()
    {
        DesignSpecification[] specifications =
        [
            new DesignSpecification.Vehicle(SizeClass.Titan),
            new DesignSpecification.Building(PlotSize.Large, BuildingKind.Production, []),
            new DesignSpecification.SceneModel(new(new(1, 2), new(3, 4), 5))
        ];
        foreach (var specification in specifications)
        {
            var source = new DefinitionDraft.Blueprint("grid-owned", new(new("保留名称", "保留说明"), specification, new([], [], [])));
            var form = Mount(source);
            try
            {
                var fields = form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().ToArray();
                Require(fields.Length == 1 && fields[0].TooltipText == "本地 ID", "网格拥有的名称和规格不能在蓝图行为表单重复编辑。");
                var captured = (DefinitionDraft.Blueprint)form.Capture();
                Require(captured.Value == source.Value, "未提供行为字段的蓝图必须原样保留网格定义。");
                Require(!form.FindChildren("*", "Button", true, false).OfType<Button>().Any(button => button.Text == "更新预览"), "蓝图表单不能提供模板专属预览按钮。");
            }
            finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
        }
    }

    [Test]
    public async Task Plot_selection_uses_array_index_even_when_plot_numbers_repeat()
    {
        var source = new DefinitionDraft.SettlementTemplate(new("town", new("core:town"), 3, new("core:town-scene"),
            [new(7, PlotSize.Small, new(1, 0, 2), VoxelOrientation.PxPy, null), new(7, PlotSize.Large, new(8, 0, 9), VoxelOrientation.PzPy, new("core:warehouse"))],
            [new(7, new("core:house"))], [new(new GoodId.Voxel(new("core:steel")), 15)], []));
        var form = Mount(source);
        try
        {
            await new UiTestInput(TestScene).Frames(2);
            var numberFields = form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Where(edit => edit.TooltipText == "地块编号").ToArray();
            form.SelectPlot(1);
            Require(TestScene.GetViewport().GuiGetFocusOwner() == numberFields[1].GetParent(), "相同地块编号不得使选取回到第一行，必须按 DraftIndex 定位第二行。");
            form.SelectPlot(0);
            Require(TestScene.GetViewport().GuiGetFocusOwner() == numberFields[0].GetParent(), "DraftIndex 0 必须定位第一行。");
            var captured = ((DefinitionDraft.SettlementTemplate)form.Capture()).Value;
            Require(captured.Plots.SequenceEqual(source.Value.Plots) && captured.FoundingBuildings.SequenceEqual(source.Value.FoundingBuildings) && captured.Inventory.SequenceEqual(source.Value.Inventory) && captured.Type == source.Value.Type && captured.SceneModel == source.Value.SceneModel,
                "未编辑模板时必须保留重复编号、场景模型、建筑与库存字段。");
        }
        finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }

    [Test]
    public async Task Creature_and_scene_model_palettes_use_scene_voxels_while_vehicle_uses_goods()
    {
        var core = HostContentScenario.CreateProjection(HostCoreScenario.Input);
        var cargo = new VoxelDefinitionId("fixture:cargo");
        var scene = new VoxelDefinitionId("fixture:scene");
        var content = new FixedContentView(core, new(1))
        {
            Blueprints = [], LocalDrafts = [], GenerationProfiles = [],
            Voxels = [new(cargo, core.Goods[0]), new(scene, null)],
            VoxelDisplays = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelDisplayProjection>>
            {
                [cargo] = new ContentLookup<VoxelDisplayProjection>.NotFound(),
                [scene] = new ContentLookup<VoxelDisplayProjection>.NotFound()
            },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            {
                [cargo] = new ContentLookup<VoxelAppearanceProjection>.NotFound(),
                [scene] = new ContentLookup<VoxelAppearanceProjection>.NotFound()
            }
        };
        var fixture = new EditorPreview { Name = "AuthoringVoxelPaletteFixture", Content = content };
        TestScene.AddChild(fixture);
        try
        {
            await FramesUntil(TestScene, () => fixture.Editor.Session is not null);
            var panel = fixture.App.FindChildren("DesignEditor", "", true, false).OfType<DesignEditorPanel>().Single();
            var palette = panel.GetNode<PanelContainer>("Tools").FindChildren("*", "OptionButton", true, false).OfType<OptionButton>().First();
            (DesignSpecification Specification, VoxelDefinitionId Expected)[] cases =
            [
                (new DesignSpecification.Creature(null, null), scene),
                (new DesignSpecification.SceneModel(null), scene),
                (new DesignSpecification.Vehicle(SizeClass.Small), cargo)
            ];
            foreach (var (specification, expected) in cases)
            {
                Require(fixture.Editor.RequestClose(() => { }) is null && !fixture.Editor.IsOpen, "更换规格前必须关闭旧编辑会话。");
                fixture.Editor.Open(EditorPreviewSession.Draft with { Specification = specification }, new DraftTarget.LocalBlueprint(null));
                await FramesUntil(TestScene, () => fixture.Editor.Session is not null && palette.ItemCount == 1 && palette.GetItemText(0).StartsWith(expected.Value, StringComparison.Ordinal));
                Require(palette.ItemCount == 1 && palette.GetItemText(0).StartsWith(expected.Value, StringComparison.Ordinal), "生物与场景模型只能使用场景体素；车辆只能使用货物体素。");
            }
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }

    [Test]
    public async Task Authoring_panel_core_control_matches_real_service_write_gate()
    {
        var fixture = new EditorPreview { Name = "AuthoringPanelGateFixture" }; TestScene.AddChild(fixture);
        try
        {
            await FramesUntil(TestScene, () => fixture.App.Authoring is not null && fixture.Editor.Session is not null);
            var panel = fixture.App.FindChildren("Authoring", "Control", true, false).OfType<AuthoringPanel>().Single();
            var service = fixture.App.Authoring!; panel.Open();
            var core = Find<Button>(panel, "OpenCore");
            Require(core.Visible == service.CanWriteCore && (!service.CanWriteCore || !core.Disabled), "core 写入入口必须服从真实服务的源码写入门槛。");
            Require(!panel.GetNode<PanelContainer>("Authoring").Visible, "网格编辑会话打开时，创作窗体必须隐藏，不能遮挡现有编辑器。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    [Test]
    public async Task Template_display_mouse_pick_selects_returned_index_and_dirty_input_immediately_blocks_old_frame()
    {
        UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
        var fixture = new EditorPreview { Name = "TemplatePickingFixture" }; TestScene.AddChild(fixture);
        var temporary = Path.Combine(Path.GetTempPath(), "train-template-picking-" + Guid.NewGuid().ToString("N"));
        var coreDirectory = Path.Combine(temporary, "core"); Directory.CreateDirectory(coreDirectory);
        AuthoringService? service = null; TemplateSceneController? controller = null; AuthoringPanel? panel = null;
        try
        {
            await FramesUntil(TestScene, () => fixture.Editor.Session is not null);
            Require(fixture.Editor.RequestClose(() => { }) is null && !fixture.Editor.IsOpen, "模板测试必须先关闭同一真实网格会话。");
            Require(!fixture.Controller.View.Loading && !fixture.App.TemplateScenes!.View.Loading,
                "关闭的网格与尚未打开的模板不得留下独立 CanvasLayer 加载文字。");
            // Isolate the tested panel from unrelated map controls which otherwise consume viewport clicks.
            fixture.App.GetNode<GameUi>("GameUi").Visible = false;
            FixedTemplatePreview? preview = null;
            var scenes = new EditorScenes
            {
                PreviewTemplateCall = (content, draft, revision) =>
                {
                    if (draft.LocalId != "repeated-plots" || draft.Plots.Length != 2) throw new InvalidOperationException("UNCONFIGURED: template");
                    preview = new(content.Generation, revision)
                    {
                        Frame = new FixedSceneFrame
                        {
                            Update = new(null, new(new(Guid.NewGuid()), new(1), new(1), revision.Value), System.Numerics.Vector3.Zero, [], [], new([], []), []),
                            TerrainHeightResult = new Lookup<double>.Missing()
                        },
                        Plots = [
                            new(0, new(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity), new(new(0, 0, 0), new(1, 1, 1))),
                            new(1, new(new(8, 0, 0), System.Numerics.Quaternion.Identity), new(new(0, 0, 0), new(3, 1, 3)))
                        ],
                        PickResult = new Lookup<int>.Found(1)
                    };
                    return new SceneResult<ITemplatePreview>.Success(preview);
                }
            };
            service = new(fixture.Editor.Host, fixture.Editor, scenes, true, Path.Combine(temporary, "mods"), coreDirectory);
            ((ScriptedContentFactory)fixture.Editor.Host.ContentFactory).OpenAuthoringPackageCall = (_, id, version, _) =>
                new ContentResult<IAuthoringPackage>.Success(new FixedAuthoringPackage(id, version, []));
            controller = new(service, fixture.Editor); TestScene.AddChild(controller.View);
            panel = new AuthoringPanel { Name = "TemplatePickingPanel" }; panel.Initialize(service, fixture.Editor); TestScene.AddChild(panel);
            controller.PlotSelected += panel.SelectPlot;
            service.OpenCore();
            await FramesUntil(TestScene, () => { service.Process(); return service.Package is not null; });
            service.NewDefinition(new DefinitionDraft.SettlementTemplate(new("repeated-plots", null, 1, null,
                [new(5, PlotSize.Small, new(0, 0, 0), VoxelOrientation.PxPy, null), new(5, PlotSize.Large, new(8, 0, 0), VoxelOrientation.PxPy, null)], [], [], [])));
            panel.Open(); service.PreviewTemplate(service.Form!);
            await FramesUntil(TestScene, () => { service.Process(); controller.Process(0); return controller.View.Interactive; });
            Require(preview is not null, "模板预览入口必须创建配置的预览。");
            Require(ReferenceEquals(service.TemplatePreview, preview), "显示与拾取必须使用同一份服务预览。");
            await UiCapture.Capture(TestScene, "developer-authoring");
            var position = TestScene.GetViewport().GetVisibleRect().End - new Vector2(35, 200);
            var origin = controller.View.CameraRig.Camera.ProjectRayOrigin(position);
            var direction = controller.View.CameraRig.Camera.ProjectRayNormal(position);
            var input = new UiTestInput(TestScene);
            await input.MouseButton(position, MouseButton.Left, true);
            await input.MouseButton(position, MouseButton.Left, false);
            Require(preview!.Rays.Count == 1, "真实视口点击必须调用当前模板的 PickPlot 一次。");
            var ray = preview!.Rays[0];
            Require(ray.Origin == new System.Numerics.Vector3(origin.X, origin.Y, origin.Z) && ray.Direction == new System.Numerics.Vector3(direction.X, direction.Y, direction.Z),
                "模板拾取必须使用显示帧相机生成的根空间射线。");
            var form = panel.FindChildren("DefinitionForm", "", true, false).OfType<AuthoringDefinitionForm>().Single();
            var plotFields = form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Where(edit => edit.TooltipText == "地块编号").ToArray();
            Require(TestScene.GetViewport().GuiGetFocusOwner() == plotFields[1].GetParent(), "返回下标1必须聚焦第二个重复编号地块，不能按编号选第一行。");
            await input.Enter(Field(form, "权重（可空）"), "2");
            Require(service.TemplatePreview is null && !controller.View.Interactive, "真实表单输入必须立即清掉旧预览拾取，不等待下一次显示帧。");
            await input.MouseButton(position, MouseButton.Left, true);
            await input.MouseButton(position, MouseButton.Left, false);
            Require(preview!.Rays.Count == 1, "失效显示帧不能继续向旧 PickPlot 发射射线。");
        }
        finally
        {
            panel?.QueueFree(); await new UiTestInput(TestScene).Frames(2);
            controller?.Dispose(); controller?.View.QueueFree();
            service?.Dispose(); fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3);
            Directory.Delete(temporary, true);
        }
    }

}
