using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class RailwayBuildException(string message) : Exception(message);

internal readonly record struct RailwaySample(ScenePoint Position, Vector3 Forward, Vector3 Right);

internal static class RailwayCoordinates
{
    internal const double CellsPerMeter = 2;
    internal const double SegmentLengthCells = 128;
    internal const double TrackSpacing = 34;
    internal const double BedTopHalfWidth = 9.5;
    internal const double BedBaseHalfWidth = 10.5;
    internal const double BedBlendWidth = 12;
    internal const double TunnelClearance = 20;
    internal const double DecorationRootClearance = 24;
    internal const double DecorationVoxelClearance = 17;
    internal const double TerrainBaselineOffset = 4;
    internal const int TerrainDepthCells = 6;
    internal const double NodeTransitionCells = 128;
    internal const double WaterSurfaceOffset = 2;
    internal const double WaterDepth = 5;
    internal const int ViewRadiusSegments = 2;
    internal const int MaxConcurrentBuilds = 2;
    internal const double PrefetchSeconds = 4;
    internal const double MaximumDisplayRate = 64;
    internal const int MaxPrefetchSegments = 4;
    internal const double OriginStepCells = 256;
    internal const double TerrainSlopeMarginCells = 26.5;
    internal const double FogHorizontalPaddingCells = 8;
    internal const float FogVerticalPaddingCells = 128;
    // Cover a voxel's footprint beyond its sampled centre, including endpoint grid snapping.
    internal const int CellFootprintMargin = 1;
    // The node and line endpoints each round by half a cell; include the full boundary voxel too.
    internal const int BoundaryGridMargin = 2;
    internal static double OutermostTrackOffset(int count) => Math.Max(0, count - 1) * TrackSpacing / 2;
    internal static double TerrainHalfWidth(int count) =>
        OutermostTrackOffset(count) + BedBaseHalfWidth + BedBlendWidth + TerrainSlopeMarginCells;
    internal static double FogHalfWidth(int count) => TerrainHalfWidth(count) + FogHorizontalPaddingCells;
    internal static readonly (double Gauge, float RailHeight, double Radius, double Width)[] WheelDimensions =
        [(3, .3f, .5, .25), (7, .4f, 1, .5), (15, .6f, 2, 1)];
    internal static (double Gauge, float RailHeight, double Radius, double Width) Wheels(SizeClass size) => size switch
    {
        SizeClass.Small => WheelDimensions[0],
        SizeClass.Medium or SizeClass.Large => WheelDimensions[1],
        SizeClass.ExtraLarge or SizeClass.Titan => WheelDimensions[2],
        _ => throw new ArgumentException("Invalid vehicle size.", nameof(size))
    };
    internal static Quaternion Rotation(Vector3 forward)
    {
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        var up = Vector3.Cross(forward, right);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new(
            right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0,
            forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1)));
    }
    // A common level datum permits straight, zero-slack edges. Local relief is generated around it.
    internal static double NodeElevation(NodeMapItem node) => 0;
    internal static ScenePoint NodePoint(NodeMapItem node) =>
        new(node.Position.X * CellsPerMeter, NodeElevation(node), node.Position.Y * CellsPerMeter);
    internal static double TrackOffset(int number, int count) => (number - (count + 1) / 2.0) * TrackSpacing;
    internal static (double Radius, double MinimumChord) AlignmentLimits(
        double horizontalLength, int trackLimit, RailwayAppearanceProjection rules)
    {
        var width = BedBaseHalfWidth + OutermostTrackOffset(trackLimit);
        // Keep the inner offset arc regular, including when the corridor is wider than the rule radius.
        var radius = Math.Max(rules.MinimumCurveRadiusMeters * CellsPerMeter, width + 0.5);
        return (radius, 4 * radius * Math.Sin(Math.Min(horizontalLength / (4 * radius), Math.PI / 2)));
    }
    internal static Vector3 Relative(ScenePoint point, ScenePoint origin) =>
        new((float)(point.X - origin.X), (float)(point.Y - origin.Y), (float)(point.Z - origin.Z));
    internal static ScenePoint Add(ScenePoint point, Vector3 offset) =>
        new(point.X + offset.X, point.Y + offset.Y, point.Z + offset.Z);
}

