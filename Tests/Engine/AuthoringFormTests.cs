using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class AuthoringFormTests(Node testScene) : TestClass(testScene)
{
    private AuthoringDefinitionForm Mount(DefinitionDraft draft)
    {
        var form = new AuthoringDefinitionForm(); TestScene.AddChild(form); form.Load(draft, new("fixture"), []); return form;
    }
    private static LineEdit Field(AuthoringDefinitionForm form, string title) =>
        form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Single(edit => edit.TooltipText == title);

    [Test]
    public async Task Surface_material_choices_keep_surface_order_and_repeated_materials()
    {
        var source = new DefinitionDraft.Voxel(new("voxel", new("体素", ""), null, null,
            AllowedVoxelOrientations.None, new VoxelCollision.FullBox(),
            new AuthoringVoxelPresentation.Mesh(new("assets/model.glb"), [MaterialCategory.Glass, MaterialCategory.Glass, MaterialCategory.Concrete]), new(false, null)));
        var form = Mount(source);
        try
        {
            var materials = form.FindChildren("*", "OptionButton", true, false).OfType<OptionButton>().Where(control => control.TooltipText == "表面材质").ToArray();
            Require(materials.Length == 3, "每个模型表面必须有单独的材质选择，重复材质不能合并。");
            Require(((DefinitionDraft.Voxel)form.Capture()).Value.Presentation is AuthoringVoxelPresentation.Mesh mesh
                && mesh.SurfaceMaterials.SequenceEqual(((AuthoringVoxelPresentation.Mesh)source.Value.Presentation!).SurfaceMaterials), "未编辑时必须保留表面顺序和重复材质。");
            materials[1].Select((int)MaterialCategory.PaintedMetal);
            var changed = (AuthoringVoxelPresentation.Mesh)((DefinitionDraft.Voxel)form.Capture()).Value.Presentation!;
            Require(changed.SurfaceMaterials.SequenceEqual(new[] { MaterialCategory.Glass, MaterialCategory.PaintedMetal, MaterialCategory.Concrete }), "修改第二个表面的材质不能更改第一、第三个表面。");
        }
        finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }

    [Test]
    public async Task Reference_choices_filter_kind_preserve_missing_value_and_refresh_without_losing_text()
    {
        var draft = new DefinitionDraft.LairType(new("den", new("巢穴", "说明"), new("core:old-scene"), null, [], null, null, null, null, null, null, null));
        var form = new AuthoringDefinitionForm(); TestScene.AddChild(form);
        try
        {
            AuthoringReferenceChoice scene = new(AuthoringReferenceKind.SceneModel,
                new ContentReference.Blueprint(new(BlueprintLibrary.Packages, new("core:scene"))), new("山洞", ""));
            AuthoringReferenceChoice building = new(AuthoringReferenceKind.Building,
                new ContentReference.Blueprint(new(BlueprintLibrary.Packages, new("core:house"))), new("房屋", ""));
            form.Load(draft, new("fixture"), [scene, building]);
            var select = form.FindChildren("*", "OptionButton", true, false).OfType<OptionButton>().Single(control => control.TooltipText == "场景模型蓝图 ID（可空）");
            Require(Enumerable.Range(0, select.ItemCount).Select(select.GetItemText).Contains("山洞 · core:scene")
                && !Enumerable.Range(0, select.ItemCount).Any(i => select.GetItemText(i).Contains("core:house")), "候选必须按引用种类筛选并显示名称和完整 ID。");
            Require(((DefinitionDraft.LairType)form.Capture()).Value.SceneModel == draft.Value.SceneModel, "缺失旧引用必须保留，不能换成候选第一项。");
            Require(form.TryCapture(out var missing) && missing is DefinitionDraft.LairType { Value.SceneModel.Value: "core:old-scene" }, "缺失引用只提示错误，仍须捕获旧值。");
            Require(form.FindChildren("*", "Label", true, false).OfType<Label>().Any(label => label.Visible && label.Text.Contains("引用不可用")), "缺失旧引用必须在字段处报错。");
            Field(form, "名称").Text = "未保存名称";
            form.SetReferenceChoices([scene, building, scene with { Reference = new ContentReference.Blueprint(new(BlueprintLibrary.Packages, new("core:old-scene"))) }]);
            Require(form.TryCapture(out var captured) && captured is DefinitionDraft.LairType { Value.Display.Name: "未保存名称" }, "刷新候选必须保留输入并解除已可用引用的错误。");
        }
        finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }

    [Test]
    public async Task Diagnostics_follow_current_row_indices_after_deletion()
    {
        var source = new DefinitionDraft.SettlementTemplate(new("town", new("城镇", ""), null, null, null,
            [new(1, PlotSize.Small, new(0, 0, 0), VoxelOrientation.PxPy, null), new(2, PlotSize.Large, new(1, 0, 0), VoxelOrientation.PxPy, null)], [], [], []));
        var form = Mount(source);
        try
        {
            var numbers = form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Where(edit => edit.TooltipText == "地块编号").ToArray();
            var remaining = numbers[1].GetParent();
            var remove = numbers[0].GetParent().GetChildren().OfType<Button>().Single(button => button.Text == "删除此行");
            remove.EmitSignal(BaseButton.SignalName.Pressed);
            await new UiTestInput(TestScene).Frames(2);
            form.SetDiagnostics([new("invalid_field", ContentDiagnosticSeverity.Error, "authoring", null, null, "/plots/0/size", null, "地块尺寸错误")]);
            Require(remaining.GetChildren().OfType<Label>().Any(label => label.Text == "地块尺寸错误"), "删除首行后的 /plots/0 错误必须显示在原第二行。");
        }
        finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }

    [Test]
    public async Task Form_reports_all_local_errors_and_keeps_identity_read_only()
    {
        var form = Mount(new DefinitionDraft.SettlementType(new("town", new("城镇", "说明"), null, false, 1, [], 1, 2)));
        try
        {
            Field(form, "权重（可空）").Text = "overflow";
            Field(form, "最少人口（可空）").Text = "overflow";
            Require(!form.TryCapture(out _), "所有本地格式错误必须阻止捕获。");
            Require(form.FindChildren("*", "Label", true, false).OfType<Label>().Count(label => label.Visible && label.Text.Contains("格式无效")) == 2,
                "一次捕获必须在两个错误字段下分别显示错误。");
            Require(!Field(form, "本地 ID").Editable, "已有定义的本地 ID 必须只读。");
        }
        finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }

    [Test]
    public async Task Enums_use_chinese_choices_and_asset_picker_returns_path_to_same_field()
    {
        var form = Mount(new DefinitionDraft.Voxel(new("voxel", new("体素", ""), null, null,
            AllowedVoxelOrientations.FourWay, new VoxelCollision.FullBox(), null, new(false, null))));
        try
        {
            var orientation = form.FindChildren("*", "OptionButton", true, false).OfType<OptionButton>().Single(control => control.TooltipText == "允许朝向");
            Require(Enumerable.Range(0, orientation.ItemCount).All(i => !System.Text.RegularExpressions.Regex.IsMatch(orientation.GetItemText(i), "[A-Za-z]")), "枚举选项必须显示中文。");
            Require(((DefinitionDraft.Voxel)form.Capture()).Value.Orientations == AllowedVoxelOrientations.FourWay, "中文选项必须保留原始朝向值。");
            var icon = Field(form, "图标资产路径");
            var requested = false; var edits = 0; form.Edited += () => edits++;
            form.AssetRequested += complete => { requested = true; complete("assets/icon.png"); };
            var picker = icon.GetParent().GetChildren().OfType<Button>().First(button => button.Text == "选择文件…");
            picker.EmitSignal(BaseButton.SignalName.Pressed);
            Require(requested && edits > 0 && ((DefinitionDraft.Voxel)form.Capture()).Value.Icon?.Value == "assets/icon.png", "选择文件按钮必须将回调路径写回原字段并报告编辑。");
        }
        finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }

    [Test]
    public async Task Missing_terrain_references_are_reported_but_preserved_in_capture()
    {
        var form = Mount(new DefinitionDraft.SettlementType(new("town", new("城镇", ""), null, false, null, [new("core:old-terrain")], null, null)));
        try
        {
            Require(form.TryCapture(out var captured) && captured is DefinitionDraft.SettlementType settlement
                && settlement.Value.PreferredTerrain.Single().Value == "core:old-terrain", "候选中缺失的偏好地形仍须捕获旧值。");
            Require(form.FindChildren("*", "Label", true, false).OfType<Label>().Any(label => label.Visible && label.Text.Contains("引用不可用")), "缺失多选引用必须显示字段错误。");
        }
        finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }

    [Test]
    public async Task Content_reference_diagnostic_replaces_local_warning_and_removal_restores_it()
    {
        var form = Mount(new DefinitionDraft.LairType(new("den", new("巢穴", ""), new("core:old-scene"), null, [], null, null, null, null, null, null, null)));
        try
        {
            const string diagnostic = "引用不可用：core:old-scene";
            form.SetDiagnostics([new("missing_reference", ContentDiagnosticSeverity.Error, "authoring", null, null, "/scene_model", null, diagnostic)]);
            var errors = form.FindChildren("*", "Label", true, false).OfType<Label>()
                .Where(label => label.Visible && label.Text.Contains("引用不可用")).ToArray();
            Require(errors.Length == 1 && errors[0].Text == diagnostic, "内容诊断已报告同一引用时，字段只能显示内容诊断，不能叠加本地引用提示。");
            form.SetDiagnostics([]);
            Require(form.FindChildren("*", "Label", true, false).OfType<Label>()
                .Any(label => label.Visible && label.Text == "引用不可用，请重新选择。"), "移除内容诊断后必须恢复缺失引用的本地提示。");
            Require(form.TryCapture(out var captured) && captured is DefinitionDraft.LairType { Value.SceneModel.Value: "core:old-scene" }, "引用提示恢复后仍须捕获原引用。");
        }
        finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }

    [Test]
    public async Task Required_reference_still_blocks_capture_when_empty()
    {
        var form = Mount(new DefinitionDraft.LairType(new("den", new("巢穴", ""), null, null, [new(new(""), 1)], null, null, null, null, null, null, null)));
        try { Require(!form.TryCapture(out _), "必填引用为空必须阻止捕获。"); }
        finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }

    [Test]
    public async Task Plot_and_vehicle_size_choices_use_existing_game_terms_and_explicit_axes()
    {
        var form = Mount(new DefinitionDraft.Voxel(new("voxel", new("体素", ""), null,
            new(null, null, null, false, null, [new PartCapability.Construction(1, PlotSize.SmallStation, SizeClass.ExtraLarge)]),
            AllowedVoxelOrientations.None, new VoxelCollision.None(), null, new(false, null))));
        try
        {
            var choices = form.FindChildren("*", "OptionButton", true, false).OfType<OptionButton>().ToArray();
            var plot = choices.Single(c => c.TooltipText == "最大建筑地块尺寸");
            var vehicle = choices.Single(c => c.TooltipText == "最大车辆尺寸（可空）");
            Require(new[] { plot.GetItemText(plot.Selected), vehicle.GetItemText(vehicle.Selected) }.SequenceEqual(new[] { "小站点", "超大型" }), "地块和车辆尺寸必须使用已有游戏术语。");
        }
        finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }
    [Test]
    public async Task Plot_orientation_displays_explicit_right_and_up_axes()
    {
        var form = new AuthoringDefinitionForm(); TestScene.AddChild(form);
        try
        {
            form.Load(new DefinitionDraft.SettlementTemplate(new("town", new("城镇", ""), null, null, null,
                [new(1, PlotSize.Small, new(0, 0, 0), VoxelOrientation.PxPy, null)], [], [], [])), new("fixture"), []);
            var orientation = form.FindChildren("*", "OptionButton", true, false).OfType<OptionButton>().Single(c => c.TooltipText == "朝向");
            Require(orientation.GetItemText(orientation.Selected) == "右 +X · 上 +Y", "朝向必须明确显示右轴和上轴。");
        }
        finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }

    [Test]
    public async Task Physical_fields_show_chinese_units()
    {
        var form = Mount(new DefinitionDraft.Voxel(new("voxel", new("体素", ""), null,
            new(null, 1, null, false, null, [new PartCapability.Propulsion(1, 1), new PartCapability.Weapon(1, 1)]),
            AllowedVoxelOrientations.None, new VoxelCollision.None(), null, new(false, null))));
        try
        {
            var labels = form.FindChildren("*", "Label", true, false).OfType<Label>().Select(c => c.Text).ToArray();
            Require(labels.Contains("牵引力（千牛）") && labels.Contains("额定速度（米/秒）")
                && labels.Contains("质量（可空）（吨）") && labels.Contains("武器间隔（秒）")
                && labels.Contains("基础价值（最小货币单位，可空）"), "物理量字段必须显示统一的中文单位，基础价值的单位和可空说明应放在同一括号内。");
        }
        finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }
}
