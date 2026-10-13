using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Railway;

internal static class TrainOperations
{
    internal static TrainCapabilities Capabilities(WorldState world, CoreContentProjection content, TrainState train)
        => TrainCapabilities.Calculate(train.Vehicles.Select(id =>
        {
            var instance = world.Instances.Single(i => i.Id == new InstanceId.Vehicle(id));
            return (world.Designs.Single(d => d.Design == instance.Design),
                instance.Parts.Select(p => (p.Id, p.Current)).ToImmutableArray());
        }).ToImmutableArray(), world.Inventories.Single(i => i.Id == train.Inventory).Goods,
            content.Goods.AddRange(world.DesignGoods.Where(g => g.Good is GoodId.Assembly)));

    internal static RailwayRuleProjection Rule(WorldState world, CoreContentProjection content)
        => content.RailwayRules.Single(r => r.Id == world.Source.Configuration.Rules.Railway);

    internal static NodeId Target(PlanStop stop) => stop switch
    {
        PlanStop.Waypoint waypoint => waypoint.Node,
        PlanStop.Station station => station.Node,
        _ => throw new InvalidOperationException("停靠点种类无效。")
    };

    internal static TrainView Read(WorldState world, CoreContentProjection content, TrainState train)
    {
        var capabilities = Capabilities(world, content, train);
        var blockers = ImmutableArray.CreateBuilder<CoreProblem>();
        var problem = capabilities.DepartureProblem(train.Id, Rule(world, content).MinimumAcceleration);
        if (problem is not null) blockers.Add(problem);
        if (capabilities.MissingProblem(train.Id) is { } missing && problem?.Code != missing.Code)
            blockers.Add(missing);
        if (train.Activity == TrainActivity.NoRoute) blockers.Add(NoRoute(train));
        if (train.Activity == TrainActivity.Waiting && train.QueueEnteredStep is not null)
        {
            if (train.NextStopIndex is { } index && train.Plan!.Stops[index] is PlanStop.Station station
                && train.Position is TrainPosition.Node node && station.Node == node.Id)
                blockers.Add(TrainDispatch.PlatformWait(train));
            else if (TrainDispatch.RequestedEdge(world, content, train) is { } edge)
                blockers.Add(TrainDispatch.TrackWait(train, edge.Id));
        }
        var armed = train.Vehicles.Any(id =>
        {
            var instance = world.Instances.Single(i => i.Id == new InstanceId.Vehicle(id));
            return world.Designs.Single(d => d.Design == instance.Design).Parts.Any(p => !p.Missing
                && Math.Min(p.MaxDurability, instance.Parts.Single(d => d.Id == p.Id).Current) > 0
                && p.Capabilities.Any(c => c is PartCapability.Weapon));
        });
        return new(train.Id, train.Position, train.Activity, train.Vehicles, train.Inventory,
            train.SpeedMetersPerSecond, capabilities.LengthMeters, capabilities.MassTons,
            capabilities.TractionKilonewtons, capabilities.Acceleration, capabilities.MaximumSpeedMetersPerSecond,
            train.Plan, train.NextStopIndex, train.RemainingDwellSteps,
            new(TargetPriority.ArmedFirst, TargetScope.ArmedOnly, armed ? EncounterAction.Engage : EncounterAction.Retreat, 0),
            blockers.ToImmutable(), null, [], train.Transit, train.OrderFacing);
    }

