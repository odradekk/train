using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Ui;

internal partial class DesignEditorPanel
{
    private void EditSpecification()
    {
        var draft = _scene.EnteredDraft;
        if (draft?.Specification is not DesignSpecification.Vehicle && draft?.Specification is not DesignSpecification.Building)
        { _service.Report("当前对象没有车辆或建筑规格。大型设备不设包络。"); return; }
        var dialog = new AcceptDialog { Title = "设计规格", OkButtonText = "预览修改" }; AddChild(dialog);
        var body = new VBoxContainer(); dialog.AddChild(body);
        OptionButton? size = null, plot = null, kind = null;
        LineEdit? tags = null;
        if (draft.Specification is DesignSpecification.Vehicle vehicle)
        {
            body.AddChild(UiStyle.Label("车辆尺寸档位")); size = Choice(body, DesignEditorLabels.VehicleSizes);
            size.Select(vehicle.Size is { } value ? (int)value : 0);
        }
        else if (draft.Specification is DesignSpecification.Building building)
        {
            body.AddChild(UiStyle.Label("地块尺寸")); plot = Choice(body, DesignEditorLabels.PlotSizes); plot.Select(building.PlotSize is { } value ? (int)value : 0);
            body.AddChild(UiStyle.Label("建筑类型")); kind = Choice(body, DesignEditorLabels.BuildingKinds); kind.Select(building.Kind is { } buildingKind ? (int)buildingKind : 0);
            body.AddChild(UiStyle.Label("设施兼容标签（逗号分隔）"));
            tags = new LineEdit { Text = string.Join(",", building.FacilityTags), PlaceholderText = string.Join(",", Enum.GetNames<FacilityTag>()) }; body.AddChild(tags);
        }
        dialog.Confirmed += () =>
        {
            DesignSpecification specification;
            if (size is not null) specification = new DesignSpecification.Vehicle((SizeClass)size.Selected);
            else
            {
                var values = tags!.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (values.Any(value => !Enum.TryParse<FacilityTag>(value, true, out var parsed) || !Enum.IsDefined(parsed)))
                { _service.Report("设施兼容标签无效。允许：" + string.Join("、", Enum.GetNames<FacilityTag>())); return; }
                specification = new DesignSpecification.Building((PlotSize)plot!.Selected, (BuildingKind)kind!.Selected, [.. values.Select(value => Enum.Parse<FacilityTag>(value, true))]);
            }
            Preview(new EditOperation.Properties(new(_name.Text, _description.Text), specification)); dialog.QueueFree();
        };
        dialog.Canceled += dialog.QueueFree; dialog.PopupCentered(new(470, 300));
    }
}
