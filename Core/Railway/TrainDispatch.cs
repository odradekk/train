using System.Collections.Immutable;
using System.Globalization;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Railway;

internal sealed class TrainDispatch
{
    private readonly WorldState _world;
    private readonly CoreContentProjection _content;
    private readonly Dictionary<TrainId, TrainCapabilities> _capabilities;
    private readonly Dictionary<(EdgeId Edge, int Track), List<TrainState>> _tracks;
    private readonly Dictionary<TrainId, (EdgeId Edge, TravelDirection Direction)> _requests = [];
    private readonly Dictionary<NodeId, int> _platforms;
    private readonly HashSet<TrainId> _platformHolders;
    private readonly HashSet<(EdgeId, int)> _usedHandoffs = [];

    internal TrainDispatch(WorldState world, CoreContentProjection content)
    {
        _world = world;
        _content = content;
        _capabilities = world.Trains.ToDictionary(t => t.Id, t => TrainOperations.Capabilities(world, content, t));
        _tracks = world.Trains.Where(t => t.Position is TrainPosition.Edge)
            .GroupBy(t => { var p = (TrainPosition.Edge)t.Position; return (p.Id, p.Track); })
            .ToDictionary(g => g.Key, g => g.ToList());
        _platforms = world.Nodes.ToDictionary(n => n.Id, n => world.OccupiedPlatforms(n.Id));
        _platformHolders = world.Trains.Where(t => t.Activity == TrainActivity.Servicing && NeedsPlatform(world, t))
            .Select(t => t.Id).ToHashSet();
        foreach (var train in world.Trains)
            if (RequestedEdge(world, content, train, _capabilities[train.Id]) is { } edge)
                _requests.Add(train.Id, (edge.Id, Direction(edge, ((TrainPosition.Node)train.Position).Id)));
    }

    internal static TravelDirection Direction(EdgeMapItem edge, NodeId node)
        => node == edge.Start ? TravelDirection.Forward : TravelDirection.Reverse;
    internal static TravelDirection Opposite(TravelDirection direction)
        => direction == TravelDirection.Forward ? TravelDirection.Reverse : TravelDirection.Forward;

    internal static bool NeedsPlatform(WorldState world, TrainState train)
        => train.Position is TrainPosition.Node node
            && world.Nodes.Single(n => n.Id == node.Id).Occupant is NodeOccupant.Settlement { Relation: not SettlementRelation.Hostile };

    internal bool TakePlatform(TrainState train, ulong step)
    {
        if (!NeedsPlatform(_world, train)) return true;
        if (_platformHolders.Contains(train.Id)) return true;
        var node = ((TrainPosition.Node)train.Position).Id;
        if (_world.Trains.Any(t => t.Id != train.Id && !_platformHolders.Contains(t.Id)
            && t.Activity == TrainActivity.Waiting && t.Position is TrainPosition.Node p && p.Id == node
            && t.NextStopIndex is { } index && t.Plan!.Stops[index] is PlanStop.Station station && station.Node == node
            && t.QueueEnteredStep is { } age && (age < (train.QueueEnteredStep ?? step)
                || age == (train.QueueEnteredStep ?? step) && t.Id.Value < train.Id.Value))) return false;
        if (_platforms[node] >= _world.Nodes.Single(n => n.Id == node).Platforms) return false;
        _platforms[node]++;
        _platformHolders.Add(train.Id);
        return true;
    }

    internal static EdgeMapItem? RequestedEdge(WorldState world, CoreContentProjection content, TrainState train,
        TrainCapabilities? knownCapabilities = null)
    {
        if (train.Position is not TrainPosition.Node node || train.Activity is TrainActivity.Stopped or TrainActivity.Servicing)
            return null;
        while (train.NextStopIndex is { } index && TrainOperations.Target(train.Plan!.Stops[index]) == node.Id)
        {
            if (train.Plan.Stops[index] is PlanStop.Station station
                && (NeedsPlatform(world, train) || station.DwellSteps > 0 || train.RemainingDwellSteps > 0)) return null;
            train = TrainOperations.Consume(train);
        }
        if (train.NextStopIndex is null) return null;
        var capabilities = knownCapabilities ?? TrainOperations.Capabilities(world, content, train);
        if (train.SpeedMetersPerSecond == 0 && capabilities.DepartureProblem(train.Id,
            TrainOperations.Rule(world, content).MinimumAcceleration) is not null) return null;
        var route = TrainRouting.FindRoute(world.Nodes, world.Edges, node.Id,
            TrainOperations.Target(train.Plan!.Stops[train.NextStopIndex.Value]), capabilities.MaximumSpeedMetersPerSecond);
        return route is null ? null : world.Edges.Single(e => e.Id == route.Value.Edges[0]);
    }