    internal static (WorldState World, ImmutableArray<CoreEvent> Events) Advance(WorldState world, CoreContentProjection content, ulong step)
    {
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        var dispatch = new TrainDispatch(world, content);
        var trains = world.Trains.ToDictionary(t => t.Id);
        var arrivals = new List<TrainId>();
        foreach (var train in world.Trains.OrderBy(t => t.Id.Value))
        {
            if (train.Activity == TrainActivity.Servicing)
            {
                var serviced = train with { RemainingDwellSteps = train.RemainingDwellSteps - 1 };
                if (serviced.RemainingDwellSteps == 0)
                { serviced = Consume(serviced); arrivals.Add(train.Id); }
                trains[train.Id] = serviced;
            }
            else if (train.Position is TrainPosition.Edge)
            {
                trains[train.Id] = Move(world, content, train, dispatch, events);
                if (trains[train.Id].Position is TrainPosition.Node) arrivals.Add(train.Id);
            }
        }
        foreach (var original in world.Trains.Where(t => t.Position is TrainPosition.Node
                     && t.Activity is not (TrainActivity.Stopped or TrainActivity.Servicing))
                     .OrderBy(t => t.QueueEnteredStep ?? step).ThenBy(t => t.Id.Value))
        {
            var train = ResolveNode(world, content, original, events, dispatch, step);
            if (train.Activity == TrainActivity.Running && train.Transit!.NextEdge is { } edgeId)
            {
                var edge = world.Edges.Single(e => e.Id == edgeId);
                if (dispatch.Enter(train, edge, true) is { } track)
                {
                    var direction = TrainDispatch.Direction(edge, ((TrainPosition.Node)train.Position).Id);
                    train = train with
                    {
                        Position = new TrainPosition.Edge(edgeId, track, direction,
                            direction == TravelDirection.Forward ? 0 : edge.LengthMeters),
                        OrderFacing = train.Transit.EnteredFrom == edgeId ? OppositeFacing(train.OrderFacing) : train.OrderFacing,
                        Transit = null, QueueEnteredStep = null
                    };
                    train = Move(world, content, train, dispatch, events);
                    if (train.Position is TrainPosition.Node) arrivals.Add(train.Id);
                }
            }
            trains[train.Id] = train;
        }
        foreach (var id in arrivals.OrderBy(id => id.Value))
            trains[id] = ResolveNode(world, content, trains[id], events, dispatch, checked(step + 1), !trains[id].StopRequested);
        return (dispatch.Finish(world with { Trains = world.Trains.Select(t => trains[t.Id]).ToImmutableArray() }), events.ToImmutable());
    }

    private static TrainState Move(WorldState world, CoreContentProjection content, TrainState train,
        TrainDispatch dispatch, ImmutableArray<CoreEvent>.Builder events)
    {
        var position = (TrainPosition.Edge)train.Position;
        var edge = world.Edges.Single(e => e.Id == position.Id);
        var forward = position.Direction == TravelDirection.Forward ? edge.End : edge.Start;
        var remaining = position.Direction == TravelDirection.Forward
            ? edge.LengthMeters - position.DistanceMeters : position.DistanceMeters;
        var capabilities = dispatch.Capabilities(train);
        var stopAfterEndpoint = train.StopRequested ? 0 : TrainRouting.PredictStopDistance(world.Nodes, world.Edges,
            forward, train.Plan!, train.NextStopIndex!.Value, capabilities.MaximumSpeedMetersPerSecond);
        var limit = dispatch.Limit(train);
        var distance = Math.Abs(limit.Position - position.DistanceMeters);
        var motion = TrainMotion.Advance(limit.Endpoint ? remaining : distance, limit.Endpoint ? stopAfterEndpoint : 0,
            train.SpeedMetersPerSecond, capabilities.Acceleration, Rule(world, content).BrakingDeceleration,
            capabilities.MaximumSpeedMetersPerSecond);
        if (!limit.Endpoint || !motion.Arrived)
        {
            var moved = position.DistanceMeters + (position.Direction == TravelDirection.Forward ? motion.DistanceMeters : -motion.DistanceMeters);
            return train with { SpeedMetersPerSecond = motion.SpeedMetersPerSecond, Position = position with
            { DistanceMeters = position.Direction == TravelDirection.Forward ? Math.Min(moved, limit.Position) : Math.Max(moved, limit.Position) } };
        }
        events.Add(new CoreEvent.TrainArrived(train.Id, forward));
        return train with { SpeedMetersPerSecond = motion.SpeedMetersPerSecond,
            Position = new TrainPosition.Node(forward), Transit = new(position.Id, null), ReturningFromBreak = null };
    }

    private static TrainOrderFacing OppositeFacing(TrainOrderFacing facing) => facing == TrainOrderFacing.FirstEndLeading
        ? TrainOrderFacing.LastEndLeading : TrainOrderFacing.FirstEndLeading;

