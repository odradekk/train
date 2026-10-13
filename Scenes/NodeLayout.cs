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
    internal const int EntryLateralGap = 4;
    // Two endpoint grid snaps can displace a connection by one cell per axis.
    private const int ConnectionSnapMargin = 2;
    private const double ConnectionTolerance = 1e-4;
    internal sealed record Entry(EdgeId Edge, ScenePoint Point, Vector3 Outward, int TrackLimit,
        int Side, double Angle, double BendAngle, double StraightLength = 0);
    internal NodeId Node { get; }
    internal int Platforms { get; }
    internal double HalfSize { get; }
    internal string Inputs { get; }
    internal ImmutableArray<Entry> Entries { get; }
    internal Vector3 StationForward { get; }
    private readonly double radius;
    private NodeLayout(NodeId node, int platforms, double halfSize, string inputs,
        ImmutableArray<Entry> entries, Vector3 stationForward, double radius)
    {
        Node = node; Platforms = platforms; HalfSize = halfSize; Inputs = inputs;
        Entries = entries; StationForward = stationForward; this.radius = radius;
    }

    internal static NodeLayout Build(SceneWorldSnapshot snapshot, NodeId id, CancellationToken cancellation = default)
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
            return (Edge: edge, Direction: new Vector3((float)(dx / length), 0, (float)(dz / length)),
                other.Position, Grid: RailwayPath.Endpoint(snapshot, edge.Id, id));
        }).ToArray();
        double Cost(Vector3 axis) => directions.Sum(item => Math.Acos(Math.Clamp(Math.Abs(Vector3.Dot(item.Direction, axis)), 0, 1)));
        var axis = Cost(Vector3.UnitX) < Cost(Vector3.UnitZ) - 1e-8 ? Vector3.UnitX : Vector3.UnitZ;
        var right = Vector3.Cross(Vector3.UnitY, axis);
        var stationExtent = StationTrackSpacing * Math.Max(0, node.Platforms - 1) / 2.0;
        var endV = (node.Platforms > 0 ? PlatformLength / 2.0 : 0) + PlatformClearance;
        var entries = new List<(Entry Entry, ScenePoint Grid, double Separation)>();
        var extent = node.Platforms > 0 ? Math.Max(endV, stationExtent + PlatformClearance + PlatformWidth) : 0;
        var corridor = 0.0;
        foreach (var item in directions.OrderBy(item => Math.Acos(Math.Clamp(Math.Abs(Vector3.Dot(item.Direction, axis)), 0, 1)))
            .ThenBy(item => item.Edge.Id.Value))
        {
            cancellation.ThrowIfCancellationRequested();
            var side = Vector3.Dot(item.Direction, axis) >= 0 ? 1 : -1;
            var inward = -item.Direction; var endForward = -side * axis; var endRight = -side * right;
            var angle = Math.Atan2(Vector3.Dot(inward, endRight), Math.Max(0, Vector3.Dot(inward, endForward)));
            var turn = angle >= 0 ? 1 : -1;
            // A 30-degree intermediate tangent bounds lateral division and keeps the arcs' forward span at most R.
            var theta = Math.Abs(angle); var phi = Math.Max(theta, Math.PI / 6);
            var laneWidth = RailwayCoordinates.OutermostTrackOffset(item.Edge.TrackLimit);
            var separation = entries.Where(other => other.Entry.Side == side && Math.Sign(other.Entry.BendAngle) == turn)
                .Sum(other => 2 * RailwayCoordinates.OutermostTrackOffset(other.Entry.TrackLimit) +
                    2 * RailwayCoordinates.BedBaseHalfWidth + EntryLateralGap);
            var lateralArc = ArcSpans(theta, phi, radius).Lateral;
            var w = -turn * (stationExtent + lateralArc + laneWidth + separation + ApproachStraight * Math.Sin(theta) + ConnectionSnapMargin);
            var u = -side * w;
            var desired = StationPose(axis, u, 0).Position;
            var snapped = new ScenePoint(item.Grid.X + Math.Round(desired.X - item.Grid.X), 0,
                item.Grid.Z + Math.Round(desired.Z - item.Grid.Z));
            var entry = new Entry(item.Edge.Id, snapped, item.Direction, item.Edge.TrackLimit, side, angle, turn * phi);
            var forwardShift = 0.0;
            for (var lane = 1 - entry.TrackLimit; lane < entry.TrackLimit; lane++)
            for (var station = 0; station < Math.Max(1, node.Platforms); station++)
                forwardShift = Math.Max(forwardShift, -SolveConnection(entry, lane, station, node.Platforms, axis, radius).Final);
            entry = entry with { Point = RailwayCoordinates.Add(snapped, axis * (float)(side * (forwardShift + ConnectionSnapMargin))) };
            extent = Math.Max(extent, Math.Max(Math.Abs(u) + laneWidth,
                Math.Abs(Vector3.Dot(RailwayCoordinates.Relative(entry.Point, default), axis))));
            corridor = Math.Max(corridor, laneWidth + RailwayCoordinates.BedBaseHalfWidth);
            entries.Add((entry, item.Grid, separation));
        }
        // Keep the inner stems separate too. A lateral gap needs its projection along the
        // intermediate tangent, plus the gap itself to separate the outward queue rays.
        for (var i = 0; i < entries.Count; i++)
        {
            var (entry, grid, separation) = entries[i];
            var lastSeparation = entries.Where(other => other.Entry.Side == entry.Side && Math.Sign(other.Entry.BendAngle) == Math.Sign(entry.BendAngle))
                .Max(other => other.Separation);
            var lateralGap = lastSeparation - separation;
            var tangentProjection = lateralGap / Math.Tan(Math.Abs(entry.BendAngle));
            entry = entry with { Point = RailwayCoordinates.Add(entry.Point, axis * (float)(entry.Side * (lateralGap + tangentProjection))) };
            entries[i] = (entry, grid, separation);
            extent = Math.Max(extent, Math.Max(Math.Abs(entry.Point.X), Math.Abs(entry.Point.Z)));
        }
        var halfSize = Math.Ceiling((extent + corridor + GroundMargin + (directions.Length > 0 ? RailwayCoordinates.CellFootprintMargin : 0)) / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize;
        for (var i = 0; i < entries.Count; i++)
        {
            var (entry, grid, separation) = entries[i];
            double ToFace(double coordinate, float outward) => Math.Abs(outward) < 1e-8 ? double.PositiveInfinity :
                (Math.CopySign(halfSize, outward) - coordinate) / outward;
            var stem = Math.Min(ToFace(entry.Point.X, entry.Outward.X), ToFace(entry.Point.Z, entry.Outward.Z));
            var desired = RailwayCoordinates.Add(entry.Point, entry.Outward * (float)stem);
            entries[i] = (entry with { Point = new(grid.X + Math.Round(desired.X - grid.X), 0,
                grid.Z + Math.Round(desired.Z - grid.Z)), StraightLength = stem }, grid, separation);
        }
        return new(id, node.Platforms, halfSize, ReadInputs(snapshot, id),
            entries.Select(item => item.Entry).OrderBy(entry => entry.Edge.Value).ToImmutableArray(), axis, radius);
    }

    internal static string ReadInputs(SceneWorldSnapshot snapshot, NodeId id)
    {
        var world = snapshot.Core.World;
        var node = world.Nodes.Single(item => item.Id == id);
        var rule = snapshot.Content.FindRailwayAppearance(world.RailwayRule);
        return RailwayDependencies.Values(new object[] { id, node.Position, node.Platforms,
            world.Edges.Where(edge => edge.Start == id || edge.End == id).OrderBy(edge => edge.Id.Value)
                .Select(edge => new object[] { edge.Id, edge.TrackLimit, edge.LengthMeters, edge.Terrain.Relief,
                    world.Nodes.Single(other => other.Id == (edge.Start == id ? edge.End : edge.Start)).Position }).ToArray(),
            rule is ContentLookup<RailwayAppearanceProjection>.Found found
                ? new object[] { found.Value.MinimumCurveRadiusMeters * RailwayCoordinates.CellsPerMeter, found.Value.MaximumGrade } : rule });
    }

    private static (double Lateral, double Forward) ArcSpans(double theta, double phi, double radius) =>
        (radius * (1 + Math.Cos(theta) - 2 * Math.Cos(phi)), radius * (2 * Math.Sin(phi) - Math.Sin(theta)));

    private static (ScenePoint Start, Vector3 Forward, Vector3 Right, double FirstArc, double Middle, double SecondArc, double Final)
        SolveConnection(Entry entry, int lane, int station, int platforms, Vector3 axis, double radius)
    {
        var inward = -entry.Outward; var forward = -entry.Side * axis;
        var right = Vector3.Cross(Vector3.UnitY, forward);
        var offset = lane * RailwayCoordinates.TrackSpacing / 2;
        var stemStart = RailwayCoordinates.Add(entry.Point, inward * ApproachStraight + Vector3.Cross(Vector3.UnitY, inward) * (float)offset);
        var start = RailwayCoordinates.Add(stemStart, inward * (float)entry.StraightLength);
        var endV = (platforms > 0 ? PlatformLength / 2.0 : 0) + PlatformClearance;
        var target = StationPose(axis, platforms > 0 ? StationX(station, platforms) : 0, entry.Side * endV).Position;
        var displacement = RailwayCoordinates.Relative(target, start);
        var theta = Math.Abs(entry.Angle); var phi = Math.Abs(entry.BendAngle); var turn = Math.Sign(entry.BendAngle);
        var spans = ArcSpans(theta, phi, radius);
        var middle = (turn * Vector3.Dot(displacement, right) - spans.Lateral) / Math.Sin(phi);
        if (middle < -ConnectionTolerance) throw new RailwayBuildException("The node entry corridor cannot reach its station tracks.");
        middle = Math.Max(0, middle);
        var final = Vector3.Dot(displacement, forward) - spans.Forward - middle * Math.Cos(phi);
        return (stemStart, forward, right, radius * (phi - theta), middle, radius * phi, final);
    }

    internal static double StationX(int index, int count) => StationTrackSpacing * (index - (count - 1) / 2.0);
    private static RailwaySample StationPose(Vector3 axis, double u, double v)
    {
        var right = Vector3.Cross(Vector3.UnitY, axis);
        return new(new(right.X * u + axis.X * v, 0, right.Z * u + axis.Z * v), axis, right);
    }
    internal RailwaySample StationSample(double u, double v) => StationPose(StationForward, u, v);
    internal Entry EntryFor(EdgeId edge) => Entries.Single(item => item.Edge == edge);
    internal Track Connection(EdgeId edge, int laneSlot, int station)
    {
        var entry = EntryFor(edge);
        var pieces = SolveConnection(entry, laneSlot, station, Platforms, StationForward, radius);
        if (pieces.Final < -ConnectionTolerance) throw new RailwayBuildException("The node entry corridor cannot reach its station tracks.");
        var turn = Math.Sign(entry.BendAngle);
        return new(pieces.Start, pieces.Forward, pieces.Right, entry.Angle,
            [(entry.StraightLength, 0), (pieces.FirstArc, turn / radius), (pieces.Middle, 0),
                (pieces.SecondArc, -turn / radius), (Math.Max(0, pieces.Final), 0)]);
    }
    internal Track Connection(EdgeId edge, double offset, int station) => Connection(edge, (int)Math.Round(offset * 2 / RailwayCoordinates.TrackSpacing), station);
    internal ScenePoint EntryPoint(EdgeId edge) => EntryFor(edge).Point;
    internal RailwaySample QueueSample(Entry entry, double distance, double offset = 0)
    {
        var forward = -entry.Outward; var right = Vector3.Cross(Vector3.UnitY, forward);
        return new(RailwayCoordinates.Add(entry.Point, forward * (float)(ApproachStraight + entry.StraightLength - distance) + right * (float)offset), forward, right);
    }
    internal ScenePanBounds PanBounds(ScenePoint origin)
    {
        var minimum = new Vector2((float)-HalfSize - RailwayCoordinates.CellFootprintMargin);
        var maximum = new Vector2((float)HalfSize + RailwayCoordinates.CellFootprintMargin);
        foreach (var entry in Entries)
        {
            var right = Vector3.Cross(Vector3.UnitY, entry.Outward);
            var width = RailwayCoordinates.TerrainHalfWidth(entry.TrackLimit) + RailwayCoordinates.BoundaryGridMargin;
            var extent = new Vector2(Math.Abs(right.X) * (float)width + RailwayCoordinates.BoundaryGridMargin,
                Math.Abs(right.Z) * (float)width + RailwayCoordinates.BoundaryGridMargin);
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
