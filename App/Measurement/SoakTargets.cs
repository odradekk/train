using Train.Core.Contracts;

namespace Train.App.Measurement;

// Decision note: 2026-10-26-performance-measurement-system
internal sealed record SoakTargets(string Profile, TrainId Train, NodeId Node, EdgeId Edge)
{
    internal static SoakTargets Create(string profile, TrainId train, NodeId node, EdgeId? edge)
    {
        if (profile is not ("standard-world" or "crowded-station" or "busy-combat" or "long-consist"))
            throw new ArgumentException("未知长测配置：" + profile);
        return new(profile, train, node, edge ?? new(profile == "standard-world" ? 15UL : 11UL));
    }
    internal bool Standard => Profile == "standard-world";
    internal string NodeView => Profile == "busy-combat" ? "node-battle" : "settlement";
    internal string PrimaryView => Standard ? "line-battle" : Profile == "long-consist" ? "follow" : NodeView;
    internal WorldObjectId PrimaryTarget => Standard ? new WorldObjectId.Edge(Edge) :
        Profile == "long-consist" ? new WorldObjectId.Train(Train) : new WorldObjectId.Node(Node);
    internal SimulationSpeed[] Speeds(int round) => round > 0 ? [SimulationSpeed.Eight] : Standard
        ? [SimulationSpeed.Eight, SimulationSpeed.One] : [SimulationSpeed.One, SimulationSpeed.Eight];

    internal void ValidateHeavy(ICoreReadView view)
    {
        void Require(bool valid, string field, object expected, object? actual)
        {
            if (!valid) throw new InvalidDataException($"长测目标不成立：{Profile}，列车 {Train.Value}、节点 {Node.Value}、线路 {Edge.Value}；{field} 期望 {expected}，实际 {actual}。");
        }
        var train = view.FindTrain(Train) is Lookup<TrainView>.Found found ? found.Value : null;
        Require(train is not null && !train.Vehicles.IsEmpty, "编组", "存在且非空", train?.Vehicles.Length);
        var node = view.World.Nodes.FirstOrDefault(item => item.Id == Node);
        Require(node is not null, "节点", "存在", "不存在");
        var edge = view.World.Edges.FirstOrDefault(item => item.Id == Edge);
        Require(edge is not null, "线路", "存在", "不存在");
        if (Profile == "long-consist")
        {
            Require(train!.Activity == TrainActivity.Running, "列车活动", TrainActivity.Running, train.Activity);
            Require(train.Vehicles.Length == 32 && train.LengthMeters == 1280, "编组规模", "32 辆／1280 米", $"{train.Vehicles.Length} 辆／{train.LengthMeters} 米");
            Require(train.Plan is { Loop: true, Stops.Length: >= 2 } && train.Plan.Stops.Any(stop => stop is PlanStop.Waypoint at && at.Node == Node),
                "运行计划", "循环且经过指定途经点", train.Plan);
            Require(edge!.Tracks > 0 && (edge.Start == Node || edge.End == Node), "线路", "有轨且连接指定节点", edge);
        }
        else
        {
            Require(node!.Occupant is NodeOccupant.Settlement { Relation: SettlementRelation.PlayerOwned }, "节点占据者", "玩家聚居地", node.Occupant);
            Require(train!.Position is TrainPosition.Node at && at.Id == Node, "列车位置", $"节点 {Node.Value}", train.Position);
            var activity = Profile == "busy-combat" ? TrainActivity.Combat : TrainActivity.Servicing;
            Require(train.Activity == activity, "列车活动", activity, train.Activity);
            if (Profile == "busy-combat") Require(view.World.Combats.Any(combat => combat.Location is WorldLocation.Node at && at.Id == Node),
                "节点战斗", "存在", "不存在");
        }
    }
}
