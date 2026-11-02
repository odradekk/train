using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class BlueprintLibraryPanel
{
    internal void NewDraft()
    {
        var dialog = new AcceptDialog { Title = "新建设计", OkButtonText = "创建并编辑", DialogHideOnOk = false };
        dialog.AddCancelButton("取消"); AddChild(dialog);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        dialog.AddChild(scroll);
        var sections = new HBoxContainer(); sections.AddThemeConstantOverride("separation", Gap * 2); scroll.AddChild(sections);
        var identity = new VBoxContainer { CustomMinimumSize = new(220, 0) }; sections.AddChild(identity);
        Text(identity, "名称");
        var name = new LineEdit { Name = "DesignName", Text = "新设计", MaxLength = 100 }; identity.AddChild(name);
        Text(identity, "类型"); var kind = Choice(identity, "DesignKind", ["车辆", "建筑", "大型设备"]);
        var dimensions = new VBoxContainer { CustomMinimumSize = new(300, 0) }; sections.AddChild(dimensions);
        var vehicleOptions = new VBoxContainer { Name = "VehicleOptions" }; dimensions.AddChild(vehicleOptions);
        Text(vehicleOptions, "车辆尺寸档位"); var size = Choice(vehicleOptions, "VehicleSize", DesignEditorLabels.VehicleSizes);
        var envelope = Text(vehicleOptions, "正在读取最大外形尺寸…"); envelope.Name = "EnvelopeLimit";
        var buildingOptions = new VBoxContainer { Name = "BuildingOptions" }; dimensions.AddChild(buildingOptions);
        Text(buildingOptions, "地块大小"); var plot = Choice(buildingOptions, "PlotSize", DesignEditorLabels.PlotSizes);
        Text(buildingOptions, "建筑类型"); var building = Choice(buildingOptions, "BuildingKind", DesignEditorLabels.BuildingKinds);
        Text(buildingOptions, "设施");
        var facilities = new GridContainer { Columns = 2 }; buildingOptions.AddChild(facilities);
        var tags = Enum.GetValues<FacilityTag>().Select(tag => (Tag: tag,
            Control: new CheckBox { Name = "Facility" + tag, Text = DesignEditorLabels.FacilityName(tag) })).ToArray();
        foreach (var tag in tags) facilities.AddChild(tag.Control);
        var equipment = Text(dimensions, "大型设备没有外形尺寸上限。");
        var destinationBox = new VBoxContainer { CustomMinimumSize = new(220, 0) }; sections.AddChild(destinationBox);
        Text(destinationBox, "保存位置"); var destination = Choice(destinationBox, "SaveDestination", ["本局蓝图库", "用户内容库"]);
        var destinationDescription = Text(destinationBox, GroupDescriptions[0]);
        Text(destinationBox, "创建后先编辑草稿，保存为蓝图后才会出现在所选库中。");
        var problem = Text(destinationBox, ""); problem.AddThemeColorOverride("font_color", UiStyle.Error);
        var request = 0;
        async void UpdateEnvelope()
        {
            var current = ++request;
            envelope.Text = "正在读取最大外形尺寸…";
            var content = _service.Host.Content;
            var draft = new DesignDraft(new("外形尺寸预览", "读取车辆档位外形上限。"),
                new DesignSpecification.Vehicle(DesignEditorLabels.VehicleSizeValues[size.Selected]), new([], [], []));
            try
            {
                var result = content is null ? null : await Task.Run(() => _service.Host.ContentFactory.EvaluateDesign(content, draft));
                if (!GodotObject.IsInstanceValid(dialog) || dialog.IsQueuedForDeletion() || current != request) return;
                var bounds = result is ContentResult<DesignEvaluation>.Success evaluated ? evaluated.Value switch
                { DesignEvaluation.Complete complete => complete.Scene.AllowedEnvelope, DesignEvaluation.Partial partial => partial.Scene.AllowedEnvelope, _ => null } : null;
                envelope.Text = bounds is null ? "最大外形尺寸暂不可用。" :
                    $"外形不能超过：长 {bounds.Maximum.Z - bounds.Minimum.Z + 1} × 宽 {bounds.Maximum.X - bounds.Minimum.X + 1} × 高 {bounds.Maximum.Y - bounds.Minimum.Y + 1} 格。";
            }
            catch { if (GodotObject.IsInstanceValid(envelope)) envelope.Text = "最大外形尺寸暂不可用。"; }
        }
        void RefreshFields()
        {
            vehicleOptions.Visible = kind.Selected == 0; buildingOptions.Visible = kind.Selected == 1; equipment.Visible = kind.Selected == 2;
            destinationDescription.Text = GroupDescriptions[destination.Selected];
            dialog.GetOkButton().Disabled = string.IsNullOrWhiteSpace(name.Text);
            problem.Text = string.IsNullOrWhiteSpace(name.Text) ? "请填写设计名称。" : "";
        }
        plot.ItemSelected += _ => { if (plot.Selected >= 4) building.Select(4); else if (building.Selected == 4) building.Select(0); };
        building.ItemSelected += _ => { if (building.Selected == 4 && plot.Selected < 4) plot.Select(4); else if (building.Selected != 4 && plot.Selected >= 4) plot.Select(0); };
        kind.ItemSelected += _ => RefreshFields(); destination.ItemSelected += _ => RefreshFields(); name.TextChanged += _ => RefreshFields();
        size.ItemSelected += _ => UpdateEnvelope();
        dialog.Confirmed += () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return;
            DesignSpecification specification = kind.Selected switch
            {
                0 => new DesignSpecification.Vehicle(DesignEditorLabels.VehicleSizeValues[size.Selected]),
                1 => new DesignSpecification.Building((PlotSize)plot.Selected, (BuildingKind)building.Selected, [.. tags.Where(tag => tag.Control.ButtonPressed).Select(tag => tag.Tag)]),
                _ => new DesignSpecification.Assembly(null)
            };
            var library = destination.Selected == 0 ? BlueprintLibrary.Local : BlueprintLibrary.User;
            Edit(() => _service.Open(new(new(name.Text.Trim(), ""), specification, new([], [], [])),
                library == BlueprintLibrary.Local ? new DraftTarget.LocalBlueprint(null) : new DraftTarget.UserBlueprint(null),
                new BlueprintSaveTarget.New(library), true, fromLibrary: true));
            if (_service.IsOpen) dialog.QueueFree(); else problem.Text = _service.Message ?? "暂时无法开始编辑。";
        };
        dialog.Canceled += dialog.QueueFree;
        RefreshFields(); UpdateEnvelope(); dialog.PopupCenteredClamped(new(850, 460)); name.GrabFocus();
    }

    private static OptionButton Choice(Node parent, string name, string[] values)
    {
        var choice = new OptionButton { Name = name }; foreach (var value in values) choice.AddItem(value); parent.AddChild(choice); return choice;
    }
}

internal static class DesignEditorLabels
{
    internal static readonly SizeClass[] VehicleSizeValues = Enum.GetValues<SizeClass>();
    internal static readonly string[] VehicleSizes = VehicleSizeValues.Select(EconomyPanels.SizeName).ToArray();
    internal static readonly string[] PlotSizes = ["小型地块", "中型地块", "大型地块", "巨型地块", "小型站点地块", "大型站点地块"];
    internal static readonly string[] BuildingKinds = ["生产建筑", "仓库", "供能建筑", "施工建筑", "车站", "防御建筑"];
    internal static string FacilityName(FacilityTag tag) => tag switch
    {
        FacilityTag.Agriculture => "农业", FacilityTag.Extraction => "开采", FacilityTag.Metallurgy => "冶金",
        FacilityTag.BuildingMaterials => "建材", FacilityTag.LightIndustry => "轻工", FacilityTag.Chemical => "化工",
        FacilityTag.FoodProcessing => "食品加工", FacilityTag.Assembly => "装配", FacilityTag.Electronics => "电子", FacilityTag.Recovery => "回收",
        _ => throw new ArgumentOutOfRangeException(nameof(tag))
    };
}