    internal static TrainState ResolveNode(WorldState world, CoreContentProjection content, TrainState train,
        ImmutableArray<CoreEvent>.Builder events, TrainDispatch dispatch, ulong step, bool running = true)
    {
        var node = ((TrainPosition.Node)train.Position).Id;
        while (train.NextStopIndex is { } index && Target(train.Plan!.Stops[index]) == node)
        {
            if (train.Plan.Stops[index] is PlanStop.Station station)
            {
                train = train with { SpeedMetersPerSecond = 0 };
                if (running && !dispatch.TakePlatform(train, step))
                    return Wait(train, step, TrainDispatch.PlatformWait(train), events);
                if (train.RemainingDwellSteps > 0 || station.DwellSteps > 0)
                {
                    train = train with { RemainingDwellSteps = train.RemainingDwellSteps > 0
                        ? train.RemainingDwellSteps : station.DwellSteps, Transit = train.Transit! with { NextEdge = null }, StopRequested = false, QueueEnteredStep = null };
                    return ChangeActivity(train, running ? TrainActivity.Servicing : TrainActivity.Stopped, events);
                }
            }
            train = Consume(train);
        }
        train = train with { StopRequested = false, Transit = train.Transit! with { NextEdge = null } };
        if (!running || train.NextStopIndex is null)
            return ChangeActivity(train with { SpeedMetersPerSecond = 0, QueueEnteredStep = null }, TrainActivity.Stopped, events);
        var capabilities = dispatch.Capabilities(train);
        if (train.SpeedMetersPerSecond == 0 && capabilities.DepartureProblem(train.Id,
            Rule(world, content).MinimumAcceleration) is { } problem)
            return ChangeActivity(train with { QueueEnteredStep = null, Transit = train.Transit! with { NextEdge = null } }, TrainActivity.Waiting, events, [problem]);
        var target = Target(train.Plan!.Stops[train.NextStopIndex.Value]);
        var route = TrainRouting.FindRoute(world.Nodes, world.Edges, node, target, capabilities.MaximumSpeedMetersPerSecond);
        if (route is null)
            return ChangeActivity(train with { SpeedMetersPerSecond = 0, QueueEnteredStep = null }, TrainActivity.NoRoute, events, [NoRoute(train)]);
        var edge = world.Edges.Single(e => e.Id == route.Value.Edges[0]);
        if (dispatch.Enter(train, edge, false) is null)
            return Wait(train, step, TrainDispatch.TrackWait(train, edge.Id), events);
        return ChangeActivity(train with { Transit = train.Transit with { NextEdge = edge.Id }, QueueEnteredStep = null },
            TrainActivity.Running, events);
    }

    private static TrainState Wait(TrainState train, ulong step, CoreProblem reason, ImmutableArray<CoreEvent>.Builder events)
        => ChangeActivity(train with { SpeedMetersPerSecond = 0, StopRequested = false,
            Transit = train.Transit! with { NextEdge = null }, QueueEnteredStep = train.QueueEnteredStep ?? step },
            TrainActivity.Waiting, events, [reason]);

    internal static TrainState Consume(TrainState train)
    {
        var next = train.NextStopIndex!.Value + 1;
        return train with { NextStopIndex = next < train.Plan!.Stops.Length ? next : train.Plan.Loop ? 0 : null };
    }

    internal static TrainState ChangeActivity(TrainState train, TrainActivity activity,
        ImmutableArray<CoreEvent>.Builder events, ImmutableArray<CoreProblem> reasons = default)
    {
        if (train.Activity != activity)
            events.Add(new CoreEvent.TrainActivityChanged(train.Id, train.Activity, activity, train.Position,
                reasons.IsDefault ? [] : reasons));
        return train with { Activity = activity };
    }

    private static CoreProblem NoRoute(TrainState train) => new("no_route", "Train.Plan", new WorldObjectId.Train(train.Id),
        train.NextStopIndex is { } index ? [Target(train.Plan!.Stops[index]).Value.ToString(System.Globalization.CultureInfo.InvariantCulture)] : []);
}
