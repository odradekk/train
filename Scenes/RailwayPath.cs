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
    // A common level datum permits straight, zero-slack edges. Local relief is generated around it.
    internal static double NodeElevation(NodeMapItem node) => 0;
    internal static ScenePoint NodePoint(NodeMapItem node) =>
        new(node.Position.X * CellsPerMeter, NodeElevation(node), node.Position.Y * CellsPerMeter);
    internal static double TrackOffset(int number, int count) => (number - (count + 1) / 2.0) * 34;
    internal static (double HalfWidth, double Radius, double MinimumChord) AlignmentLimits(
        double horizontalLength, int trackLimit, RailwayAppearanceProjection rules)
    {
        var width = 10.5 + 17 * Math.Max(0, trackLimit - 1);
        // Keep the inner offset arc regular, including when the corridor is wider than the rule radius.
        var radius = Math.Max(rules.MinimumCurveRadiusMeters * CellsPerMeter, width + 0.5);
        return (width, radius, 4 * radius * Math.Sin(Math.Min(horizontalLength / (4 * radius), Math.PI / 2)));
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
    internal double LengthCells { get; }
    internal double CorridorHalfWidthCells { get; }
    internal int SegmentCount => checked((int)Math.Ceiling(LengthCells / RailwayCoordinates.SegmentLengthCells));
    private readonly ScenePoint start;
    private readonly double forwardX;
    private readonly double forwardZ;
    private readonly double side;
    private readonly double rise;
    private readonly HorizontalAlignment horizontal;
    private readonly CircularBend vertical;

    internal RailwayPath(SceneWorldSnapshot snapshot, EdgeId id)
    {
        Edge = snapshot.Core.FindEdge(id) is Lookup<EdgeView>.Found edge ? edge.Value.Map :
            throw new RailwayBuildException("The railway edge is missing.");
        Start = snapshot.Core.FindNode(Edge.Start) is Lookup<NodeView>.Found first ? first.Value.Map :
            throw new RailwayBuildException("The start node is missing.");
        End = snapshot.Core.FindNode(Edge.End) is Lookup<NodeView>.Found last ? last.Value.Map :
            throw new RailwayBuildException("The end node is missing.");
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
        side = RailwayRandom.Unit(Seed, id.Value, 0, 1) < 0.5 ? -1 : 1;
        rise = RailwayRandom.Unit(Seed, id.Value, 0, 2) < 0.5 ? -1 : 1;
        var slope = Math.Min(Math.Atan(Rules.MaximumGrade) / 2, Math.Max(0, Edge.Terrain.Relief) * 0.03);
        vertical = new(LengthCells, slope);
        var limits = RailwayCoordinates.AlignmentLimits(vertical.ForwardLength, Edge.TrackLimit, Rules);
        CorridorHalfWidthCells = limits.HalfWidth;
        var chord = Math.Min(vertical.ForwardLength, Math.Max(direct, limits.MinimumChord));
        horizontal = new(vertical.ForwardLength, chord, limits.Radius);
    }

    internal RailwaySample Sample(double distanceCells, double offsetCells = 0)
    {
        if (distanceCells < 0 || distanceCells > LengthCells)
        {
            var endpoint = Sample(Math.Clamp(distanceCells, 0, LengthCells), offsetCells);
            return endpoint with { Position = RailwayCoordinates.Add(endpoint.Position,
                endpoint.Forward * (float)(distanceCells - Math.Clamp(distanceCells, 0, LengthCells))) };
        }
        var (height, progress, grade) = vertical.Sample(distanceCells);
        var (lateral, forward, heading) = horizontal.Sample(progress);
        var tx = forwardX * Math.Cos(heading) + forwardZ * side * Math.Sin(heading);
        var tz = forwardZ * Math.Cos(heading) - forwardX * side * Math.Sin(heading);
        var right = new Vector3((float)tz, 0, (float)-tx);
        var tangent = Vector3.Normalize(new((float)(tx * Math.Cos(grade)), (float)(rise * Math.Sin(grade)),
            (float)(tz * Math.Cos(grade))));
        var point = new ScenePoint(start.X + forwardX * forward + forwardZ * side * lateral + tz * offsetCells,
            start.Y + rise * height, start.Z + forwardZ * forward - forwardX * side * lateral - tx * offsetCells);
        return new(point, tangent, right);
    }

    internal double Locate(ScenePoint point)
    {
        var dx = point.X - start.X;
        var dz = point.Z - start.Z;
        var lateral = (dx * forwardZ - dz * forwardX) * side;
        var forward = dx * forwardX + dz * forwardZ;
        var progress = horizontal.Locate(lateral, forward);
        if (vertical.IsStraight) return progress;
        var lower = 0.0;
        var upper = LengthCells;
        for (var i = 0; i < 64; i++)
        {
            var middle = (lower + upper) / 2;
            if (vertical.Sample(middle).Forward < progress) lower = middle;
            else upper = middle;
        }
        return (lower + upper) / 2;
    }

    internal bool IntersectsArcRange(SceneRay ray, ScenePoint origin, double begin, double end, double entry, double exit)
    {
        var dx = origin.X + ray.Origin.X - start.X;
        var dz = origin.Z + ray.Origin.Z - start.Z;
        return horizontal.Intersects((dx * forwardZ - dz * forwardX) * side, dx * forwardX + dz * forwardZ,
            (ray.Direction.X * forwardZ - ray.Direction.Z * forwardX) * side,
            ray.Direction.X * forwardX + ray.Direction.Z * forwardZ, entry, exit,
            vertical.Sample(begin).Forward, vertical.Sample(end).Forward, Math.Max(64, Edge.Tracks * 17 + 40));
    }

    internal SceneBounds Bounds(double begin, double end, ScenePoint origin, double width)
    {
        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);
        foreach (var value in horizontal.Extrema(vertical.Sample(begin).Forward, vertical.Sample(end).Forward,
            (forwardZ * side, forwardX), (-forwardX * side, forwardZ)))
        {
            var point = new Vector3((float)(start.X + forwardX * value.Forward + forwardZ * side * value.Lateral - origin.X),
                0, (float)(start.Z + forwardZ * value.Forward - forwardX * side * value.Lateral - origin.Z));
            minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point);
        }
        var heights = new[] { Sample(begin).Position.Y, Sample(end).Position.Y,
            Sample(Math.Clamp(LengthCells / 2, begin, end)).Position.Y };
        minimum.Y = (float)(heights.Min() - origin.Y); maximum.Y = (float)(heights.Max() - origin.Y);
        return new(minimum - new Vector3((float)width, 128, (float)width), maximum + new Vector3((float)width, 128, (float)width));
    }

    private sealed class HorizontalAlignment
    {
        private readonly Piece[] pieces;
        private readonly double length;
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
            double begin, double end, double width)
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
                if (progress < begin || progress > end) continue;
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
        internal double Radius => angle == 0 ? double.PositiveInfinity : length / (4 * angle);
        internal double ForwardLength => angle == 0 ? length : length * Math.Sin(angle) / angle;
        internal (double Lateral, double Forward, double Heading) Sample(double distance)
        {
            if (angle < 1e-12) return (0, distance, 0);
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
