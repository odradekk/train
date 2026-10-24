using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class SceneStreamingScopeTests
{
    [TestMethod]
    public void CancelledAttemptRebuildsWithNewIdentityAndLateBuildDoesNotReplaceFrameMembership()
    {
        var observer = new RecordingStreamingObserver();
        var stamp = WorldRequestScenario.Hit.Frame;
        var scene = new SceneStreamingSceneId(stamp.Session, 1);
        var scope = new SceneStreamingScope(observer, scene, RailwayFixtures.Location, SceneStreamingRole.Active);
        var first = scope.Need(SceneStreamingPartKind.RailwaySegment, "segment");
        scope.Queue(first);
        scope.Cancel(first);
        var second = scope.Need(SceneStreamingPartKind.RailwaySegment, "segment");
        Assert.AreEqual(first.Part, second.Part);
        Assert.AreEqual(first.Value + 1, second.Value);
        scope.Queue(second);
        scope.Admit(second, [new(9)]);
        Assert.AreEqual(17, scope.Build(first, () => 17));
        scope.Frame(stamp, [first.Part], null);

        var streamed = StreamingScenario.AssertFrameMatches(observer.Snapshot(), Frame(stamp));
        CollectionAssert.AreEqual(new[] { second }, streamed.Parts.ToArray());
        var changes = observer.Snapshot().OfType<SceneStreamingEvent.Changed>().ToArray();
        Assert.IsTrue(changes.Any(value => value.Attempt == first && value.Stage == SceneStreamingStage.Built));
        Assert.IsTrue(changes.Any(value => value.Attempt == first && value.Stage == SceneStreamingStage.Cancelled));
        Assert.IsTrue(changes.Any(value => value.Attempt == second && value.Stage == SceneStreamingStage.Needed));
        CollectionAssert.AreEqual(new[] { new SceneObjectId(9) }, changes.Single(value =>
            value.Attempt == second && value.Stage == SceneStreamingStage.Admitted).Objects.ToArray());
    }

    [TestMethod]
    public void RetainedFrameContainsAllLiveAttemptsAndKeepsCoverageAfterLaterFramesAndClose()
    {
        var observer = new RecordingStreamingObserver();
        var stamp = WorldRequestScenario.Hit.Frame;
        var scope = new SceneStreamingScope(observer, new(stamp.Session, 1),
            new WorldLocation.Node(new(1)), SceneStreamingRole.Active);
        var terrain = scope.Need(SceneStreamingPartKind.NodeTerrain, "terrain");
        var rail = scope.Need(SceneStreamingPartKind.NodeRail, "rail");
        scope.Admit(terrain, [new(1)]);
        scope.Admit(rail, [new(2)]);
        var coverage = new FixedStreamingCoverage
        {
            Points = ImmutableDictionary<(double, double), ImmutableArray<SceneStreamingPartId>>.Empty
                .Add((3, 4), [terrain.Part]).Add((30, 40), [])
        };
        var live = new List<SceneStreamingPartId> { terrain.Part, rail.Part };
        scope.Frame(stamp, live, coverage);
        var retained = StreamingScenario.AssertFrameMatches(observer.Snapshot(), Frame(stamp));
        live.Remove(terrain.Part);
        var nextStamp = stamp with { Revision = stamp.Revision + 1 };
        scope.Frame(nextStamp, live, coverage);
        var next = StreamingScenario.AssertFrameMatches(observer.Snapshot(), Frame(nextStamp));
        scope.Close();

        CollectionAssert.AreEquivalent(new[] { terrain, rail }, retained.Parts.ToArray());
        CollectionAssert.AreEqual(new[] { rail }, next.Parts.ToArray());
        CollectionAssert.AreEqual(new[] { terrain }, retained.Parts.Except(next.Parts).ToArray());
        Assert.AreEqual(terrain.Part, retained.Coverage!.FindTerrainParts(3, 4).Single());
        Assert.IsTrue(retained.Coverage.FindTerrainParts(30, 40).IsEmpty);
    }

    [TestMethod]
    public void FailedAttemptKeepsItsOutcomeWhenTheSceneCloses()
    {
        var observer = new RecordingStreamingObserver();
        var scope = new SceneStreamingScope(observer, new(WorldRequestScenario.Hit.Frame.Session, 1),
            RailwayFixtures.Location, SceneStreamingRole.Active);
        var attempt = scope.Need(SceneStreamingPartKind.RailwaySegment, "segment");
        scope.Queue(attempt);
        Assert.Throws<InvalidOperationException>(() => scope.Build<int>(attempt, () => throw new InvalidOperationException()));
        scope.CancelPending();
        scope.Close();
        var changes = observer.Snapshot().OfType<SceneStreamingEvent.Changed>().ToArray();
        Assert.AreEqual(1, changes.Count(value => value.Attempt == attempt && value.Stage == SceneStreamingStage.Failed));
        Assert.IsFalse(changes.Any(value => value.Stage == SceneStreamingStage.Cancelled));
    }

    [TestMethod]
    public void PromotionAndCloseKeepTheOpenedSceneIdentity()
    {
        var observer = new RecordingStreamingObserver();
        var scene = new SceneStreamingSceneId(WorldRequestScenario.Hit.Frame.Session, 2);
        var location = new WorldLocation.Node(new(1));
        var scope = new SceneStreamingScope(observer, scene, location, SceneStreamingRole.Candidate);
        scope.Activate();
        scope.Close();
        scope.Close();
        var events = observer.Snapshot();
        Assert.AreEqual(3, events.Length);
        Assert.AreEqual(new SceneStreamingEvent.Opened(scene, location, SceneStreamingRole.Candidate), events[0]);
        Assert.AreEqual(new SceneStreamingEvent.Activated(scene), events[1]);
        Assert.AreEqual(new SceneStreamingEvent.Closed(scene), events[2]);
    }

    private static FixedSceneFrame Frame(SceneFrameStamp stamp) => new()
    {
        Update = new(null, stamp, Vector3.Zero, [], [], new([], []), [])
    };
}
