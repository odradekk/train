using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using Train.Scenes.Contracts;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class FollowRouteAnalysisTests
{
    [TestMethod]
    public void HeadUsesFutureCenterArcRatherThanVehiclePoseTangent()
    {
        var frames = new[] { Frame(1, 0, 0), Frame(2, 2, 0), Frame(3, 2, 4), Frame(4, 2, 6) };
        var result = Analyze(frames, Range(1, Bounds(-1, -1, 3, .2))).Frames[0].Scenes;

        // Pose +Z would put the nose outside immediately. Actual route first runs along +X.
        Assert.AreEqual(.5, result.AheadMeters!.Value, 1e-8);
        Assert.AreEqual(new ScenePoint(2, 0, 1), result.FirstMissingPoint);
        Assert.IsTrue(result.VehicleCentersCovered);
    }

    [TestMethod]
    public void ForwardWalksIntermediateCurveVerticesRatherThanHeadChords()
    {
        var report = Analyze([Frame(1, 0, 0), Frame(2, 2, 0), Frame(3, 2, 4), Frame(4, 2, 6)],
            Range(1, Bounds(-10, -10, 10, 10)));

        Assert.AreEqual(3d, report.Frames[0].Scenes.AheadMeters!.Value, 1e-8);
        Assert.IsTrue(report.Frames[0].Scenes.ObservationLimited);
        Assert.IsFalse(report.Frames[0].Scenes.BoundaryLimited);
        Assert.IsNull(report.Summary.MinimumScenesAheadMeters);
    }

    [TestMethod]
    public void TruncatedCoverageKeepsLowerBoundsAndCountsUnknownForEachTarget()
    {
        var frames = new[] { Frame(1, 0, 0), Frame(2, 0, 80) with { DecisionSample = false },
            Frame(3, 0, 82) with { DecisionSample = false } };
        var report = Analyze(frames, Range(1, Bounds(-10, -10, 10, 200)));

        Assert.AreEqual(1, report.Summary.ObservationLimitedScenesFrames);
        Assert.AreEqual(40d, report.Summary.MinimumScenesAheadLowerBoundMeters);
        Assert.IsNull(report.Summary.MinimumScenesAheadMeters);
        Assert.AreEqual(0, report.Summary.UnknownScenes32MetersFrames);
        Assert.AreEqual(1, report.Summary.UnknownScenes64MetersFrames);
        Assert.AreEqual(1, report.Summary.UnknownDisplayed64MetersFrames);
    }

    [TestMethod]
    public void FogBetweenDrawsStopsFrozenCoverage()
    {
        var range = Range(1, Bounds(-10, -10, 10, 20)) with { Fog = [Bounds(-1, 3, 1, 4)] };
        var result = Analyze([Frame(1, 0, 0), Frame(2, 0, 6), Frame(3, 0, 10)], range).Frames[0].Scenes;

        Assert.AreEqual("fog", result.Missing);
        Assert.AreEqual(.5, result.AheadMeters!.Value, 1e-8);
        Assert.AreEqual(new ScenePoint(0, 0, 3), result.FirstMissingPoint);
    }

    [TestMethod]
    public void LaterConstructionCannotFillEarlierGeometry()
    {
        var first = Range(1, Bounds(-1, -1, 1, 3));
        var later = Range(2, Bounds(-1, -1, 1, 20));
        var frames = new[] { Frame(1, 0, 0), Frame(2, 0, 6) with { ScenesRanges = 2, DisplayedRanges = 2 }, Frame(3, 0, 10) };
        var result = FollowRouteAnalysis.Analyze(frames, new Dictionary<long, RouteRanges> { [1] = first, [2] = later }).Frames[0].Scenes;

        Assert.AreEqual(1d, result.AheadMeters!.Value, 1e-8);
        Assert.AreEqual("terrain_bounds_missing", result.Missing);
    }

    [TestMethod]
    public void ReceivedAndDisplayedPoseGapsAreUnknown()
    {
        var range = Range(1, Bounds(-20, -20, 20, 20));
        var missing = Analyze([Frame(1, 0, 0) with { ExpectedVehicles = [1, 2] }], range).Frames[0];
        Assert.IsNull(missing.Scenes.AheadMeters);
        Assert.IsNull(missing.Scenes.VehicleCentersCovered);
        Assert.AreEqual("vehicle_pose_missing", missing.Scenes.Missing);

        var shown = Analyze([Frame(1, 0, 0) with { DisplayedVehicles = [] }, Frame(2, 0, 4)], range).Frames[0];
        Assert.IsNull(shown.Displayed.AheadMeters);
        Assert.IsNotNull(shown.Scenes.AheadMeters);
    }

    [TestMethod]
    public void ReverseOrderUsesLastVehicleCenterAndFutureTravelDirection()
    {
        RouteFrame Reverse(long draw, double lastZ)
        {
            RouteVehicle[] vehicles = [new(1, new(0, 0, lastZ + 4), 2), new(2, new(0, 0, lastZ), 2)];
            return Frame(draw, 0, 0) with { Vehicles = vehicles, DisplayedVehicles = vehicles, FirstEndLeading = false };
        }
        var result = Analyze([Reverse(1, 2), Reverse(2, 0), Reverse(3, -2)], Range(1, Bounds(-1, 2, 1, 10))).Frames[0].Scenes;

        Assert.IsTrue(result.VehicleCentersCovered);
        Assert.AreEqual(new ScenePoint(0, 0, 1), result.FirstMissingPoint);
        Assert.IsNull(result.SampledBodyCovered);
        Assert.AreEqual("initial_history_insufficient", result.BodyUnknown);
    }

    [TestMethod]
    public void CurvedBodyStitchesCoordinateChangeAcrossSpaces()
    {
        var old = Range(1, Bounds(-10, -10, 10, 10));
        var next = Range(2, Bounds(101, 0, 102, 3)) with
        {
            Frame = Stamp(2), Coordinates = new(2), Fog = [Bounds(101, .5, 101.5, 2.5)]
        };
        var frames = new[]
        {
            Frame(1, 0, 0) with { TrainLengthMeters = 2 },
            Frame(2, 2, 0) with { TrainLengthMeters = 2 },
            InSpace(Frame(3, 102, 2), 2, 2) with
            {
                TrainLengthMeters = 2,
                Transition = new(Stamp(), new(2000, 0, 0), Stamp(2), new(2100, 0, 0), false)
            },
            InSpace(Frame(4, 102, 4), 2, 2) with { TrainLengthMeters = 2 },
            InSpace(Frame(5, 102, 6), 2, 2) with { TrainLengthMeters = 2 }
        };
        var report = FollowRouteAnalysis.Analyze(frames, new Dictionary<long, RouteRanges> { [1] = old, [2] = next });

        Assert.IsTrue(report.Frames[2].Scenes.SampledBodyCovered);
        Assert.IsTrue(report.Frames[2].Displayed.SampledBodyCovered);
        Assert.IsNull(report.Frames[2].Scenes.BodyUnknown);
        Assert.AreEqual("initial_history_insufficient", report.Frames[0].Scenes.BodyUnknown);
    }

    [TestMethod]
    public void SpaceChangeDoesNotUseLeadingVehicleCenterAsBoundary()
    {
        foreach (var length in new[] { 2d, 8d })
        {
            RouteFrame Consist(long draw, double z, ulong space)
            {
                RouteVehicle[] vehicles = [new(1, new(0, 0, z + length / 2), length),
                    new(2, new(0, 0, z - length / 2), length)];
                return InSpace(Frame(draw, 0, z), space, (long)space) with
                { Vehicles = vehicles, DisplayedVehicles = vehicles, ExpectedVehicles = [1, 2],
                    TrainLengthMeters = length };
            }
            var frames = new[] { Consist(1, 0, 1), Consist(2, 4, 1),
                Consist(3, 8, 2) with { Transition = new(Stamp(), new(), Stamp(2), new(), false) },
                Consist(4, 12, 2) };
            var ranges = new Dictionary<long, RouteRanges>
            {
                [1] = Range(1, Bounds(-1, -1, 1, 4)),
                [2] = Range(2, Bounds(-1, -1, 1, 100)) with { Frame = Stamp(2), Coordinates = new(2) }
            };
            var report = FollowRouteAnalysis.Analyze(frames, ranges);
            Assert.IsFalse(report.Frames[0].Scenes.BoundaryLimited);
            Assert.IsTrue(report.Frames[0].Scenes.ObservationLimited);
            Assert.AreEqual("boundary_position_unknown", report.Frames[0].Scenes.Missing);
            Assert.IsTrue(report.Summary.UnknownScenes64MetersFrames > 0);
            // The reference center is at z=4; the leading center and interpolated head already extend beyond it.
            Assert.AreEqual(0d, report.Frames[1].Scenes.AheadMeters);
            Assert.IsTrue(report.Frames[1].Scenes.ObservationLimited);
            Assert.AreEqual("boundary_position_unknown", report.Frames[1].Scenes.Missing);
            Assert.IsNull(report.Frames[1].Scenes.FirstMissingPoint);
            if (length == 2)
                Assert.IsFalse(report.Frames[1].Scenes.SampledBodyCovered,
                    "Unknown forward boundary position must not suppress an observed body gap.");
            else Assert.IsNull(report.Frames[1].Scenes.SampledBodyCovered);
        }
    }

    [TestMethod]
    public void RecordingEndNeverExtrapolatesAHeadOrPretendsToBeSpaceBoundary()
    {
        var result = Analyze([Frame(1, 0, 0)], Range(1, Bounds(-10, -10, 10, 10))).Frames[0].Scenes;
        Assert.IsNull(result.AheadMeters);
        Assert.IsTrue(result.ObservationLimited);
        Assert.IsFalse(result.BoundaryLimited);
        Assert.AreEqual("head_future_unobserved", result.Missing);
    }

    [TestMethod]
    public void PathSwitchAndMultitrackBoundaryInvalidateHistory()
    {
        var first = Frame(1, 0, 0) with { Position = JsonSerializer.SerializeToElement(new { platform_number = 0, next_edge = 1, entered_from = 2 }) };
        var second = Frame(2, 0, 4) with { Position = JsonSerializer.SerializeToElement(new { platform_number = 1, next_edge = 1, entered_from = 2 }) };
        var third = second with { Draw = 3, Vehicles = [new(1, new(0, 0, 8), 2)], DisplayedVehicles = [new(1, new(0, 0, 8), 2)] };
        var result = Analyze([first, second, third], Range(1, Bounds(-10, -10, 10, 20))).Frames[1].Scenes;
        Assert.IsNull(result.SampledBodyCovered);
        Assert.AreEqual("node_path_changed", result.BodyUnknown);

        var changed = InSpace(Frame(2, 0, 4), 2, 2) with { Transition = new(Stamp(), new(), Stamp(2), new(), true) };
        var later = InSpace(Frame(3, 0, 8), 2, 2);
        var range = Range(2, Bounds(-10, -10, 10, 20)) with { Frame = Stamp(2), Coordinates = new(2) };
        var multiple = FollowRouteAnalysis.Analyze([first, changed, later],
            new Dictionary<long, RouteRanges> { [1] = Range(1, Bounds(-10, -10, 10, 20)), [2] = range });
        Assert.AreEqual("multitrack_boundary", multiple.Frames[1].Scenes.BodyUnknown);
    }

    [TestMethod]
    public void InitialPreoperableAndPostrollAreExcludedButLaterInoperableIsKept()
    {
        var frames = new[] { Frame(1, 0, 0) with { Operable = false }, Frame(2, 0, 4),
            Frame(3, 0, 8) with { Operable = false }, Frame(4, 0, 12) with { DecisionSample = false } };
        var report = Analyze(frames, Range(1, Bounds(-10, -10, 10, 20)));

        Assert.IsFalse(report.Frames[0].DecisionEligible);
        Assert.AreEqual("initial_preoperable", report.Frames[0].ExcludedReason);
        Assert.IsTrue(report.Frames[2].DecisionEligible);
        Assert.IsFalse(report.Frames[3].DecisionEligible);
        Assert.AreEqual("postroll", report.Frames[3].ExcludedReason);
        Assert.AreEqual(1, report.Summary.ExcludedPreoperableFrames);
    }

    [TestMethod]
    public void RangeDeltasReplayRemovalReplacementFogInheritanceAndReset()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var original = new RouteRangeChange(1, null, Stamp(), new(1),
            [new(1, 0, Bounds(-1, -1, 1, 1))], [new(2, 0, Bounds(-1, -1, 1, 1))], [], [Bounds(0, 0, 1, 1)]);
        var updated = new RouteRangeChange(2, 1, Stamp(), new(1), [new(3, 0, Bounds(-2, -2, 2, 2))], [], [new(1, 0)], null);
        var reset = new RouteRangeChange(3, null, Stamp(), new(1), [], [], [], []);
        var states = FollowRouteAnalysis.ReadRanges(new[] { JsonSerializer.Serialize(original, options),
            JsonSerializer.Serialize(updated, options), JsonSerializer.Serialize(reset, options) });

        Assert.AreEqual(3ul, states[2].Terrain.Single().Object);
        Assert.AreEqual(2ul, states[2].Rails.Single().Object);
        Assert.AreEqual(original.Fog!.Single(), states[2].Fog.Single());
        Assert.AreEqual(0, states[3].Terrain.Length);
        Assert.AreEqual(0, states[3].Rails.Length);
        Assert.AreEqual(0, states[3].Fog.Length);
    }

    private static FollowRouteAnalysis.Report Analyze(RouteFrame[] frames, RouteRanges range) =>
        FollowRouteAnalysis.Analyze(frames, new Dictionary<long, RouteRanges> { [range.Id] = range });
    private static SceneFrameStamp Stamp(ulong space = 1) => new(new(Guid.Empty), new(1), new(space), 1);
    private static RouteBounds Bounds(double x0, double z0, double x1, double z1) => new(new(x0, -1, z0), new(x1, 1, z1));
    private static RouteRanges Range(long id, RouteBounds bounds) => new(id, Stamp(), new(1), [new(1, 0, bounds)], [new(2, 0, bounds)], []);
    private static RouteFrame InSpace(RouteFrame frame, ulong space, long ranges) => frame with
    { Latest = Stamp(space), Displayed = Stamp(space), ScenesRanges = ranges, DisplayedRanges = ranges };
    private static RouteFrame Frame(long draw, double centerX, double centerZ, double leadingLength = 2)
    {
        RouteVehicle[] vehicles = [new(1, new(centerX, 0, centerZ), leadingLength)];
        return new(draw, draw, (ulong)draw, Stamp(), Stamp(), 1, 1,
            vehicles, vehicles, true, JsonSerializer.SerializeToElement(new { }), 0, TrainLengthMeters: 1);
    }
}
