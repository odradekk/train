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

    internal static (WorldState World, ImmutableArray<CoreEvent> Events) Advance(WorldState world, CoreContentProjection content)
    {
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        var trains = world.Trains.Select(train => Step(world, content, train, events)).ToImmutableArray();
        return (world with { Trains = trains }, events.ToImmutable());
    }

    private static TrainState Step(WorldState world, CoreContentProjection content, TrainState train,
        ImmutableArray<CoreEvent>.Builder events)
    {
        if (train.Activity == TrainActivity.Stopped) return train;
        if (train.Activity == TrainActivity.Servicing)
        {
            train = train with { RemainingDwellSteps = train.RemainingDwellSteps - 1 };
            if (train.RemainingDwellSteps > 0) return train;
            train = Consume(train);
            return ResolveNode(world, content, train, events);
        }
        if (train.Position is TrainPosition.Node node)
        {
            train = ResolveNode(world, content, train, events);
            if (train.Activity != TrainActivity.Running || train.Transit!.NextEdge is not { } edgeId) return train;
            var edge = world.Edges.Single(e => e.Id == edgeId);
            var direction = node.Id == edge.Start ? TravelDirection.Forward : TravelDirection.Reverse;
            var facing = train.Transit.EnteredFrom == edgeId
                ? train.OrderFacing == TrainOrderFacing.FirstEndLeading
                    ? TrainOrderFacing.LastEndLeading : TrainOrderFacing.FirstEndLeading
                : train.OrderFacing;
            train = train with
            {
                Position = new TrainPosition.Edge(edgeId, TrainRouting.FindTrack(edge)!.Value, direction,
                    direction == TravelDirection.Forward ? 0 : edge.LengthMeters),
                Transit = null, OrderFacing = facing
            };
        }
        var position = (TrainPosition.Edge)train.Position;
        var current = world.Edges.Single(e => e.Id == position.Id);
        var forward = position.Direction == TravelDirection.Forward ? current.End : current.Start;
        var remaining = position.Direction == TravelDirection.Forward
            ? current.LengthMeters - position.DistanceMeters : position.DistanceMeters;
        var capabilities = Capabilities(world, content, train);
        var stopAfterEndpoint = train.StopRequested ? 0 : TrainRouting.PredictStopDistance(world.Nodes, world.Edges,
            forward, train.Plan!, train.NextStopIndex!.Value, capabilities.MaximumSpeedMetersPerSecond);
        var motion = TrainMotion.Advance(remaining, stopAfterEndpoint, train.SpeedMetersPerSecond,
            capabilities.Acceleration, Rule(world, content).BrakingDeceleration, capabilities.MaximumSpeedMetersPerSecond);
        train = train with { SpeedMetersPerSecond = motion.SpeedMetersPerSecond };
        if (!motion.Arrived)
            return train with { Position = position with { DistanceMeters = position.DistanceMeters
                + (position.Direction == TravelDirection.Forward ? motion.DistanceMeters : -motion.DistanceMeters) } };
        train = train with { Position = new TrainPosition.Node(forward), Transit = new(position.Id, null) };
        events.Add(new CoreEvent.TrainArrived(train.Id, forward));
        return ResolveNode(world, content, train, events, !train.StopRequested);
    }

    internal static TrainState ResolveNode(WorldState world, CoreContentProjection content, TrainState train,
        ImmutableArray<CoreEvent>.Builder events, bool running = true)
    {
        var node = ((TrainPosition.Node)train.Position).Id;
        while (train.NextStopIndex is { } index && Target(train.Plan!.Stops[index]) == node)
        {
            if (train.Plan.Stops[index] is PlanStop.Station station)
            {
                train = train with { SpeedMetersPerSecond = 0 };
                if (train.RemainingDwellSteps > 0 || station.DwellSteps > 0)
                {
                    train = train with { RemainingDwellSteps = train.RemainingDwellSteps > 0
                        ? train.RemainingDwellSteps : station.DwellSteps, Transit = train.Transit! with { NextEdge = null }, StopRequested = false };
                    return ChangeActivity(train, running ? TrainActivity.Servicing : TrainActivity.Stopped, events);
                }
            }
            train = Consume(train);
        }
        train = train with { StopRequested = false, Transit = train.Transit! with { NextEdge = null } };
        if (!running || train.NextStopIndex is null)
            return ChangeActivity(train with { SpeedMetersPerSecond = 0 }, TrainActivity.Stopped, events);
        var capabilities = Capabilities(world, content, train);
        if (train.SpeedMetersPerSecond == 0 && capabilities.DepartureProblem(train.Id,
            Rule(world, content).MinimumAcceleration) is { } problem)
            return ChangeActivity(train, TrainActivity.Waiting, events, [problem]);
        var target = Target(train.Plan!.Stops[train.NextStopIndex.Value]);
        var route = TrainRouting.FindRoute(world.Nodes, world.Edges, node, target, capabilities.MaximumSpeedMetersPerSecond);
        if (route is null)
            return ChangeActivity(train with { SpeedMetersPerSecond = 0 }, TrainActivity.NoRoute, events, [NoRoute(train)]);
        return ChangeActivity(train with { Transit = train.Transit with { NextEdge = route.Value.Edges[0] } },
            TrainActivity.Running, events);
    }

    private static TrainState Consume(TrainState train)
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
