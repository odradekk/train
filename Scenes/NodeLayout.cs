using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class NodeLayout
{
    // #372 starts vehicles up to 104 cells; leave 12 cells at each platform end.
    internal const int PlatformLength = 128;
    internal const int PlatformWidth = 8;
    internal const int PlatformClearance = 16;
    internal const int StationTrackSpacing = 40;
    internal const int GroundMargin = 32;
    internal const int ApproachStraight = 16;
    internal const int QueueGap = 4;
    internal sealed record Entry(EdgeId Edge, ScenePoint Point, Vector3 Outward, int TrackLimit)
    {
        internal int Side { get; init; }
        internal double Angle { get; init; }
        internal double BendAngle { get; init; }
        internal double Radius { get; init; }
        internal double StraightLength { get; init; }
        internal double Separation { get; init; }
    }
    internal sealed record EntriesData(NodeId Node, int Platforms, double HalfSize, string Inputs,
        ImmutableArray<Entry> Entries, Vector3 StationForward)
    {
        internal Entry EntryFor(EdgeId edge) => Entries.Single(item => item.Edge == edge);
        internal ScenePoint EntryPoint(EdgeId edge) => EntryFor(edge).Point;
        internal RailwaySample StationSample(double u, double v) => StationPose(StationForward, u, v);
        internal ScenePanBounds PanBounds(ScenePoint origin) => Bounds(HalfSize, Entries, origin);
    }
    internal NodeId Node => data.Node;
    internal int Platforms => data.Platforms;
    internal double HalfSize => data.HalfSize;
    internal string Inputs => data.Inputs;
    internal ImmutableArray<Entry> Entries => data.Entries;
    internal Vector3 StationForward => data.StationForward;
    private readonly EntriesData data;
    private readonly ImmutableDictionary<(EdgeId Edge, int Lane, int Station), Track> connections;
    private NodeLayout(EntriesData data, ImmutableDictionary<(EdgeId, int, int), Track> connections)
    { this.data = data; this.connections = connections; }

    internal static string InputKey(SceneWorldSnapshot snapshot, NodeId id) => BuildEntries(snapshot, id).Inputs;

    internal static EntriesData BuildEntries(SceneWorldSnapshot snapshot, NodeId id)
    {
        var world = snapshot.Core.World;
        var node = world.Nodes.Single(item => item.Id == id);
        var rules = snapshot.Content.FindRailwayAppearance(world.RailwayRule) is ContentLookup<RailwayAppearanceProjection>.Found found
            ? found.Value : throw new RailwayBuildException("The railway appearance is missing.");
        var radius = rules.MinimumCurveRadiusMeters * RailwayCoordinates.CellsPerMeter;
        if (!double.IsFinite(radius) || radius <= 0) throw new RailwayBuildException("The railway curve radius is invalid.");
        var directions = world.Edges.Where(edge => edge.Start == id || edge.End == id).OrderBy(edge => edge.Id.Value).Select(edge =>
        {
            var other = world.Nodes.Single(item => item.Id == (edge.Start == id ? edge.End : edge.Start));
            var dx = other.Position.X - node.Position.X; var dz = other.Position.Y - node.Position.Y;
            var length = Math.Sqrt(dx * dx + dz * dz);
            if (!double.IsFinite(length) || length <= 0) throw new RailwayBuildException("The node entry bearing is invalid.");
            return (Edge: edge, Direction: new Vector3((float)(dx / length), 0, (float)(dz / length)), other.Position);
        }).ToArray();
        double Cost(Vector3 axis) => directions.Sum(item => Math.Acos(Math.Clamp(Math.Abs(Vector3.Dot(item.Direction, axis)), 0, 1)));
        var axis = Cost(Vector3.UnitX) < Cost(Vector3.UnitZ) - 1e-8 ? Vector3.UnitX : Vector3.UnitZ;
        var right = Vector3.Cross(Vector3.UnitY, axis);
        var stationExtent = StationTrackSpacing * Math.Max(0, node.Platforms - 1) / 2.0;
        var endV = (node.Platforms > 0 ? PlatformLength / 2.0 : 0) + PlatformClearance;
        var entries = ImmutableArray.CreateBuilder<Entry>();
        var extent = node.Platforms > 0 ? Math.Max(endV, stationExtent + PlatformClearance + PlatformWidth) : 0;
        var corridor = 0.0;
        foreach (var item in directions.OrderBy(item => Math.Acos(Math.Clamp(Math.Abs(Vector3.Dot(item.Direction, axis)), 0, 1)))
            .ThenBy(item => item.Edge.Id.Value))
        {
            var side = Vector3.Dot(item.Direction, axis) >= 0 ? 1 : -1;
            var inward = -item.Direction; var endForward = -side * axis; var endRight = -side * right;
            var angle = Math.Atan2(Vector3.Dot(inward, endRight), Math.Max(0, Vector3.Dot(inward, endForward)));
            var turn = angle >= 0 ? 1 : -1;
            // A 30-degree intermediate tangent bounds lateral division and keeps the arcs' forward span at most R.
            var theta = Math.Abs(angle); var phi = Math.Max(theta, Math.PI / 6);
            var lateralArc = radius * (1 + Math.Cos(theta) - 2 * Math.Cos(phi));
            var forwardArc = radius * (2 * Math.Sin(phi) - Math.Sin(theta));
            var laneWidth = RailwayCoordinates.OutermostTrackOffset(item.Edge.TrackLimit);
            var separation = entries.Where(other => other.Side == side && Math.Sign(other.BendAngle) == turn)
                .Sum(other => 2 * RailwayCoordinates.OutermostTrackOffset(other.TrackLimit) +
                    2 * RailwayCoordinates.BedBaseHalfWidth + QueueGap);
            var w = -turn * (stationExtent + lateralArc + laneWidth + separation + ApproachStraight * Math.Sin(theta) + 2);
            var u = -side * w;
            var grid = EndpointGrid(snapshot, item.Edge, id);
            var desired = StationPose(axis, u, 0).Position;
            var snapped = new ScenePoint(grid.X + Math.Round(desired.X - grid.X), 0,
                grid.Z + Math.Round(desired.Z - grid.Z));
            var entry = new Entry(item.Edge.Id, snapped, item.Direction, item.Edge.TrackLimit)
                { Side = side, Angle = angle, BendAngle = turn * phi, Radius = radius, Separation = separation };
            var maxForward = 0.0;
            for (var lane = 1 - entry.TrackLimit; lane < entry.TrackLimit; lane++)
            for (var station = 0; station < Math.Max(1, node.Platforms); station++)
            {
                var offset = lane * RailwayCoordinates.TrackSpacing / 2;
                var start = RailwayCoordinates.Add(snapped, inward * ApproachStraight + Vector3.Cross(Vector3.UnitY, inward) * (float)offset);
                var target = StationPose(axis, node.Platforms > 0 ? StationX(station, node.Platforms) : 0, side * endV).Position;
                var lateral = Vector3.Dot(RailwayCoordinates.Relative(target, start), endRight);
                var middle = (turn * lateral - lateralArc) / Math.Sin(phi);
                if (middle < -1e-5) throw new RailwayBuildException("The node entry corridor cannot reach its station tracks.");
                var laneForward = Vector3.Dot(RailwayCoordinates.Relative(start, snapped), endForward);
                maxForward = Math.Max(maxForward, laneForward + forwardArc + Math.Max(0, middle) * Math.Cos(phi));
            }
            extent = Math.Max(extent, Math.Max(Math.Abs(u) + laneWidth, endV + maxForward));
            corridor = Math.Max(corridor, laneWidth + RailwayCoordinates.BedBaseHalfWidth);
            entries.Add(entry with { Point = RailwayCoordinates.Add(snapped, axis * (float)(side * (endV + maxForward + 2))) });
        }
        // Stagger the inner stems as well as their lateral positions: parallel outward rays must stay distinct.
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var lastSeparation = entries.Where(other => other.Side == entry.Side && Math.Sign(other.BendAngle) == Math.Sign(entry.BendAngle))
                .Max(other => other.Separation);
            var extra = (lastSeparation - entry.Separation) * (1 + 1 / Math.Tan(Math.Abs(entry.BendAngle)));
            entry = entry with { Point = RailwayCoordinates.Add(entry.Point, axis * (float)(entry.Side * extra)) };
            entries[i] = entry;
            extent = Math.Max(extent, Math.Max(Math.Abs(entry.Point.X), Math.Abs(entry.Point.Z)));
        }
        var halfSize = Math.Ceiling((extent + corridor + GroundMargin + (directions.Length > 0 ? 1 : 0)) / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i]; var grid = EndpointGrid(snapshot, world.Edges.Single(edge => edge.Id == entry.Edge), id);
            double ToFace(double coordinate, float outward) => Math.Abs(outward) < 1e-8 ? double.PositiveInfinity :
                (Math.CopySign(halfSize, outward) - coordinate) / outward;
            var stem = Math.Min(ToFace(entry.Point.X, entry.Outward.X), ToFace(entry.Point.Z, entry.Outward.Z));
            var desired = RailwayCoordinates.Add(entry.Point, entry.Outward * (float)stem);
            entries[i] = entry with { Point = new(grid.X + Math.Round(desired.X - grid.X), 0,
                grid.Z + Math.Round(desired.Z - grid.Z)), StraightLength = stem };
        }
        var inputs = RailwayDependencies.Values(new object[] { id, node.Position, node.Platforms,
            directions.Select(item => new object[] { item.Edge.Id, item.Edge.TrackLimit, item.Edge.LengthMeters, item.Edge.Terrain.Relief, item.Position }).ToArray(), radius, rules.MaximumGrade });
        return new(id, node.Platforms, halfSize, inputs, entries.OrderBy(entry => entry.Edge.Value).ToImmutableArray(), axis);
    }

    private static ScenePoint EndpointGrid(SceneWorldSnapshot snapshot, EdgeMapItem edge, NodeId node) =>
        RailwayPath.Endpoint(snapshot, edge.Id, node);

    internal static NodeLayout Build(SceneWorldSnapshot snapshot, NodeId id, CancellationToken cancellation = default)
    {
        var data = BuildEntries(snapshot, id);
        var paths = ImmutableDictionary.CreateBuilder<(EdgeId, int, int), Track>();
        foreach (var entry in data.Entries)
        for (var lane = 1 - entry.TrackLimit; lane < entry.TrackLimit; lane++)
        for (var station = 0; station < Math.Max(1, data.Platforms); station++)
        {
            cancellation.ThrowIfCancellationRequested();
            var inward = -entry.Outward; var forward = -entry.Side * data.StationForward;
            var right = Vector3.Cross(Vector3.UnitY, forward);
            var offset = lane * RailwayCoordinates.TrackSpacing / 2;
            var start = RailwayCoordinates.Add(entry.Point, inward * (float)(ApproachStraight + entry.StraightLength) + Vector3.Cross(Vector3.UnitY, inward) * (float)offset);
            var endV = (data.Platforms > 0 ? PlatformLength / 2.0 : 0) + PlatformClearance;
            var target = data.StationSample(data.Platforms > 0 ? StationX(station, data.Platforms) : 0, entry.Side * endV).Position;
            var displacement = RailwayCoordinates.Relative(target, start);
            var theta = Math.Abs(entry.Angle); var phi = Math.Abs(entry.BendAngle); var turn = Math.Sign(entry.BendAngle);
            var lateralArc = entry.Radius * (1 + Math.Cos(theta) - 2 * Math.Cos(phi));
            var forwardArc = entry.Radius * (2 * Math.Sin(phi) - Math.Sin(theta));
            var middle = (turn * Vector3.Dot(displacement, right) - lateralArc) / Math.Sin(phi);
            var final = Vector3.Dot(displacement, forward) - forwardArc - middle * Math.Cos(phi);
            if (middle < -1e-4 || final < -1e-4)
                throw new RailwayBuildException("The node entry corridor cannot reach its station tracks.");
            middle = Math.Max(0, middle); final = Math.Max(0, final);
            var stemStart = RailwayCoordinates.Add(entry.Point, inward * ApproachStraight + Vector3.Cross(Vector3.UnitY, inward) * (float)offset);
            paths.Add((entry.Edge, lane, station), new(stemStart, forward, right, entry.Angle,
                [(entry.StraightLength, 0), (entry.Radius * (phi - theta), turn / entry.Radius), (middle, 0),
                    (entry.Radius * phi, -turn / entry.Radius), (final, 0)]));
        }
        return new(data, paths.ToImmutable());
    }

    internal static double StationX(int index, int count) => StationTrackSpacing * (index - (count - 1) / 2.0);
    private static RailwaySample StationPose(Vector3 axis, double u, double v)
    {
        var right = Vector3.Cross(Vector3.UnitY, axis);
        return new(new(right.X * u + axis.X * v, 0, right.Z * u + axis.Z * v), axis, right);
    }
    internal RailwaySample StationSample(double u, double v) => data.StationSample(u, v);
    internal Entry EntryFor(EdgeId edge) => data.EntryFor(edge);
    internal Track Connection(EdgeId edge, int laneSlot, int station) => connections[(edge, laneSlot, station)];
    internal Track Connection(EdgeId edge, double offset, int station) => Connection(edge, (int)Math.Round(offset * 2 / RailwayCoordinates.TrackSpacing), station);
    internal ScenePoint EntryPoint(EdgeId edge) => data.EntryPoint(edge);
    internal RailwaySample QueueSample(Entry entry, double distance, double offset = 0)
    {
        var forward = -entry.Outward; var right = Vector3.Cross(Vector3.UnitY, forward);
        return new(RailwayCoordinates.Add(entry.Point, forward * (float)(ApproachStraight - distance) + right * (float)offset), forward, right);
    }
    internal ScenePanBounds PanBounds(ScenePoint origin) => data.PanBounds(origin);
    private static ScenePanBounds Bounds(double halfSize, ImmutableArray<Entry> entries, ScenePoint origin)
    {
        var minimum = new Vector2((float)-halfSize - 1); var maximum = new Vector2((float)halfSize + 1);
        foreach (var entry in entries)
        {
            var right = Vector3.Cross(Vector3.UnitY, entry.Outward);
            var width = RailwayCoordinates.TerrainHalfWidth(entry.TrackLimit) + 2;
            var extent = new Vector2(Math.Abs(right.X) * (float)width + 2, Math.Abs(right.Z) * (float)width + 2);
            var at = new Vector2((float)entry.Point.X, (float)entry.Point.Z);
            minimum = Vector2.Min(minimum, at - extent); maximum = Vector2.Max(maximum, at + extent);
        }
        var shift = new Vector2((float)origin.X, (float)origin.Z);
        return new(minimum - shift, maximum - shift);
    }

    internal sealed class Track
    {
        private readonly ScenePoint start;
        private readonly Vector3 forward, right;
        private readonly double angle;
        private readonly (double Length, double Curvature)[] pieces;
        internal double Length { get; }
        internal Track(ScenePoint start, Vector3 forward, Vector3 right, double angle,
            (double Length, double Curvature)[] pieces)
        {
            this.start = start; this.forward = forward; this.right = right; this.angle = angle;
            this.pieces = pieces.Where(piece => piece.Length > 1e-8).ToArray();
            Length = this.pieces.Sum(piece => piece.Length);
        }
        internal RailwaySample Sample(double arc)
        {
            var remaining = Math.Clamp(arc, 0, Length); var heading = angle; var u = 0.0; var v = 0.0;
            foreach (var piece in pieces)
            {
                var distance = Math.Min(remaining, piece.Length);
                var next = heading + distance * piece.Curvature;
                if (piece.Curvature == 0) { u += distance * Math.Sin(heading); v += distance * Math.Cos(heading); }
                else { u += (Math.Cos(heading) - Math.Cos(next)) / piece.Curvature; v += (Math.Sin(next) - Math.Sin(heading)) / piece.Curvature; }
                heading = next; remaining -= distance;
                if (remaining <= 0) break;
            }
            var tangent = forward * (float)Math.Cos(heading) + right * (float)Math.Sin(heading);
            var extension = arc - Math.Clamp(arc, 0, Length);
            var point = new ScenePoint(start.X + forward.X * v + right.X * u + tangent.X * extension, start.Y,
                start.Z + forward.Z * v + right.Z * u + tangent.Z * extension);
            return new(point, tangent, Vector3.Cross(Vector3.UnitY, tangent));
        }
    }
}
