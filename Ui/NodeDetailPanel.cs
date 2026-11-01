using System.Text;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal sealed class NodeDetailPanel
{
    private readonly NodeId _id;
    private readonly PanelTarget _target;
    private readonly Func<bool> _available;
    private readonly Action<PanelTarget, NodeId> _formation;
    private readonly Action<PanelTarget, NodeId> _founding;
    private readonly Action<InstanceId> _refit;
    private readonly Action<InstanceId> _service;
    private readonly Func<WorldObjectId, ICoreReadView, IContentView, string> _nameOf;
    internal DetailPanelControls Controls { get; }

    internal NodeDetailPanel(PanelContainer panel, VBoxContainer body, PanelTarget target, NodeId id,
        Func<bool> available,
        Action<PanelTarget, NodeId> formation,
        Action<PanelTarget, NodeId> founding,
        Action<InstanceId> refit,
        Action<InstanceId> service,
        Func<WorldObjectId, ICoreReadView, IContentView, string> nameOf)
    {
        _id = id;
        _target = target;
        _available = available;
        _formation = formation;
        _founding = founding;
        _refit = refit;
        _service = service;
        _nameOf = nameOf;
        Controls = new(panel, body, target, Describe, Actions);
    }

    private List<DetailAction> Actions(ICoreReadView view)
    {
        var actions = new List<DetailAction>();
        actions.Add(new("Form", "编组", true, () =>
        {
            if (_available()) _formation(_target, _id);
        }));
        var occupant = view.World.Nodes.FirstOrDefault(item => item.Id == _id)?.Occupant;
        if (occupant is NodeOccupant.Empty)
            actions.Add(new("FoundSettlement", "建立聚居地…", false, () =>
            {
                if (_available()) _founding(_target, _id);
            }));

        if (view.FindLocation(new WorldLocation.Node(_id)) is Lookup<LocationView>.Found locationForRefit)
            foreach (var vehicle in locationForRefit.Value.Instances.OfType<InstanceId.Vehicle>())
            {
                var id = vehicle;
                actions.Add(new("RefitParkedVehicle", $"改装车辆 {id.Id.Value}…", false, () => { if (_available()) _refit(id); }));
                if (view.FindInstance(id) is Lookup<InstanceView>.Found { Value.Ownership: SettlementRelation.PlayerOwned })
                    actions.Add(new("ParkedVehicleServices", $"车辆 {id.Id.Value} 服务…", false, () => { if (_available()) _service(id); }));
            }
        return actions;
    }

    private string Describe(ICoreReadView view, IContentView content)
    {
        var text = new StringBuilder();
        if (view.FindNode(_id) is Lookup<NodeView>.Found foundNode)
        {
            var map = foundNode.Value.Map;
            var occupant = map.Occupant switch { NodeOccupant.Settlement owned => $"聚居地 · {WorldPanels.Relation(owned.Relation)}",
                NodeOccupant.Lair => "生物聚落", _ => "空节点" };
            text.AppendLine($"占据者：{occupant}\n位置：{map.Position.X:0.##} m，{map.Position.Y:0.##} m\n地貌：{map.Terrain.TypeId.Value}\n站台：{map.OccupiedPlatforms} / {map.Platforms}\n");
            if (map.FoundingJob is not null) text.AppendLine("建城进行中");
            text.AppendLine("节点资源");
            foreach (var resource in map.Resources) text.AppendLine($"{WorldPanels.GoodName(resource.Good, content)}：{resource.Quantity}");
            text.AppendLine("\n地点车辆");
            if (view.FindLocation(new WorldLocation.Node(_id)) is Lookup<LocationView>.Found location)
                foreach (var instance in location.Value.Instances.OfType<InstanceId.Vehicle>())
                    if (view.FindInstance(instance) is Lookup<InstanceView>.Found vehicle)
                        text.AppendLine($"车辆 {instance.Id.Value} · {(vehicle.Value.Details is InstanceDetails.Vehicle details && details.Train is { } trainId ? $"列车 {trainId.Value}" : "未编组")} · {WorldPanels.Relation(vehicle.Value.Ownership)}");
            text.AppendLine("\n相邻线路");
            foreach (var edge in foundNode.Value.AdjacentEdges) text.AppendLine(_nameOf(new WorldObjectId.Edge(edge), view, content));
        }
        else return "已不存在";
        return text.ToString().ReplaceLineEndings("\n");
    }
}
