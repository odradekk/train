using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using Train.Scenes.Contracts;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class FollowRouteAnalysisTests
{
    [TestMethod]
    public void AheadDistanceFollowsObservedCurveRatherThanEndpointDisplacement()
    {
        var ranges = Range(1, Bounds(-10, -10, 10, 10));
        var report = Analyze([Frame(1, 0, 0), Frame(2, 4, 0), Frame(3, 4, 4)], ranges);

        Assert.AreEqual(4d, report.Frames[0].Scenes.AheadMeters);
        Assert.IsTrue(report.Frames[0].Scenes.ObservationLimited);
        Assert.IsNull(report.Summary.MinimumScenesAheadMeters);
    }

    [TestMethod]
    public void FogBetweenDrawnHeadPositionsStopsCoverage()
    {
        var range = Range(1, Bounds(-10, -10, 10, 10)) with { Fog = [Bounds(-1, 2, 1, 3)] };
        var result = Analyze([Frame(1, 0, 0), Frame(2, 0, 6)], range).Frames[0].Scenes;

        Assert.AreEqual("fog", result.Missing);
        Assert.AreEqual(.5, result.AheadMeters);
        Assert.AreEqual(new ScenePoint(0, 0, 2), result.FirstMissingPoint);
        Assert.IsFalse(result.ObservationLimited);
    }

    [TestMethod]
    public void LaterConstructionCannotFillAnEarlierFramesCoverage()
    {
        var first = Range(1, Bounds(-1, -3, 1, 2));
        var later = Range(2, Bounds(-1, -3, 1, 20));
        var report = FollowRouteAnalysis.Analyze([Frame(1, 0, 0), Frame(2, 0, 8) with { ScenesRanges = 2, DisplayedRanges = 2 }],
            new Dictionary<long, RouteRanges> { [1] = first, [2] = later });

        Assert.AreEqual(1d, report.Frames[0].Scenes.AheadMeters);
        Assert.AreEqual("terrain_bounds_missing", report.Frames[0].Scenes.Missing);
        Assert.IsTrue(report.Frames[1].Scenes.ObservationLimited);
    }

    [TestMethod]
    public void CoordinateAndOrientationBoundariesAreTruncationsRatherThanPasses()
    {
        var range = Range(1, Bounds(-20, -20, 20, 20));
        var nextSpace = Frame(3, 0, 8) with { Latest = Stamp(2) };
        var report = Analyze([Frame(1, 0, 0), Frame(2, 0, 4), nextSpace], range);

        Assert.AreEqual(2d, report.Frames[0].Scenes.AheadMeters);
        Assert.IsTrue(report.Frames[0].Scenes.BoundaryLimited);
        Assert.IsTrue(report.Frames[2].FirstInSpace);
        Assert.IsNull(report.Frames[2].Scenes.AheadMeters);
        Assert.AreEqual("ranges_coordinates_mismatch", report.Frames[2].Scenes.Missing);
        var reversed = Analyze([Frame(1, 0, 0), Frame(2, 0, 4) with { FirstEndLeading = false }], range);
        Assert.IsTrue(reversed.Frames[0].Scenes.BoundaryLimited);
        Assert.AreEqual(0d, reversed.Frames[0].Scenes.AheadMeters);
    }

    [TestMethod]
    public void MissingDisplayedPosesAreUnknownRatherThanBuilt()
    {
        var frame = Frame(1, 0, 0) with { DisplayedVehicles = [] };
        var report = Analyze([frame], Range(1, Bounds(-20, -20, 20, 20)));

        Assert.IsNull(report.Frames[0].Displayed.AheadMeters);
        Assert.IsNull(report.Frames[0].Displayed.VehicleCentersCovered);
        Assert.AreEqual(1, report.Summary.UnknownDisplayedFrames);
        Assert.IsNotNull(report.Frames[0].Scenes.AheadMeters);
    }

    [TestMethod]
    public void ReversedTrainUsesLastVehicleAndSeparatesCentersFromBodyEndpoints()
    {
        RouteVehicle[] vehicles = [new(1, new(0, 0, 3), [0, 0, 0, 1], 2), new(2, new(0, 0, 0), [0, 0, 0, 1], 2)];
        var frame = Frame(1, 0, 0) with { Vehicles = vehicles, DisplayedVehicles = vehicles, FirstEndLeading = false };
        var result = Analyze([frame], Range(1, Bounds(-1, 0, 1, 4))).Frames[0].Scenes;

        Assert.IsTrue(result.VehicleCentersCovered);
        Assert.IsFalse(result.SampledBodyCovered);
        Assert.AreEqual(0d, result.AheadMeters);
        Assert.AreEqual(new ScenePoint(0, 0, -1), result.FirstMissingPoint);
    }

    [TestMethod]
    public void FirstFrameOfNewSpaceKeepsZeroAndSourcesUseSeparateRanges()
    {
        var broad = Range(1, Bounds(-20, -20, 20, 20));
        var fogged = Range(2, Bounds(-20, -20, 20, 20)) with
        {
            Frame = Stamp(2), Coordinates = new(2), Fog = [Bounds(-1, -1, 1, 1)]
        };
        var clear = fogged with { Id = 3, Fog = [] };
        var frame = Frame(2, 0, 0) with { Latest = Stamp(2), Displayed = Stamp(2), ScenesRanges = 3, DisplayedRanges = 2 };
        var report = FollowRouteAnalysis.Analyze([Frame(1, 0, 0), frame],
            new Dictionary<long, RouteRanges> { [1] = broad, [2] = fogged, [3] = clear });

        Assert.IsTrue(report.Frames[1].FirstInSpace);
        Assert.AreEqual(0d, report.Frames[1].Displayed.AheadMeters);
        Assert.AreEqual("fog", report.Frames[1].Displayed.Missing);
        Assert.IsNull(report.Frames[1].Scenes.Missing);
        Assert.AreEqual(0d, report.Summary.MinimumDisplayedAheadMeters);
    }

    [TestMethod]
    public void MissingReceivedVehicleIsUnknownEvenWhenAllRecordedVehiclesAreCovered()
    {
        var frame = Frame(1, 0, 0) with { ExpectedVehicles = [1, 2] };
        var report = Analyze([frame], Range(1, Bounds(-20, -20, 20, 20)));

        Assert.IsNull(report.Frames[0].Scenes.AheadMeters);
        Assert.IsNull(report.Frames[0].Scenes.VehicleCentersCovered);
        Assert.AreEqual("vehicle_pose_missing", report.Frames[0].Scenes.Missing);
    }

    [TestMethod]
    public void FogMaximumBoundaryIsExcludedWhileGeometryMaximumIsIncluded()
    {
        var range = Range(1, Bounds(-1, -3, 1, 0)) with { Fog = [Bounds(-1, -3, 1, 0)] };
        var result = Analyze([Frame(1, 0, 0)], range).Frames[0].Scenes;

        Assert.AreEqual(0d, result.AheadMeters);
        Assert.IsNull(result.Missing);
        Assert.IsTrue(result.ObservationLimited);
    }

    private static FollowRouteAnalysis.Report Analyze(RouteFrame[] frames, RouteRanges range) =>
        FollowRouteAnalysis.Analyze(frames, new Dictionary<long, RouteRanges> { [range.Id] = range });

    private static SceneFrameStamp Stamp(ulong space = 1) => new(new(Guid.Empty), new(1), new(space), 1);
    private static RouteBounds Bounds(double x0, double z0, double x1, double z1) => new(new(x0, -1, z0), new(x1, 1, z1));
    private static RouteRanges Range(long id, RouteBounds bounds) => new(id, Stamp(), new(1),
        [new(1, 0, bounds)], [new(2, 0, bounds)], []);
    private static RouteFrame Frame(long draw, double headX, double headZ)
    {
        RouteVehicle[] vehicles = [new(1, new(headX, 0, headZ - 1), [0, 0, 0, 1], 2)];
        return new(draw, draw, 1, (ulong)draw, 1, "One", Stamp(), Stamp(), Stamp(), 1, 1,
            vehicles, vehicles, true, JsonSerializer.SerializeToElement(new { }), 0, 0);
    }
}
