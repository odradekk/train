using System.Text;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal sealed class NodeDetailPanel
{
    private readonly PanelTarget _target;
    private readonly Func<bool> _available;
    private readonly Action<PanelTarget, NodeId> _formation;
    private readonly Action<PanelTarget, SettlementId> _settlement;
    private readonly Action<SettlementId> _combat;
    private readonly Action<PanelTarget, NodeId> _founding;
    private readonly Action<InstanceId> _refit;
    private readonly Action<InstanceId> _service;
    private readonly Func<WorldObjectId, string> _nameOf;
    internal DetailPanelControls Controls { get; }

    internal NodeDetailPanel(PanelContainer panel, VBoxContainer body, PanelTarget target,
        Func<bool> available,
        Action<PanelTarget, NodeId> formation,
        Action<PanelTarget, SettlementId> settlement,
        Action<SettlementId> combat,
        Action<PanelTarget, NodeId> founding,
        Action<InstanceId> refit,
        Action<InstanceId> service,
        Func<WorldObjectId, string> nameOf)
    {
        _target = target;
        _available = available;
        _formation = formation;
        _settlement = settlement;
        _combat = combat;
        _founding = founding;
        _refit = refit;
        _service = service;
        _nameOf = nameOf;
        Controls = new(panel, body, target, Describe, Actions);
    }

    private List<(string Name, string Text, bool Primary, Action Press)> Actions(ICoreReadView view)
    {
        var bound = _target;
        var target = bound.Object;
        var actions = new List<(string Name, string Text, bool Primary, Action Press)>();
        if (target is WorldObjectId.Node node)
        {
            actions.Add(("Form", "编组", true, () =>
            {
                if (_available()) _formation(bound, node.Id);
            }));
            var occupant = view.World.Nodes.FirstOrDefault(item => item.Id == node.Id)?.Occupant;
            if (occupant is NodeOccupant.Settlement settlement)
            {
                actions.Add(("Economy", "库存、市场与服务…", false, () =>
                {
                    if (_available()) _settlement(bound, settlement.Id);
                }));
                actions.Add(("OpenSettlementCombat", "战斗…", false, () =>
                {
                    if (_available()) _combat(settlement.Id);
                }));
            }
            else if (occupant is NodeOccupant.Empty)
                actions.Add(("FoundSettlement", "建立聚居地…", false, () =>
                {
                    if (_available()) _founding(bound, node.Id);
                }));
        }
        if (target is WorldObjectId.Node refitNode && view.FindLocation(new WorldLocation.Node(refitNode.Id)) is Lookup<LocationView>.Found locationForRefit)
            foreach (var vehicle in locationForRefit.Value.Instances.OfType<InstanceId.Vehicle>())
            {
                var id = vehicle;
                actions.Add(("RefitParkedVehicle", $"改装车辆 {id.Id.Value}…", false, () => { if (_available()) _refit(id); }));
                if (view.FindInstance(id) is Lookup<InstanceView>.Found { Value.Ownership: SettlementRelation.PlayerOwned })
                    actions.Add(("ParkedVehicleServices", $"车辆 {id.Id.Value} 服务…", false, () => { if (_available()) _service(id); }));
            }
        return actions;
    }

    private string Describe(ICoreReadView view, IContentView content)
    {
        var target = _target.Object;
        var text = new StringBuilder();
        if (target is WorldObjectId.Node node && view.FindNode(node.Id) is Lookup<NodeView>.Found foundNode)
        {
            var map = foundNode.Value.Map;
            var occupant = map.Occupant switch { NodeOccupant.Settlement owned => $"聚居地 · {WorldPanels.Relation(owned.Relation)}",
                NodeOccupant.Lair => "生物聚落", _ => "空节点" };
            text.AppendLine($"占据者：{occupant}\n位置：{map.Position.X:0.##} m，{map.Position.Y:0.##} m\n地貌：{map.Terrain.TypeId.Value}\n站台：{map.OccupiedPlatforms} / {map.Platforms}\n");
            if (map.FoundingJob is not null) text.AppendLine("建城进行中");
            text.AppendLine("节点资源");
            foreach (var resource in map.Resources) text.AppendLine($"{WorldPanels.GoodName(resource.Good, content)}：{resource.Quantity}");
            text.AppendLine("\n地点车辆");
            if (view.FindLocation(new WorldLocation.Node(node.Id)) is Lookup<LocationView>.Found location)
                foreach (var instance in location.Value.Instances.OfType<InstanceId.Vehicle>())
                    if (view.FindInstance(instance) is Lookup<InstanceView>.Found vehicle)
                        text.AppendLine($"车辆 {instance.Id.Value} · {(vehicle.Value.Details is InstanceDetails.Vehicle details && details.Train is { } trainId ? $"列车 {trainId.Value}" : "未编组")} · {WorldPanels.Relation(vehicle.Value.Ownership)}");
            text.AppendLine("\n相邻线路");
            foreach (var edge in foundNode.Value.AdjacentEdges) text.AppendLine(_nameOf(new WorldObjectId.Edge(edge)));
        }
        else return "已不存在";
        return text.ToString().ReplaceLineEndings("\n");
    }
}
