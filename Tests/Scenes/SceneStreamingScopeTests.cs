using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class SceneStreamingScopeTests
{
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

}