internal sealed class RailwayPath
{
    internal EdgeMapItem Edge { get; }
    internal NodeMapItem Start { get; }
    internal NodeMapItem End { get; }
    internal ulong Seed { get; }
    internal RailwayAppearanceProjection Rules { get; }
    private readonly Lazy<NodeLayout> startLayout;
    private readonly Lazy<NodeLayout> endLayout;
    internal NodeLayout StartLayout => startLayout.Value;
    internal NodeLayout EndLayout => endLayout.Value;
    internal double LengthCells { get; }
    internal int SegmentCount => checked((int)Math.Ceiling(LengthCells / RailwayCoordinates.SegmentLengthCells));
    private readonly ScenePoint start;
    private readonly double forwardX;
    private readonly double forwardZ;
    private readonly double side;
    private readonly double rise;
    private readonly HorizontalAlignment horizontal;
    private readonly CircularBend vertical;
    private readonly (double Begin, double End, double Curvature)[] trackPieces;
    private const ulong HorizontalSidePurpose = 1;
    private const ulong VerticalSidePurpose = 2;
    // Reserve half the permitted grade; relief scales the shallow, level-ended vertical bend.
    private const double GradeReserve = 0.5;
    private const double ReliefSlopeRadians = 0.03;

    internal RailwayPath(SceneWorldSnapshot snapshot, EdgeId id, CancellationToken cancellation = default)
    {
        Edge = snapshot.Core.FindEdge(id) is Lookup<EdgeView>.Found edge ? edge.Value.Map :
            throw new RailwayBuildException("The railway edge is missing.");
        Start = snapshot.Core.FindNode(Edge.Start) is Lookup<NodeView>.Found first ? first.Value.Map :
            throw new RailwayBuildException("The start node is missing.");
        End = snapshot.Core.FindNode(Edge.End) is Lookup<NodeView>.Found last ? last.Value.Map :
            throw new RailwayBuildException("The end node is missing.");
        startLayout = new(() => NodeLayout.Build(snapshot, Start.Id, cancellation));
        endLayout = new(() => NodeLayout.Build(snapshot, End.Id, cancellation));
        Seed = snapshot.Core.World.WorldSeed;
        Rules = snapshot.Content.FindRailwayAppearance(snapshot.Core.World.RailwayRule) is ContentLookup<RailwayAppearanceProjection>.Found rules
            ? rules.Value : throw new RailwayBuildException("The railway appearance is missing.");
        start = RailwayCoordinates.NodePoint(Start);
        var end = RailwayCoordinates.NodePoint(End);
        var dx = end.X - start.X;
        var dz = end.Z - start.Z;
        var direct = Math.Sqrt(dx * dx + dz * dz);
        LengthCells = Edge.LengthMeters * RailwayCoordinates.CellsPerMeter;
        if (!double.IsFinite(LengthCells) || !double.IsFinite(direct) || direct <= 0 || LengthCells < direct ||
            !double.IsFinite(Rules.MinimumCurveRadiusMeters) || Rules.MinimumCurveRadiusMeters <= 0 ||
            !double.IsFinite(Rules.MaximumGrade) || Rules.MaximumGrade <= 0)
            throw new RailwayBuildException("The edge length or railway appearance cannot produce a valid alignment.");
        forwardX = dx / direct;
        forwardZ = dz / direct;
        side = RailwayRandom.Unit(Seed, id.Value, 0, HorizontalSidePurpose) < 0.5 ? -1 : 1;
        rise = RailwayRandom.Unit(Seed, id.Value, 0, VerticalSidePurpose) < 0.5 ? -1 : 1;
        var slope = Math.Min(Math.Atan(Rules.MaximumGrade) * GradeReserve, Math.Max(0, Edge.Terrain.Relief) * ReliefSlopeRadians);
        vertical = new(LengthCells, slope);
        var limits = RailwayCoordinates.AlignmentLimits(vertical.ForwardLength, Edge.TrackLimit, Rules);
        var chord = Math.Min(vertical.ForwardLength, Math.Max(direct, limits.MinimumChord));
        horizontal = new(vertical.ForwardLength, chord, limits.Radius);
        trackPieces = horizontal.Ranges.Select(piece =>
            (vertical.Distance(piece.Begin), vertical.Distance(piece.End), side * piece.Curvature)).ToArray();
    }

