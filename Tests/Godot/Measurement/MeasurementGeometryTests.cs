using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class MeasurementGeometryTests
{
    [TestMethod]
    public void FogIndexQueriesClosedHorizontalBounds()
    {
        var near = new SceneBounds(new(-2, 100, -2), new(2, 110, 2));
        var touching = new SceneBounds(new(2, -10, 2), new(4, -5, 4));
        var negative = new SceneBounds(new(-40, 0, -30), new(-20, 1, -10));
        var far = new SceneBounds(new(1000, 0, 1000), new(1010, 1, 1010));
        var index = new SceneProbeFogIndex([far, touching, negative, near]);

        CollectionAssert.AreEquivalent(new[] { near, touching },
            index.Query(new(new(-2, 0, -2), new(2, 1, 2))).ToArray());
        CollectionAssert.AreEquivalent(new[] { negative },
            index.Query(new(new(-35, 0, -25), new(-25, 1, -15))).ToArray());
        CollectionAssert.AreEquivalent(new[] { far },
            index.Query(new(new(1005, 0, 1005), new(1020, 1, 1020))).ToArray());
        Assert.AreEqual(0, index.Query(new(new(10, 0, 10), new(20, 1, 20))).Count());
        Assert.AreEqual(0, new SceneProbeFogIndex([]).Query(new(Vector3.Zero, Vector3.One)).Count());
    }

    [TestMethod]
    public void FogExcludesMaximumBoundaryAndItsTangencyWithTheNearArea()
    {
        var fog = new SceneBounds(new(-288, -128, 440), new(-256, 128, 472));
        Assert.IsTrue(MeasurementRun.ProbeFogContains(new(-288, 0, 450), fog));
        Assert.IsFalse(MeasurementRun.ProbeFogContains(new(-256, 0, 450), fog));
        Assert.IsFalse(MeasurementRun.ProbeFogContains(new(-270, 0, 472), fog));
        Assert.IsFalse(MeasurementRun.ProbeFogOverlaps(new(-192, 0, 459.2434f), 64, fog));
        Assert.IsTrue(MeasurementRun.ProbeFogOverlaps(new(-192.01f, 0, 459.2434f), 64, fog));
        Assert.IsTrue(MeasurementRun.ProbeFogOverlaps(new(-352, 0, 450), 64, fog));
        Assert.IsFalse(MeasurementRun.ProbeFogOverlaps(new(-352.01f, 0, 450), 64, fog));
    }

    [TestMethod]
    public void RailwaySamplesKeepRingRadiusAndDistinctPositionsInsideReferenceTerrain()
    {
        var terrain = new HashSet<(int, int)>();
        for (var x = -80; x <= 80; x++) for (var z = -4; z <= 4; z++) terrain.Add((x, z));
        foreach (var radius in new[] { 32f, 64f })
        {
            var points = MeasurementRun.ProbeBandRing(Vector3.Zero, radius, terrain);
            Assert.AreEqual(8, points.Length);
            Assert.AreEqual(8, points.Distinct().Count());
            Assert.IsTrue(points.All(point => Math.Abs(point.Length() - radius) < 1e-4));
            Assert.IsTrue(points.All(point => terrain.Contains(((int)Math.Floor(point.X), (int)Math.Floor(point.Z)))));
            Assert.IsTrue(points.Any(point => point.X < 0) && points.Any(point => point.X > 0));
        }
        Assert.AreEqual(0, MeasurementRun.ProbeBandRing(Vector3.Zero, 64, []).Length);
    }

    [TestMethod]
    public void NearAreaUsesHorizontalDistanceToOccupiedBounds()
    {
        var bounds = new SceneBounds(new(60, 100, 10), new(70, 120, 20));
        Assert.AreEqual(Math.Sqrt(3700), MeasurementRun.ProbeDistance(Vector3.Zero, bounds), 1e-8);
        Assert.AreEqual(0, MeasurementRun.ProbeDistance(new(65, -100, 15), bounds));
    }

    [TestMethod]
    public void BoundsAccountForRotationPlacementAndFloatingOrigin()
    {
        var transformed = MeasurementRun.ProbeTransformBounds(new(new(-1, 0, -2), new(3, 4, 2)),
            new(new(10, 0, 20), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)), new(2, 0, 4));
        Assert.IsTrue(Vector3.Distance(new(6, 0, 13), transformed.Minimum) < 1e-5);
        Assert.IsTrue(Vector3.Distance(new(10, 4, 17), transformed.Maximum) < 1e-5);
    }

    [TestMethod]
    public void PlacementRotatesAroundTheCellCenter()
    {
        var pose = MeasurementRun.ProbePlacement(new(10, 20, 30), VoxelOrientation.PzPy);
        Assert.IsTrue(Vector3.Distance(new(10.5f, 20.5f, 30.5f), pose.Position + Vector3.Transform(new(.5f), pose.Rotation)) < 1e-5);
        Assert.IsTrue(Vector3.Distance(Vector3.UnitZ, Vector3.Transform(Vector3.UnitX, pose.Rotation)) < 1e-5);
    }
}
