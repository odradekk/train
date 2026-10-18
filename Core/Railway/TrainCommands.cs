using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Trading;
using Train.Core.World;
using Train.Core.Construction;

namespace Train.Core.Railway;

internal static class TrainCommands
{
    internal static void Validate(CoreCommand command)
    {
        switch (command)
        {
            case CoreCommand.DebugAddRailBreak broken:
                if (broken.Edge.Value == 0 || broken.Track < 1 || !double.IsFinite(broken.DistanceMeters) || broken.DistanceMeters < 0)
                    throw new ArgumentException("断点须提供非零线路编号、正轨号及有限非负距离。", nameof(command));
                return;
            case CoreCommand.FormTrain form:
                if (form.OrderedVehicles.IsDefault || form.OrderedVehicles.Any(v => v.Value == 0))
                    throw new ArgumentException("车辆数组必须初始化且编号非零。", nameof(command));
                return;
            case CoreCommand.ReplaceRunPlan replace:
                ArgumentNullException.ThrowIfNull(replace.Plan);
                if (replace.Plan.Stops.IsDefault || replace.Plan.Stops.Any(s => s is null))
                    throw new ArgumentException("停靠点数组必须初始化。", nameof(command));
                foreach (var stop in replace.Plan.Stops)
                {
                    if (TrainOperations.Target(stop).Value == 0)
                        throw new ArgumentException("节点编号必须非零。", nameof(command));
                    if (stop is not PlanStop.Station station) continue;
                    if (station.GoodsRules.IsDefault) throw new ArgumentException("货物规则数组必须初始化。", nameof(command));
                    foreach (var rule in station.GoodsRules) AutomaticGoodsOperations.Validate(rule);
                }
                TrainId(replace.Train);
                return;
            case CoreCommand.DisbandTrain disband: TrainId(disband.Train); return;
            case CoreCommand.StartTrain start: TrainId(start.Train); return;
            case CoreCommand.StopTrain stop: TrainId(stop.Train); return;
            default: throw new NotImplementedException($"{command.GetType().Name} 尚未实现。");
        }
        static void TrainId(TrainId id)
        {
            if (id.Value == 0) throw new ArgumentException("列车编号必须非零。", nameof(command));
        }
    }

