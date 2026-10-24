using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class StreamingRouteAnalysisTests
{
    [TestMethod]
    public void EdgeWindowsFollowTravelDirectionAndUseHalfOpenNodeRanges()
    {
        var forward = StreamingRouteAnalysis.Window(Edge(TravelDirection.Forward, 900), 1000);
        var reverse = StreamingRouteAnalysis.Window(Edge(TravelDirection.Reverse, 100), 1000);
        Assert.AreEqual(-100d, forward.NodeEntryCoreMeters);
        Assert.AreEqual(forward.NodeEntryCoreMeters, reverse.NodeEntryCoreMeters);
        CollectionAssert.AreEqual(forward.Labels, reverse.Labels);
        Assert.IsTrue(StreamingRouteAnalysis.Window(Edge(TravelDirection.Forward, 872), 1000).Labels.Contains("node-entry"));
        Assert.IsFalse(StreamingRouteAnalysis.Window(Edge(TravelDirection.Forward, 871.999), 1000).Labels.Contains("node-entry"));
        Assert.IsTrue(StreamingRouteAnalysis.Window(Edge(TravelDirection.Forward, 127.999), 1000).Labels.Contains("node-exit"));
        Assert.IsFalse(StreamingRouteAnalysis.Window(Edge(TravelDirection.Forward, 128), 1000).Labels.Contains("node-exit"));
        CollectionAssert.AreEqual(new[] { "line-middle" }, StreamingRouteAnalysis.Window(Edge(TravelDirection.Forward, 500), 1000).Labels);
    }

    [TestMethod]
    public void NodeEntryAndExitWindowsOverlapAndKeepSignedCoordinates()
    {
        var overlap = StreamingRouteAnalysis.Window(Node(50), 100);
        Assert.AreEqual(50d, overlap.NodeEntryCoreMeters);
        Assert.AreEqual(-50d, overlap.NodeExitCoreMeters);
        CollectionAssert.AreEqual(new[] { "node-entry", "node-exit" }, overlap.Labels);
        Assert.AreEqual("node-entry|node-exit", overlap.Key);
        Assert.IsTrue(StreamingRouteAnalysis.Window(Node(-128), 1000).Labels.Contains("node-entry"));
        Assert.IsFalse(StreamingRouteAnalysis.Window(Node(-128.001), 1000).Labels.Contains("node-entry"));
        Assert.IsFalse(StreamingRouteAnalysis.Window(Node(128), 1000).Labels.Contains("node-entry"));
        Assert.IsTrue(StreamingRouteAnalysis.Window(Node(872), 1000).Labels.Contains("node-exit"));
        Assert.IsFalse(StreamingRouteAnalysis.Window(Node(1128), 1000).Labels.Contains("node-exit"));
        Assert.AreEqual("outside-windows", StreamingRouteAnalysis.Window(Node(500), 1000).Key);
    }

    [TestMethod]
    public void SpeedUsesDisplayedThreeDimensionalDistanceCellScaleElapsedTimeAndCompletedSteps()
    {
        var before = Frame(1, 10, 100, new(1_000, 2, 3));
        var after = Frame(2, 12, 100 + 6 * SimulationTime.StepsPerSecond, new(1_006, 10, 3));
        var speed = StreamingRouteAnalysis.Speeds([before, after]).Single();
        Assert.AreEqual(3d, speed.ActualSimulationRate, .000001);
        Assert.AreEqual(2.5, speed.SceneMetersPerRealSecond!.Value, .000001);
        Assert.IsNull(speed.Unknown);
        Assert.AreEqual(before.Window.Key, speed.Window);
    }

    [TestMethod]
    public void AbsoluteDisplayedPositionsRemainStableAcrossSameSpaceOriginRebase()
    {
        // The producer has already added each D origin; a mesh-root rebase changes revision,
        // while the physical displayed point and its absolute position stay unchanged.
        var before = Frame(1, 0, 0, new(1_000_000_000_000, 0, 0));
        var after = Frame(2, .2, 1, before.DisplayedPosition) with { Coordinates = Stamp() with { Revision = 2 } };
        var speed = StreamingRouteAnalysis.Speeds([before, after]).Single();
        Assert.AreEqual(0d, speed.SceneMetersPerRealSecond!.Value);
        Assert.IsNull(speed.Unknown);
    }

    [TestMethod]
    public void SpaceTransitionAlignsOriginsAndRejectsUnknownOrAmbiguousBoundary()
    {
        var fromStamp = Stamp();
        var to = fromStamp with { Space = new(2), Revision = 2 };
        var before = Frame(1, 0, 0, new(110, 0, 0));
        var after = Frame(2, 2, 2, new(1014, 0, 0)) with
        {
            Coordinates = to, Displayed = to,
            Transition = new(fromStamp, new(100, 0, 0), to, new(1000, 0, 0), false)
        };
        var known = StreamingRouteAnalysis.Speeds([before, after]).Single();
        Assert.AreEqual(1d, known.SceneMetersPerRealSecond!.Value, .000001);
        foreach (var sample in new[] { after with { Transition = null }, after with { Transition = after.Transition! with { MultipleTracks = true } } })
        {
            var unknown = StreamingRouteAnalysis.Speeds([before, sample]).Single();
            Assert.IsNull(unknown.SceneMetersPerRealSecond);
            Assert.IsNotNull(unknown.Unknown);
        }
        Assert.AreEqual("transition_missing", StreamingRouteAnalysis.Speeds([before,
            after with { Transition = after.Transition! with { From = fromStamp with { Revision = 99, Space = new(9) } } }]).Single().Unknown);
    }

    [TestMethod]
    public void SummariesWeightActualElapsedTimeKeepUnknownCountsAndSeparateWindows()
    {
        // First window: 1 second at 2x and 3 seconds at 4x => 3.5x.
        // Its only known scene interval travels 4 cells in 1 second => 2 m/s.
        // The second window travels 8 cells in 2 seconds => 2 m/s at 1x.
        var frames = new[]
        {
            Frame(1, 0, 0, new(0, 0, 0)),
            Frame(2, 1, 120, new(4, 0, 0)),
            Frame(3, 4, 840, null) with { Window = new(null, null, ["node-entry"]) },
            Frame(4, 6, 960, new(20, 0, 0)) with { Window = new(null, null, ["node-entry"]) },
            Frame(5, 8, 1080, new(28, 0, 0)) with { Window = new(null, null, ["node-entry"]) }
        };
        var summaries = StreamingRouteAnalysis.SummarizeSpeeds(StreamingRouteAnalysis.Speeds(frames));
        Assert.AreEqual(2, summaries.Length);
        var middle = summaries.Single(summary => summary.Window == "line-middle");
        Assert.AreEqual(2, middle.Intervals);
        Assert.AreEqual(1, middle.UnknownSceneIntervals);
        Assert.AreEqual(4d, middle.Seconds, .000001);
        Assert.AreEqual(3.5, middle.ActualSimulationRate, .000001);
        Assert.AreEqual(2d, middle.SceneMetersPerRealSecond!.Value, .000001);
        var entry = summaries.Single(summary => summary.Window == "node-entry");
        Assert.AreEqual(2, entry.Intervals);
        Assert.AreEqual(1, entry.UnknownSceneIntervals);
        Assert.AreEqual(4d, entry.Seconds, .000001);
        Assert.AreEqual(1d, entry.ActualSimulationRate, .000001);
        Assert.AreEqual(2d, entry.SceneMetersPerRealSecond!.Value, .000001);
    }
    private static TrainPosition.Edge Edge(TravelDirection direction, double distance) => new(new(1), 0, direction, distance);
    private static TrainPosition.Node Node(double distance) => new(new(1), new(1), 0, new(2), 0, NodeTravelPhase.Approaching, distance, null);
    private static SceneFrameStamp Stamp() => new(new(Guid.Empty), new(1), new(1), 1);
    private static StreamingRouteFrame Frame(long draw, double at, ulong steps, ScenePoint? point) => new(draw, at, steps,
        Stamp(), Stamp(), point, null, new(null, null, ["line-middle"]), new(default, default, 0, 0, 0, 0, 0), null);
}
