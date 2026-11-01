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
        if (draft?.Specification is not DesignSpecification.Vehicle && draft?.Specification is not DesignSpecification.Building && draft?.Specification is not DesignSpecification.SceneModel)
        { _service.Report("当前对象没有可编辑规格。大型设备不设包络。"); return; }
        var dialog = new AcceptDialog { Title = "设计规格", OkButtonText = "预览修改" }; AddChild(dialog);
        var body = new VBoxContainer(); dialog.AddChild(body);
        OptionButton? size = null, plot = null, kind = null;
        LineEdit? tags = null;
        CheckButton? flatten = null;
        SpinBox? fromX = null, fromZ = null, toX = null, toZ = null, height = null;
        if (draft.Specification is DesignSpecification.Vehicle vehicle)
        {
            body.AddChild(UiStyle.Label("车辆尺寸档位")); size = Choice(body, DesignEditorLabels.VehicleSizes);
            size.Select(vehicle.Size is { } value ? Array.IndexOf(DesignEditorLabels.VehicleSizeValues, value) : 0);
        }
        else if (draft.Specification is DesignSpecification.Building building)
        {
            body.AddChild(UiStyle.Label("地块尺寸")); plot = Choice(body, DesignEditorLabels.PlotSizes); plot.Select(building.PlotSize is { } value ? (int)value : 0);
            body.AddChild(UiStyle.Label("建筑类型")); kind = Choice(body, DesignEditorLabels.BuildingKinds); kind.Select(building.Kind is { } buildingKind ? (int)buildingKind : 0);
            body.AddChild(UiStyle.Label("设施兼容标签（逗号分隔）"));
            tags = new LineEdit { Text = string.Join(",", building.FacilityTags), PlaceholderText = string.Join(",", Enum.GetNames<FacilityTag>()) }; body.AddChild(tags);
        }
        else if (draft.Specification is DesignSpecification.SceneModel sceneModel)
        {
            flatten = new CheckButton { Text = "启用地形平整范围", ButtonPressed = sceneModel.Flatten is not null }; body.AddChild(flatten);
            fromX = Number(body, "起点 X", -4096, 4096, sceneModel.Flatten?.From.X ?? 0);
            fromZ = Number(body, "起点 Z", -4096, 4096, sceneModel.Flatten?.From.Z ?? 0);
            toX = Number(body, "终点 X", -4096, 4096, sceneModel.Flatten?.To.X ?? 0);
            toZ = Number(body, "终点 Z", -4096, 4096, sceneModel.Flatten?.To.Z ?? 0);
            height = Number(body, "平整高度", -4096, 4096, sceneModel.Flatten?.Height ?? 0);
        }
        dialog.Confirmed += () =>
        {
            DesignSpecification specification;
            if (size is not null) specification = new DesignSpecification.Vehicle(DesignEditorLabels.VehicleSizeValues[size.Selected]);
            else if (flatten is not null)
                specification = new DesignSpecification.SceneModel(flatten.ButtonPressed
                    ? new FlattenRegion(new((int)fromX!.Value, (int)fromZ!.Value), new((int)toX!.Value, (int)toZ!.Value), (int)height!.Value) : null);
            else
            {
                var values = tags!.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (values.Any(value => !Enum.TryParse<FacilityTag>(value, true, out var parsed) || !Enum.IsDefined(parsed)))
                { _service.Report("设施兼容标签无效。允许：" + string.Join("、", Enum.GetNames<FacilityTag>())); return; }
                specification = new DesignSpecification.Building((PlotSize)plot!.Selected, (BuildingKind)kind!.Selected, [.. values.Select(value => Enum.Parse<FacilityTag>(value, true))]);
            }
            Execute(new EditOperation.Properties(new(_name.Text, _description.Text), specification)); dialog.QueueFree();
        };
        dialog.Canceled += dialog.QueueFree; dialog.PopupCentered(new(470, 300));
    }
}