    internal static ScenePoint Endpoint(SceneWorldSnapshot snapshot, EdgeId id, NodeId node)
    {
        var edge = snapshot.Core.World.Edges.Single(item => item.Id == id);
        var first = snapshot.Core.World.Nodes.Single(item => item.Id == edge.Start);
        var last = snapshot.Core.World.Nodes.Single(item => item.Id == edge.End);
        var start = RailwayCoordinates.NodePoint(first);
        if (node == first.Id) return start;
        var rules = snapshot.Content.FindRailwayAppearance(snapshot.Core.World.RailwayRule) is ContentLookup<RailwayAppearanceProjection>.Found found
            ? found.Value : throw new RailwayBuildException("The railway appearance is missing.");
        var end = RailwayCoordinates.NodePoint(last);
        var dx = end.X - start.X; var dz = end.Z - start.Z;
        var direct = Math.Sqrt(dx * dx + dz * dz);
        var length = edge.LengthMeters * RailwayCoordinates.CellsPerMeter;
        if (direct <= 0 || length < direct || !double.IsFinite(length))
            throw new RailwayBuildException("The edge length cannot produce a valid alignment.");
        var slope = Math.Min(Math.Atan(rules.MaximumGrade) * GradeReserve, Math.Max(0, edge.Terrain.Relief) * ReliefSlopeRadians);
        var vertical = new CircularBend(length, slope);
        var limits = RailwayCoordinates.AlignmentLimits(vertical.ForwardLength, edge.TrackLimit, rules);
        var chord = Math.Min(vertical.ForwardLength, Math.Max(direct, limits.MinimumChord));
        var rise = RailwayRandom.Unit(snapshot.Core.World.WorldSeed, id.Value, 0, VerticalSidePurpose) < 0.5 ? -1 : 1;
        return new(start.X + dx / direct * chord, start.Y + rise * vertical.Sample(length).Height, start.Z + dz / direct * chord);
    }

    // Approved #315 decision 12 uses the closed planar scale even when the vertical profile bends.
    internal double TrackArc(double centerArc, double offset)
    {
        if (centerArc < 0) return centerArc;
        var distance = 0.0;
        foreach (var piece in trackPieces)
        {
            distance += (Math.Min(centerArc, piece.End) - piece.Begin) * (1 - piece.Curvature * offset);
            if (centerArc <= piece.End) return distance;
        }
        return distance + centerArc - LengthCells;
    }

    internal double CenterArc(double trackArc, double offset)
    {
        if (trackArc < 0) return trackArc;
        foreach (var piece in trackPieces)
        {
            var scale = 1 - piece.Curvature * offset;
            var length = (piece.End - piece.Begin) * scale;
            if (trackArc <= length) return piece.Begin + trackArc / scale;
            trackArc -= length;
        }
        return LengthCells + trackArc;
    }