    internal static CommandEffect Execute(WorldState world, CoreContentProjection content, CoreCommand command, SimulationPosition boundary)
    {
        var step = boundary.CompletedSteps;
        if (command is CoreCommand.DebugAddRailBreak broken)
        {
            var edge = world.Edges.FirstOrDefault(e => e.Id == broken.Edge);
            if (edge is null) return Reject("object_missing", "Edge", new WorldObjectId.Edge(broken.Edge));
            if (broken.Track > edge.Tracks || broken.DistanceMeters > edge.LengthMeters)
                return Reject("invalid_state", "Break", new WorldObjectId.Edge(broken.Edge));
            if (world.Next.Break == ulong.MaxValue) return Reject("identity_exhausted", "World.Next.Break");
            var item = new TrackBreakView(new(world.Next.Break), broken.Track, broken.DistanceMeters);
            return new CommandEffect.Accepted(content, [new CoreEvent.RailBroken(edge.Id, item)], world with
            {
                Edges = world.Edges.Select(e => e.Id == edge.Id ? e with { Breaks = e.Breaks.Add(item) } : e).ToImmutableArray(),
                TrackHandoffs = world.TrackHandoffs.Where(h => h.Edge != edge.Id || h.Track != item.Track).ToImmutableArray(),
                Next = world.Next with { Break = world.Next.Break + 1 }
            });
        }
        if (command is CoreCommand.FormTrain form) return Form(world, content, form);
        var id = command switch
        {
            CoreCommand.DisbandTrain c => c.Train,
            CoreCommand.ReplaceRunPlan c => c.Train,
            CoreCommand.StartTrain c => c.Train,
            CoreCommand.StopTrain c => c.Train,
            _ => throw new InvalidOperationException("铁路命令种类无效。")
        };
        var subject = new WorldObjectId.Train(id);
        var train = world.Trains.FirstOrDefault(t => t.Id == id);
        if (train is null) return Reject("object_missing", "Train", subject);
        if (command is CoreCommand.DisbandTrain) return Disband(world, content, train, boundary);
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        switch (command)
        {
            case CoreCommand.ReplaceRunPlan replace:
                if (replace.Plan.Stops.IsEmpty) return Reject("plan_empty", "Plan.Stops", subject);
                for (var index = 0; index < replace.Plan.Stops.Length; index++)
                {
                    var target = TrainOperations.Target(replace.Plan.Stops[index]);
                    if (!world.Nodes.Any(n => n.Id == target))
                        return Reject("object_missing", $"Plan.Stops[{index}].Node", new WorldObjectId.Node(target));
                    if (replace.Plan.Stops[index] is PlanStop.Station station)
                    {
                        var goods = new HashSet<GoodId>();
                        for (var ruleIndex = 0; ruleIndex < station.GoodsRules.Length; ruleIndex++)
                            if (!goods.Add(station.GoodsRules[ruleIndex].Good))
                                return Reject("plan_duplicate_good", $"Plan.Stops[{index}].GoodsRules[{ruleIndex}].Good", subject);
                    }
                }
                if (replace.Plan.Loop && replace.Plan.Stops.Select(TrainOperations.Target).Distinct().Count() == 1)
                    return Reject("plan_degenerate", "Plan.Stops", subject);
                train = RunReports.End(ref world, content, train, boundary, RunEndReason.PlanReplaced, events);
                train = train with { Plan = replace.Plan, NextStopIndex = 0, RemainingDwellSteps = 0,
                    AutomaticEvaluationRemainingSteps = null, CurrentRun = RunReports.Begin(boundary), QueueEnteredStep = null };
                if (train.Position is TrainPosition.Node { Phase: NodeTravelPhase.Departing } departing)
                {
                    while (train.NextStopIndex is { } index && replace.Plan.Stops[index] is PlanStop.Waypoint waypoint
                        && waypoint.Node == departing.Id)
                        train = TrainOperations.ConsumeStop(ref world, content, train, boundary, events);
                    if (train.NextStopIndex is null)
                        train = train.SpeedMetersPerSecond > 0 ? train with { StopRequested = true }
                            : TrainOperations.ChangeActivity(train with { StopRequested = false }, TrainActivity.Stopped, events);
                }
                if (train.Position is TrainPosition.Node && train.Activity != TrainActivity.Stopped)
                    train = TrainOperations.ResolveNode(ref world, content, train, events, new TrainDispatch(world, content), step, boundary);
                break;
            case CoreCommand.StartTrain:
                if (ConstructionJobs.TrainLocked(world, train)) return Reject("instance_locked", "Train", subject);
                if (train.Plan is null) return Reject("plan_missing", "Train.Plan", subject);
                if (train.NextStopIndex is null) return Reject("plan_completed", "Train.NextStopIndex", subject);
                if (train.Activity != TrainActivity.Stopped)
                {
                    train = train with { StopRequested = false };
                    break;
                }
                if (TrainOperations.Capabilities(world, content, train).DepartureProblem(id,
                    TrainOperations.Rule(world, content).MinimumAcceleration) is { } problem)
                    return new CommandEffect.Rejected([problem]);
                train = TrainOperations.ResolveNode(ref world, content, train with { StopRequested = false }, events, new TrainDispatch(world, content), step, boundary);
                break;
            case CoreCommand.StopTrain:
                if (train.SpeedMetersPerSecond > 0 || train.Position is TrainPosition.Edge) train = train with { StopRequested = true };
                else train = TrainOperations.ChangeActivity(train with { SpeedMetersPerSecond = 0,
                    StopRequested = false, QueueEnteredStep = null }, TrainActivity.Stopped, events);
                break;
        }
        return new CommandEffect.Accepted(content, events.ToImmutable(), world with
        { Trains = world.Trains.Select(t => t.Id == id ? train : t).ToImmutableArray() });
    }

