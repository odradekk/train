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
