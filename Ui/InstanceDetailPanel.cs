using System.Text;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal sealed class InstanceDetailPanel
{
    private readonly InstanceId _id;
    private readonly Func<bool> _available;
    private readonly Action<InstanceId> _refit;
    private readonly Action<InstanceId> _service;
    internal DetailPanelControls Controls { get; }

    internal InstanceDetailPanel(PanelContainer panel, VBoxContainer body, PanelTarget target, InstanceId id,
        Func<bool> available,
        Action<InstanceId> refit,
        Action<InstanceId> service)
    {
        _id = id;
        _available = available;
        _refit = refit;
        _service = service;
        Controls = new(panel, body, target, Describe, Actions);
    }

    private List<DetailAction> Actions(ICoreReadView view)
    {
        var actions = new List<DetailAction>();
        actions.Add(new("RefitDesign", "改装设计…", false, () => { if (_available()) _refit(_id); }));
        if (_id is InstanceId.Vehicle && view.FindInstance(_id) is Lookup<InstanceView>.Found
            { Value.Ownership: SettlementRelation.PlayerOwned, Value.Location: WorldLocation.Node })
            actions.Add(new("VehicleServices", "车辆服务…", false, () => { if (_available()) _service(_id); }));
        return actions;
    }

    private string Describe(ICoreReadView view, IContentView content)
    {
        var text = new StringBuilder();
        if (view.FindInstance(_id) is Lookup<InstanceView>.Found foundInstance)
        {
            var value = foundInstance.Value;
            var location = WorldPanels.LocationText(value.Location, view, content);
            text.AppendLine($"位置：{location}\n归属：{WorldPanels.Relation(value.Ownership)}\n结构耐久：{value.Structure.EffectiveCurrent} / {value.Structure.Maximum}\n");
            if (value.Details is InstanceDetails.Vehicle vehicle)
                text.AppendLine(vehicle.Train is { } trainId ? $"编组：列车 {trainId.Value}" : "未编组");
            else if (value.Details is InstanceDetails.Building building)
                text.AppendLine($"地块：{building.Plot.Value}");
            foreach (var problem in value.MissingContent) text.AppendLine(CommandDiagnostics.Describe(problem, content));
        }
        else return "已不存在";
        return text.ToString().ReplaceLineEndings("\n");
    }
}
