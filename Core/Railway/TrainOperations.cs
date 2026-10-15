using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.World;
using Train.Core.Trading;

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

    internal static PlanStop.Station? StationAt(TrainState train, NodeId node)
    {
        for (var count = 0; count < (train.Plan?.Stops.Length ?? 0) && train.NextStopIndex is { } index
             && Target(train.Plan!.Stops[index]) == node; count++)
        {
            if (train.Plan.Stops[index] is PlanStop.Station station) return station;
            train = Consume(train);
        }
        return null;
    }

    private static TrainState AfterLocalStops(TrainState train, NodeId node)
    {
        for (var count = 0; count < (train.Plan?.Stops.Length ?? 0) && train.NextStopIndex is { } index
             && Target(train.Plan!.Stops[index]) == node; count++) train = Consume(train);
        return train;
    }

    private static EdgeId? Departure(WorldState world, CoreContentProjection content, TrainState train, NodeId node)
    {
        train = AfterLocalStops(train, node);
        if (train.NextStopIndex is not { } index) return null;
        var route = TrainRouting.FindRoute(world.Nodes, world.Edges, node, Target(train.Plan!.Stops[index]),
            Capabilities(world, content, train).MaximumSpeedMetersPerSecond);
        return route is { } found && !found.Edges.IsEmpty ? found.Edges[0] : null;
    }

    private static bool MustStop(TrainState train, TrainPosition.Node position)
        => train.StopRequested || StationAt(train, position.Id) is not null
            || AfterLocalStops(train, position.Id).NextStopIndex is null
            || position.NextEdge is null || position.NextEdge == position.EnteredFrom;

    internal static TrainView Read(WorldState world, CoreContentProjection content, TrainState train)
    {
        var capabilities = Capabilities(world, content, train);
        var blockers = ImmutableArray.CreateBuilder<CoreProblem>();
        var problem = capabilities.DepartureProblem(train.Id, Rule(world, content).MinimumAcceleration);
        if (problem is not null) blockers.Add(problem);
        if (capabilities.MissingProblem(train.Id) is { } missing && problem?.Code != missing.Code) blockers.Add(missing);
        if (train.Activity == TrainActivity.NoRoute) blockers.Add(NoRoute(train));
        if (train.QueueEnteredStep is not null && train.Position is TrainPosition.Node position)
        {
            if (position.Phase != NodeTravelPhase.Departing && TrainDispatch.NeedsPlatform(world, train)
                && position.PlatformNumber is null) blockers.Add(TrainDispatch.PlatformWait(train));
            else if (position.Phase == NodeTravelPhase.Departing && position.NextEdge is { } edge)
                blockers.Add(TrainDispatch.TrackWait(train, edge));
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
            blockers.ToImmutable(), train.CurrentRun is { } run ? RunReports.Read(run) : null,
            world.RecentRunReports.Where(r => r.Train == train.Id).ToImmutableArray(), train.OrderFacing);
    }

    internal static (WorldState World, ImmutableArray<CoreEvent> Events) Advance(WorldState world, CoreContentProjection content, ulong step)
    {
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        var dispatch = new TrainDispatch(world, content);
        var trains = world.Trains.ToDictionary(t => t.Id);
        foreach (var original in world.Trains.OrderBy(t => t.QueueEnteredStep ?? step).ThenBy(t => t.Id.Value))
        {
            var train = original;
            if (train.Activity == TrainActivity.Stopped) continue;
            if (train.Activity == TrainActivity.Servicing)
            {
                train = train with { RemainingDwellSteps = train.RemainingDwellSteps - 1 };
                if (train.RemainingDwellSteps == 0)
                {
                    var boundary = new SimulationPosition(checked(step + 1), 0);
                    train = ConsumeStop(ref world, content, train, boundary, events);
                    train = ResolveNode(ref world, content, train, events, dispatch, boundary.CompletedSteps, boundary);
                }
                else
                {
                    train = AutomaticGoodsOperations.Advance(ref world, content, train, checked(step + 1), true, events);
                    dispatch.RefreshCapabilities(world, train);
                }
            }
            else if (train.Position is TrainPosition.Edge)
                train = MoveEdge(ref world, content, train, dispatch, events, checked(step + 1));
            else
            {
                // Queue age uses the start of the step; reports belong to its committed boundary.
                train = ResolveNode(ref world, content, train, events, dispatch, step, new(checked(step + 1), 0));
                if (train.Activity == TrainActivity.Running)
                    train = MoveNode(ref world, content, train, dispatch, events, checked(step + 1));
            }
            trains[train.Id] = train;
            world = world with { Trains = world.Trains.Select(t => t.Id == train.Id ? train : t).ToImmutableArray() };
        }

        var exits = trains.Values.Where(t => t.Position is TrainPosition.Node
                { Phase: NodeTravelPhase.Departing, NextEdge: not null } p
            && t.Activity is TrainActivity.Running or TrainActivity.Waiting
            && p.DistanceMeters >= NodeDimensions.Read(world, content, p.Id).Length
            && (t.SpeedMetersPerSecond > 0 || dispatch.Capabilities(t).DepartureProblem(t.Id,
                Rule(world, content).MinimumAcceleration) is null)).ToArray();
        dispatch.RegisterExits(exits);
        foreach (var train in exits.OrderBy(t => t.QueueEnteredStep ?? checked(step + 1)).ThenBy(t => t.Id.Value))
        {
            var position = (TrainPosition.Node)train.Position;
            var edge = world.Edges.Single(e => e.Id == position.NextEdge);
            if (dispatch.Enter(train, edge, true) is { } track)
            {
                var direction = TrainDispatch.Direction(edge, position.Id);
                trains[train.Id] = ChangeActivity(RunReports.EnteredEdge(world, content, train, edge, checked(step + 1)) with
                {
                    Position = new TrainPosition.Edge(edge.Id, track, direction,
                        direction == TravelDirection.Forward ? 0 : edge.LengthMeters), QueueEnteredStep = null
                }, TrainActivity.Running, events);
            }
            else if (train.NextStopIndex is null && train.StopRequested)
                trains[train.Id] = ChangeActivity(train with { SpeedMetersPerSecond = 0,
                    StopRequested = false, QueueEnteredStep = null }, TrainActivity.Stopped, events);
            else trains[train.Id] = Wait(train, checked(step + 1), TrainDispatch.TrackWait(train, edge.Id), events);
        }
        return (dispatch.Finish(world with { Trains = world.Trains.Select(t => trains[t.Id]).ToImmutableArray() }), events.ToImmutable());
    }

    private static TrainState MoveEdge(ref WorldState world, CoreContentProjection content, TrainState train,
        TrainDispatch dispatch, ImmutableArray<CoreEvent>.Builder events, ulong step)
    {
        var position = (TrainPosition.Edge)train.Position;
        var edge = world.Edges.Single(e => e.Id == position.Id);
        var forward = position.Direction == TravelDirection.Forward ? edge.End : edge.Start;
        var remaining = position.Direction == TravelDirection.Forward ? edge.LengthMeters - position.DistanceMeters : position.DistanceMeters;
        var capabilities = dispatch.Capabilities(train);
        var rule = Rule(world, content);
        var stopAfterEndpoint = PredictStopDistance(world, content, train, forward, edge.Id, dispatch, step);
        // A nonzero boundary speed is the same braking parabola as a virtual stop beyond the boundary.
        var target = Math.Min(stopAfterEndpoint ?? double.PositiveInfinity,
            Math.Pow(Math.Min(capabilities.MaximumSpeedMetersPerSecond, rule.NodeSpeedLimitMetersPerSecond), 2)
                / (2 * rule.BrakingDeceleration));
        var limit = dispatch.Limit(train);
        var distance = Math.Abs(limit.Position - position.DistanceMeters);
        var motion = TrainMotion.Advance(limit.Endpoint ? remaining : distance, limit.Endpoint ? target : 0,
            train.SpeedMetersPerSecond, capabilities.Acceleration, rule.BrakingDeceleration, capabilities.MaximumSpeedMetersPerSecond);
        if (!limit.Endpoint || !motion.Arrived)
        {
            var moved = position.DistanceMeters + (position.Direction == TravelDirection.Forward ? motion.DistanceMeters : -motion.DistanceMeters);
            return train with { SpeedMetersPerSecond = motion.SpeedMetersPerSecond, Position = position with
            { DistanceMeters = position.Direction == TravelDirection.Forward ? Math.Min(moved, limit.Position) : Math.Max(moved, limit.Position) } };
        }
        events.Add(new CoreEvent.TrainArrived(train.Id, forward));
        train = train with { SpeedMetersPerSecond = motion.SpeedMetersPerSecond,
            Position = new TrainPosition.Node(forward, position.Id, null, NodeTravelPhase.Approaching, 0, null), ReturningFromBreak = null };
        return ResolveNode(ref world, content, train, events, dispatch, step, new(step, 0));
    }

    internal static double? PredictStopDistance(WorldState world, CoreContentProjection content, TrainState train,
        NodeId node, EdgeId? enteredFrom, TrainDispatch dispatch, ulong step)
    {
        var distance = 0.0;
        var visited = new HashSet<(NodeId, EdgeId?, int?)>();
        while (visited.Add((node, enteredFrom, train.NextStopIndex)))
        {
            var dimensions = NodeDimensions.Read(world, content, node);
            var position = new TrainPosition.Node(node, enteredFrom, Departure(world, content, train, node),
                NodeTravelPhase.Approaching, 0, null);
            train = train with { Position = position };
            if (MustStop(train, position))
                return distance + (dispatch.PredictPlatform(train, step) ? dimensions.Stop : dimensions.Approach);
            var edge = world.Edges.Single(e => e.Id == position.NextEdge);
            if (dispatch.Enter(train, edge, false) is null) return distance + dimensions.Length;
            distance += dimensions.Length + edge.LengthMeters;
            train = AfterLocalStops(train, node);
            node = edge.Start == node ? edge.End : edge.Start;
            enteredFrom = edge.Id;
        }
        return null;
    }

    private static TrainState MoveNode(ref WorldState world, CoreContentProjection content, TrainState train,
        TrainDispatch dispatch, ImmutableArray<CoreEvent>.Builder events, ulong step)
    {
        var position = (TrainPosition.Node)train.Position;
        var dimensions = NodeDimensions.Read(world, content, position.Id);
        var rule = Rule(world, content);
        var capabilities = dispatch.Capabilities(train);
        var mustStop = position.Phase is not (NodeTravelPhase.Departing or NodeTravelPhase.Docked) && MustStop(train, position);
        var endpoint = mustStop
            ? TrainDispatch.NeedsPlatform(world, train) && position.PlatformNumber is null && position.DistanceMeters <= dimensions.Approach
                ? dimensions.Approach : dimensions.Stop
            : dimensions.Length;
        var target = double.PositiveInfinity;
        if (mustStop) target = 0;
        else if (position.NextEdge is { } next)
        {
            var edge = world.Edges.Single(e => e.Id == next);
            if (dispatch.Enter(train, edge, false) is null) target = 0;
            else
            {
                var after = position.Phase == NodeTravelPhase.Departing ? train : AfterLocalStops(train, position.Id);
                var node = edge.Start == position.Id ? edge.End : edge.Start;
                target = edge.LengthMeters + (PredictStopDistance(world, content, after, node, edge.Id, dispatch, step) ?? double.PositiveInfinity);
            }
        }
        var motion = TrainMotion.Advance(Math.Max(0, endpoint - position.DistanceMeters),
            double.IsPositiveInfinity(target) ? null : target, train.SpeedMetersPerSecond,
            capabilities.Acceleration, rule.BrakingDeceleration, Math.Min(capabilities.MaximumSpeedMetersPerSecond, rule.NodeSpeedLimitMetersPerSecond));
        var moved = Math.Min(endpoint, position.DistanceMeters + motion.DistanceMeters);
        train = train with { SpeedMetersPerSecond = motion.SpeedMetersPerSecond, Position = position with { DistanceMeters = moved } };
        if (mustStop && motion.Arrived)
        {
            var phase = endpoint == dimensions.Stop ? NodeTravelPhase.Docked : NodeTravelPhase.Waiting;
            train = train with { Position = ((TrainPosition.Node)train.Position) with { Phase = phase }, SpeedMetersPerSecond = 0 };
            if (train.StopRequested)
                return ChangeActivity(train with { StopRequested = false, QueueEnteredStep = null }, TrainActivity.Stopped, events);
            return ResolveNode(ref world, content, train, events, dispatch, step, new(step, 0));
        }
        if (position.Phase != NodeTravelPhase.Departing && moved >= dimensions.Stop && motion.DistanceMeters > 0)
        {
            if (position.NextEdge == position.EnteredFrom)
                train = train with { OrderFacing = OppositeFacing(train.OrderFacing) };
            while (train.NextStopIndex is { } index && train.Plan!.Stops[index] is PlanStop.Waypoint waypoint && waypoint.Node == position.Id)
                train = ConsumeStop(ref world, content, train, new(step, 0), events);
            train = train with { Position = ((TrainPosition.Node)train.Position) with { Phase = NodeTravelPhase.Departing } };
        }
        return train;
    }

    internal static TrainState ResolveNode(ref WorldState world, CoreContentProjection content, TrainState train,
        ImmutableArray<CoreEvent>.Builder events, TrainDispatch dispatch, ulong step, SimulationPosition boundary, bool running = true)
    {
        var position = (TrainPosition.Node)train.Position;
        if (position.Phase == NodeTravelPhase.Departing && train.Activity == TrainActivity.Waiting
            && train.QueueEnteredStep is not null)
        {
            if (dispatch.Capabilities(train).DepartureProblem(train.Id, Rule(world, content).MinimumAcceleration) is { } blocked)
                return ChangeActivity(train with { QueueEnteredStep = null }, TrainActivity.Waiting, events, [blocked]);
            return train;
        }
        if (position.Phase != NodeTravelPhase.Departing)
        {
            position = position with { NextEdge = Departure(world, content, train, position.Id) };
            train = train with { Position = position };
            if (TrainDispatch.NeedsPlatform(world, train) && position.PlatformNumber is null)
            {
                if (dispatch.TakePlatform(train, step) is { } platform)
                {
                    position = position with { PlatformNumber = platform };
                    train = train with { Position = position, QueueEnteredStep = null };
                }
                else
                {
                    train = train with { QueueEnteredStep = train.QueueEnteredStep ?? step };
                    if (train.SpeedMetersPerSecond == 0 && position.Phase is NodeTravelPhase.Waiting or NodeTravelPhase.Docked)
                        return Wait(train, step, TrainDispatch.PlatformWait(train), events);
                }
            }
            else train = train with { QueueEnteredStep = null };
            if (!running) return ChangeActivity(train, TrainActivity.Stopped, events);
            if (position.Phase == NodeTravelPhase.Waiting)
            {
                if (position.NextEdge is null && StationAt(train, position.Id) is null
                    && AfterLocalStops(train, position.Id).NextStopIndex is not null)
                    return ChangeActivity(train, TrainActivity.NoRoute, events, [NoRoute(train)]);
                position = position with { Phase = NodeTravelPhase.Approaching };
                train = train with { Position = position };
            }
            if (position.Phase == NodeTravelPhase.Docked)
            {
                while (train.NextStopIndex is { } index && Target(train.Plan!.Stops[index]) == position.Id)
                {
                    if (train.Plan.Stops[index] is PlanStop.Station station)
                    {
                        if (train.AutomaticEvaluationRemainingSteps is null)
                        {
                            train = ChangeActivity(train with { RemainingDwellSteps = station.DwellSteps,
                                AutomaticEvaluationRemainingSteps = 0, StopRequested = false }, TrainActivity.Servicing, events);
                            train = AutomaticGoodsOperations.Advance(ref world, content, train, step, false, events);
                            dispatch.RefreshCapabilities(world, train);
                        }
                        if (train.RemainingDwellSteps > 0)
                            return ChangeActivity(train with { StopRequested = false }, TrainActivity.Servicing, events);
                        train = ConsumeStop(ref world, content, train, boundary, events);
                    }
                    else if (StationAt(train, position.Id) is not null || AfterLocalStops(train, position.Id).NextStopIndex is null)
                        train = ConsumeStop(ref world, content, train, boundary, events);
                    else break;
                }
                if (train.NextStopIndex is null)
                    return ChangeActivity(train with { StopRequested = false, QueueEnteredStep = null,
                        Position = position with { NextEdge = null } }, TrainActivity.Stopped, events);
                position = position with { NextEdge = Departure(world, content, train, position.Id) };
                train = train with { Position = position };
                if (position.NextEdge is null)
                {
                    if (dispatch.Capabilities(train).DepartureProblem(train.Id, Rule(world, content).MinimumAcceleration) is { } blocker)
                        return ChangeActivity(train, TrainActivity.Waiting, events, [blocker]);
                    return ChangeActivity(train, TrainActivity.NoRoute, events, [NoRoute(train)]);
                }
            }
        }
        if (train.SpeedMetersPerSecond == 0 && dispatch.Capabilities(train).DepartureProblem(train.Id,
                Rule(world, content).MinimumAcceleration) is { } problem)
        {
            if (train.NextStopIndex is null)
                return ChangeActivity(train with { StopRequested = false, QueueEnteredStep = null }, TrainActivity.Stopped, events);
            return ChangeActivity(train, TrainActivity.Waiting, events, [problem]);
        }
        return ChangeActivity(train, TrainActivity.Running, events);
    }

    internal static (WorldState World, ImmutableArray<CoreEvent> Events) AdaptNodes(WorldState world,
        CoreContentProjection content, SimulationPosition boundary)
    {
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        var trains = ImmutableArray.CreateBuilder<TrainState>();
        foreach (var original in world.Trains)
        {
            var train = original;
            if (train.Position is not TrainPosition.Node position) { trains.Add(train); continue; }
            var dimensions = NodeDimensions.Read(world, content, position.Id);
            var distance = train.SpeedMetersPerSecond == 0 ? position.Phase switch
            {
                NodeTravelPhase.Waiting => dimensions.Approach,
                NodeTravelPhase.Docked => dimensions.Stop,
                NodeTravelPhase.Departing when train.Activity == TrainActivity.Waiting
                    && train.QueueEnteredStep is not null => dimensions.Length,
                _ => Math.Min(position.DistanceMeters, dimensions.Length)
            } : Math.Min(position.DistanceMeters, dimensions.Length);
            if (position.Phase == NodeTravelPhase.Approaching && distance > dimensions.Stop)
            {
                if (MustStop(train, position))
                { distance = dimensions.Stop; train = train with { SpeedMetersPerSecond = 0 }; position = position with { Phase = NodeTravelPhase.Docked }; }
                else
                {
                    for (var count = 0; count < (train.Plan?.Stops.Length ?? 0) && train.NextStopIndex is { } index
                        && Target(train.Plan!.Stops[index]) == position.Id; count++)
                        train = ConsumeStop(ref world, content, train, boundary, events);
                    position = position with { Phase = NodeTravelPhase.Departing };
                }
            }
            trains.Add(train with { Position = position with { DistanceMeters = distance } });
        }
        return (world with { Trains = trains.ToImmutable() }, events.ToImmutable());
    }

    private static TrainState Wait(TrainState train, ulong step, CoreProblem reason, ImmutableArray<CoreEvent>.Builder events)
        => ChangeActivity(train with { SpeedMetersPerSecond = 0, QueueEnteredStep = train.QueueEnteredStep ?? step },
            TrainActivity.Waiting, events, [reason]);

    private static TrainOrderFacing OppositeFacing(TrainOrderFacing facing) => facing == TrainOrderFacing.FirstEndLeading
        ? TrainOrderFacing.LastEndLeading : TrainOrderFacing.FirstEndLeading;

    internal static TrainState Consume(TrainState train)
    {
        var next = train.NextStopIndex!.Value + 1;
        return train with { NextStopIndex = next < train.Plan!.Stops.Length ? next : train.Plan.Loop ? 0 : null };
    }

    internal static TrainState ConsumeStop(ref WorldState world, CoreContentProjection content, TrainState train,
        SimulationPosition boundary, ImmutableArray<CoreEvent>.Builder events)
    {
        train = Consume(train) with { AutomaticEvaluationRemainingSteps = null };
        if (train.NextStopIndex is null || train.NextStopIndex == 0)
        {
            train = RunReports.End(ref world, content, train, boundary,
                train.NextStopIndex is null ? RunEndReason.PlanCompleted : RunEndReason.LoopCompleted, events);
            if (train.NextStopIndex == 0) train = train with { CurrentRun = RunReports.Begin(boundary) };
        }
        return train;
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
        train.NextStopIndex is { } index ? [Target(train.Plan!.Stops[train.NextStopIndex.Value]).Value.ToString(System.Globalization.CultureInfo.InvariantCulture)] : []);
}
