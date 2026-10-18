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
    internal static ImmutableArray<SceneMesh> Build(RailwayPath path, int segment, ScenePoint origin,
        Func<GridColumn, double?>? ground = null)
    {
        if (path.Edge.Tracks == 0) return [];
        var start = segment * RailwayCoordinates.SegmentLengthCells;
        var end = Math.Min(start + RailwayCoordinates.SegmentLengthCells, path.LengthCells);
        if (start >= end) return [];
        var gaps = Gaps(path.Edge);
        return Build(path.Sample, start, end, origin, path.Edge.Tracks, gaps)
            .AddRange(BreakProps(path, start, end, origin, ground, gaps));
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

    private readonly record struct TrackGap(int Track, double From, double To);

    private static TrackGap[] Gaps(EdgeMapItem edge) => edge.Breaks.IsDefaultOrEmpty ? [] : edge.Breaks
        .Where(item => item.Track >= 1 && item.Track <= edge.Tracks).Select(item =>
        {
            var center = item.DistanceMeters * RailwayCoordinates.CellsPerMeter;
            return new TrackGap(item.Track, center - RailwayCoordinates.BreakGapCells / 2, center + RailwayCoordinates.BreakGapCells / 2);
        }).ToArray();

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
        var railSteps = Math.Max(1, (int)Math.Ceiling(end - start));
        var sleeperCount = (int)(Math.Ceiling(end / SleeperSpacingCells) - Math.Ceiling(start / SleeperSpacingCells));
        var rails = new MeshBuilder(BuiltInMaterial.RailSteel, tracks * sections.Length * 2 * (railSteps * 12 + 18) * 6);
        var sleepers = new MeshBuilder(BuiltInMaterial.Sleeper, sleeperCount * tracks * 5 * 6 * 6);
        for (var track = 1; track <= tracks; track++)
        {
            var offset = RailwayCoordinates.TrackOffset(track, tracks);
            var spans = OpenSpans(start, end, gaps.Where(gap => gap.Track == track));
            foreach (var (gauge, height, width, profile) in sections)
            {
                foreach (var railOffset in new[] { offset - gauge / 2, offset + gauge / 2 })
                foreach (var (spanStart, spanEnd) in spans)
                {
                    var previous = Ring(spanStart);
                    var steps = RingSteps(sampleAt, spanStart, spanEnd, railOffset, width, height);
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
                void Box(float lateral, float height, float halfWidth, float halfHeight)
                {
                    Vector3 Corner(float x, float y, float z) => center + sample.Right * (x + lateral) + up * (y + height) + sample.Forward * z;
                    var a = Corner(-halfWidth, -halfHeight, -SleeperHalfLengthCells); var b = Corner(halfWidth, -halfHeight, -SleeperHalfLengthCells);
                    var c = Corner(halfWidth, halfHeight, -SleeperHalfLengthCells); var d = Corner(-halfWidth, halfHeight, -SleeperHalfLengthCells);
                    var e = Corner(-halfWidth, -halfHeight, SleeperHalfLengthCells); var f = Corner(halfWidth, -halfHeight, SleeperHalfLengthCells);
                    var g = Corner(halfWidth, halfHeight, SleeperHalfLengthCells); var h = Corner(-halfWidth, halfHeight, SleeperHalfLengthCells);
                    sleepers.Quad(d, c, b, a); sleepers.Quad(e, f, g, h);
                    sleepers.Quad(a, b, f, e); sleepers.Quad(d, h, g, c);
                    sleepers.Quad(a, e, h, d); sleepers.Quad(b, c, g, f);
                }
            }
        }
        return new[] { rails.Build(), sleepers.Build() }.Where(mesh => !mesh.Vertices.IsEmpty).ToImmutableArray();
    }

    private const float PropHalfWidth = 1.2f;
    private const double PropClearance = 16 + PropHalfWidth + .25;

    private static ImmutableArray<SceneMesh> BreakProps(RailwayPath path, double start, double end, ScenePoint origin,
        Func<GridColumn, double?>? ground, TrackGap[] gaps)
    {
        if (gaps.Length == 0) return [];
        var tracks = path.Edge.Tracks;
        var debris = new MeshBuilder(BuiltInMaterial.Sleeper, 256);
        var steel = new MeshBuilder(BuiltInMaterial.RailSteel, 256);
        var construction = new MeshBuilder(BuiltInMaterial.Construction, 512);
        var open = Enumerable.Range(1, tracks).Where(track => path.Edge.Breaks.All(item => item.Track != track))
            .Select(track => RailwayCoordinates.TrackOffset(track, tracks)).ToArray();
        foreach (var br in path.Edge.Breaks.Where(item => item.Track >= 1 && item.Track <= tracks).OrderBy(item => item.Id.Value))
        {
            var center = br.DistanceMeters * RailwayCoordinates.CellsPerMeter;
            if (center + RailwayCoordinates.BreakGapCells / 2 <= start || center - RailwayCoordinates.BreakGapCells / 2 >= end) continue;
            var offset = RailwayCoordinates.TrackOffset(br.Track, tracks);
            foreach (var along in new[] { -4d, 4d })
            foreach (var side in new[] { -2.5d, 2.5d })
            {
                var arc = center + along;
                if (arc < start || arc >= end) continue;
                var sample = At(arc, offset + side);
                var up = Vector3.Normalize(Vector3.Cross(sample.Forward, sample.Right));
                debris.Box(RailwayCoordinates.Relative(sample.Position, origin) + up * .35f, sample.Right, up, sample.Forward, .35f, .12f, .25f);
            }
            if (!path.Edge.Works.Any(work => work.Kind == RailwayWorkKind.RepairBreak && work.RepairingBreak == br.Id)) continue;
            Barrier(offset, center - RailwayCoordinates.BreakGapCells / 2);
            Barrier(offset, center + RailwayCoordinates.BreakGapCells / 2);
            if (ground is null || center < start || center >= end) continue;
            var sideSign = offset < 0 ? -1 : 1;
            var place = offset + sideSign * PropClearance;
            foreach (var lane in open)
                place = sideSign > 0 ? Math.Max(place, lane + PropClearance) : Math.Min(place, lane - PropClearance);
            GroundBox(debris, 0, PropHalfWidth, .6f, PropHalfWidth);
            GroundBox(steel, 2.5f, .8f, .7f, .8f);
            GroundBox(construction, -2.5f, .35f, 1.1f, .35f);
            GroundBox(construction, 5f, .35f, 1.1f, .35f);

            void Barrier(double lane, double arc)
            {
                if (arc < start || arc >= end) return;
                var sample = At(arc, lane);
                var up = Vector3.Normalize(Vector3.Cross(sample.Forward, sample.Right));
                construction.Box(RailwayCoordinates.Relative(sample.Position, origin) + up * .75f,
                    sample.Right, up, sample.Forward, 4f, .75f, .25f);
            }
            void GroundBox(MeshBuilder builder, float along, float halfX, float halfY, float halfZ)
            {
                var sample = At(center + along, place);
                var column = new GridColumn((int)Math.Floor(sample.Position.X - origin.X), (int)Math.Floor(sample.Position.Z - origin.Z));
                if (ground(column) is not double surface) return;
                var forward = new Vector3(sample.Forward.X, 0, sample.Forward.Z);
                var right = new Vector3(sample.Right.X, 0, sample.Right.Z);
                if (forward.LengthSquared() < 1e-8f) forward = Vector3.UnitZ;
                if (right.LengthSquared() < 1e-8f) right = Vector3.UnitX;
                var point = RailwayCoordinates.Relative(sample.Position, origin);
                builder.Box(new(point.X, (float)(surface - origin.Y) + halfY, point.Z),
                    Vector3.Normalize(right), Vector3.UnitY, Vector3.Normalize(forward), halfX, halfY, halfZ);
            }
        }
        return new[] { debris.Build(), steel.Build(), construction.Build() }.Where(mesh => !mesh.Vertices.IsEmpty).ToImmutableArray();

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

    private sealed class MeshBuilder(BuiltInMaterial material, int capacity)
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
            Triangle(a, b, c, railHead);
            Triangle(a, c, d, railHead);
        }
        private void Triangle(Vector3 a, Vector3 b, Vector3 c, bool railHead)
        {
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            Add(a); Add(b); Add(c);
            void Add(Vector3 point)
            {
                indices.Add(vertices.Count);
                // Built-in steel uses UV.x to distinguish the running surface from the rusty section.
                vertices.Add(new(point, normal, new(railHead ? 1 : 0, 0), Vector2.Zero, ExposedFaceEdges.None, 0, 0));
            }
        }
        internal SceneMesh Build() => new(new SceneMaterial.BuiltIn(material), vertices.DrainToImmutable(), indices.DrainToImmutable());
    }
}
