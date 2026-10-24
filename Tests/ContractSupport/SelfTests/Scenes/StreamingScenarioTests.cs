using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;

namespace Train.ContractSupport.Tests.Scenes;

[TestClass]
public sealed class StreamingScenarioTests
{
    [TestMethod]
    public void RetainedCoverageAndEventsRemainReadableAfterClosing()
    {
        var stamp = WorldRequestScenario.Hit.Frame;
        var scene = new SceneStreamingSceneId(stamp.Session, 1);
        var part = new SceneStreamingPartId(scene, SceneStreamingPartKind.NodeTerrain, "opaque");
        var attempt = new SceneStreamingAttemptId(part, 1);
        var coverage = new FixedStreamingCoverage
        {
            Points = ImmutableDictionary<(double, double), ImmutableArray<SceneStreamingPartId>>.Empty
                .Add((2, 3), [part]).Add((20, 30), [])
        };
        var observer = new RecordingStreamingObserver();
        observer.OnEvent(new SceneStreamingEvent.Opened(scene, new WorldLocation.Node(new(1)), SceneStreamingRole.Candidate));
        observer.OnEvent(new SceneStreamingEvent.Changed(attempt, SceneStreamingStage.Admitted, [new(7)]));
        observer.OnEvent(new SceneStreamingEvent.Frame(scene, stamp, [attempt], coverage));
        var retained = observer.Snapshot();
        observer.OnEvent(new SceneStreamingEvent.Activated(scene));
        observer.OnEvent(new SceneStreamingEvent.Closed(scene));
        var frame = new FixedSceneFrame { Update = new(null, stamp, Vector3.Zero, [], [], new([], []), []) };
        var streamed = StreamingScenario.AssertFrameMatches(retained, frame);
        Assert.AreEqual(3, retained.Length);
        Assert.AreEqual(5, observer.Snapshot().Length);
        Assert.AreEqual(part, streamed.Coverage!.FindTerrainParts(2, 3).Single());
        Assert.IsTrue(streamed.Coverage.FindTerrainParts(20, 30).IsEmpty);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => coverage.FindTerrainParts(0, 0));
        StringAssert.StartsWith(error.Message, "UNCONFIGURED:");
    }

    [TestMethod]
    public async Task ObserverCapturesConcurrentCallbacksWithoutLosingEvents()
    {
        var observer = new RecordingStreamingObserver();
        var scene = new SceneStreamingSceneId(WorldRequestScenario.Hit.Frame.Session, 1);
        await Task.WhenAll(Enumerable.Range(1, 16).Select(index => Task.Run(() =>
            observer.OnEvent(new SceneStreamingEvent.Changed(
                new(new(scene, SceneStreamingPartKind.RailwaySegment, index.ToString()), 1),
                SceneStreamingStage.Built, [])))));
        Assert.AreEqual(16, observer.Snapshot().Length);
        Assert.AreEqual(16, observer.Snapshot().Cast<SceneStreamingEvent.Changed>()
            .Select(value => value.Attempt).Distinct().Count());
    }

    [TestMethod]
    public void SharedAssertionRejectsMissingOrMismatchedFrameEvents()
    {
        var stamp = WorldRequestScenario.Hit.Frame;
        var frame = new FixedSceneFrame { Update = new(null, stamp, Vector3.Zero, [], [], new([], []), []) };
        Assert.ThrowsExactly<AssertFailedException>(() => StreamingScenario.AssertFrameMatches([], frame));
        var scene = new SceneStreamingSceneId(stamp.Session, 1);
        var other = new SceneStreamingSceneId(stamp.Session, 2);
        Assert.ThrowsExactly<AssertFailedException>(() => StreamingScenario.AssertFrameMatches(
            [new SceneStreamingEvent.Frame(scene, stamp,
                [new(new(other, SceneStreamingPartKind.NodeTerrain, "other"), 1)], null)], frame));
    }
}
