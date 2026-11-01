using System.Text;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal sealed class InstanceDetailPanel
{
    private readonly PanelTarget _target;
    private readonly Func<bool> _available;
    private readonly Action<InstanceId> _refit;
    private readonly Action<InstanceId> _service;
    internal DetailPanelControls Controls { get; }

    internal InstanceDetailPanel(PanelContainer panel, VBoxContainer body, PanelTarget target,
        Func<bool> available,
        Action<InstanceId> refit,
        Action<InstanceId> service)
    {
        _target = target;
        _available = available;
        _refit = refit;
        _service = service;
        Controls = new(panel, body, target, Describe, Actions);
    }

    private List<(string Name, string Text, bool Primary, Action Press)> Actions(ICoreReadView view)
    {
        var bound = _target;
        var target = bound.Object;
        var actions = new List<(string Name, string Text, bool Primary, Action Press)>();
        if (target is WorldObjectId.Instance instance)
        {
            actions.Add(("RefitDesign", "改装设计…", false, () => { if (_available()) _refit(instance.Id); }));
            if (instance.Id is InstanceId.Vehicle && view.FindInstance(instance.Id) is Lookup<InstanceView>.Found
                { Value.Ownership: SettlementRelation.PlayerOwned, Value.Location: WorldLocation.Node })
                actions.Add(("VehicleServices", "车辆服务…", false, () => { if (_available()) _service(instance.Id); }));
        }
        return actions;
    }

    private string Describe(ICoreReadView view, IContentView content)
    {
        var target = _target.Object;
        var text = new StringBuilder();
        if (target is WorldObjectId.Instance instance && view.FindInstance(instance.Id) is Lookup<InstanceView>.Found foundInstance)
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
