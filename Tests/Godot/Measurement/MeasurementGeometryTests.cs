using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using Train.Scenes.Contracts;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class MeasurementGeometryTests
{
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
    public void FogBetweenRingSamplesOverlapsTheReferenceTerrainBand()
    {
        var terrain = new HashSet<(int, int)>();
        for (var x = -80; x <= 80; x++) for (var z = -4; z <= 4; z++) terrain.Add((x, z));

        Assert.IsTrue(MeasurementRun.ProbeFogOverlapsBand(Vector3.Zero, 64,
            new(new(10, 100, 0), new(12, 110, 2)), terrain));
        Assert.IsFalse(MeasurementRun.ProbeFogOverlapsBand(Vector3.Zero, 64,
            new(new(10, 100, 20), new(12, 110, 22)), terrain));
    }

    [TestMethod]
    public void FogInsideTerrainButOutsideTheNearCircleDoesNotOverlap()
    {
        Assert.IsFalse(MeasurementRun.ProbeFogOverlapsBand(Vector3.Zero, 64,
            new(new(60, 0, 40), new(61, 1, 41)), [(60, 40)]));
    }

    [TestMethod]
    public void BandOverlapIncludesMinimumTangencyButExcludesMaximumTangency()
    {
        Assert.IsTrue(MeasurementRun.ProbeFogOverlapsBand(Vector3.Zero, 64,
            new(new(64, 0, 0), new(65, 1, 1)), [(64, 0)]));
        Assert.IsFalse(MeasurementRun.ProbeFogOverlapsBand(Vector3.Zero, 64,
            new(new(-65, 0, 0), new(-64, 1, 1)), [(-65, 0)]));
    }

    [TestMethod]
    public void BandOverlapUsesAbsoluteNegativeCellCoordinates()
    {
        Assert.IsTrue(MeasurementRun.ProbeFogOverlapsBand(new(-100, 0, -100), 64,
            new(new(-90, 0, -100), new(-89, 1, -99)), [(-90, -100)]));
        Assert.IsFalse(MeasurementRun.ProbeFogOverlapsBand(new(-100, 0, -100), 64,
            new(new(-90, 0, -100), new(-89, 1, -99)), [(10, 0)]));
    }
}
