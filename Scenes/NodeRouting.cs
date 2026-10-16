using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed partial class NodeLayout
{
    internal sealed record TrainApproach(NodeRailPath Inlet, NodeRailPath Branch);
    private sealed record Turn(NodeRailPath Curve, ImmutableDictionary<Stop, NodeRailPath> Connections);
    private readonly Dictionary<(EdgeId, Stop), TrainApproach> trainApproaches = [];
    private readonly Dictionary<int, Turn> turns = [];
    private readonly List<NodeRailPath> turnRails = [];
    private ImmutableDictionary<EdgeId, int> entryTracks = ImmutableDictionary<EdgeId, int>.Empty;
    internal Stop Bypass { get; private set; } = null!;
    internal IEnumerable<(Func<double, RailwaySample> Sample, double Length)> TurnRoutes() =>
        turnRails.Select(route => ((Func<double, RailwaySample>)route.Sample, route.Length));

    internal Entry? TrainEntry(EdgeId? enteredFrom) => enteredFrom is { } edge
        ? Entries.FirstOrDefault(entry => entry.Edge == edge)
        : Entries.FirstOrDefault(entry => entryTracks.GetValueOrDefault(entry.Edge) > 0);

    internal RailwaySample TrainBoundary(Entry entry)
    {
        var forward = -entry.Outward;
        var right = Vector3.Cross(Vector3.UnitY, forward);
        var count = entryTracks.GetValueOrDefault(entry.Edge);
        var offset = count > 0 ? RailwayCoordinates.TrackOffset(1, count) : 0;
        return new(RailwayCoordinates.Add(entry.Point, right * (float)offset), forward, right);
    }

    internal TrainApproach Approach(Entry entry, Stop stop) => trainApproaches[(entry.Edge, stop)];

    internal NodeRailPath StationTail(Stop stop, int side)
    {
        var target = StationEnd(stop, side);
        return NodeRailPath.Straight(stop.Center, side * StationForward,
            Vector3.Dot(RailwayCoordinates.Relative(target, stop.Center), side * StationForward));
    }

    internal NodeRailPath Exit(Entry entry, Stop stop)
    {
        var boundary = TrainBoundary(entry);
        var curve = NodeRailPath.From(StopConnection(entry, stop));
        return NodeRailPath.Join(curve.Reverse(), NodeRailPath.Straight(
            RailwayCoordinates.Add(boundary.Position, boundary.Forward * ApproachStraight), entry.Outward, ApproachStraight));
    }

    internal NodeRailPath TurnExit(int side, Stop stop, Entry exit)
    {
        var turn = turns[side];
        var end = turn.Curve.Sample(turn.Curve.Length);
        var target = StationEnd(Bypass, exit.Side);
        var length = Vector3.Dot(RailwayCoordinates.Relative(target, end.Position), end.Forward);
        return NodeRailPath.Join(turn.Connections[stop], turn.Curve,
            NodeRailPath.Straight(end.Position, end.Forward, length), Exit(exit, Bypass));
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

    private Track StopConnection(Entry entry, Stop stop)
    {
        var line = 0;
        if (!routeStops.IsEmpty)
        {
            var across = Vector3.Cross(Vector3.UnitY, StationForward);
            line = Enumerable.Range(0, routeStops.Length).First(index =>
                Math.Abs(Vector3.Dot(RailwayCoordinates.Relative(routeStops[index].Center, stop.Center), across)) < .001 &&
                Math.Abs(routeStops[index].Center.Y - stop.Center.Y) < .001);
        }
        var count = entryTracks.GetValueOrDefault(entry.Edge);
        var offset = count > 0 ? RailwayCoordinates.TrackOffset(1, count) : 0;
        return Connection(entry.Edge, offset, line);
    }

    private void PrepareTrainRoutes(SceneWorldSnapshot snapshot)
    {
        Bypass ??= new(0, 0, new(0, 0, 0), StationForward);
        entryTracks = snapshot.Core.World.Edges.Where(edge => Entries.Any(entry => entry.Edge == edge.Id))
            .ToImmutableDictionary(edge => edge.Id, edge => edge.Tracks);
        var stops = Stops.Append(Bypass).Distinct().ToArray();
        foreach (var entry in Entries)
        foreach (var stop in stops)
        {
            var boundary = TrainBoundary(entry);
            var inlet = NodeRailPath.Straight(boundary.Position, boundary.Forward, ApproachStraight + entry.StraightLength);
            var curve = NodeRailPath.From(StopConnection(entry, stop));
            var end = curve.Sample(curve.Length);
            var tail = NodeRailPath.Straight(end.Position, end.Forward,
                Vector3.Dot(RailwayCoordinates.Relative(stop.Center, end.Position), end.Forward));
            trainApproaches.Add((entry.Edge, stop), new(inlet,
                NodeRailPath.Join(curve.Slice(entry.StraightLength, curve.Length), tail)));
        }

        var appearance = ((ContentLookup<RailwayAppearanceProjection>.Found)snapshot.Content
            .FindRailwayAppearance(snapshot.Core.World.RailwayRule)).Value;
        var extent = AxisRange(Bounds, StationForward);
        var across = Vector3.Cross(Vector3.UnitY, StationForward);
        var returnAcross = Vector3.Dot(RailwayCoordinates.Relative(Bypass.Center, default), across);
        foreach (var side in Entries.Where(entry => entryTracks[entry.Edge] > 0).GroupBy(entry => entry.Side)
            .Where(group => group.Count() > 1).Select(group => -group.Key))
        {
            var outward = side * StationForward;
            var right = Vector3.Cross(Vector3.UnitY, outward);
            var startAcross = returnAcross - 2 * radius;
            var needed = stops.Max(stop =>
            {
                var lateral = startAcross - Vector3.Dot(RailwayCoordinates.Relative(stop.Center, default), across);
                return LateralJoin(Math.Abs(lateral), radius).Forward +
                    Math.Max(ApproachStraight, 1.5 * Math.Abs(stop.Center.Y - Bypass.Center.Y) / appearance.MaximumGrade);
            });
            var along = (side > 0 ? extent.Max : extent.Min) + side * (needed + GroundMargin);
            var at = StationForward * (float)along + across * (float)startAcross;
            var start = new ScenePoint(at.X, Bypass.Center.Y, at.Z);
            var curve = NodeRailPath.From(new Track(start, outward, right, 0, [(Math.PI * radius, side / radius)], start.Y));
            var joins = stops.ToImmutableDictionary(stop => stop, stop => JoinOutward(StationEnd(stop, side),
                start, outward, radius, appearance.MaximumGrade));
            turns.Add(side, new(curve, joins));
            turnRails.AddRange(joins.Values);
            turnRails.Add(curve);
            var end = curve.Sample(curve.Length);
            var near = StationEnd(Bypass, side);
            turnRails.Add(NodeRailPath.Straight(end.Position, end.Forward,
                Vector3.Dot(RailwayCoordinates.Relative(near, end.Position), end.Forward)));
        }
        if (turnRails.Count == 0) return;
        var minimum = Bounds.Minimum; var maximum = Bounds.Maximum;
        foreach (var route in turnRails)
        for (var arc = 0.0; arc < route.Length + 4; arc += 4)
        {
            var point = route.Sample(Math.Min(arc, route.Length)).Position;
            var at = new Vector2((float)point.X, (float)point.Z);
            var margin = new Vector2((float)(RailwayCoordinates.BedBaseHalfWidth + RailwayCoordinates.BedBlendWidth + GroundMargin));
            minimum = Vector2.Min(minimum, at - margin); maximum = Vector2.Max(maximum, at + margin);
        }
        minimum = new(MathF.Floor(minimum.X / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize,
            MathF.Floor(minimum.Y / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize);
        maximum = new(MathF.Ceiling(maximum.X / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize,
            MathF.Ceiling(maximum.Y / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize);
        Bounds = new(minimum, maximum);
        HalfSize = Math.Max(Math.Max(Math.Abs(minimum.X), Math.Abs(minimum.Y)), Math.Max(Math.Abs(maximum.X), Math.Abs(maximum.Y)));
    }

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
        var ramp = Math.Max(ApproachStraight, 1.5 * Math.Abs(end.Y - start.Y) / grade);
        var straight = Vector3.Dot(delta, forward) - join.Forward - ramp;
        if (straight < -ConnectionTolerance) throw new RailwayBuildException("The node turnaround cannot reach its return track.");
        var curvature = Math.Sign(lateral) / radius;
        return NodeRailPath.From(new Track(start, forward, right, 0,
            [(Math.Max(0, straight), 0), (radius * join.Angle, curvature), (join.Middle, 0),
                (radius * join.Angle, -curvature), (ramp, 0)], end.Y));
    }
}