    private static CommandEffect Form(WorldState world, CoreContentProjection content, CoreCommand.FormTrain command)
    {
        if (command.OrderedVehicles.IsEmpty) return Reject("composition_empty", "OrderedVehicles");
        if (command.OrderedVehicles.Distinct().Count() != command.OrderedVehicles.Length)
            return Reject("vehicle_duplicate", "OrderedVehicles");
        SettlementState? settlement = null;
        foreach (var id in command.OrderedVehicles)
        {
            var subject = new WorldObjectId.Instance(new InstanceId.Vehicle(id));
            var instance = world.Instances.FirstOrDefault(i => i.Id == new InstanceId.Vehicle(id));
            if (instance is null) return Reject("object_missing", "OrderedVehicles", subject);
            if (instance.Ownership != SettlementRelation.PlayerOwned) return Reject("not_owned", "OrderedVehicles", subject);
            if (ConstructionJobs.Lock(world, instance.Id) == InstanceLock.UnderDeconstruction)
                return Reject("instance_locked", "OrderedVehicles", subject);
            if (instance.Details is not InstanceDetails.Vehicle { Train: null }) return Reject("vehicle_in_train", "OrderedVehicles", subject);
            var parked = world.Settlements.Single(s => s.ParkedVehicles.Contains(id));
            if (settlement is not null && settlement.Id != parked.Id) return Reject("vehicles_not_colocated", "OrderedVehicles", subject);
            settlement = parked;
        }
        if (settlement!.Relation == SettlementRelation.Hostile)
            return Reject("access_denied", "OrderedVehicles", new WorldObjectId.Settlement(settlement.Id));
        if (world.Next.Train == ulong.MaxValue || world.Next.Inventory == ulong.MaxValue)
            return Reject("identity_exhausted", "World.Next");
        var train = new TrainId(world.Next.Train);
        var inventory = new InventoryId(world.Next.Inventory);
        var added = new TrainState(train, new TrainPosition.Node(settlement.Node, null, null, NodeTravelPhase.Waiting,
            TrainOperations.Rule(world, content).ApproachLengthMeters, null), command.OrderedVehicles, inventory,
            TrainOrderFacing.FirstEndLeading, TrainActivity.Stopped, 0, null, null, 0, false, null, null);
        return new CommandEffect.Accepted(content, [new CoreEvent.TrainCompositionChanged(train, command.OrderedVehicles)], world with
        {
            Trains = world.Trains.Add(added), Inventories = world.Inventories.Add(new(inventory, new InventoryOwner.Train(train), [], [])),
            Instances = world.Instances.Select(i => i.Id is InstanceId.Vehicle v && command.OrderedVehicles.Contains(v.Id)
                ? i with { Details = ((InstanceDetails.Vehicle)i.Details) with { Train = train } } : i).ToImmutableArray(),
            Settlements = world.Settlements.Select(s => s.Id == settlement.Id ? s with
                { ParkedVehicles = s.ParkedVehicles.Except(command.OrderedVehicles).ToImmutableArray() } : s).ToImmutableArray(),
            Next = world.Next with { Train = world.Next.Train + 1, Inventory = world.Next.Inventory + 1 }
        }, [new WorldObjectId.Train(train), new WorldObjectId.Inventory(inventory)]);
    }

    private static CommandEffect Disband(WorldState world, CoreContentProjection content, TrainState train, SimulationPosition boundary)
    {
        var subject = new WorldObjectId.Train(train.Id);
        if (train.Position is not TrainPosition.Node node || train.SpeedMetersPerSecond != 0)
            return Reject("train_not_stationary_at_node", "Train.Position", subject);
        var settlement = world.Settlements.FirstOrDefault(s => s.Node == node.Id);
        if (settlement is null) return Reject("settlement_unavailable", "Train.Position", subject);
        if (settlement.Relation == SettlementRelation.Hostile) return Reject("access_denied", "Train.Position", subject);
        var inventory = world.Inventories.Single(i => i.Id == train.Inventory);
        if (!inventory.Goods.IsEmpty || !inventory.Reservations.IsEmpty)
            return Reject("inventory_not_empty", "Train.Inventory", subject);
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        train = RunReports.End(ref world, content, train, boundary, RunEndReason.TrainDisbanded, events);
        events.Add(new CoreEvent.TrainCompositionChanged(train.Id, []));
        return new CommandEffect.Accepted(content, events.ToImmutable(), world with
        {
            Trains = world.Trains.Where(t => t.Id != train.Id).ToImmutableArray(), Inventories = world.Inventories.Where(i => i.Id != train.Inventory).ToImmutableArray(),
            Instances = world.Instances.Select(i => i.Id is InstanceId.Vehicle v && train.Vehicles.Contains(v.Id)
                ? i with { Details = ((InstanceDetails.Vehicle)i.Details) with { Train = null } } : i).ToImmutableArray(),
            Settlements = world.Settlements.Select(s => s.Id == settlement.Id ? s with
                { ParkedVehicles = s.ParkedVehicles.AddRange(train.Vehicles).OrderBy(v => v.Value).ToImmutableArray() } : s).ToImmutableArray()
        });
    }

    private static CommandEffect.Rejected Reject(string code, string field, WorldObjectId? subject = null)
        => new([new(code, field, subject, [])]);
}
