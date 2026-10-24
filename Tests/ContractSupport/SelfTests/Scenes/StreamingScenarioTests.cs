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
