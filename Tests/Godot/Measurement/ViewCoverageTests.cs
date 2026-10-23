using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class ViewCoverageTests
{
    [TestMethod]
    public void IntersectsStableZeroPlaneInDisplayedRootCoordinates()
    {
        var frame = Frame(new(100, 20, 300), fog: [new(new(2, -20, 4), new(2, -20, 4))]);
        var sample = ViewCoverage.Sample(frame, [new(new(2, -10, 4), new(0, -2, 0), 1, 20)]);
        Assert.AreEqual(1, sample.FogOnly);
        Assert.AreEqual(1, sample.Eligible);
    }

    [TestMethod]
    public void ExcludesNoForwardAndClippedIntersectionsAndIncludesClipBoundaries()
    {
        var sample = ViewCoverage.Sample(Frame(), [
            Ray(0, 1, 0), Ray(0, 0, 1),
            new(new(0, 10, 0), new(0, -1, 0), 11, 20),
            new(new(0, 10, 0), new(0, -1, 0), 0, 9),
            new(new(0, 10, 0), new(0, -1, 0), 10, 10)]);
        Assert.AreEqual(2, sample.ExcludedNoForward);
        Assert.AreEqual(1, sample.ExcludedNear);
        Assert.AreEqual(1, sample.ExcludedFar);
        Assert.AreEqual(1, sample.Eligible);
        Assert.IsFalse(ViewCoverage.Sample(Frame(), [Ray(0, 1, 0)]).Known);
    }

    [TestMethod]
    public void TerrainTruthWinsAndFogRequiresAllThreeCoordinatesIncludingBoundaries()
    {
        var frame = Frame(fog: [new(new(-1, -1, -1), new(1, 1, 1))]);
        frame = new FixedSceneFrame
        {
            Update = frame.Update,
            TerrainHeightResult = new Lookup<double>.Missing(),
            Heights = ImmutableDictionary<(double, double), Lookup<double>>.Empty.Add((0, 0), new Lookup<double>.Found(20))
        };
        var sample = ViewCoverage.Sample(frame, [Vertical(0, 0), Vertical(1, 1), Vertical(2, 0)]);
        Assert.AreEqual(1, sample.Terrain);
        Assert.AreEqual(1, sample.FogOnly);
        Assert.AreEqual(1, sample.Neither);
        // A box may contain the XZ point while missing the stable baseline in Y.
        var elevatedFog = Frame(fog: [new(new(-10, 1, -10), new(10, 2, 10))]);
        Assert.AreEqual(1, ViewCoverage.Sample(elevatedFog, [Vertical(0, 0)]).Neither);
    }

    [TestMethod]
    public void UsesTerrainQueryInsteadOfInferringCurvedTerrainFromItsBoundingBox()
    {
        // The corner (1,1) lies inside a quarter-turn's XZ bounds but outside its terrain.
        var frame = new FixedSceneFrame
        {
            Update = Frame().Update, TerrainHeightResult = new Lookup<double>.Missing(),
            Heights = ImmutableDictionary<(double, double), Lookup<double>>.Empty
                .Add((10, 0), new Lookup<double>.Found(0)).Add((0, 10), new Lookup<double>.Found(0))
        };
        var sample = ViewCoverage.Sample(frame, [Vertical(10, 0), Vertical(0, 10), Vertical(1, 1)]);
        Assert.AreEqual(1, sample.Neither);
        Assert.AreEqual(2, sample.Terrain);
    }

    [TestMethod]
    public void GapEstimatesUseActualSpacingAndCensorAtUnknownSpaceWindowAndEnds()
    {
        var stamp = Frame().Update.Frame;
        var gap = ViewCoverage.Sample(Frame(), [Vertical(0, 0)]);
        var clear = gap with { Terrain = 1, Neither = 0 };
        var result = ViewCoverage.Analyze([
            new(0, stamp, "a", gap), new(.2, stamp, "a", gap), new(.7, stamp, "a", clear),
            new(1, stamp, "a", gap), new(1.4, stamp, "a", null),
            new(2, stamp, "a", gap), new(2.2, stamp with { Space = new(2) }, "a", gap),
            new(2.5, stamp with { Space = new(2) }, "b", gap), new(3, stamp with { Space = new(2) }, "b", gap)]);
        Assert.AreEqual(5, result.Neither.Intervals.Length);
        Assert.AreEqual(.7, result.Neither.Intervals[0].DurationSeconds, .00001);
        Assert.IsTrue(result.Neither.Intervals[0].LeftCensored);
        Assert.IsFalse(result.Neither.Intervals[0].RightCensored);
        Assert.AreEqual(.4, result.Neither.Intervals[1].DurationSeconds, .00001);
        Assert.IsTrue(result.Neither.Intervals[1].RightCensored);
        Assert.IsTrue(result.Neither.Intervals[^1].RightCensored);
        Assert.AreEqual(.6, result.SampleIntervals.MaxSeconds!.Value, .00001);
    }

    [TestMethod]
    public void DomainBoundaryEndsLeftHeldGapAtBoundaryWithoutJoiningNextDomain()
    {
        var stamp = Frame().Update.Frame;
        var gap = ViewCoverage.Sample(Frame(), [Vertical(0, 0)]);
        var analysis = ViewCoverage.Analyze([
            new(0, stamp, "entry", gap), new(.2, stamp, "entry", gap),
            new(.3, stamp, "exit", gap), new(.5, stamp with { Space = new(2) }, "exit", gap),
            new(.7, null, "exit", null)]);
        Assert.AreEqual(3, analysis.Neither.Intervals.Length);
        var intervals = analysis.Neither.Intervals;
        Assert.AreEqual(.3, intervals[0].DurationSeconds, .00001);
        Assert.AreEqual(.3, intervals[1].StartSeconds, .00001);
        Assert.AreEqual(.5, intervals[1].EndSeconds, .00001);
        Assert.AreEqual(.7, intervals[2].EndSeconds, .00001);
        Assert.IsTrue(intervals.All(interval => interval.LeftCensored && interval.RightCensored));
        Assert.AreEqual(.7, analysis.Neither.Durations.TotalSeconds, .00001);
    }

    [TestMethod]
    public void ActualSampleIntervalsIncludeUnknownAndEveryDomainBoundary()
    {
        var stamp = Frame().Update.Frame;
        var gap = ViewCoverage.Sample(Frame(), [Vertical(0, 0)]);
        var analysis = ViewCoverage.Analyze([
            new(0, stamp, "entry", gap), new(.1, stamp, "entry", null),
            new(.4, null, "entry", null), new(.9, stamp with { Space = new(2) }, "exit", gap)]);
        Assert.AreEqual(3, analysis.SampleIntervals.Count);
        Assert.AreEqual(.9, analysis.SampleIntervals.TotalSeconds, .00001);
        Assert.AreEqual(.3, analysis.SampleIntervals.P50Seconds!.Value, .00001);
        Assert.AreEqual(.5, analysis.SampleIntervals.P99Seconds!.Value, .00001);
        Assert.AreEqual(.5, analysis.SampleIntervals.MaxSeconds!.Value, .00001);
    }
    [TestMethod]
    public void RealEndExtendsOnlyRightCensoredTailWithoutInventingSampleInterval()
    {
        var stamp = Frame().Update.Frame;
        var gap = ViewCoverage.Sample(Frame(), [Vertical(0, 0)]);
        var analysis = ViewCoverage.Analyze([new(1, stamp, "entry", gap), new(1.2, stamp, "entry", gap)], 1.35);
        var tail = analysis.Neither.Intervals.Single();
        Assert.AreEqual(1.35, tail.EndSeconds, .00001);
        Assert.AreEqual(.35, tail.DurationSeconds, .00001);
        Assert.IsTrue(tail.RightCensored);
        Assert.AreEqual(1, analysis.SampleIntervals.Count);
        Assert.AreEqual(.2, analysis.SampleIntervals.TotalSeconds, .00001);
        var unknownTail = ViewCoverage.Analyze([new(1, stamp, "entry", gap), new(1.2, stamp, "entry", null)], 1.35);
        Assert.AreEqual(1.2, unknownTail.Neither.Intervals.Single().EndSeconds, .00001);
    }
    private static CoverageRay Ray(double x, double y, double z) => new(new(0, 10, 0), new(x, y, z), 0, 100);
    private static CoverageRay Vertical(double x, double z) => new(new(x, 10, z), new(0, -1, 0), 0, 100);
    private static FixedSceneFrame Frame(ScenePoint origin = default, ImmutableArray<SceneBounds> fog = default) => new()
    {
        Origin = origin, TerrainHeightResult = new Lookup<double>.Missing(),
        Update = new(null, new(new(Guid.Empty), new(1), new(1), 1), default, [], [], new(fog.IsDefault ? [] : fog, []), [])
    };
}
