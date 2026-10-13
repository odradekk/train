using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class RailwayPathTests
{
    [TestMethod]
    [DataRow(800.0, 600.0, 1.0)]
    [DataRow(600.0, 600.0, 0.0)]
    [DataRow(600.0, 600.0, 10.0)]
    [DataRow(4.0, 4.0, 0.0)]
    [DataRow(4.0, 1.0, 1.0)]
    [DataRow(20.0, 10.0, 1.0)]
    [DataRow(600.0, 28.224, 1.0)]
    [DataRow(6000.0, 1.0, 10.0)]
    public void SampledThreeDimensionalArcLengthMatchesCoreMeters(double length, double direct, double relief)
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(length, direct, relief), RailwayFixtures.Edge);
        var previous = path.Sample(0).Position;
        var measured = 0.0;
        var count = Math.Max(12000, (int)Math.Ceiling(length * 8));
        for (var i = 1; i <= count; i++)
        {
            var current = path.Sample(length * 2 * i / count).Position;
            measured += Distance(previous, current);
            previous = current;
        }
        Assert.AreEqual(length * 2, measured, 0.001);
        Assert.AreEqual(length * 2, path.LengthCells);
    }

    [TestMethod]
    [DataRow(4.0, 1.0, 1.0, 1)]
    [DataRow(20.0, 10.0, 1.0, 3)]
    [DataRow(800.0, 600.0, 10.0, 3)]
    [DataRow(600.0, 28.224, 1.0, 3)]
    [DataRow(6000.0, 1.0, 10.0, 6)]
    [DataRow(600.0, 600.0, 10.0, 3)]
    public void EndpointChordUsesSampledHorizontalLengthAndReservedCorridorWidth(
        double length, double direct, double relief, int trackLimit)
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(length, direct, relief,
            trackLimit: trackLimit), RailwayFixtures.Edge);
        var previous = path.Sample(0).Position;
        var horizontalLength = 0.0;
        var count = Math.Max(12000, (int)Math.Ceiling(length * 8));
        for (var i = 1; i <= count; i++)
        {
            var current = path.Sample(length * 2 * i / count).Position;
            horizontalLength += Math.Sqrt(Math.Pow(current.X - previous.X, 2) +
                Math.Pow(current.Z - previous.Z, 2));
            previous = current;
        }
        var width = 10.5 + 17 * (trackLimit - 1);
        var radius = Math.Max(80, width + 0.5);
        var minimumChord = 4 * radius * Math.Sin(Math.Min(horizontalLength / (4 * radius), Math.PI / 2));
        var expected = Math.Min(horizontalLength, Math.Max(direct * 2, minimumChord));
        Assert.AreEqual(expected, Distance(path.Sample(0).Position, previous), 0.001);
        if (length == direct && relief > 0)
            Assert.IsTrue(horizontalLength < direct * 2, "Relief reduces horizontal length below map distance");
    }

    [TestMethod]
    public void EndpointsUseMapCoordinatesAndLevelTangents()
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(), RailwayFixtures.Edge);
        AssertPoint(new(0, 0, 0), path.Sample(0).Position);
        AssertPoint(new(0, 0, 1200), path.Sample(1600).Position);
        AssertVector(Vector3.UnitZ, path.Sample(0).Forward);
        AssertVector(Vector3.UnitZ, path.Sample(1600).Forward);
        Assert.AreEqual(0.0, RailwayCoordinates.NodeElevation(path.Start));
        Assert.AreEqual(0.0, RailwayCoordinates.NodeElevation(path.End));
    }

    [TestMethod]
    [DataRow(800.0, 600.0, 1)]
    [DataRow(600.0, 28.224, 3)]
    [DataRow(6000.0, 1.0, 3)]
    [DataRow(20.0, 10.0, 3)]
    [DataRow(600.0, 28.224, 6)]
    [DataRow(4.0, 4.0, 1)]
    public void SampledPositionsMeetCurveRadiusAndGradeLimits(double length, double direct, int trackLimit)
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(length, direct, relief: 10,
            trackLimit: trackLimit), RailwayFixtures.Edge);
        var previous = path.Sample(0).Position;
        var beforePrevious = previous;
        var minimumRadius = Math.Max(80, 10.5 + 17 * (trackLimit - 1));
        var count = Math.Max(4000, (int)Math.Ceiling(length * 8));
        for (var i = 1; i <= count; i++)
        {
            var current = path.Sample(length * 2 * i / count).Position;
            var dx = current.X - previous.X;
            var dz = current.Z - previous.Z;
            var horizontal = Math.Sqrt(dx * dx + dz * dz);
            var grade = Math.Abs(current.Y - previous.Y) / horizontal;
            Assert.IsTrue(grade <= 0.12 + 1e-6);
            if (i > 1)
            {
                var ax = previous.X - beforePrevious.X;
                var az = previous.Z - beforePrevious.Z;
                var chord = Math.Sqrt(Math.Pow(current.X - beforePrevious.X, 2) +
                    Math.Pow(current.Z - beforePrevious.Z, 2));
                var curvature = 2 * Math.Abs(ax * dz - az * dx) /
                    (Math.Sqrt(ax * ax + az * az) * horizontal * chord);
                Assert.IsTrue(curvature <= 1 / minimumRadius + 1e-6, $"Curvature {curvature}");
            }
            beforePrevious = previous;
            previous = current;
        }
    }

    [TestMethod]
    public void DiagonalMapAxisRotatesTheWholeAlignmentAndLocateRecoversArcDistance()
    {
        var original = RailwayFixtures.Snapshot();
        var world = original.Core.World;
        var first = world.Nodes[0] with { Position = new(30, -20) };
        var last = world.Nodes[1] with { Position = new(390, 460) };
        var snapshot = RailwayFixtures.ReplaceCore(RailwayFixtures.ReplaceCore(original, first), last);
        var axis = new RailwayPath(original, RailwayFixtures.Edge);
        var diagonal = new RailwayPath(snapshot, RailwayFixtures.Edge);
        AssertPoint(new(60, 0, -40), diagonal.Sample(0).Position);
        AssertPoint(new(780, 0, 920), diagonal.Sample(1600).Position);
        AssertVector(new(0.6f, 0, 0.8f), diagonal.Sample(1600).Forward);
        for (var i = 0; i <= 32; i++)
        {
            var distance = 50.0 * i;
            var sample = axis.Sample(distance).Position;
            var rotated = new ScenePoint(60 + sample.X * 0.8 + sample.Z * 0.6,
                sample.Y, -40 - sample.X * 0.6 + sample.Z * 0.8);
            AssertPoint(rotated, diagonal.Sample(distance).Position);
            Assert.AreEqual(distance, diagonal.Locate(rotated), 1e-6);
        }
    }

    [TestMethod]
    public void CoreDistanceMapsToSymmetricTracksAtThirtyFourCellSpacing()
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(tracks: 4), RailwayFixtures.Edge);
        const double coreDistanceMeters = 137;
        var center = path.Sample(coreDistanceMeters * 2);
        var tracks = Enumerable.Range(1, 4).Select(number =>
            path.Sample(coreDistanceMeters * 2, RailwayCoordinates.TrackOffset(number, 4))).ToArray();
        for (var i = 0; i < tracks.Length; i++)
        {
            var offset = (i - 1.5) * 34;
            AssertPoint(new(center.Position.X + center.Right.X * offset, center.Position.Y,
                center.Position.Z + center.Right.Z * offset), tracks[i].Position);
            AssertVector(center.Forward, tracks[i].Forward);
            if (i > 0) Assert.AreEqual(34.0, Distance(tracks[i - 1].Position, tracks[i].Position), 1e-5);
        }
        AssertPoint(center.Position, new((tracks[0].Position.X + tracks[3].Position.X) / 2,
            (tracks[0].Position.Y + tracks[3].Position.Y) / 2, (tracks[0].Position.Z + tracks[3].Position.Z) / 2));
    }

    [TestMethod]
    [DataRow(4.0, 4.0, 0.0, 1)]
    [DataRow(4.0, 4.0, 10.0, 3)]
    [DataRow(20.0, 10.0, 1.0, 3)]
    [DataRow(800.0, 600.0, 10.0, 3)]
    [DataRow(600.0, 28.224, 0.0, 3)]
    [DataRow(6000.0, 1.0, 10.0, 6)]
    public void WholeCorridorFormsSimplePolygonWithinEndpointPlanes(
        double length, double direct, double relief, int trackLimit)
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(length, direct, relief,
            trackLimit: trackLimit), RailwayFixtures.Edge);
        var width = 10.5 + 17 * (trackLimit - 1);
        const int count = 384;
        var polygon = new ScenePoint[2 * (count + 1)];
        var end = path.Sample(length * 2);
        AssertVector(Vector3.UnitZ, path.Sample(0).Forward);
        AssertVector(Vector3.UnitZ, end.Forward);
        Assert.AreEqual(0.0, end.Position.X, 1e-6);
        Assert.AreEqual(0.0, end.Position.Y, 1e-6);
        Assert.IsTrue(end.Position.Z > 0 && end.Position.Z <= length * 2 + 1e-6);
        var nearStart = path.Sample(0.001).Position;
        var nearEnd = path.Sample(length * 2 - 0.001).Position;
        Assert.IsTrue(nearStart.Z > 0 && nearEnd.Z < end.Position.Z);
        Assert.IsTrue(Math.Abs(nearStart.X) < 1e-6 && Math.Abs(nearStart.Y) < 1e-6);
        Assert.IsTrue(Math.Abs(nearEnd.X - end.Position.X) < 1e-6 &&
            Math.Abs(nearEnd.Y - end.Position.Y) < 1e-6);
        for (var i = 0; i <= count; i++)
        {
            var distance = length * 2 * i / count;
            polygon[i] = path.Sample(distance, width).Position;
            polygon[polygon.Length - 1 - i] = path.Sample(distance, -width).Position;
        }
        foreach (var point in polygon)
            Assert.IsTrue(point.Z >= -1e-6 && point.Z <= end.Position.Z + 1e-6,
                $"Corridor leaves endpoint planes: {point}");
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            Assert.IsTrue(Distance(a, b) > 1e-9, "Collapsed boundary segment");
            for (var j = i + 2; j < polygon.Length; j++)
            {
                if (i == 0 && j == polygon.Length - 1) continue;
                if (Intersects(a, b, polygon[j], polygon[(j + 1) % polygon.Length]))
                    Assert.Fail($"Boundary segments {i} and {j} intersect");
            }
        }
    }

    [TestMethod]
    public void TrackOccupancyDoesNotChangeAlignmentAndLocateIncludesCorridorOffsets()
    {
        var empty = new RailwayPath(RailwayFixtures.Snapshot(600, 28.224, tracks: 0, trackLimit: 3), RailwayFixtures.Edge);
        var occupied = new RailwayPath(RailwayFixtures.Snapshot(600, 28.224, tracks: 3, trackLimit: 3), RailwayFixtures.Edge);
        foreach (var distance in new[] { 0.0, 37, 200, 450, 600, 750, 1000, 1163, 1200 })
            foreach (var offset in new[] { -44.5, -17.0, 0, 17, 44.5 })
            {
                var sample = empty.Sample(distance, offset);
                Assert.AreEqual(sample, occupied.Sample(distance, offset));
                Assert.AreEqual(distance, empty.Locate(sample.Position), 1e-4);
            }
    }

    [TestMethod]
    public void VehicleOverhangExtendsEndpointTangentsAndRepeatedBuildsAreIdentical()
    {
        var first = new RailwayPath(RailwayFixtures.Snapshot(), RailwayFixtures.Edge);
        var second = new RailwayPath(RailwayFixtures.Snapshot(), RailwayFixtures.Edge);
        foreach (var distance in new[] { -30.0, 0, 400, 800, 1200, 1600, 1630 })
        {
            Assert.AreEqual(first.Sample(distance, 17), second.Sample(distance, 17));
            if (distance >= 0 && distance <= 1600) continue;
            var endpoint = first.Sample(Math.Clamp(distance, 0, 1600), 17);
            var extension = distance - Math.Clamp(distance, 0, 1600);
            AssertPoint(new(endpoint.Position.X + endpoint.Forward.X * extension,
                endpoint.Position.Y + endpoint.Forward.Y * extension,
                endpoint.Position.Z + endpoint.Forward.Z * extension), first.Sample(distance, 17).Position);
            AssertVector(endpoint.Forward, first.Sample(distance, 17).Forward);
        }
    }

    [TestMethod]
    [DataRow(599.0, 600.0)]
    public void LengthBelowMapDistanceFailsWithoutChangingCoreInput(double length, double direct)
    {
        var snapshot = RailwayFixtures.Snapshot(length, direct);
        Assert.ThrowsExactly<RailwayBuildException>(() => new RailwayPath(snapshot, RailwayFixtures.Edge));
        Assert.AreEqual(length, snapshot.Core.World.Edges[0].LengthMeters);
        Assert.AreEqual(3UL, snapshot.Core.World.WorldSeed);
    }

    [TestMethod]
    [DataRow(0.0, -34.0)]
    [DataRow(0.0, 34.0)]
    [DataRow(2.0, -34.0)]
    [DataRow(2.0, 34.0)]
    public void OffsetTrackMemberSpacingUsesClosedArcWithBoundedGradeError(double relief, double offset)
    {
        var rules = RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 100 };
        var snapshot = RailwayFixtures.Snapshot(600, 300, relief, 3, content: RailwayFixtures.Content(rules: rules));
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var center = path.TrackArc(380, offset);
        var first = path.CenterArc(center - 32, offset);
        var last = path.CenterArc(center + 32, offset);
        var measured = RailwayFixtures.MeasuredTrackLength(path, first, last, offset);
        // The independent chord sum measures the real 3D curve, rather than the approved planar approximation.
        var slope = relief == 0 ? 0 : Math.Atan(rules.MaximumGrade);
        var innerScale = 1 - Math.Abs(offset) / (rules.MinimumCurveRadiusMeters * 2);
        var relativeError = Math.Sqrt(Math.Pow(Math.Cos(slope), 2) + Math.Pow(Math.Sin(slope) / innerScale, 2)) - 1;
        Assert.AreEqual(64, measured, 64 * relativeError + 1e-6);
        Assert.IsTrue(Vector3.Distance(path.Sample(first).Forward, path.Sample(last).Forward) > .01,
            "The selected interval must exercise a bend.");
    }

    [TestMethod]
    public void OffsetTrackTangentMatchesIndependentPositionDifferencesOnGrade()
    {
        var snapshot = RailwayFixtures.Snapshot(600, 300, 2, 3,
            content: RailwayFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 100 }));
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        foreach (var offset in new[] { -34.0, 34.0 })
        {
            var first = path.Sample(379.999, offset).Position;
            var last = path.Sample(380.001, offset).Position;
            var expected = Vector3.Normalize(new Vector3((float)(last.X - first.X), (float)(last.Y - first.Y), (float)(last.Z - first.Z)));
            AssertVector(expected, path.Sample(380, offset).Forward);
        }
    }

    private static double Distance(ScenePoint a, ScenePoint b) => Math.Sqrt(
        Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));
    private static bool Intersects(ScenePoint a, ScenePoint b, ScenePoint c, ScenePoint d)
    {
        if (Math.Max(a.X, b.X) < Math.Min(c.X, d.X) - 1e-9 ||
            Math.Max(c.X, d.X) < Math.Min(a.X, b.X) - 1e-9 ||
            Math.Max(a.Z, b.Z) < Math.Min(c.Z, d.Z) - 1e-9 ||
            Math.Max(c.Z, d.Z) < Math.Min(a.Z, b.Z) - 1e-9) return false;
        var abC = Cross(a, b, c);
        var abD = Cross(a, b, d);
        var cdA = Cross(c, d, a);
        var cdB = Cross(c, d, b);
        return abC * abD <= 0 && cdA * cdB <= 0;
    }
    private static double Cross(ScenePoint a, ScenePoint b, ScenePoint c) =>
        (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
    private static void AssertPoint(ScenePoint expected, ScenePoint actual) => Assert.IsTrue(Distance(expected, actual) <= 1e-5,
        $"Expected {expected}, actual {actual}");
    private static void AssertVector(Vector3 expected, Vector3 actual) => Assert.IsTrue(Vector3.Distance(expected, actual) <= 1e-6,
        $"Expected {expected}, actual {actual}");
}