    internal RailwaySample Sample(double distanceCells, double offsetCells = 0)
    {
        if (distanceCells < 0 || distanceCells > LengthCells)
        {
            var endpoint = Sample(Math.Clamp(distanceCells, 0, LengthCells), offsetCells);
            return endpoint with
            {
                Position = RailwayCoordinates.Add(endpoint.Position,
                endpoint.Forward * (float)(distanceCells - Math.Clamp(distanceCells, 0, LengthCells)))
            };
        }
        var (height, progress, grade) = vertical.Sample(distanceCells);
        var (lateral, forward, heading) = horizontal.Sample(progress);
        var tx = forwardX * Math.Cos(heading) + forwardZ * side * Math.Sin(heading);
        var tz = forwardZ * Math.Cos(heading) - forwardX * side * Math.Sin(heading);
        var right = new Vector3((float)tz, 0, (float)-tx);
        var scale = 1 - side * horizontal.Curvature(progress) * offsetCells;
        var tangent = Vector3.Normalize(new((float)(tx * Math.Cos(grade) * scale), (float)(rise * Math.Sin(grade)),
            (float)(tz * Math.Cos(grade) * scale)));
        var point = new ScenePoint(start.X + forwardX * forward + forwardZ * side * lateral + tz * offsetCells,
            start.Y + rise * height, start.Z + forwardZ * forward - forwardX * side * lateral - tx * offsetCells);
        return new(point, tangent, right);
    }

    internal RailwaySample SampleFromNode(NodeId node, RailwaySample boundary, double inwardArc)
    {
        var direction = node == Edge.End ? 1 : -1;
        var endpoint = direction > 0 ? LengthCells : 0;
        var offset = RailwayCoordinates.TrackOffset(1, Math.Max(1, Edge.Tracks));
        var end = Sample(endpoint, offset);
        var arc = TrackArc(endpoint, offset) + direction * inwardArc;
        var sample = Sample(CenterArc(arc, offset), offset);
        return new(RailwayCoordinates.Add(boundary.Position, RailwayCoordinates.Relative(sample.Position, end.Position)),
            direction * sample.Forward, direction * sample.Right);
    }

    internal RailwaySample SampleWithNodes(double distanceCells, double offsetCells)
    {
        if (distanceCells >= 0 && distanceCells <= LengthCells) return Sample(distanceCells, offsetCells);
        var atStart = distanceCells < 0;
        var layout = atStart ? StartLayout : EndLayout;
        var entry = layout.EntryFor(Edge.Id);
        var inlet = layout.Approach(entry, layout.Bypass).Inlet;
        var inwardArc = atStart ? -distanceCells : distanceCells - LengthCells;
        var sample = inlet.Sample(inwardArc);
        var boundary = inlet.Sample(0);
        var endpoint = Sample(atStart ? 0 : LengthCells, offsetCells);
        var direction = atStart ? -1 : 1;
        return new(RailwayCoordinates.Add(endpoint.Position, RailwayCoordinates.Relative(sample.Position, boundary.Position)),
            direction * sample.Forward, direction * sample.Right);
    }

    internal double Locate(ScenePoint point)
    {
        var dx = point.X - start.X;
        var dz = point.Z - start.Z;
        var lateral = (dx * forwardZ - dz * forwardX) * side;
        var forward = dx * forwardX + dz * forwardZ;
        var progress = horizontal.Locate(lateral, forward);
        if (vertical.IsStraight) return progress;
        return vertical.Distance(progress);
    }

    internal bool IntersectsArcRange(SceneRay ray, ScenePoint origin, RailwayArcRange range, double entry, double exit)
    {
        var dx = origin.X + ray.Origin.X - start.X;
        var dz = origin.Z + ray.Origin.Z - start.Z;
        return horizontal.Intersects((dx * forwardZ - dz * forwardX) * side, dx * forwardX + dz * forwardZ,
            (ray.Direction.X * forwardZ - ray.Direction.Z * forwardX) * side,
            ray.Direction.X * forwardX + ray.Direction.Z * forwardZ, entry, exit,
            vertical.Sample(Math.Clamp(range.Begin, 0, LengthCells)).Forward,
            vertical.Sample(Math.Clamp(range.End, 0, LengthCells)).Forward, range.Width, range.End >= LengthCells);
    }