    internal int? Enter(TrainState train, EdgeMapItem edge, bool reserve)
    {
        var direction = Direction(edge, ((TrainPosition.Node)train.Position).Id);
        var opposite = Opposite(direction);
        _requests[train.Id] = (edge.Id, direction);
        var opposing = _requests.Values.Any(r => r.Edge == edge.Id && r.Direction == opposite);
        var handoffs = _world.TrackHandoffs.Where(h => h.Edge == edge.Id && !_usedHandoffs.Contains((h.Edge, h.Track)))
            .Where(h => _requests.Values.Any(r => r.Edge == h.Edge && r.Direction == h.Direction)).ToArray();
        int? selected = null;
        if (!opposing)
            for (var track = 1; track <= edge.Tracks; track++)
                if (Open(edge, track) && _tracks.TryGetValue((edge.Id, track), out var trains)
                    && trains.Any(t => t.Id != train.Id)
                    && trains.Where(t => t.Id != train.Id).All(t => ((TrainPosition.Edge)t.Position).Direction == direction
                        && EntrySpace(train, t, edge, direction)))
                { selected = track; break; }
        if (selected is null)
            for (var track = 1; track <= edge.Tracks; track++)
            {
                if (!Open(edge, track) || _tracks.TryGetValue((edge.Id, track), out var occupants)
                    && occupants.Any(t => t.Id != train.Id)) continue;
                var handoff = handoffs.FirstOrDefault(h => h.Track == track);
                if (handoff is not null && handoff.Direction != direction) continue;
                if (handoff is null && opposing && (handoffs.Any(h => h.Direction == opposite)
                    || _tracks.Any(t => t.Key.Edge == edge.Id
                        && t.Value.Any(other => other.Id != train.Id && ((TrainPosition.Edge)other.Position).Direction == direction)))) continue;
                selected = track;
                break;
            }
        if (selected is { } number && reserve)
        {
            var position = new TrainPosition.Edge(edge.Id, number, direction,
                direction == TravelDirection.Forward ? 0 : edge.LengthMeters);
            if (!_tracks.TryGetValue((edge.Id, number), out var trains))
                _tracks.Add((edge.Id, number), trains = []);
            trains.Add(train with { Position = position, Transit = null });
            _usedHandoffs.Add((edge.Id, number));
        }
        return selected;
    }

    private static bool Open(EdgeMapItem edge, int track) => !edge.Breaks.Any(b => b.Track == track)
        && !edge.Works.Any(w => w.Track == track && w.Kind == RailwayWorkKind.RepairBreak);

    private bool EntrySpace(TrainState entering, TrainState ahead, EdgeMapItem edge, TravelDirection direction)
    {
        var distance = ((TrainPosition.Edge)ahead.Position).DistanceMeters;
        var available = (direction == TravelDirection.Forward ? distance : edge.LengthMeters - distance) - Gap(entering, ahead);
        return available >= entering.SpeedMetersPerSecond * entering.SpeedMetersPerSecond
            / (2 * TrainOperations.Rule(_world, _content).BrakingDeceleration);
    }

    private double Gap(TrainState first, TrainState second) =>
        (_capabilities[first.Id].LengthMeters + _capabilities[second.Id].LengthMeters) / 2
        + TrainOperations.Rule(_world, _content).MinimumGapMeters;

    internal TrainCapabilities Capabilities(TrainState train) => _capabilities[train.Id];

