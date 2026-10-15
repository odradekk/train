using System.Collections.Immutable;
using System.Numerics;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class RailwayMeshes
{
    internal static ImmutableArray<SceneMesh> Build(RailwayPath path, int segment, ScenePoint origin)
    {
        if (path.Edge.Tracks == 0) return [];
        var start = segment * RailwayCoordinates.SegmentLengthCells;
        var end = Math.Min(start + RailwayCoordinates.SegmentLengthCells, path.LengthCells);
        if (start >= end) return [];
        return Build(path.Sample, start, end, origin, path.Edge.Tracks);
    }

    internal static ImmutableArray<SceneMesh> Build(Func<double, RailwaySample> sample, double start, double end, ScenePoint origin) =>
        Build((arc, offset) =>
        {
            var at = sample(arc);
            return at with { Position = RailwayCoordinates.Add(at.Position, at.Right * (float)offset) };
        }, start, end, origin, 1);

    private static ImmutableArray<SceneMesh> Build(Func<double, double, RailwaySample> sampleAt,
        double start, double end, ScenePoint origin, int tracks)
    {
        var rails = new MeshBuilder(BuiltInMaterial.RailSteel);
        var sleepers = new MeshBuilder(BuiltInMaterial.Sleeper);
        for (var track = 1; track <= tracks; track++)
        {
            var offset = RailwayCoordinates.TrackOffset(track, tracks);
            foreach (var (gauge, height, _, _) in RailwayCoordinates.WheelDimensions)
            {
                var width = height * .65f;
                Vector2[] profile = [new(-width, -height), new(width, -height),
                    new(width, -.8f * height), new(.2f * width, -.8f * height),
                    new(.2f * width, -.2f * height), new(width, -.2f * height), new(width, 0),
                    new(-width, 0), new(-width, -.2f * height), new(-.2f * width, -.2f * height),
                    new(-.2f * width, -.8f * height), new(-width, -.8f * height)];
                foreach (var railOffset in new[] { offset - gauge / 2, offset + gauge / 2 })
                {
                    var previous = Ring(start);
                    var steps = (int)Math.Ceiling(end - start);
                    for (var step = 1; step <= steps; step++)
                    {
                        var next = Ring(start + (end - start) * step / steps);
                        for (var side = 0; side < profile.Length; side++)
                        {
                            var following = (side + 1) % profile.Length;
                            rails.Quad(previous[side], previous[following], next[following], next[side]);
                        }
                        previous = next;
                    }
                    Cap(start, false);
                    Cap(end, true);

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
            for (var index = (long)Math.Ceiling(start / 1.5); index * 1.5 < end; index++)
            {
                var sample = sampleAt(index * 1.5, offset);
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
                    var a = Corner(-halfWidth, -halfHeight, -.3f); var b = Corner(halfWidth, -halfHeight, -.3f);
                    var c = Corner(halfWidth, halfHeight, -.3f); var d = Corner(-halfWidth, halfHeight, -.3f);
                    var e = Corner(-halfWidth, -halfHeight, .3f); var f = Corner(halfWidth, -halfHeight, .3f);
                    var g = Corner(halfWidth, halfHeight, .3f); var h = Corner(-halfWidth, halfHeight, .3f);
                    sleepers.Quad(d, c, b, a); sleepers.Quad(e, f, g, h);
                    sleepers.Quad(a, b, f, e); sleepers.Quad(d, h, g, c);
                    sleepers.Quad(a, e, h, d); sleepers.Quad(b, c, g, f);
                }
            }
        }
        return [rails.Build(), sleepers.Build()];
    }

    private sealed class MeshBuilder(BuiltInMaterial material)
    {
        private readonly List<SceneVertex> vertices = [];
        private readonly List<int> indices = [];
        internal void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Triangle(a, b, c);
            Triangle(a, c, d);
        }
        private void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            foreach (var point in new[] { a, b, c })
            {
                indices.Add(vertices.Count);
                vertices.Add(new(point, normal, Vector2.Zero, Vector2.Zero, ExposedFaceEdges.None, 0, 0));
            }
        }
        internal SceneMesh Build() => new(new SceneMaterial.BuiltIn(material), vertices.ToImmutableArray(), indices.ToImmutableArray());
    }
}
