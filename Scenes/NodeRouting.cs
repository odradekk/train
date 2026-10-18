using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed partial class NodeLayout
{
    internal sealed record TrainApproach(NodeRailPath Inlet, NodeRailPath Branch);
    private sealed record Turn(NodeRailPath Curve, ImmutableDictionary<Stop, NodeRailPath> Connections, NodeRailPath Return);
    private readonly Dictionary<(EdgeId, int, Stop), TrainApproach> trainApproaches = [];
    private readonly Dictionary<int, Turn> turns = [];
    private readonly List<NodeRailPath> turnRails = [];
    private readonly List<(EdgeId Edge, int Lane, NodeRailPath Path)> sharedTurnRails = [];
    private ImmutableDictionary<EdgeId, int> entryTracks = ImmutableDictionary<EdgeId, int>.Empty;
    internal ImmutableDictionary<EdgeId, int> EntryTracks => entryTracks;
    internal Stop Bypass { get; private set; } = null!;
    internal IEnumerable<(Func<double, RailwaySample> Sample, double Length)> TurnRoutes() =>
        turnRails.Select(route => ((Func<double, RailwaySample>)route.Sample, route.Length))
        .Concat(sharedTurnRails.Where(rail => !Enumerable.Range(1, entryTracks.GetValueOrDefault(rail.Edge))
            .Any(number => Math.Abs(RailwayCoordinates.TrackOffset(number, entryTracks[rail.Edge]) * 2 /
                RailwayCoordinates.TrackSpacing - rail.Lane) < ConnectionTolerance))
            .Select(rail => ((Func<double, RailwaySample>)rail.Path.Sample, rail.Path.Length)));

    internal Entry? TrainEntry(EdgeId? enteredFrom) => enteredFrom is { } edge
        ? Entries.FirstOrDefault(entry => entry.Edge == edge)
        : Entries.Where(entry => entryTracks.GetValueOrDefault(entry.Edge) > 0).MinBy(entry => entry.Edge.Value);

    internal int TrainTrackNumber(Entry entry, int? track) =>
        track is > 0 && track <= entryTracks.GetValueOrDefault(entry.Edge) ? track.Value : 1;

    internal RailwaySample TrainBoundary(Entry entry, int? track = null)
    {
        var forward = -entry.Outward;
        var right = Vector3.Cross(Vector3.UnitY, forward);
        var offset = TrainTrackOffset(entry, track);
        return new(RailwayCoordinates.Add(entry.Point, right * (float)offset), forward, right);
    }

    internal TrainApproach Approach(Entry entry, Stop stop, int? track = null) =>
        trainApproaches[(entry.Edge, TrainTrackNumber(entry, track), stop)];

    internal NodeRailPath StationTail(Stop stop, int side)
    {
        var target = StationEnd(stop, side);
        return NodeRailPath.Straight(stop.Center, side * StationForward,
            Vector3.Dot(RailwayCoordinates.Relative(target, stop.Center), side * StationForward));
    }

    internal NodeRailPath Exit(Entry entry, Stop stop, int? track = null)
    {
        var boundary = TrainBoundary(entry, track);
        var curve = NodeRailPath.From(StopConnection(entry, stop, TrainTrackOffset(entry, track)));
        return NodeRailPath.Join(curve.Reverse(), NodeRailPath.Straight(
            RailwayCoordinates.Add(boundary.Position, boundary.Forward * ApproachStraight), entry.Outward, ApproachStraight));
    }

    internal NodeRailPath TurnExit(int side, Stop stop, Entry exit, int? track = null)
    {
        var turn = turns[side];
        var end = turn.Return.Sample(turn.Return.Length);
        var target = StationEnd(Bypass, exit.Side);
        var length = Vector3.Dot(RailwayCoordinates.Relative(target, end.Position), end.Forward);
        return NodeRailPath.Join(turn.Connections[stop], turn.Curve, turn.Return,
            NodeRailPath.Straight(end.Position, end.Forward, length), Exit(exit, Bypass, track));
    }

    private ScenePoint StationEnd(Stop stop, int side)
    {
        var extent = Template is not null && !routeStops.IsEmpty
            ? AxisRange(NodeSettlement.Bounds(Template), StationForward) : (-PlatformClearance, (double)PlatformClearance);
        var along = !routeStops.IsEmpty ? (side > 0 ? extent.Item2 + GroundMargin : extent.Item1 - GroundMargin)
            : side * PlatformClearance;
        return RailwayCoordinates.Add(stop.Center, StationForward * (float)(along -
            Vector3.Dot(RailwayCoordinates.Relative(stop.Center, default), StationForward)));
    }

    private Track StopConnection(Entry entry, Stop stop, double? laneOffset = null)
    {
        var line = 0;
        if (!routeStops.IsEmpty)
        {
            var across = Vector3.Cross(Vector3.UnitY, StationForward);
            line = Enumerable.Range(0, routeStops.Length).First(index =>
                Math.Abs(Vector3.Dot(RailwayCoordinates.Relative(routeStops[index].Center, stop.Center), across)) < .001 &&
                Math.Abs(routeStops[index].Center.Y - stop.Center.Y) < .001);
        }
        var offset = laneOffset ?? TrainTrackOffset(entry);
        return Connection(entry.Edge, offset, line);
    }

    private double TrainTrackOffset(Entry entry, int? track = null)
    {
        var count = entryTracks.GetValueOrDefault(entry.Edge);
        return count > 0 ? RailwayCoordinates.TrackOffset(TrainTrackNumber(entry, track), count) * entryTrackDirections[entry.Edge] : 0;
    }

    private readonly Dictionary<EdgeId, int> entryTrackDirections = [];
    private void ReadEntryTracks(SceneWorldSnapshot snapshot)
    {
        entryTracks = snapshot.Core.World.Edges.Where(edge => Entries.Any(entry => entry.Edge == edge.Id))
            .ToImmutableDictionary(edge => edge.Id, edge => edge.Tracks);
        foreach (var edge in snapshot.Core.World.Edges.Where(edge => entryTracks.ContainsKey(edge.Id)))
            entryTrackDirections.Add(edge.Id, edge.End == Node ? 1 : -1);
    }

    private void PrepareTrainRoutes()
    {
        var stops = Stops.Append(Bypass).Distinct().ToArray();
        foreach (var entry in Entries)
        foreach (var track in Enumerable.Range(1, Math.Max(1, entryTracks.GetValueOrDefault(entry.Edge))))
        foreach (var stop in stops)
        {
            var boundary = TrainBoundary(entry, track);
            var inlet = NodeRailPath.Straight(boundary.Position, boundary.Forward, ApproachStraight + entry.StraightLength);
            var curve = NodeRailPath.From(StopConnection(entry, stop, TrainTrackOffset(entry, track)));
            var end = curve.Sample(curve.Length);
            var tail = NodeRailPath.Straight(end.Position, end.Forward,
                Vector3.Dot(RailwayCoordinates.Relative(stop.Center, end.Position), end.Forward));
            trainApproaches.Add((entry.Edge, track, stop), new(inlet,
                NodeRailPath.Join(curve.Slice(entry.StraightLength, curve.Length), tail)));
        }
    }

    private void PrepareTurns(ScenePanBounds envelope, double maximumGrade)
    {
        turns.Clear();
        turnRails.Clear();
        sharedTurnRails.Clear();
        var stops = Stops.Append(Bypass).Distinct().ToArray();
        foreach (var side in Entries.GroupBy(entry => entry.Side)
            .Where(group => group.Count() >= 2 || hasSettlement).Select(group => -group.Key))
        {
            var outward = side * StationForward;
            var right = Vector3.Cross(Vector3.UnitY, outward);
            var candidates = Entries.Where(entry => entry.Side == side).ToArray();
            Vector3 tangent;
            ScenePoint neck;
            ImmutableDictionary<Stop, NodeRailPath> joins;
            if (candidates.Length > 0)
            {
                // Leave the existing fan along its outermost ray; every new bend is beyond that fan.
                var entry = candidates.OrderByDescending(item => Math.Atan2(Vector3.Dot(item.Outward, right),
                    Vector3.Dot(item.Outward, outward))).ThenByDescending(item =>
                        Vector3.Dot(RailwayCoordinates.Relative(item.Point, default), right)).ThenBy(item => item.Edge.Value).First();
                tangent = entry.Outward;
                var lane = -(entry.TrackLimit - 1);
                var offset = lane * RailwayCoordinates.TrackSpacing / 2;
                var shared = stops.ToImmutableDictionary(stop => stop, stop =>
                {
                    var connection = NodeRailPath.From(StopConnection(entry, stop, offset));
                    return connection.Slice(entry.StraightLength, connection.Length).Reverse();
                });
                var stem = shared[Bypass].Sample(shared[Bypass].Length).Position;
                var distance = ExteriorDistance(stem, tangent, envelope);
                var connection = StopConnection(entry, Bypass, offset);
                neck = connection.Sample(entry.StraightLength - distance).Position;
                var extension = NodeRailPath.From(connection)
                    .Slice(entry.StraightLength - distance, entry.StraightLength).Reverse();
                joins = shared.ToImmutableDictionary(pair => pair.Key, pair => NodeRailPath.Join(pair.Value, extension));
                sharedTurnRails.AddRange(joins.Values.Select(path => (entry.Edge, lane, path)));
            }
            else
            {
                tangent = outward;
                var across = Vector3.Cross(Vector3.UnitY, StationForward);
                var lateral = stops.Max(stop => Vector3.Dot(RailwayCoordinates.Relative(stop.Center, default), across));
                var extent = AxisRange(envelope, outward).Max;
                var needed = stops.Max(stop => LateralJoin(Math.Abs(lateral -
                    Vector3.Dot(RailwayCoordinates.Relative(stop.Center, default), across)), radius).Forward +
                    RampLength(stop.Center.Y - Bypass.Center.Y, maximumGrade));
                var at = outward * (float)(extent + needed + GroundMargin) + across * (float)lateral;
                neck = new(at.X, Bypass.Center.Y, at.Z);
                joins = stops.ToImmutableDictionary(stop => stop, stop => JoinOutward(StationEnd(stop, side),
                    neck, outward, radius, maximumGrade));
                turnRails.AddRange(joins.Values);
            }
            var neckRight = Vector3.Cross(Vector3.UnitY, tangent);
            // The spur and its reverse are the same rail. The three arcs turn the train around
            // at one point without a second access neck or a reversal of the core facing.
            var spur = NodeRailPath.From(new Track(neck, tangent, neckRight, 0,
                [(Math.PI * radius / 2, 1 / radius), (4 * radius, 0)], neck.Y));
            var apex = spur.Sample(spur.Length);
            var loop = NodeRailPath.From(new Track(apex.Position, apex.Forward, apex.Right, 0,
                [(Math.PI * radius / 3, 1 / radius), (5 * Math.PI * radius / 3, -1 / radius),
                    (Math.PI * radius / 3, 1 / radius)], neck.Y));
            var curve = NodeRailPath.Join(spur, loop, spur.Reverse());
            var returning = joins[Bypass].Reverse();
            turns.Add(side, new(curve, joins, returning));
            turnRails.Add(spur);
            turnRails.Add(loop);
        }
    }

    private double ExteriorDistance(ScenePoint start, Vector3 tangent, ScenePanBounds envelope)
    {
        var extent = AxisRange(envelope, tangent).Max;
        foreach (var rail in turnRails)
        for (var arc = 0.0; arc <= rail.Length; arc += radius / 4)
            extent = Math.Max(extent, Vector3.Dot(RailwayCoordinates.Relative(rail.Sample(arc).Position, default), tangent));
        var origin = RailwayCoordinates.Relative(start, default);
        var distance = extent - Vector3.Dot(origin, tangent) + 2 * radius + GroundMargin;
        foreach (var entry in Entries)
        {
            // These rays head away from the exterior half-plane containing the entire new loop.
            if (Vector3.Dot(tangent, entry.Outward) <= 0) continue;
            var sine = tangent.X * entry.Outward.Z - tangent.Z * entry.Outward.X;
            if (Math.Abs(sine) < 1e-7) continue;
            var delta = RailwayCoordinates.Relative(start, entry.Point);
            var separation = Math.Sign(sine) * (delta.X * entry.Outward.Z - delta.Z * entry.Outward.X);
            var normal = Vector3.Cross(Vector3.UnitY, tangent);
            var normalProjection = Math.Sign(sine) * (normal.X * entry.Outward.Z - normal.Z * entry.Outward.X);
            // The complete spur and loop fit in [0, 2R] along the ray and [0, 8R] along its normal.
            var margin = 8 * radius * Math.Max(0, -normalProjection) +
                RailwayCoordinates.TerrainHalfWidth(entry.TrackLimit) + GroundMargin;
            distance = Math.Max(distance, (margin - separation) / Math.Abs(sine));
        }
        return distance;
    }

    private static double RampLength(double rise, double maximumGrade) =>
        Math.Max(ApproachStraight, 1.5 * Math.Abs(rise) / maximumGrade);

    private static (double Angle, double Middle, double Forward) LateralJoin(double lateral, double radius)
    {
        var angle = Math.Min(Math.PI / 6, Math.Acos(Math.Clamp(1 - lateral / (2 * radius), -1, 1)));
        var middle = angle < 1e-8 ? 0 : Math.Max(0, (lateral - 2 * radius * (1 - Math.Cos(angle))) / Math.Sin(angle));
        return (angle, middle, 2 * radius * Math.Sin(angle) + middle * Math.Cos(angle));
    }

    private static NodeRailPath JoinOutward(ScenePoint start, ScenePoint end, Vector3 forward, double radius, double grade)
    {
        var right = Vector3.Cross(Vector3.UnitY, forward);
        var delta = RailwayCoordinates.Relative(end, start);
        var lateral = Vector3.Dot(delta, right);
        var join = LateralJoin(Math.Abs(lateral), radius);
        var ramp = RampLength(end.Y - start.Y, grade);
        var straight = Vector3.Dot(delta, forward) - join.Forward - ramp;
        if (straight < -ConnectionTolerance) throw new RailwayBuildException("The node turnaround cannot reach its return track.");
        var curvature = Math.Sign(lateral) / radius;
        return NodeRailPath.From(new Track(start, forward, right, 0,
            [(Math.Max(0, straight), 0), (radius * join.Angle, curvature), (join.Middle, 0),
                (radius * join.Angle, -curvature), (ramp, 0)], end.Y));
    }
}