    internal (double Position, bool Endpoint) Limit(TrainState train)
    {
        var position = (TrainPosition.Edge)train.Position;
        var edge = _world.Edges.Single(e => e.Id == position.Id);
        var bound = position.Direction == TravelDirection.Forward ? edge.LengthMeters : 0;
        var endpoint = true;
        if (AheadBreak(edge, train) is { } broken)
        { bound = broken.DistanceMeters; endpoint = false; }
        foreach (var other in _tracks[(edge.Id, position.Track)])
        {
            var ahead = (TrainPosition.Edge)other.Position;
            if (other.Id == train.Id || (position.Direction == TravelDirection.Forward
                ? ahead.DistanceMeters <= position.DistanceMeters : ahead.DistanceMeters >= position.DistanceMeters)) continue;
            var limit = position.Direction == TravelDirection.Forward
                ? ahead.DistanceMeters - Gap(train, other) : ahead.DistanceMeters + Gap(train, other);
            if (position.Direction == TravelDirection.Forward ? limit < bound : limit > bound)
            { bound = limit; endpoint = false; }
        }
        return (position.Direction == TravelDirection.Forward ? Math.Max(bound, position.DistanceMeters)
            : Math.Min(bound, position.DistanceMeters), endpoint);
    }

    internal static TrackBreakView? AheadBreak(EdgeMapItem edge, TrainState train)
    {
        var position = (TrainPosition.Edge)train.Position;
        var origin = edge.Breaks.FirstOrDefault(b => b.Id == train.ReturningFromBreak);
        return edge.Breaks.Where(b => b.Track == position.Track
                && !(origin is not null && b.DistanceMeters == origin.DistanceMeters && b.Id.Value <= origin.Id.Value)
                && (position.Direction == TravelDirection.Forward ? b.DistanceMeters >= position.DistanceMeters
                    : b.DistanceMeters <= position.DistanceMeters))
            .OrderBy(b => position.Direction == TravelDirection.Forward ? b.DistanceMeters : -b.DistanceMeters)
            .ThenByDescending(b => b.Id.Value).FirstOrDefault();
    }

    internal WorldState Finish(WorldState world)
    {
        var trains = world.Trains.ToDictionary(t => t.Id);
        foreach (var group in world.Trains.Where(t => t.Position is TrainPosition.Edge)
                     .GroupBy(t => { var p = (TrainPosition.Edge)t.Position; return (p.Id, p.Track); }))
        {
            var edge = world.Edges.Single(e => e.Id == group.Key.Id);
            if (group.Any(t => t.SpeedMetersPerSecond != 0 || t.ReturningFromBreak is not null
                || AheadBreak(edge, t) is null)) continue;
            foreach (var train in group)
                trains[train.Id] = train with
                {
                    Position = ((TrainPosition.Edge)train.Position) with { Direction = Opposite(((TrainPosition.Edge)train.Position).Direction) },
                    OrderFacing = train.OrderFacing == TrainOrderFacing.FirstEndLeading ? TrainOrderFacing.LastEndLeading : TrainOrderFacing.FirstEndLeading,
                    ReturningFromBreak = AheadBreak(edge, train)!.Id
                };
        }
        world = world with { Trains = world.Trains.Select(t => trains[t.Id]).ToImmutableArray() };
        var requests = world.Trains.Select(t => (Train: t, Edge: RequestedEdge(world, _content, t, _capabilities[t.Id])))
            .Where(r => r.Edge is not null).Select(r => (r.Edge!.Id, Direction(r.Edge, ((TrainPosition.Node)r.Train.Position).Id))).ToHashSet();
        var handoffs = _world.TrackHandoffs.Where(h => !_usedHandoffs.Contains((h.Edge, h.Track))
            && requests.Contains((h.Edge, h.Direction))).ToDictionary(h => (h.Edge, h.Track));
        foreach (var (key, occupants) in _tracks)
            if (Open(world.Edges.Single(e => e.Id == key.Edge), key.Track)
                && !world.Trains.Any(t => t.Position is TrainPosition.Edge p && p.Id == key.Edge && p.Track == key.Track))
            {
                var direction = Opposite(((TrainPosition.Edge)occupants[0].Position).Direction);
                if (requests.Contains((key.Edge, direction))) handoffs[key] = new(key.Edge, key.Track, direction);
            }
        return world with { TrackHandoffs = handoffs.Values.OrderBy(h => h.Edge.Value).ThenBy(h => h.Track).ToImmutableArray() };
    }

    internal static CoreProblem TrackWait(TrainState train, EdgeId edge) => new("track_wait", "Train.Position",
        new WorldObjectId.Train(train.Id), [edge.Value.ToString(CultureInfo.InvariantCulture)]);
    internal static CoreProblem PlatformWait(TrainState train) => new("platform_wait", "Train.Plan",
        new WorldObjectId.Train(train.Id), [((TrainPosition.Node)train.Position).Id.Value.ToString(CultureInfo.InvariantCulture)]);
}
