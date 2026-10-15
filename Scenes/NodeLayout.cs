using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class NodeLayout
{
    internal const int PlatformClearance = 16;
    internal const int StationTrackSpacing = 40;
    internal const int GroundMargin = 32;
    internal const int ApproachStraight = 16;
    internal const int QueueGap = 4;
    internal const int EntryLateralGap = 4;
    // Two grid snaps contribute at most 0.5 + 0.5 = 1 cell per axis.
    // The first snap is included in forwardShift; the final snap can shorten the last straight by
    // 0.5 + 0.5 * cot(phi) <= 1.37 cells (phi >= 30 degrees), so the reserve rounds up to two.
    private const int ConnectionSnapMargin = 2;
    private const double ConnectionTolerance = 1e-4;
    internal sealed record Entry(EdgeId Edge, ScenePoint Point, Vector3 Outward, int TrackLimit,
        int Side, double Angle, double BendAngle, double StraightLength = 0);
    internal NodeId Node { get; }
    internal sealed record Stop(int PlotNumber, double LocalZ, ScenePoint Center, Vector3 Forward);
    internal sealed record ParkingLine(ScenePoint Start, Vector3 Forward, double Length);
    internal SettlementLayoutProjection? Template { get; private init; }
    internal ImmutableArray<Stop> Stops { get; private init; } = [];
    internal ImmutableArray<ParkingLine> Parking { get; private init; } = [];
    internal ImmutableArray<SceneProblem> Problems { get; private init; } = [];
    internal ScenePanBounds Bounds { get; private set; }
    private ImmutableArray<Stop> routeStops = [];
    private readonly Dictionary<(EdgeId, int, int), Track> connections = [];
    private ImmutableArray<RailwaySample> samples = [];
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
        Bounds = new(new((float)-halfSize), new((float)halfSize));
    }

    internal static NodeLayout Build(SceneWorldSnapshot snapshot, NodeId id, CancellationToken cancellation = default)
    {
        var world = snapshot.Core.World;
        var node = world.Nodes.Single(item => item.Id == id);
        var template = NodeSettlement.Read(snapshot, id);
        if (template is not null && template.Plots.Any(NodeSettlement.Station))
            return BuildSettlement(snapshot, id, template, cancellation);
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
        var entries = new List<(Entry Entry, ScenePoint Grid, double Separation)>();
        var extent = 0.0;
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
            var w = -turn * (lateralArc + laneWidth + separation + ApproachStraight * Math.Sin(theta) + ConnectionSnapMargin);
            var u = -side * w;
            var desired = StationPose(axis, u, 0).Position;
            var snapped = new ScenePoint(item.Grid.X + Math.Round(desired.X - item.Grid.X), 0,
                item.Grid.Z + Math.Round(desired.Z - item.Grid.Z));
            var entry = new Entry(item.Edge.Id, snapped, item.Direction, item.Edge.TrackLimit, side, angle, turn * phi);
            var forwardShift = 0.0;
            for (var lane = 1 - entry.TrackLimit; lane < entry.TrackLimit; lane++)
                forwardShift = Math.Max(forwardShift, -SolveConnection(entry, lane, axis, radius).Final);
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
        if (template is not null)
        {
            var bounds = NodeSettlement.Bounds(template);
            extent = Math.Max(extent, Math.Max(Math.Max(Math.Abs(bounds.Minimum.X), Math.Abs(bounds.Maximum.X)),
                Math.Max(Math.Abs(bounds.Minimum.Y), Math.Abs(bounds.Maximum.Y))));
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
        var result = new NodeLayout(id, 0, halfSize, ReadInputs(snapshot, id),
            entries.Select(item => item.Entry).OrderBy(entry => entry.Edge.Value).ToImmutableArray(), axis, radius)
            { Template = template };
        result.CaptureSamples(snapshot);
        return result;
    }

    internal static string ReadInputs(SceneWorldSnapshot snapshot, NodeId id)
    {
        var world = snapshot.Core.World;
        var node = world.Nodes.Single(item => item.Id == id);
        var rule = snapshot.Content.FindRailwayAppearance(world.RailwayRule);
        return RailwayDependencies.Values(new object[] { id, node.Position, NodeSettlement.Inputs(NodeSettlement.Read(snapshot, id)),
            world.Edges.Where(edge => edge.Start == id || edge.End == id).OrderBy(edge => edge.Id.Value)
                .Select(edge => new object[] { edge.Id, edge.TrackLimit, edge.LengthMeters, edge.Terrain.Relief,
                    world.Nodes.Single(other => other.Id == (edge.Start == id ? edge.End : edge.Start)).Position }).ToArray(),
            rule is ContentLookup<RailwayAppearanceProjection>.Found found
                ? new object[] { found.Value.MinimumCurveRadiusMeters * RailwayCoordinates.CellsPerMeter, found.Value.MaximumGrade } : rule });
    }

    private static (double Min, double Max) AxisRange(ScenePanBounds bounds, Vector3 axis)
    {
        var values = new[] { bounds.Minimum, bounds.Maximum, new Vector2(bounds.Minimum.X, bounds.Maximum.Y),
            new Vector2(bounds.Maximum.X, bounds.Minimum.Y) }.Select(p => p.X * axis.X + p.Y * axis.Z).ToArray();
        return (values.Min(), values.Max());
    }

    private static NodeLayout BuildSettlement(SceneWorldSnapshot snapshot, NodeId id, SettlementLayoutProjection template,
        CancellationToken cancellation)
    {
        var world = snapshot.Core.World;
        var node = world.Nodes.Single(item => item.Id == id);
        var rule = snapshot.Content.FindRailwayAppearance(world.RailwayRule) is ContentLookup<RailwayAppearanceProjection>.Found found
            ? found.Value : throw new RailwayBuildException("The railway appearance is missing.");
        var radius = rule.MinimumCurveRadiusMeters * RailwayCoordinates.CellsPerMeter;
        var first = template.Plots.Where(NodeSettlement.Station).OrderBy(p => p.Number).First();
        var firstPose = SceneGeometry.Placement(first.At, first.Orientation);
        var axis = Vector3.Transform(Vector3.UnitX, firstPose.Rotation);
        var across = Vector3.Transform(Vector3.UnitZ, firstPose.Rotation);
        var stops = ImmutableArray.CreateBuilder<Stop>();
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        foreach (var plot in template.Plots.Where(NodeSettlement.Station).OrderBy(p => p.Number))
        {
            var pose = SceneGeometry.Placement(plot.At, plot.Orientation);
            var forward = Vector3.Transform(Vector3.UnitX, pose.Rotation);
            if (Math.Abs(Vector3.Dot(forward, axis)) < .999f)
            {
                problems.Add(new("station_axis_mismatch", SceneStage.Build, new WorldObjectId.Node(id), null, null, null,
                    $"Station plot {plot.Number} uses a different railway axis."));
                continue;
            }
            foreach (var z in plot.Size == PlotSize.SmallStation ? new[] { -40, 40 } : new[] { -80, -40, 40, 80 })
            {
                var center = NodeSettlement.Point(pose, 0, 0, z);
                stops.Add(new(plot.Number, z, new(center.X, center.Y, center.Z), forward));
            }
        }
        var lines = stops.GroupBy(stop => (Along: Math.Round(Vector3.Dot(RailwayCoordinates.Relative(stop.Center, default), across), 4),
            Height: stop.Center.Y)).Select(group => group.First()).ToImmutableArray();
        var templateBounds = NodeSettlement.Bounds(template);
        var along = AxisRange(templateBounds, axis);
        var lateral = AxisRange(templateBounds, across);
        var parking = Enumerable.Range(0, 2).Select(index =>
        {
            var at = axis * (float)along.Min + across * (float)(lateral.Min - GroundMargin - index * StationTrackSpacing);
            return new ParkingLine(new(at.X, firstPose.Position.Y, at.Z), axis, along.Max - along.Min);
        }).ToImmutableArray();
        var entries = new List<Entry>();
        var separations = new Dictionary<EdgeId, double>();
        var groups = new Dictionary<(int Side, int Turn), double>();
        foreach (var edge in world.Edges.Where(edge => edge.Start == id || edge.End == id).OrderBy(edge => edge.Id.Value))
        {
            cancellation.ThrowIfCancellationRequested();
            var other = world.Nodes.Single(item => item.Id == (edge.Start == id ? edge.End : edge.Start));
            var outward = Vector3.Normalize(new((float)(other.Position.X - node.Position.X), 0, (float)(other.Position.Y - node.Position.Y)));
            var side = Vector3.Dot(outward, axis) >= 0 ? 1 : -1;
            var forward = -side * axis; var right = Vector3.Cross(Vector3.UnitY, forward);
            var inward = -outward; var laneRight = Vector3.Cross(Vector3.UnitY, inward);
            var angle = Math.Atan2(Vector3.Dot(inward, right), Math.Max(0, Vector3.Dot(inward, forward)));
            var theta = Math.Abs(angle); var phi = Math.Max(theta, Math.PI / 6); var turn = angle >= 0 ? 1 : -1;
            var spans = ArcSpans(theta, phi, radius);
            var targets = lines.Select(stop => Target(stop, side)).ToArray();
            var lanes = Enumerable.Range(1 - edge.TrackLimit, 2 * edge.TrackLimit - 1).Select(lane => lane * RailwayCoordinates.TrackSpacing / 2).ToArray();
            var separation = groups.GetValueOrDefault((side, turn));
            separations.Add(edge.Id, separation);
            groups[(side, turn)] = separation + 2 * RailwayCoordinates.OutermostTrackOffset(edge.TrackLimit) + 2 * PlatformClearance + EntryLateralGap;
            var v = turn * (targets.Min(p => turn * Vector3.Dot(RailwayCoordinates.Relative(p, default), right)) - spans.Lateral -
                ApproachStraight * turn * Vector3.Dot(inward, right) - lanes.Max(offset => turn * offset * Vector3.Dot(laneRight, right)) - GroundMargin - separation);
            double Middle(ScenePoint target, double offset) => (turn * (Vector3.Dot(RailwayCoordinates.Relative(target, default), right) - v -
                ApproachStraight * Vector3.Dot(inward, right) - offset * Vector3.Dot(laneRight, right)) - spans.Lateral) / Math.Sin(phi);
            var u = targets.Min(p => lanes.Min(offset => Vector3.Dot(RailwayCoordinates.Relative(p, default), forward) -
                ApproachStraight * Vector3.Dot(inward, forward) - offset * Vector3.Dot(laneRight, forward) - spans.Forward -
                Middle(p, offset) * Math.Cos(phi) - Math.Max(ApproachStraight, 1.5 * Math.Abs(p.Y) / rule.MaximumGrade))) - ConnectionSnapMargin;
            var desired = forward * (float)u + right * (float)v;
            var grid = RailwayPath.Endpoint(snapshot, edge.Id, id);
            var point = new ScenePoint(grid.X + Math.Round(desired.X - grid.X), 0, grid.Z + Math.Round(desired.Z - grid.Z));
            entries.Add(new(edge.Id, point, outward, edge.TrackLimit, side, angle, turn * phi));
        }
        // Lateral fan-out alone moves parallel entries along the same ray. Separate their inner stems too.
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var last = entries.Where(other => other.Side == entry.Side && Math.Sign(other.BendAngle) == Math.Sign(entry.BendAngle))
                .Max(other => separations[other.Edge]);
            var gap = last - separations[entry.Edge];
            var desired = RailwayCoordinates.Add(entry.Point, axis * (float)(entry.Side * (gap + gap / Math.Tan(Math.Abs(entry.BendAngle)))));
            var grid = RailwayPath.Endpoint(snapshot, entry.Edge, id);
            entries[i] = entry with { Point = new(grid.X + Math.Round(desired.X - grid.X), 0, grid.Z + Math.Round(desired.Z - grid.Z)) };
        }
        var minimum = templateBounds.Minimum - new Vector2(GroundMargin);
        var maximum = templateBounds.Maximum + new Vector2(GroundMargin);
        foreach (var line in parking)
        { Include(line.Start, GroundMargin); Include(RailwayCoordinates.Add(line.Start, line.Forward * (float)line.Length), GroundMargin); }
        foreach (var entry in entries)
        for (var lane = 1 - entry.TrackLimit; lane < entry.TrackLimit; lane++)
        for (var line = 0; line < lines.Length; line++)
        {
            var track = Connect(entry, lane, lines[line]);
            for (var arc = 0.0; arc < track.Length + 4; arc += 4)
                Include(track.Sample(Math.Min(arc, track.Length)).Position, RailwayCoordinates.TerrainHalfWidth(entry.TrackLimit) + GroundMargin);
        }
        minimum = new(MathF.Floor(minimum.X / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize, MathF.Floor(minimum.Y / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize);
        maximum = new(MathF.Ceiling(maximum.X / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize, MathF.Ceiling(maximum.Y / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            double Face(double coordinate, float direction, double min, double max) => Math.Abs(direction) < 1e-8 ? double.PositiveInfinity :
                ((direction > 0 ? max : min) - coordinate) / direction;
            var stem = Math.Min(Face(entry.Point.X, entry.Outward.X, minimum.X, maximum.X), Face(entry.Point.Z, entry.Outward.Z, minimum.Y, maximum.Y));
            var end = RailwayCoordinates.Add(entry.Point, entry.Outward * (float)stem);
            var point = new ScenePoint(entry.Point.X + Math.Round(end.X - entry.Point.X), 0, entry.Point.Z + Math.Round(end.Z - entry.Point.Z));
            // Use the actual snapped straight length in both node and line boundary sampling.
            var actualStem = Vector3.Dot(RailwayCoordinates.Relative(point, entry.Point), entry.Outward);
            entries[i] = entry with { Point = point, StraightLength = actualStem };
        }
        var result = new NodeLayout(id, stops.Count, Math.Max(Math.Max(Math.Abs(minimum.X), Math.Abs(maximum.X)), Math.Max(Math.Abs(minimum.Y), Math.Abs(maximum.Y))),
            ReadInputs(snapshot, id), entries.ToImmutableArray(), axis, radius)
        { Template = template, Stops = stops.ToImmutable(), Parking = parking, Problems = problems.ToImmutable(), Bounds = new(minimum, maximum), routeStops = lines };
        foreach (var entry in result.Entries)
        for (var lane = 1 - entry.TrackLimit; lane < entry.TrackLimit; lane++)
        for (var line = 0; line < lines.Length; line++) result.connections.Add((entry.Edge, lane, line), Connect(entry, lane, lines[line]));
        result.CaptureSamples(snapshot);
        return result;

        ScenePoint Target(Stop stop, int side)
        {
            var projection = Vector3.Dot(RailwayCoordinates.Relative(stop.Center, default), axis);
            return RailwayCoordinates.Add(stop.Center, axis * (float)((side > 0 ? along.Max + GroundMargin : along.Min - GroundMargin) - projection));
        }
        Track Connect(Entry entry, int lane, Stop stop)
        {
            var inward = -entry.Outward; var forward = -entry.Side * axis; var right = Vector3.Cross(Vector3.UnitY, forward);
            var stemStart = RailwayCoordinates.Add(entry.Point, inward * ApproachStraight + Vector3.Cross(Vector3.UnitY, inward) * (float)(lane * RailwayCoordinates.TrackSpacing / 2));
            var start = RailwayCoordinates.Add(stemStart, inward * (float)entry.StraightLength);
            var target = Target(stop, entry.Side);
            var delta = RailwayCoordinates.Relative(target, start);
            var theta = Math.Abs(entry.Angle); var phi = Math.Abs(entry.BendAngle); var turn = Math.Sign(entry.BendAngle);
            var spans = ArcSpans(theta, phi, radius);
            var middle = (turn * Vector3.Dot(delta, right) - spans.Lateral) / Math.Sin(phi);
            var final = Vector3.Dot(delta, forward) - spans.Forward - middle * Math.Cos(phi);
            return new(stemStart, forward, right, entry.Angle, [(entry.StraightLength, 0), (radius * (phi - theta), turn / radius),
                (middle, 0), (radius * phi, -turn / radius), (final, 0)], target.Y);
        }
        void Include(ScenePoint p, double margin)
        { var at = new Vector2((float)p.X, (float)p.Z); minimum = Vector2.Min(minimum, at - new Vector2((float)margin)); maximum = Vector2.Max(maximum, at + new Vector2((float)margin)); }
    }

    private void CaptureSamples(SceneWorldSnapshot snapshot)
    {
        var points = ImmutableArray.CreateBuilder<RailwaySample>();
        var routes = Entries.SelectMany(entry => EntryRoutes(entry, snapshot.Core.World.Edges.Single(e => e.Id == entry.Edge).Tracks))
            .Concat(StationRoutes(true)).Concat(Parking.Select(line => ((Func<double, RailwaySample>)(arc => new(RailwayCoordinates.Add(line.Start, line.Forward * (float)arc), line.Forward,
                Vector3.Cross(Vector3.UnitY, line.Forward))), line.Length)));
        foreach (var (sample, length) in routes)
            for (var arc = 0.0; arc < length + 2; arc += 2) points.Add(sample(Math.Min(arc, length)));
        samples = points.ToImmutable();
    }
    internal bool ContainsPlot(ScenePoint point) => Template is { } template && NodeSettlement.ContainsPlot(template, point);
    internal bool ClearOfTracks(ScenePoint point, double clearance) => samples.All(sample =>
        Math.Pow(point.X - sample.Position.X, 2) + Math.Pow(point.Z - sample.Position.Z, 2) >= Math.Pow(clearance + 1, 2));

    private static (double Lateral, double Forward) ArcSpans(double theta, double phi, double radius) =>
        (radius * (1 + Math.Cos(theta) - 2 * Math.Cos(phi)), radius * (2 * Math.Sin(phi) - Math.Sin(theta)));

    private static (ScenePoint Start, Vector3 Forward, Vector3 Right, double FirstArc, double Middle, double SecondArc, double Final)
        SolveConnection(Entry entry, int lane, Vector3 axis, double radius)
    {
        var inward = -entry.Outward; var forward = -entry.Side * axis;
        var right = Vector3.Cross(Vector3.UnitY, forward);
        var offset = lane * RailwayCoordinates.TrackSpacing / 2;
        var stemStart = RailwayCoordinates.Add(entry.Point, inward * ApproachStraight + Vector3.Cross(Vector3.UnitY, inward) * (float)offset);
        var start = RailwayCoordinates.Add(stemStart, inward * (float)entry.StraightLength);
        var target = StationPose(axis, 0, entry.Side * PlatformClearance).Position;
        var displacement = RailwayCoordinates.Relative(target, start);
        var theta = Math.Abs(entry.Angle); var phi = Math.Abs(entry.BendAngle); var turn = Math.Sign(entry.BendAngle);
        var spans = ArcSpans(theta, phi, radius);
        var middle = (turn * Vector3.Dot(displacement, right) - spans.Lateral) / Math.Sin(phi);
        if (middle < -ConnectionTolerance) throw new RailwayBuildException("The node entry corridor cannot reach its station tracks.");
        middle = Math.Max(0, middle);
        var final = Vector3.Dot(displacement, forward) - spans.Forward - middle * Math.Cos(phi);
        return (stemStart, forward, right, radius * (phi - theta), middle, radius * phi, final);
    }

    private static RailwaySample StationPose(Vector3 axis, double u, double v)
    {
        var right = Vector3.Cross(Vector3.UnitY, axis);
        return new(new(right.X * u + axis.X * v, 0, right.Z * u + axis.Z * v), axis, right);
    }
    internal RailwaySample StationSample(double u, double v) => StationPose(StationForward, u, v);
    internal IEnumerable<(Func<double, RailwaySample> Sample, double Length)> EntryRoutes(Entry entry, int tracks)
    {
        for (var number = 1; number <= tracks; number++)
        {
            var offset = RailwayCoordinates.TrackOffset(number, tracks);
            var forward = -entry.Outward;
            var right = Vector3.Cross(Vector3.UnitY, forward);
            var start = RailwayCoordinates.Add(entry.Point, right * (float)offset);
            yield return (arc => new(RailwayCoordinates.Add(start, forward * (float)arc), forward, right), ApproachStraight);
            for (var station = 0; station < Math.Max(1, routeStops.Length); station++)
            {
                var curve = Connection(entry.Edge, offset, station);
                yield return (curve.Sample, curve.Length);
            }
        }
    }
    internal IEnumerable<(Func<double, RailwaySample> Sample, double Length)> StationRoutes(bool anyTracks)
    {
        if (!routeStops.IsEmpty)
        {
            var extent = AxisRange(NodeSettlement.Bounds(Template!), StationForward);
            foreach (var stop in routeStops)
            {
                var u = Vector3.Dot(RailwayCoordinates.Relative(stop.Center, default), StationForward);
                var start = RailwayCoordinates.Add(stop.Center, StationForward * (float)(extent.Min - GroundMargin - u));
                yield return (arc => new(RailwayCoordinates.Add(start, StationForward * (float)arc), StationForward,
                    Vector3.Cross(Vector3.UnitY, StationForward)), extent.Max - extent.Min + GroundMargin * 2);
            }
            yield break;
        }
        if (!anyTracks) yield break;
        yield return (arc => StationSample(0, -PlatformClearance + arc), PlatformClearance * 2);
    }
    internal Entry EntryFor(EdgeId edge) => Entries.Single(item => item.Edge == edge);
    internal Track Connection(EdgeId edge, int laneSlot, int station)
    {
        if (!routeStops.IsEmpty) return connections[(edge, laneSlot, station)];
        var entry = EntryFor(edge);
        var pieces = SolveConnection(entry, laneSlot, StationForward, radius);
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
        var minimum = Bounds.Minimum;
        var maximum = Bounds.Maximum;
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
        private readonly double endHeight;
        private readonly double rampStart;
        internal double Length { get; }
        internal Track(ScenePoint start, Vector3 forward, Vector3 right, double angle,
            (double Length, double Curvature)[] pieces, double endHeight = 0)
        {
            this.start = start; this.forward = forward; this.right = right; this.angle = angle;
            this.pieces = pieces.Where(piece => piece.Length > 1e-8).ToArray();
            Length = this.pieces.Sum(piece => piece.Length);
            this.endHeight = endHeight;
            rampStart = Length - this.pieces.LastOrDefault().Length;
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
            var t = Math.Clamp((arc - rampStart) / Math.Max(Length - rampStart, 1e-9), 0, 1);
            var rise = endHeight - start.Y;
            point = point with { Y = start.Y + rise * t * t * (3 - 2 * t) };
            var slope = t > 0 && t < 1 ? rise * 6 * t * (1 - t) / (Length - rampStart) : 0;
            tangent = Vector3.Normalize(tangent + Vector3.UnitY * (float)slope);
            return new(point, tangent, Vector3.Normalize(Vector3.Cross(Vector3.UnitY, tangent)));
        }
    }
}
