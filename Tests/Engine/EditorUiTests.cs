using System.Collections.Immutable;
using Train.Scenes.ContractSupport;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.App.Saves;
using Train.Ui;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class EditorUiTests(Node testScene) : TestClass(testScene)
{
    private async Task<EditorPreview> Ready(EditorPreview? fixture = null)
    {
        fixture ??= new EditorPreview { Name = "EditorUiFixture" }; TestScene.AddChild(fixture);
        await FramesUntil(TestScene, () => fixture.Editor.Session is not null && fixture.Controller.Display?.DisplayedFrame is not null);
        return fixture;
    }
    [Test]
    public async Task Editor_details_show_item_names_capabilities_and_numeric_coordinates()
    {
        var voxel = new GoodId.Voxel(new("fixture:motor"));
        var assembly = new GoodId.Assembly(new("fixture-assembly-composition"));
        var definition = new CoreDefinitionReference.Voxel(voxel.Definition);
        var missing = new CoreDefinitionReference.Composite(new("fixture:missing-part"));
        var address = new BlueprintAddress(BlueprintLibrary.User, new("fixture-equipment"));
        var evaluation = (DesignEvaluation.Complete)EditorPreviewSession.Evaluate(EditorPreviewSession.Draft);
        var content = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
        {
            LocalDrafts = [], Voxels = [], GenerationProfiles = [],
            Blueprints = [new(address, new("设备样本", ""), null, true, [])],
            BlueprintRecords = new Dictionary<BlueprintAddress, ContentLookup<BlueprintRecord>>
            {
                [address] = new ContentLookup<BlueprintRecord>.Found(new(address, evaluation.Definition, [], evaluation))
            },
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>>
            {
                [voxel] = new ContentLookup<GoodPresentation>.Found(new(voxel, new(new("牵引电机", ""), null, null, false), null, false)),
                [assembly] = new ContentLookup<GoodPresentation>.Found(new(assembly, new(new("转动设备", ""), null, null, false), null, false))
            },
            DefinitionDisplays = new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
            {
                [definition] = new ContentLookup<ContentDisplay>.Found(new(new("牵引电机", ""), null, null, false)),
                [missing] = new ContentLookup<ContentDisplay>.NotFound()
            }
        };
        var fixture = await Ready(new EditorPreview { Name = "EditorTextFixture", Content = content });
        try
        {
            var session = fixture.Factory.Session!;
            ImmutableArray<GoodsQuantity> bom = [new(voxel, 3), new(assembly, 2)];
            ImmutableArray<PartCapability> capabilities =
            [
                new PartCapability.Propulsion(12.56789, 34.56789), new PartCapability.Control(), new PartCapability.Storage(56),
                new PartCapability.Production(7.1234), new PartCapability.Extraction(8.56789),
                new PartCapability.Construction(9.87654, PlotSize.SmallStation, SizeClass.Titan), new PartCapability.Power(10.12345),
                new PartCapability.Weapon(11, 1.2345), new PartCapability.Platform()
            ];
            var projection = new DesignProjection(new("fixture-design"), new("fixture-composition"), new DesignKind.Vehicle(SizeClass.Small, 1),
                [new(new(1), definition, voxel, 1, 1, new(1), capabilities, false), new(new(2), missing, voxel, 1, 1, new(1), [], true)],
                2, 2, 1, new(1), [], bom, 5, [], true, []);
            evaluation = evaluation with
            {
                Core = new(HostCoreScenario.Input, projection, []),
                Scene = evaluation.Scene with { RotationSweeps = [new([0],
                    new ComputedValue<ImmutableArray<GridPosition>>.Known([new(-2, 3, 4)]),
                    new ComputedValue<ImmutableArray<GridPosition>>.Known([new(-2, 3, 4)]))] }
            };
            session.SetState(state => state with { CurrentEvaluation = evaluation });
            var panel = EditorPanel(fixture);
            await FramesUntil(TestScene, () => Displayed(panel, "牵引电机 × 3"));
            var list = panel.FindChildren("*", "ItemList", true, false).OfType<ItemList>().Single();
            var rows = string.Join("\n", Enumerable.Range(0, list.ItemCount).Select(list.GetItemText));
            Require(rows.Contains("体素 (0, 0, 0)") && rows.Contains("组装体 (8, 0, 0)") && rows.Contains("功能复合体"),
                "层级列表必须显示中文种类和数值坐标。");
            foreach (var text in new[] { "转动设备 × 2", "牵引力 12.57 kN", "额定速度 34.57 m/s", "控制", "库存容量 56",
                         "生产工作量 7.12/秒", "开采工作量 8.57/秒", "施工工作量 9.88/秒", "小站点", "泰坦", "能量 10.12/秒",
                         "武器伤害 11", "1.23 秒", "站台", "部件定义不可用", "(-2, 3, 4)" })
                Require(Displayed(panel, text), $"设计信息缺少玩家文字或数值：{text}");
            foreach (var text in new[] { "GridPosition {", "GoodId", "Voxel {", "Assembly {", "Propulsion {", "fixture-assembly-composition", "fixture:missing-part", "fixture-design" })
                Require(!rows.Contains(text, StringComparison.Ordinal) && !Displayed(panel, text), $"编辑器不能显示记录原文或内部标识：{text}");
            Require(panel.FindChildren("PlaceEquipment", "Button", true, false).OfType<Button>().Single().Text.Contains("用户库"),
                "设备来源必须使用中文库名。");
            await UiCapture.Capture(TestScene, "player-editor-item-text");

            session.SetState(state => state with
            {
                CurrentEvaluation = new DesignEvaluation.Partial(evaluation.Definition,
                    new(new ComputedValue<double>.Unknown([]), new ComputedValue<Money>.Unknown([]), new ComputedValue<long>.Unknown([]),
                        new ComputedValue<ImmutableArray<PartProjection>>.Unknown([]), new ComputedValue<ImmutableArray<GoodsQuantity>>.Known(bom),
                        new ComputedValue<int>.Unknown([])), new(0, new(0), 0, [], []), null, evaluation.Scene, [])
            });
            await FramesUntil(TestScene, () => Displayed(panel, "信息不完整") && Displayed(panel, "转动设备 × 2"));
            Require(!Displayed(panel, assembly.Composition.Value) && !Displayed(panel, "GoodId"), "部分评估的材料清单也必须使用物品名称。");
            session.SetState(state => state with { CurrentEvaluation = evaluation with { Core = evaluation.Core! with { Design = projection with { BillOfMaterials = [] } } } });
            await FramesUntil(TestScene, () => Displayed(panel, "BOM：无"));
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    [Test]
    public async Task Unpublished_nested_assembly_bom_uses_a_placeholder_instead_of_a_composition_hash()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            app.Saves!.SetAutoSaveInterval(null);
            var host = app.Host!;
            await FramesUntil(TestScene, () => !host.IsBusy, timeout: ContentPreparationTimeout);
            Require(host.BeginNewGame(new("core:basic-through-world"), 1, "stage1-world-v1", host.SessionOptions!).Problem is null,
                "真实新局必须可创建。");
            await FramesUntil(TestScene, () => !host.IsBusy, timeout: ContentPreparationTimeout);
            var child = new DesignDraft(new("嵌套储物设备", ""), new DesignSpecification.Assembly(null),
                new([new(new("core:storage-equipment"), VoxelOrientation.PxPy, null, null)], [new(new(0, 0, 0), 0)], []));
            var draft = new DesignDraft(new("未发布组装体", ""), new DesignSpecification.Assembly(null),
                new([new(new("core:structural-metal"), VoxelOrientation.PxPy, null, null)], [new(new(0, 0, 0), 0)],
                    [new NestedDesignItem.AssemblyCopy(new(1, 0, 0), VoxelOrientation.PxPy, child)]));
            app.Editor!.Open(draft, new DraftTarget.LocalBlueprint(null));
            await FramesUntil(TestScene, () => app.Editor.Session is not null);
            var panel = Find<DesignEditorPanel>(app, "DesignEditor");
            await FramesUntil(TestScene, () => Displayed(panel, "BOM："));
            var evaluation = (DesignEvaluation.Complete)app.Editor.State!.CurrentEvaluation!;
            var composition = evaluation.Core!.Design.BillOfMaterials.Select(item => item.Good).OfType<GoodId.Assembly>().Single().Composition;
            var information = panel.FindChildren("*", "Label", true, false).OfType<Label>().Single(label => label.Text.Contains("BOM："));
            GD.Print("issue-743 unpublished assembly information: " + information.Text);
            await UiCapture.Capture(TestScene, "player-editor-unpublished-assembly");
            Require(information.Text.Contains("组装体（名称不可用）", StringComparison.Ordinal) && !information.Text.Contains(composition.Value, StringComparison.Ordinal),
                "未发布的嵌套组装体必须说明名称不可用，不能回退成构成哈希。");
        }
        finally { await fixture.Cleanup(); }
    }
    [Test]
    public async Task Unavailable_instance_design_shows_a_player_reason_and_logs_the_hash()
    {
        var id = new InstanceId.Vehicle(new(41));
        var hash = new DesignHash("unavailable-instance-design-hash");
        var world = WorldSceneFixture.EmptyWorld(new(0, 0));
        var fixture = await Ready(new EditorPreview
        {
            Name = "EditorUnavailableDesignFixture",
            Content = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
            {
                Blueprints = [], LocalDrafts = [], Voxels = [], GenerationProfiles = [],
                Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> { [hash] = new ContentLookup<DesignEvaluation>.NotFound() }
            },
            World = new(world.Position, world.InputIdentity)
            {
                Economy = world.Economy, World = world.World,
                Trains = new Dictionary<TrainId, Lookup<TrainView>> { [new(1)] = new Lookup<TrainView>.Missing() },
                Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
                {
                    [id] = new Lookup<InstanceView>.Found(new(id, hash, new("composition"), new WorldLocation.Node(new(1)),
                        SettlementRelation.PlayerOwned, new InstanceDetails.Vehicle(SizeClass.Small, null), InstanceLock.None, new(1, 1), [], []))
                }
            }
        });
        try
        {
            fixture.Editor.OpenInstance(id);
            var panel = EditorPanel(fixture);
            await FramesUntil(TestScene, () => Displayed(panel, "设计缺失或不可用"));
            Require(!Displayed(panel, hash.Value), "设计不可用时，玩家提示不能带出设计哈希。");
            await UiCapture.Capture(TestScene, "player-editor-design-unavailable");
            await new UiTestInput(TestScene).PressKey(Key.Quoteleft);
            Require(ConsoleShows(fixture, hash.Value), "设计不可用的原始哈希必须保留在日志中。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    [Test]
    public async Task Orientation_labels_preserve_allowed_order_and_selected_orientation()
    {
        var core = HostContentScenario.CreateProjection(HostCoreScenario.Input);
        var voxels = new[] { new VoxelDefinitionId("fixture:fixed"), new VoxelDefinitionId("fixture:four-way"), new VoxelDefinitionId("fixture:all-axes") };
        var content = new FixedContentView(core, new(1))
        {
            Blueprints = [], LocalDrafts = [], GenerationProfiles = [], Voxels = [.. voxels.Select(id => new VoxelCatalogEntry(id, core.Goods[0]))],
            VoxelDisplays = voxels.ToDictionary(id => id, id => (ContentLookup<VoxelDisplayProjection>)new ContentLookup<VoxelDisplayProjection>.Found(new(id, new(id.Value, ""), new("icon")))),
            VoxelAppearances = voxels.Select((id, index) => (id, index)).ToDictionary(item => item.id, item =>
                (ContentLookup<VoxelAppearanceProjection>)new ContentLookup<VoxelAppearanceProjection>.Found(new(item.id, (AllowedVoxelOrientations)item.index,
                    new VoxelCollision.FullBox(), new VoxelPresentation.Mesh(new("model"), []), new(false, null))))
        };
        var fixture = await Ready(new EditorPreview { Name = "EditorOrientationFixture", Content = content });
        try
        {
            var panel = EditorPanel(fixture);
            var choices = panel.FindChildren("*", "OptionButton", true, false).OfType<OptionButton>().ToArray();
            var palette = choices.Single(choice => choice.ItemCount == 3 && choice.GetItemText(0).Contains("fixture:fixed"));
            var orientation = choices.Single(choice => choice.ItemCount == 1 && choice.GetItemText(0) == "右 +X · 上 +Y");
            var input = new UiTestInput(TestScene);
            foreach (var (index, count, last) in new[] { (0, 1, "右 +X · 上 +Y"), (1, 4, "右 -Z · 上 +Y"), (2, 24, "右 -Z · 上 -Y") })
            {
                await input.Choose(palette, index);
                Require(orientation.ItemCount == count && orientation.GetItemText(0) == "右 +X · 上 +Y" && orientation.GetItemText(count - 1) == last,
                    "朝向标签必须保留固定、四向和全部朝向的原顺序。");
            }
            Require(orientation.GetItemText(18) == "右 +Z · 上 +Y", "转轴标签必须按右方向轴、上方向轴解释。");
            await input.Choose(orientation, 23);
            await ActivateEditorButton(panel, "Preview");
            Require(fixture.Factory.Session!.PreviewCalls.Last() is EditOperation.Cells { Action: CellEdit.Add { Value.Orientation: VoxelOrientation.NzNy } },
                "更换朝向文字后，所选下标仍必须提交原来的正交朝向。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
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
            Require(controller.Display.Root.Visible &&
                controller.Display.Root.GetNodeOrNull<MeshInstance3D>("DebugLines") is null &&
                ReferenceEquals(controller.Display.DisplayedFrame, displayed) &&
                geometry.IsInsideTree() && geometry.IsVisibleInTree(),
                "选区变化必须立即清除旧线框并保留已提交几何。");
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
            await FramesUntil(TestScene, () => Displayed(panel, "场景已更新，请重新操作。") && !Displayed(panel, "stale_preview"));
            Require(!Displayed(panel, "预览已过期"), "过期预览的玩家文字不能带出原始说明。");
            await new UiTestInput(TestScene).PressKey(Key.Quoteleft);
            Require(ConsoleShows(fixture, "stale_preview") && ConsoleShows(fixture, "预览已过期"),
                "过期预览的诊断码和原始说明必须留在控制台。");
            await new UiTestInput(TestScene).PressKey(Key.Quoteleft);
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
            foreach (var (id, shown, hidden) in new[]
                     {
                         (20, "缺失定义已锁定，不能进行这个操作。", "edit_locked"),
                         (30, "场景出现问题，详情见日志。", "functional_composite_closed")
                     })
            {
                await EnsureVisible(list);
                var index = Enumerable.Range(0, list.ItemCount).Single(item => list.GetItemText(item).StartsWith($"{id} ·"));
                await input.ClickItem(list, index);
                await ActivateEditorButton(panel, "EnterObject");
                Require(fixture.Editor.State!.EnteredObject is null, "进入被拒绝时必须保留原层级。");
                await FramesUntil(TestScene, () => Displayed(panel, shown) && !Displayed(panel, hidden));
            }
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    [Test]
    public async Task Element_list_updated_in_place_matches_a_full_rebuild_and_keeps_selection_rows()
    {
        var fixture = await Ready();
        try
        {
            var panel = EditorPanel(fixture); var session = fixture.Factory.Session!;
            var list = panel.FindChildren("*", "ItemList", true, false).OfType<ItemList>().Single();
            var revision = 100UL;
            async Task<string[]> Show(IEnumerable<EditElement> elements, ImmutableArray<EditElementId> selection = default)
            {
                var values = elements.ToImmutableArray();
                session.SetState(state => state with
                {
                    Snapshot = state.Snapshot with { Stamp = state.Snapshot.Stamp with { Revision = new(++revision) } },
                    Elements = values, Selection = selection.IsDefault ? [] : selection
                });
                await FramesUntil(TestScene, () => list.ItemCount == values.Length && fixture.Editor.State?.Snapshot.Stamp.Revision.Value == revision);
                await new UiTestInput(TestScene).Frames(2);
                return [.. Enumerable.Range(0, list.ItemCount).Select(list.GetItemText)];
            }
            static EditElement Cell(ulong id, bool locked = false) => new(new(id), null, EditElementKind.Cell, new((int)id, 0, 0), null, [], locked);
            var initial = Enumerable.Range(1, 600).Select(id => Cell((ulong)id)).ToList();
            await Show(initial);
            // 相邻的小变化走原地更新：先删 300、插 1000 和 1001、改 301，再删 500 和 501。
            var inserted = initial.Where(item => item.Id.Value != 300).Select(item => item.Id.Value == 301 ? Cell(301, true) : item).ToList();
            inserted.InsertRange(299, [Cell(1000), Cell(1001)]);
            var removed = inserted.Where(item => item.Id.Value is not (500 or 501)).ToList();
            var updated = new List<string[]>();
            updated.Add(await Show(inserted, [new(1001)]));
            Require(list.GetSelectedItems() is [var selected] && list.GetItemText(selected).StartsWith("1001 ·"), "选中元素必须对应更新后的同一行。");
            updated.Add(await Show(removed));
            foreach (var (elements, index) in new[] { inserted, removed }.Select((elements, index) => (elements, index)))
            {
                await Show(Enumerable.Range(2000, 600).Select(id => Cell((ulong)id)));
                var rebuilt = await Show(elements);
                Require(updated[index].AsEnumerable().SequenceEqual(rebuilt), "原地更新后的元素列表必须与整表重建相同。");
            }
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    private static DesignEditorPanel EditorPanel(EditorPreview fixture) => Find<DesignEditorPanel>(fixture.App, "DesignEditor");
    private static bool Displayed(Control panel, string text) => panel.FindChildren("*", "Label", true, false).OfType<Label>()
        .Any(label => label.IsVisibleInTree() && label.Text.Contains(text, StringComparison.Ordinal));
    private static bool ConsoleShows(EditorPreview fixture, string text) => fixture.App.FindChildren("*", "RichTextLabel", true, false)
        .OfType<RichTextLabel>().Any(label => label.IsVisibleInTree() && label.GetParsedText().Contains(text, StringComparison.Ordinal));
    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
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
    public async Task Failed_publish_message_and_successful_frame_diagnostics_remain_separate_in_editor_and_console()
    {
        var fixture = await Ready();
        try
        {
            var panel = EditorPanel(fixture);
            var libraryRequests = 0;
            panel.LibraryRequested += () => libraryRequests++;
            ((ScriptedContentFactory)fixture.App.Host!.ContentFactory).PrepareEditCall = (_, _) =>
                new ContentResult<IContentCandidate>.Faulted("publish-rejected", "蓝图发布被拒绝。", null);
            await ActivateEditorButton(panel, "SaveBlueprint");
            var dialog = panel.GetChildren().OfType<AcceptDialog>().Single(item => item.Title == "保存有效蓝图");
            await new UiTestInput(TestScene).Activate(dialog.GetOkButton(), dialog);
            await FramesUntil(TestScene, () => fixture.Editor.IsOpen && !fixture.Editor.IsBusy);
            Require(libraryRequests == 0 && fixture.Editor.Message?.Contains("蓝图发布被拒绝") == true,
                "异步发布失败必须重开编辑器并保留失败原因，不返回蓝图库。");
            var message = fixture.Editor.Message;
            await new UiTestInput(TestScene).Frames(2);
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
            Require(fixture.Editor.Message == message && Displayed(panel, "蓝图发布被拒绝"),
                "成功帧诊断不得覆盖发布失败原因。");
            foreach (var text in new[] { "引用的定义缺失，相关部分不能显示或编辑。", "来源内容包不可用。", "蓝图发布被拒绝" })
                Require(Displayed(EditorPanel(fixture), text), $"成功帧诊断必须在编辑器显示：{text}");
            foreach (var text in new[] { "content_missing", "missing-editor-device", "fixture-package", "设备编辑受阻" })
                Require(!Displayed(EditorPanel(fixture), text), $"成功帧诊断不能向玩家显示：{text}");
            await UiCapture.Capture(TestScene, "player-editor-failure-diagnostics");
            await new UiTestInput(TestScene).PressKey(Key.Quoteleft);
            foreach (var text in new[] { "content_missing", "missing-editor-device", "fixture-package", "设备编辑受阻", "蓝图发布被拒绝" })
                Require(ConsoleShows(fixture, text), $"成功帧诊断必须在控制台显示：{text}");
            await new UiTestInput(TestScene).PressKey(Key.Quoteleft);
            session.PresentCall = (_, _) => new SceneResult<ISceneFrame>.Success(new FixedSceneFrame
            {
                Update = frame.Update,
                TerrainHeightResult = new Train.Core.Contracts.Lookup<double>.Missing()
            });
            fixture.Controller.Process(0);
            await new UiTestInput(TestScene).Frames(2);
            Require(!Displayed(panel, "content_missing") && !Displayed(panel, "引用的定义缺失") &&
                fixture.Editor.Message == message && Displayed(panel, "蓝图发布被拒绝"),
                "诊断清除后列表必须移除旧诊断，仍保留发布失败原因。");
            fixture.Editor.RequestClose(() => { });
            fixture.Editor.ResolveClose(LeaveChoice.Discard);
            await new UiTestInput(TestScene).Frames(2);
            Require(libraryRequests == 0, "失败重开后放弃退出不得触发残留的蓝图库请求。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }

    [Test]
    public async Task Successful_blueprint_publish_returns_to_library_once()
    {
        var fixture = await Ready();
        try
        {
            var panel = EditorPanel(fixture);
            var libraryRequests = 0;
            panel.LibraryRequested += () => libraryRequests++;
            ((ScriptedContentFactory)fixture.App.Host!.ContentFactory).PrepareEditCall = (basis, _) =>
                new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(
                    new FixedContentView(basis.Core, basis.Generation, new(1)) { Blueprints = [], LocalDrafts = [] },
                    basis, ContentChange.Persistent,
                    edit: new PreparedContentEdit.BlueprintSaved(new(BlueprintLibrary.Local, new("saved")), new("saved-hash"))));
            await ActivateEditorButton(panel, "SaveBlueprint");
            var dialog = panel.GetChildren().OfType<AcceptDialog>().Single(item => item.Title == "保存有效蓝图");
            await new UiTestInput(TestScene).Activate(dialog.GetOkButton(), dialog);
            await FramesUntil(TestScene, () => !fixture.Editor.IsOpen && !fixture.Editor.IsBusy);
            await new UiTestInput(TestScene).Frames(2);
            Require(libraryRequests == 1, "只有蓝图异步发布成功才应返回蓝图库，且只能请求一次。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    [Test]
    public async Task Multiple_scene_failure_problems_are_visible_together()
    {
        var fixture = await Ready();
        try
        {
            fixture.Factory.Session!.PresentCall = (_, _) => new SceneResult<ISceneFrame>.Failure([
                new("first_failure", SceneStage.Present, null, null, null, null, "第一条场景失败。"),
                new("second_failure", SceneStage.Present, null, null, null, null, "第二条场景失败。")]);
            fixture.Controller.Process(0);
            await new UiTestInput(TestScene).Frames(2);
            var panel = EditorPanel(fixture);
            var shown = panel.FindChildren("*", "Label", true, false).OfType<Label>()
                .Where(label => label.IsVisibleInTree()).Sum(label => Count(label.Text, "场景出现问题，详情见日志。"));
            Require(shown == 2 && !Displayed(panel, "first_failure") && !Displayed(panel, "second_failure")
                && !Displayed(panel, "第一条场景失败") && !Displayed(panel, "第二条场景失败"),
                "场景失败的每条问题都要显示玩家说明，不能只保留最后一条，也不能显示诊断码。");
            await new UiTestInput(TestScene).PressKey(Key.Quoteleft);
            Require(ConsoleShows(fixture, "first_failure") && ConsoleShows(fixture, "second_failure"),
                "两条场景失败的诊断码必须留在控制台。");
            await new UiTestInput(TestScene).PressKey(Key.Quoteleft);
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
    public async Task Design_diagnostic_keeps_message_and_locate_button_and_status_omits_blank_segments()
    {
        var fixture = await Ready();
        try
        {
            var session = fixture.Factory.Session!;
            var evaluation = (DesignEvaluation.Complete)session.Read().CurrentEvaluation!;
            var diagnostic = new ContentDiagnostic("design_invalid", ContentDiagnosticSeverity.Error, "evaluate",
                new ContentReference.Design(new("empty-design")),
                null, "/layout/cells", new DesignElementPath.Cell([], 0), "布局必须非空。");
            session.SetState(state => state with
            {
                IsDirty = true,
                CurrentEvaluation = evaluation with { Diagnostics = [diagnostic] },
                EvaluationDiagnostics = [diagnostic]
            });
            var panel = EditorPanel(fixture);
            await FramesUntil(TestScene, () => Displayed(panel, "布局必须非空。") && Displayed(panel, "未保存修改"));
            var status = panel.FindChildren("*", "Label", true, false).OfType<Label>()
                .Single(label => label.IsVisibleInTree() && label.Text.StartsWith("层级 ", StringComparison.Ordinal));
            var segments = status.Text.Split(" · ", StringSplitOptions.None);
            Require(segments.All(segment => !string.IsNullOrWhiteSpace(segment)) && segments.Contains("未保存修改"),
                "状态行只能保留非空段，并包含未保存修改。实际：" + status.Text);
            Require(Displayed(panel, "布局必须非空。") && !Displayed(panel, "design_invalid") && !Displayed(panel, "DesignHash")
                && !Displayed(panel, "/layout/cells") && !Displayed(panel, "包："),
                "设计诊断只显示说明，不显示内部码、哈希、路径和包。");
            var rows = panel.FindChildren("*", "HBoxContainer", true, false).OfType<HBoxContainer>().Where(box =>
                box.GetChildren().OfType<Label>().Any(label => label.Text.Contains("布局必须非空。", StringComparison.Ordinal))).ToArray();
            Require(rows.Length == 1, "含「布局必须非空。」的诊断行必须恰好一行。实际：" + rows.Length);
            Require(rows[0].GetChildren().OfType<Button>().Any(button => button.Name == "LocateDiagnostic" && button.Text == "定位"),
                "设计诊断行必须保留定位按钮。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    [Test]
    public async Task Preview_conflict_shows_player_text_and_keeps_raw_values_in_the_log()
    {
        var fixture = await Ready();
        try
        {
            var session = fixture.Factory.Session!;
            session.PreviewCall = operation => new FixedEditPreview(session.Capture().Stamp, operation)
            {
                CanApply = false,
                Conflicts =
                [
                    new(new(1, 0, 2), null, new("occupancy_conflict", SceneStage.Edit, null, null, null, null,
                        "The brush intersects another object's cells.")),
                    new(null, null, new("edit_locked", SceneStage.Edit, null,
                        new ContentReference.Design(new("locked-device")), new("missing-package"), null, "缺失定义禁止旋转。"))
                ]
            };
            var panel = EditorPanel(fixture);
            await ActivateEditorButton(panel, "Properties");
            foreach (var text in new[] { "(1, 0, 2)", "与其他部分的占格重叠。", "缺失定义已锁定，不能进行这个操作。", "来源内容包不可用。" })
                Require(Displayed(panel, text), $"预览冲突界面缺少：{text}");
            foreach (var text in new[] { "GridPosition", "occupancy_conflict", "edit_locked", "locked-device", "missing-package", "禁止旋转", "The brush intersects" })
                Require(!Displayed(panel, text), $"预览冲突不能向玩家显示：{text}");
            await UiCapture.Capture(TestScene, "player-editor-preview-conflict");
            await new UiTestInput(TestScene).PressKey(Key.Quoteleft);
            foreach (var text in new[] { "occupancy_conflict", "The brush intersects", "edit_locked", "locked-device", "missing-package", "禁止旋转" })
                Require(ConsoleShows(fixture, text), $"预览冲突控制台缺少：{text}");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
    [Test]
    public async Task Missing_definition_locks_show_package_names_and_keep_raw_values_in_the_log()
    {
        var named = new PackageId("named-lock-package");
        var missing = new PackageId("missing-lock-package");
        var fixture = new EditorPreview
        {
            Name = "EditorLockFixture",
            Content = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
            {
                Blueprints = [], LocalDrafts = [], Voxels = [], GenerationProfiles = [],
                Packages =
                [
                    new(new PackageMetadata(named, "装甲包", "1", [], []), new("named-root"), PackageState.Available, []),
                    new(null, new("missing-root"), PackageState.Disabled, [])
                ]
            }
        };
        TestScene.AddChild(fixture);
        try
        {
            await FramesUntil(TestScene, () => fixture.Editor.Session is not null && fixture.Controller.Display?.DisplayedFrame is not null);
            var session = fixture.Factory.Session!;
            var cell = new DesignElementPath.Cell([], 0);
            var composite = new DesignElementPath.FunctionalComposite([], 1);
            var current = (DesignEvaluation.Complete)session.Read().CurrentEvaluation!;
            var locked = current with
            {
                Scene = current.Scene with
                {
                    EditLocks =
                    [
                        new(cell, new ContentReference.Core(new CoreDefinitionReference.Voxel(new("named-lock-package:missing-voxel"))),
                            named, [LockedDesignOperation.Move, LockedDesignOperation.Paint], []),
                        new(composite, new ContentReference.Core(new CoreDefinitionReference.Composite(new("missing-lock-package:missing-part"))),
                            missing, [LockedDesignOperation.Enter, LockedDesignOperation.Rotate], [])
                    ]
                }
            };
            ((ScriptedContentFactory)fixture.App.Host!.ContentFactory).EvaluateDesignCall = (_, draft) =>
                new ContentResult<DesignEvaluation>.Success(locked with { Definition = draft });
            session.Change(session.Capture().Draft);
            var panel = EditorPanel(fixture);
            await FramesUntil(TestScene, () => Displayed(panel, "装甲包") && Displayed(panel, "来源内容包不可用。"));
            foreach (var text in new[]
                     {
                         "缺失定义，相关部分不能显示或编辑。", "来源内容包：装甲包。", "受阻操作：移动选区、涂装。",
                         "受阻操作：进入所选设备、选区旋转 90°。"
                     })
                Require(Displayed(panel, text), $"锁定行缺少：{text}");
            foreach (var text in new[] { "named-lock-package", "missing-lock-package", "missing-voxel", "missing-part", "Move", "Paint", "Enter", "Rotate" })
                Require(!Displayed(panel, text), $"锁定行不能向玩家显示：{text}");
            await UiCapture.Capture(TestScene, "player-editor-lock-rows");
            DesignElementPath? located = null;
            session.LocateCall = (_, path) =>
            {
                located = path;
                return new SceneResult<EditElementId>.Success(new(1));
            };
            var row = panel.FindChildren("*", "HBoxContainer", true, false).OfType<HBoxContainer>().Single(box =>
                box.GetChildren().OfType<Label>().Any(label => label.Text.Contains("装甲包", StringComparison.Ordinal)));
            var button = row.GetChildren().OfType<Button>().Single(item => item.Name == "LocateLockedElement" && item.Text == "定位锁定项");
            await EnsureVisible(button);
            await new UiTestInput(TestScene).Activate(button);
            await FramesUntil(TestScene, () => located is DesignElementPath.Cell locatedCell && locatedCell.CellIndex == 0
                && fixture.Editor.State!.Selection.Contains(new EditElementId(1)));
            await new UiTestInput(TestScene).PressKey(Key.Quoteleft);
            foreach (var text in new[] { "named-lock-package:missing-voxel", "missing-lock-package:missing-part", "named-lock-package", "missing-lock-package" })
                Require(ConsoleShows(fixture, text), $"锁定行控制台缺少：{text}");
            var logs = fixture.App.FindChildren("*", "", true, false).OfType<GameConsole>().Single().LogEvents;
            foreach (var text in new[] { "禁止：Move、Paint", "禁止：Enter、Rotate" })
                Require(logs.Any(log => log.EventId == "editor_design_locked" && log.Message.Contains(text, StringComparison.Ordinal)),
                    $"锁定行日志缺少：{text}");
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
    [Test]
    public async Task Undo_and_redo_buttons_follow_history_state()
    {
        var fixture = await Ready();
        try
        {
            var panel = EditorPanel(fixture); var session = fixture.Factory.Session!;
            var undo = Find<Button>(panel, "Undo"); var redo = Find<Button>(panel, "Redo");
            await FramesUntil(TestScene, () => undo.Disabled && redo.Disabled);
            session.SetState(state => state with { CanUndo = true, CanRedo = false });
            await FramesUntil(TestScene, () => !undo.Disabled && redo.Disabled);
            session.SetState(state => state with { CanUndo = false, CanRedo = true });
            await FramesUntil(TestScene, () => undo.Disabled && !redo.Disabled);
            session.SetState(state => state with { CanUndo = true, CanRedo = true });
            await FramesUntil(TestScene, () => !undo.Disabled && !redo.Disabled);
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
