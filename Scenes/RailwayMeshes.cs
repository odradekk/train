using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class RailwayMeshes
{
    // The 80 m content radius permits 2.25 m chords, including a three-track outer rail.
    private const double MaximumRingSpacingCells = 4.5;
    private const double RingErrorCells = .0198;
    private const double SleeperSpacingCells = 1.5;
    private const float SleeperHalfLengthCells = .3f;
    internal static ImmutableArray<SceneMesh> Build(RailwayPath path, int segment, ScenePoint origin)
    {
        if (path.Edge.Tracks == 0) return [];
        var start = segment * RailwayCoordinates.SegmentLengthCells;
        var end = Math.Min(start + RailwayCoordinates.SegmentLengthCells, path.LengthCells);
        if (start >= end) return [];
        var gaps = Gaps(path.Edge, path.LengthCells);
        return Build(path.Sample, start, end, origin, path.Edge.Tracks, gaps)
            .AddRange(BreakProps(path, start, end, origin, gaps));
    }

    internal static ImmutableArray<SceneMesh> Build(Func<double, RailwaySample> sample, double start, double end, ScenePoint origin) =>
        Build((arc, offset) =>
        {
            var at = sample(arc);
            return at with { Position = RailwayCoordinates.Add(at.Position, at.Right * (float)offset) };
        }, start, end, origin, 1, []);

    internal static ImmutableArray<SceneBounds> Footprint(Func<double, RailwaySample> sample, double start, double end,
        int tracks, double minimumY, double maximumY)
    {
        if (tracks == 0) return [];
        var firstSleeper = Math.Ceiling(start / SleeperSpacingCells) * SleeperSpacingCells;
        var lastSleeper = (Math.Ceiling(end / SleeperSpacingCells) - 1) * SleeperSpacingCells;
        var begin = firstSleeper < end ? Math.Min(start, firstSleeper - SleeperHalfLengthCells) : start;
        var finish = lastSleeper >= start ? Math.Max(end, lastSleeper + SleeperHalfLengthCells) : end;
        return ConstructionFog.Band(At, begin, finish, RailwayCoordinates.OutermostTrackOffset(tracks) +
            RailwayCoordinates.BedBaseHalfWidth, minimumY, maximumY);

        RailwaySample At(double arc)
        {
            var clamped = Math.Clamp(arc, start, end);
            var at = sample(clamped);
            return at with { Position = RailwayCoordinates.Add(at.Position, at.Forward * (float)(arc - clamped)) };
        }
    }

    private readonly record struct TrackGap(BreakId Id, int Track, double Center, double From, double To);

    private static TrackGap[] Gaps(EdgeMapItem edge, double limit)
    {
        if (edge.Breaks.IsDefaultOrEmpty) return [];
        var half = RailwayCoordinates.BreakGapCells / 2;
        return edge.Breaks.Where(item => item.Track >= 1 && item.Track <= edge.Tracks).Select(item =>
        {
            var center = item.DistanceMeters * RailwayCoordinates.CellsPerMeter;
            var from = Math.Max(0, center - half);
            var to = Math.Min(limit, center + half);
            return new TrackGap(item.Id, item.Track, center, from, to);
        }).Where(gap => gap.To - gap.From > 1e-4).ToArray();
    }

    private static List<(double Start, double End)> OpenSpans(double start, double end, IEnumerable<TrackGap> gaps)
    {
        var spans = new List<(double Start, double End)> { (start, end) };
        foreach (var gap in gaps)
        {
            var next = new List<(double Start, double End)>();
            foreach (var span in spans)
            {
                if (gap.To <= span.Start || gap.From >= span.End) { next.Add(span); continue; }
                if (gap.From > span.Start) next.Add((span.Start, gap.From));
                if (gap.To < span.End) next.Add((gap.To, span.End));
            }
            spans = next;
        }
        return spans.Where(span => span.End - span.Start > 1e-4).ToList();
    }

    private static ImmutableArray<SceneMesh> Build(Func<double, double, RailwaySample> sampleAt,
        double start, double end, ScenePoint origin, int tracks, TrackGap[] gaps)
    {
        var sections = RailwayCoordinates.WheelDimensions.Select(dimension =>
        {
            var (gauge, height, _, _) = dimension;
            var width = height * .65f;
            Vector2[] profile = [new(-width, -height), new(width, -height),
                new(width, -.8f * height), new(.2f * width, -.8f * height),
                new(.2f * width, -.2f * height), new(width, -.2f * height), new(width, 0),
                new(-width, 0), new(-width, -.2f * height), new(-.2f * width, -.2f * height),
                new(-.2f * width, -.8f * height), new(-width, -.8f * height)];
            return (gauge, height, width, profile);
        }).ToArray();
        var railSpans = new List<(double Offset, double Start, double End, int Steps,
            float Width, float Height, Vector2[] Profile)>[tracks];
        var railCapacity = 0;
        for (var track = 1; track <= tracks; track++)
        {
            var planned = railSpans[track - 1] = [];
            var offset = RailwayCoordinates.TrackOffset(track, tracks);
            var spans = OpenSpans(start, end, gaps.Where(gap => gap.Track == track));
            foreach (var (gauge, height, width, profile) in sections)
            foreach (var railOffset in new[] { offset - gauge / 2, offset + gauge / 2 })
            foreach (var (spanStart, spanEnd) in spans)
            {
                var steps = RingSteps(sampleAt, spanStart, spanEnd, railOffset, width, height);
                planned.Add((railOffset, spanStart, spanEnd, steps, width, height, profile));
                railCapacity += 6 * (profile.Length * steps + 6);
            }
        }
        var sleeperCount = (int)(Math.Ceiling(end / SleeperSpacingCells) - Math.Ceiling(start / SleeperSpacingCells));
        var rails = new MeshBuilder(BuiltInMaterial.RailSteel, railCapacity);
        var sleepers = new MeshBuilder(BuiltInMaterial.Sleeper, sleeperCount * tracks * 5 * 6 * 6);
        for (var track = 1; track <= tracks; track++)
        {
            var offset = RailwayCoordinates.TrackOffset(track, tracks);
            foreach (var (railOffset, spanStart, spanEnd, steps, width, height, profile) in railSpans[track - 1])
            {
                var previous = Ring(spanStart);
                for (var step = 1; step <= steps; step++)
                {
                    var next = Ring(spanStart + (spanEnd - spanStart) * step / steps);
                    for (var side = 0; side < profile.Length; side++)
                    {
                        var following = (side + 1) % profile.Length;
                        rails.Quad(previous[side], previous[following], next[following], next[side], railHead: side == 6);
                    }
                    previous = next;
                }
                Cap(spanStart, false);
                Cap(spanEnd, true);

                Vector3[] Ring(double arc) => profile.Select(point => Point(arc, point)).ToArray();
                Vector3 Point(double arc, Vector2 point)
                {
                    var sample = sampleAt(arc, railOffset);
                    var up = Vector3.Normalize(Vector3.Cross(sample.Forward, sample.Right));
                    return RailwayCoordinates.Relative(sample.Position, origin) + sample.Right * point.X + up * point.Y;
                }
                void Cap(double arc, bool forward)
                {
                    Rectangle(-width, width, -height, -.8f * height);
                    Rectangle(-.2f * width, .2f * width, -.8f * height, -.2f * height);
                    Rectangle(-width, width, -.2f * height, 0);
                    void Rectangle(float left, float right, float bottom, float top)
                    {
                        var a = Point(arc, new(left, bottom));
                        var b = Point(arc, new(right, bottom));
                        var c = Point(arc, new(right, top));
                        var d = Point(arc, new(left, top));
                        if (forward) rails.Quad(a, b, c, d);
                        else rails.Quad(d, c, b, a);
                    }
                }
            }
            // Global spacing keeps a sleeper at a segment boundary in exactly one segment.
            var trackGaps = gaps.Where(gap => gap.Track == track).ToArray();
            for (var index = (long)Math.Ceiling(start / SleeperSpacingCells); index * SleeperSpacingCells < end; index++)
            {
                var arc = index * SleeperSpacingCells;
                if (trackGaps.Any(gap => arc >= gap.From && arc < gap.To)) continue;
                var sample = sampleAt(arc, offset);
                var up = Vector3.Normalize(Vector3.Cross(sample.Forward, sample.Right));
                var center = RailwayCoordinates.Relative(sample.Position, origin) - up * .8f;
                Box(0, 0, 8.5f, .2f);
                var smallHalfGauge = (float)RailwayCoordinates.WheelDimensions[0].Gauge / 2;
                var mediumHalfGauge = (float)RailwayCoordinates.WheelDimensions[1].Gauge / 2;
                Box(-smallHalfGauge, .35f, .3f, .15f); Box(smallHalfGauge, .35f, .3f, .15f);
                Box(-mediumHalfGauge, .3f, .3f, .1f); Box(mediumHalfGauge, .3f, .3f, .1f);
                void Box(float lateral, float height, float halfWidth, float halfHeight) => sleepers.Box(
                    center + sample.Right * lateral + up * height, sample.Right, up, sample.Forward,
                    halfWidth, halfHeight, SleeperHalfLengthCells);
            }
        }
        return new[] { rails.Build(), sleepers.Build() }.Where(mesh => !mesh.Vertices.IsEmpty).ToImmutableArray();
    }

    private static ImmutableArray<SceneMesh> BreakProps(RailwayPath path, double start, double end, ScenePoint origin, TrackGap[] gaps)
    {
        if (gaps.Length == 0) return [];
        var tracks = path.Edge.Tracks;
        var debris = new MeshBuilder(BuiltInMaterial.Sleeper, 256);
        var limit = path.LengthCells;
        foreach (var gap in gaps.OrderBy(item => item.Id.Value))
        {
            if (gap.To <= start || gap.From >= end) continue;
            var center = gap.Center;
            var offset = RailwayCoordinates.TrackOffset(gap.Track, tracks);
            foreach (var along in new[] { -4d, 4d })
            foreach (var side in new[] { -2.5d, 2.5d })
            {
                var arc = Math.Clamp(center + along, 0.25, limit - 0.25);
                if (arc < start || arc >= end) continue;
                var sample = At(arc, offset + side);
                var up = Vector3.Normalize(Vector3.Cross(sample.Forward, sample.Right));
                debris.Box(RailwayCoordinates.Relative(sample.Position, origin) + up * .35f, sample.Right, up, sample.Forward, .35f, .12f, .25f);
            }
        }
        var mesh = debris.Build();
        return mesh.Vertices.IsEmpty ? [] : [mesh];

        RailwaySample At(double arc, double offset)
        {
            var sample = path.Sample(arc);
            return sample with { Position = RailwayCoordinates.Add(sample.Position, sample.Right * (float)offset) };
        }
    }

    private static int RingSteps(Func<double, double, RailwaySample> sampleAt, double start, double end,
        double offset, float width, float height)
    {
        var steps = Math.Max(1, (int)Math.Ceiling((end - start) / MaximumRingSpacingCells));
        var originalSteps = Math.Max(1, (int)Math.Ceiling(end - start));
        // A uniform grid remains sufficient for the content curves. Tighter curves or grade changes
        // shrink this rail's grid; checking the whole section also covers its changing up vector.
        while (steps < originalSteps && !Fits(steps)) steps = Math.Min(originalSteps, steps * 2);
        return steps;

        bool Fits(int count)
        {
            var previous = sampleAt(start, offset);
            for (var step = 1; step <= count; step++)
            {
                var begin = start + (end - start) * (step - 1) / count;
                var finish = start + (end - start) * step / count;
                var next = sampleAt(finish, offset);
                var chord = RailwayCoordinates.Relative(next.Position, previous.Position);
                var upBefore = Vector3.Normalize(Vector3.Cross(previous.Forward, previous.Right));
                var upAfter = Vector3.Normalize(Vector3.Cross(next.Forward, next.Right));
                for (var probe = 1; probe < 16; probe++)
                {
                    var at = sampleAt(begin + (finish - begin) * probe / 16, offset);
                    var relative = RailwayCoordinates.Relative(at.Position, previous.Position);
                    var fraction = chord.LengthSquared() == 0 ? 0 :
                        Math.Clamp(Vector3.Dot(relative, chord) / chord.LengthSquared(), 0, 1);
                    var up = Vector3.Normalize(Vector3.Cross(at.Forward, at.Right));
                    var error = (relative - chord * fraction).Length() +
                        width * (at.Right - Vector3.Lerp(previous.Right, next.Right, fraction)).Length() +
                        height * (up - Vector3.Lerp(upBefore, upAfter, fraction)).Length();
                    if (error > RingErrorCells) return false;
                }
                previous = next;
            }
            return true;
        }
    }

    internal sealed class MeshBuilder(BuiltInMaterial material, int capacity = 0)
    {
        private readonly ImmutableArray<SceneVertex>.Builder vertices = ImmutableArray.CreateBuilder<SceneVertex>(capacity);
        private readonly ImmutableArray<int>.Builder indices = ImmutableArray.CreateBuilder<int>(capacity);
        internal void Box(Vector3 center, Vector3 right, Vector3 up, Vector3 forward, float hx, float hy, float hz)
        {
            Vector3 Corner(float x, float y, float z) => center + right * x + up * y + forward * z;
            var a = Corner(-hx, -hy, -hz); var b = Corner(hx, -hy, -hz);
            var c = Corner(hx, hy, -hz); var d = Corner(-hx, hy, -hz);
            var e = Corner(-hx, -hy, hz); var f = Corner(hx, -hy, hz);
            var g = Corner(hx, hy, hz); var h = Corner(-hx, hy, hz);
            Quad(d, c, b, a); Quad(e, f, g, h); Quad(a, b, f, e);
            Quad(d, h, g, c); Quad(a, e, h, d); Quad(b, c, g, f);
        }
        internal void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, bool railHead = false)
        {
            var firstNormal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            var secondNormal = Vector3.Normalize(Vector3.Cross(c - a, d - a));
            var firstA = Vertex(a, firstNormal);
            var firstC = Vertex(c, firstNormal);
            var secondA = Vertex(a, secondNormal);
            var secondC = Vertex(c, secondNormal);
            var start = vertices.Count;
            Add(firstA); Add(Vertex(b, firstNormal)); Add(firstC);
            if (firstA == secondA && firstC == secondC)
            {
                indices.Add(start);
                indices.Add(start + 2);
            }
            else { Add(secondA); Add(secondC); }
            Add(Vertex(d, secondNormal));

            void Add(SceneVertex vertex)
            {
                indices.Add(vertices.Count);
                vertices.Add(vertex);
            }
            // Built-in steel uses UV.x to distinguish the running surface from the rusty section.
            SceneVertex Vertex(Vector3 point, Vector3 normal) =>
                new(point, normal, new(railHead ? 1 : 0, 0), Vector2.Zero, ExposedFaceEdges.None, 0, 0);
        }
        internal SceneMesh Build() => new(new SceneMaterial.BuiltIn(material), vertices.DrainToImmutable(), indices.DrainToImmutable());
    }
}
