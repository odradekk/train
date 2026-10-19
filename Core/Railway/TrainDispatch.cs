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
    private readonly Dictionary<TrainId, (EdgeId Edge, int? Track, TravelDirection Direction)> _requests = [];
    private readonly Dictionary<NodeId, HashSet<int>> _platforms;
    private readonly Dictionary<TrainId, int> _platformHolders;
    private readonly HashSet<(EdgeId, int)> _usedHandoffs = [];

    internal TrainDispatch(WorldState world, CoreContentProjection content)
    {
        _world = world;
        _content = content;
        _capabilities = world.Trains.ToDictionary(t => t.Id, t => TrainOperations.Capabilities(world, content, t));
        _tracks = world.Trains.Where(t => t.Position is TrainPosition.Edge)
            .GroupBy(t => { var p = (TrainPosition.Edge)t.Position; return (p.Id, p.Track); })
            .ToDictionary(g => g.Key, g => g.ToList());
        _platforms = world.Nodes.ToDictionary(n => n.Id, _ => new HashSet<int>());
        _platformHolders = [];
        foreach (var train in world.Trains)
        {
            if (train.Position is TrainPosition.Node { PlatformNumber: { } platform } position)
            {
                _platforms[position.Id].Add(platform);
                _platformHolders.Add(train.Id, platform);
            }
            if (RequestedEdge(world, content, train, _capabilities[train.Id]) is { } edge)
                _requests.Add(train.Id, (edge.Id, ((TrainPosition.Node)train.Position).NextTrack,
                    Direction(edge, ((TrainPosition.Node)train.Position).Id)));
        }
    }

    internal static TravelDirection Direction(EdgeMapItem edge, NodeId node)
        => node == edge.Start ? TravelDirection.Forward : TravelDirection.Reverse;
    internal static TravelDirection Opposite(TravelDirection direction)
        => direction == TravelDirection.Forward ? TravelDirection.Reverse : TravelDirection.Forward;

    internal static bool NeedsPlatform(WorldState world, TrainState train)
        => train.Position is TrainPosition.Node node
            && node.Phase != NodeTravelPhase.Departing
            && TrainOperations.StationAt(train, node.Id) is not null
            && world.Nodes.Single(n => n.Id == node.Id).Occupant is NodeOccupant.Settlement { Relation: not SettlementRelation.Hostile };

    internal static bool HoldsPlatform(WorldState world, TrainState train)
        => train.Position is TrainPosition.Node { PlatformNumber: not null };

    internal bool PredictPlatform(TrainState train, ulong step)
    {
        if (!NeedsPlatform(_world, train) || HoldsPlatform(_world, train)) return true;
        var node = ((TrainPosition.Node)train.Position).Id;
        var ahead = _world.Trains.Count(t => EarlierPlatformRequest(t, train, node, step));
        return _world.Nodes.Single(n => n.Id == node).Platforms - _world.OccupiedPlatforms(node) > ahead;
    }

    private bool EarlierPlatformRequest(TrainState other, TrainState train, NodeId node, ulong step)
        => other.Id != train.Id && !HoldsPlatform(_world, other) && NeedsPlatform(_world, other)
            && other.Position is TrainPosition.Node p && p.Id == node
            && other.Activity != TrainActivity.Stopped && other.QueueEnteredStep is { } age
            && (age < (train.QueueEnteredStep ?? step) || age == (train.QueueEnteredStep ?? step) && other.Id.Value < train.Id.Value);

    internal int? TakePlatform(TrainState train, ulong step)
    {
        if (_platformHolders.TryGetValue(train.Id, out var held)) return held;
        if (!NeedsPlatform(_world, train)) return null;
        var node = ((TrainPosition.Node)train.Position).Id;
        if (_world.Trains.Any(t => !_platformHolders.ContainsKey(t.Id) && EarlierPlatformRequest(t, train, node, step))) return null;
        var capacity = _world.Nodes.Single(n => n.Id == node).Platforms;
        if (_platforms[node].Count >= capacity) return null;
        var number = Enumerable.Range(1, capacity).First(n => !_platforms[node].Contains(n));
        _platforms[node].Add(number);
        _platformHolders.Add(train.Id, number);
        return number;
    }

    internal static EdgeMapItem? RequestedEdge(WorldState world, CoreContentProjection content, TrainState train,
        TrainCapabilities? knownCapabilities = null)
    {
        if (train.Position is not TrainPosition.Node { Phase: NodeTravelPhase.Departing, NextEdge: { } edge }
            || train.Activity is not (TrainActivity.Waiting or TrainActivity.Retreating) || train.QueueEnteredStep is null)
            return null;
        if ((knownCapabilities ?? TrainOperations.Capabilities(world, content, train)).DepartureProblem(train.Id,
            TrainOperations.Rule(world, content).MinimumAcceleration) is not null) return null;
        return world.Edges.Single(e => e.Id == edge);
    }

    internal void RegisterExits(IEnumerable<TrainState> trains)
    {
        foreach (var train in trains)
        {
            var position = (TrainPosition.Node)train.Position;
            var edge = _world.Edges.Single(e => e.Id == position.NextEdge);
            _requests[train.Id] = (edge.Id, position.NextTrack, Direction(edge, position.Id));
        }
    }

    internal int? Enter(TrainState train, EdgeMapItem edge, bool reserve)
    {
        var selected = SelectTrack(train, edge, choosing: false);
        if (selected is { } number && reserve)
        {
            var direction = Direction(edge, ((TrainPosition.Node)train.Position).Id);
            var position = new TrainPosition.Edge(edge.Id, number, direction,
                direction == TravelDirection.Forward ? 0 : edge.LengthMeters);
            if (!_tracks.TryGetValue((edge.Id, number), out var trains))
                _tracks.Add((edge.Id, number), trains = []);
            trains.Add(train with { Position = position });
            _usedHandoffs.Add((edge.Id, number));
        }
        return selected;
    }

    internal TrainState FixExit(TrainState train)
    {
        if (train.Position is not TrainPosition.Node { Phase: NodeTravelPhase.Departing, NextEdge: { } next } position)
            return train;
        var edge = _world.Edges.Single(e => e.Id == next);
        if (position.NextTrack is { } current && Open(edge, current)) return train;
        var selected = SelectTrack(train, edge, choosing: true)
            ?? Enumerable.Range(1, edge.Tracks).Where(track => Open(edge, track)).Select(track => (int?)track).FirstOrDefault()
            ?? (position.NextTrack <= edge.Tracks ? position.NextTrack : null);
        return train with { Position = position with { NextTrack = selected } };
    }

    private int? SelectTrack(TrainState train, EdgeMapItem edge, bool choosing)
    {
        var node = (TrainPosition.Node)train.Position;
        bool Available(int track) => Open(edge, track)
            && (choosing || node.Phase != NodeTravelPhase.Departing || node.NextTrack == track)
            && (!choosing || !_world.Trains.Any(other => other.Id != train.Id && other.Position is TrainPosition.Node
                { Phase: NodeTravelPhase.Departing, NextEdge: { } next, NextTrack: { } fixedTrack }
                && next == edge.Id && fixedTrack == track));
        var direction = Direction(edge, ((TrainPosition.Node)train.Position).Id);
        var opposite = Opposite(direction);
        var opposing = _requests.Any(r => r.Key != train.Id && r.Value.Edge == edge.Id && r.Value.Direction == opposite);
        var handoffs = _world.TrackHandoffs.Where(h => h.Edge == edge.Id && !_usedHandoffs.Contains((h.Edge, h.Track)))
            .Where(h => _requests.Values.Any(r => r.Edge == h.Edge && r.Track == h.Track && r.Direction == h.Direction)).ToArray();
        int? selected = null;
        if (!opposing)
            for (var track = 1; track <= edge.Tracks; track++)
                if (Available(track) && _tracks.TryGetValue((edge.Id, track), out var trains)
                    && trains.Any(t => t.Id != train.Id)
                    && trains.Where(t => t.Id != train.Id).All(t => ((TrainPosition.Edge)t.Position).Direction == direction
                        && EntrySpace(train, t, edge, direction)))
                { selected = track; break; }
        if (selected is null)
            for (var track = 1; track <= edge.Tracks; track++)
            {
                if (!Available(track) || _tracks.TryGetValue((edge.Id, track), out var occupants)
                    && occupants.Any(t => t.Id != train.Id)) continue;
                var handoff = handoffs.FirstOrDefault(h => h.Track == track);
                if (handoff is not null && handoff.Direction != direction) continue;
                if (handoff is null && opposing && (handoffs.Any(h => h.Direction == opposite)
                    || _tracks.Any(t => t.Key.Edge == edge.Id
                        && t.Value.Any(other => other.Id != train.Id && ((TrainPosition.Edge)other.Position).Direction == direction)))) continue;
                selected = track;
                break;
            }
        return selected;
    }

    private bool Open(EdgeMapItem edge, int track) => track >= 1 && track <= edge.Tracks && !edge.Breaks.Any(b => b.Track == track);

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

    internal void RefreshCapabilities(WorldState world, TrainState train)
        => _capabilities[train.Id] = TrainOperations.Capabilities(world, _content, train);

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

    internal WorldState Finish(WorldState world, IReadOnlySet<TrainId>? skipBreaks = null)
    {
        skipBreaks ??= new HashSet<TrainId>();
        var trains = world.Trains.ToDictionary(t => t.Id);
        foreach (var group in world.Trains.Where(t => t.Position is TrainPosition.Edge && !skipBreaks.Contains(t.Id))
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
            .Where(r => r.Edge is not null).Select(r => (r.Edge!.Id, ((TrainPosition.Node)r.Train.Position).NextTrack,
                Direction(r.Edge, ((TrainPosition.Node)r.Train.Position).Id))).ToHashSet();
        var handoffs = _world.TrackHandoffs.Where(h => !_usedHandoffs.Contains((h.Edge, h.Track))
            && requests.Contains((h.Edge, h.Track, h.Direction))).ToDictionary(h => (h.Edge, h.Track));
        foreach (var (key, occupants) in _tracks)
            if (Open(world.Edges.Single(e => e.Id == key.Edge), key.Track)
                && !world.Trains.Any(t => t.Position is TrainPosition.Edge p && p.Id == key.Edge && p.Track == key.Track))
            {
                var direction = Opposite(((TrainPosition.Edge)occupants[0].Position).Direction);
                if (requests.Contains((key.Edge, key.Track, direction))) handoffs[key] = new(key.Edge, key.Track, direction);
            }
        return world with { TrackHandoffs = handoffs.Values.OrderBy(h => h.Edge.Value).ThenBy(h => h.Track).ToImmutableArray() };
    }

    internal static CoreProblem TrackWait(TrainState train, EdgeId edge) => new("track_wait", "Train.Position",
        new WorldObjectId.Train(train.Id), [edge.Value.ToString(CultureInfo.InvariantCulture)]);
    internal static CoreProblem PlatformWait(TrainState train) => new("platform_wait", "Train.Plan",
        new WorldObjectId.Train(train.Id), [((TrainPosition.Node)train.Position).Id.Value.ToString(CultureInfo.InvariantCulture)]);
}