    internal SceneBounds Bounds(double begin, double end, ScenePoint origin, double width)
    {
        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);
        foreach (var value in horizontal.Extrema(vertical.Sample(Math.Clamp(begin, 0, LengthCells)).Forward,
            vertical.Sample(Math.Clamp(end, 0, LengthCells)).Forward,
            (forwardZ * side, forwardX), (-forwardX * side, forwardZ)))
        {
            var point = new Vector3((float)(start.X + forwardX * value.Forward + forwardZ * side * value.Lateral - origin.X),
                0, (float)(start.Z + forwardZ * value.Forward - forwardX * side * value.Lateral - origin.Z));
            minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point);
        }
        foreach (var arc in new[] { begin, end })
        {
            var point = RailwayCoordinates.Relative(Sample(arc).Position, origin);
            minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point);
        }
        var heights = new[] { Sample(begin).Position.Y, Sample(end).Position.Y,
            Sample(Math.Clamp(LengthCells / 2, begin, end)).Position.Y };
        minimum.Y = (float)(heights.Min() - origin.Y); maximum.Y = (float)(heights.Max() - origin.Y);
        return new(minimum - new Vector3((float)width, RailwayCoordinates.FogVerticalPaddingCells, (float)width),
            maximum + new Vector3((float)width, RailwayCoordinates.FogVerticalPaddingCells, (float)width));
    }

    private sealed class HorizontalAlignment
    {
        private readonly Piece[] pieces;
        private readonly double length;
        internal IEnumerable<(double Begin, double End, double Curvature)> Ranges =>
            pieces.Select(piece => (piece.Start, piece.Start + piece.Length, piece.Curvature));
        private readonly record struct Piece(double Start, double Length, double X, double Z, double Heading, double Curvature)
        {
            internal (double Lateral, double Forward, double Heading) Sample(double distance)
            {
                var turn = Curvature * distance;
                var chord = Curvature == 0 ? distance : 2 * Math.Sin(turn / 2) / Curvature;
                var middle = Heading + turn / 2;
                var (sin, cos) = SinCos(middle);
                return (X + chord * sin, Z + chord * cos, Heading + turn);
            }
        }
        internal HorizontalAlignment(double length, double chord, double radius)
        {
            this.length = length;
            var angle = Math.Min(length / (4 * radius), Math.PI / 2);
            double ends, transverse;
            if (chord >= length - (2 * Math.PI - 4) * radius)
            {
                var lower = 0.0;
                var upper = angle;
                for (var i = 0; i < 80; i++)
                {
                    var middle = (lower + upper) / 2;
                    if (length - 4 * radius * (middle - Math.Sin(middle)) > chord) lower = middle;
                    else upper = middle;
                }
                angle = chord == length ? 0 : (lower + upper) / 2;
                ends = length - 4 * radius * angle;
                transverse = 0;
            }
            else
            {
                angle = Math.PI / 2;
                ends = chord - 4 * radius;
                transverse = (length - ends - 2 * Math.PI * radius) / 2;
            }
            var built = new List<Piece>(7);
            var distance = 0.0;
            var pose = (Lateral: 0.0, Forward: 0.0, Heading: 0.0);
            Add(ends / 2, 0);
            Add(radius * angle, 1 / radius);
            Add(transverse, 0);
            Add(2 * radius * angle, -1 / radius);
            Add(transverse, 0);
            Add(radius * angle, 1 / radius);
            // Numerical root error affects the endpoint chord; these lengths still sum to the requested arc length.
            Add(Math.Max(0, length - distance), 0);
            pieces = built.ToArray();
            void Add(double size, double curvature)
            {
                if (size <= 0) return;
                var piece = new Piece(distance, size, pose.Lateral, pose.Forward, pose.Heading, curvature);
                built.Add(piece);
                pose = piece.Sample(size);
                distance += size;
            }
        }
        private static (double Sin, double Cos) SinCos(double angle) => angle == Math.PI / 2 ? (1, 0) :
            angle == -Math.PI / 2 ? (-1, 0) : Math.SinCos(angle);
        internal (double Lateral, double Forward, double Heading) Sample(double distance)
        {
            foreach (var piece in pieces)
                if (distance <= piece.Start + piece.Length) return piece.Sample(Math.Max(0, distance - piece.Start));
            var last = pieces[^1];
            return last.Sample(last.Length);
        }
        internal double Curvature(double distance)
        {
            foreach (var piece in pieces)
                if (distance <= piece.Start + piece.Length) return piece.Curvature;
            return 0;
        }
        internal List<(double Lateral, double Forward, double Heading)> Extrema(double begin, double end,
            (double X, double Z) axisX, (double X, double Z) axisZ)
        {
            var values = new List<(double Lateral, double Forward, double Heading)>();
            foreach (var piece in pieces)
            {
                if (piece.Start > end || piece.Start + piece.Length < begin) continue;
                var first = piece.Sample(Math.Max(0, begin - piece.Start));
                var last = piece.Sample(Math.Min(piece.Length, end - piece.Start));
                values.Add(first); values.Add(last);
                if (piece.Curvature == 0) continue;
                foreach (var axis in new[] { axisX, axisZ })
                    for (var turn = -2; turn <= 2; turn++)
                    {
                        var angle = Math.Atan2(-axis.Z, axis.X) + turn * Math.PI;
                        if (angle >= Math.Min(first.Heading, last.Heading) && angle <= Math.Max(first.Heading, last.Heading))
                            values.Add(piece.Sample((angle - piece.Heading) / piece.Curvature));
                    }
            }
            return values;
        }
        internal double Locate(double x, double z)
        {
            var best = 0.0;
            var squared = double.PositiveInfinity;
            foreach (var piece in pieces)
            {
                Check(piece, 0); Check(piece, piece.Length);
                if (piece.Curvature == 0)
                {
                    var (sin, cos) = SinCos(piece.Heading);
                    Check(piece, Math.Clamp((x - piece.X) * sin + (z - piece.Z) * cos, 0, piece.Length));
                    continue;
                }
                var cx = piece.X + Math.Cos(piece.Heading) / piece.Curvature;
                var cz = piece.Z - Math.Sin(piece.Heading) / piece.Curvature;
                var heading = Math.Atan2((z - cz) * piece.Curvature, -(x - cx) * piece.Curvature);
                var finish = piece.Heading + piece.Length * piece.Curvature;
                for (var turn = -1; turn <= 1; turn++)
                {
                    var candidate = heading + turn * 2 * Math.PI;
                    if (candidate >= Math.Min(piece.Heading, finish) && candidate <= Math.Max(piece.Heading, finish))
                        Check(piece, (candidate - piece.Heading) / piece.Curvature);
                }
            }
            return Math.Clamp(best, 0, length);
            void Check(Piece piece, double distance)
            {
                var sample = piece.Sample(distance);
                var value = Math.Pow(sample.Lateral - x, 2) + Math.Pow(sample.Forward - z, 2);
                if (value < squared) { squared = value; best = piece.Start + distance; }
            }
        }

        internal bool Intersects(double x, double z, double vx, double vz, double entry, double exit,
            double begin, double end, double width, bool includeEnd)
        {
            // Fog bounds are a broad phase. Split the ray at the actual strip boundaries before
            // checking arc ownership, so a rotated or curved bounding box cannot hide loaded geometry.
            var cuts = new List<double> { entry, exit };
            foreach (var piece in pieces)
            {
                if (piece.Start > end || piece.Start + piece.Length < begin) continue;
                var first = piece.Sample(Math.Max(0, begin - piece.Start));
                var last = piece.Sample(Math.Min(piece.Length, end - piece.Start));
                var (sin, cos) = SinCos(first.Heading);
                Plane(first.Lateral, first.Forward, sin, cos);
                var finish = SinCos(last.Heading);
                Plane(last.Lateral, last.Forward, finish.Sin, finish.Cos);
                if (piece.Curvature == 0)
                {
                    Plane(first.Lateral + cos * width, first.Forward - sin * width, cos, -sin);
                    Plane(first.Lateral - cos * width, first.Forward + sin * width, cos, -sin);
                }
                else
                {
                    var cx = piece.X + Math.Cos(piece.Heading) / piece.Curvature;
                    var cz = piece.Z - Math.Sin(piece.Heading) / piece.Curvature;
                    var radius = Math.Abs(1 / piece.Curvature);
                    Circle(cx, cz, radius + width);
                    if (radius > width) Circle(cx, cz, radius - width);
                }
            }
            cuts.Sort();
            for (var i = 1; i < cuts.Count; i++)
            {
                var t = (cuts[i - 1] + cuts[i]) / 2;
                var px = x + vx * t; var pz = z + vz * t;
                var progress = Locate(px, pz);
                // Match column ownership: shared endpoints belong to the following segment.
                if (progress < begin || progress > end || progress == end && !includeEnd) continue;
                var nearest = Sample(progress);
                if (Math.Pow(px - nearest.Lateral, 2) + Math.Pow(pz - nearest.Forward, 2) <= width * width) return true;
            }
            return false;
            void Cut(double t) { if (t > entry && t < exit) cuts.Add(t); }
            void Plane(double px, double pz, double nx, double nz)
            {
                var denominator = vx * nx + vz * nz;
                if (denominator != 0) Cut(((px - x) * nx + (pz - z) * nz) / denominator);
            }
            void Circle(double cx, double cz, double radius)
            {
                var a = vx * vx + vz * vz;
                if (a == 0) return;
                var ox = x - cx; var oz = z - cz;
                var b = ox * vx + oz * vz;
                var discriminant = b * b - a * (ox * ox + oz * oz - radius * radius);
                if (discriminant < 0) return;
                var root = Math.Sqrt(discriminant);
                Cut((-b - root) / a); Cut((-b + root) / a);
            }
        }
    }

    // The level-ended vertical profile has exact three-dimensional length and a monotone horizontal projection.
    private sealed class CircularBend(double length, double angle)
    {
        internal bool IsStraight => angle < 1e-12;
        internal double Radius => IsStraight ? double.PositiveInfinity : length / (4 * angle);
        internal double ForwardLength => IsStraight ? length : length * Math.Sin(angle) / angle;
        internal double Distance(double progress)
        {
            if (IsStraight) return progress;
            var first = Radius * Math.Sin(angle);
            if (progress <= first) return Radius * Math.Asin(Math.Clamp(progress / Radius, -1, 1));
            if (progress <= 3 * first)
                return length / 4 + Radius * (angle - Math.Asin(Math.Clamp(2 * Math.Sin(angle) - progress / Radius, -1, 1)));
            return 3 * length / 4 + Radius * (angle + Math.Asin(Math.Clamp(progress / Radius - 4 * Math.Sin(angle), -1, 1)));
        }
        internal (double Height, double Forward, double Grade) Sample(double distance)
        {
            if (IsStraight) return (0, distance, 0);
            var radius = Radius;
            var quarter = length / 4;
            if (distance <= quarter)
            {
                var heading = distance / radius;
                return (radius * (1 - Math.Cos(heading)), radius * Math.Sin(heading), heading);
            }
            if (distance <= 3 * quarter)
            {
                var heading = angle - (distance - quarter) / radius;
                return (radius * (1 + Math.Cos(heading) - 2 * Math.Cos(angle)),
                    radius * (2 * Math.Sin(angle) - Math.Sin(heading)), heading);
            }
            var final = -angle + (distance - 3 * quarter) / radius;
            return (radius * (1 - Math.Cos(final)), radius * (4 * Math.Sin(angle) + Math.Sin(final)), final);
        }

    }
}

internal static class RailwayRandom
{
    internal static double Unit(ulong seed, ulong edge, long cell, ulong purpose)
    {
        var value = seed ^ (edge * 0x9E3779B97F4A7C15UL) ^ (unchecked((ulong)cell) * 0xD1B54A32D192ED03UL) ^ purpose;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return ((value ^ (value >> 31)) >> 11) * (1.0 / (1UL << 53));
    }
}
