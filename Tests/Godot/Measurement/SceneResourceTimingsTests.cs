using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using Train.Rendering;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class SceneResourceTimingsTests
{
    [TestMethod]
    public void ADrawSumsEveryPreparationCallAndKeepsCommitSeparate()
    {
        var timings = new SceneResourceTimings();
        Assert.IsNull(timings.Draw(1, 10, "node", "node", true));
        timings.Measure(SceneDisplayWork.Prepare, TimeSpan.FromMilliseconds(1));
        timings.Measure(SceneDisplayWork.PrepareCandidate, TimeSpan.FromMilliseconds(.5));
        timings.Measure(SceneDisplayWork.Prepare, TimeSpan.FromMilliseconds(2));
        timings.Measure(SceneDisplayWork.Commit, TimeSpan.FromMilliseconds(20));
        var frame = timings.Draw(2, 11, "node", "node", true)!;
        Assert.IsTrue(frame.AfterOperable);
        Assert.AreEqual(.0035, frame.PreparationTotalSeconds, 1e-12);
        Assert.AreEqual(.02, frame.CommitSeconds, 1e-12);
        Assert.AreEqual(0, timings.Draw(3, 12, "node", "node", true)!.PreparationTotalSeconds);
    }

    [TestMethod]
    public void OnlyTheFirstCommittedInteractiveDrawOfEachOpeningIsExcluded()
    {
        var timings = new SceneResourceTimings();
        timings.Draw(1, 10, "map", null, true);
        Assert.IsFalse(timings.Draw(2, 11, "node", "node", false)!.AfterOperable);
        Assert.IsFalse(timings.Draw(3, 12, "node", "node", true)!.AfterOperable);
        Assert.IsTrue(timings.Draw(4, 13, "node", "node", true)!.AfterOperable);
        Assert.IsTrue(timings.Draw(5, 14, "node", "node", false)!.AfterOperable);
        Assert.IsFalse(timings.Draw(6, 15, "edge", "edge", true)!.AfterOperable);
        Assert.IsTrue(timings.Draw(7, 16, "edge", "edge", true)!.AfterOperable);
        Assert.IsFalse(timings.Draw(8, 17, "map", null, false)!.AfterOperable);
    }

    [TestMethod]
    public void RoutePhaseAndSpaceTransitionsDoNotRestartTheOpeningExclusion()
    {
        var timings = new SceneResourceTimings();
        timings.Draw(1, 10, "route-opening", "route", false);
        Assert.IsFalse(timings.Draw(2, 11, "route-opening", "route", true)!.AfterOperable);
        Assert.IsTrue(timings.Draw(3, 12, "route-follow", "route", false)!.AfterOperable);
        Assert.IsTrue(timings.Draw(4, 13, "route-follow", "route", true)!.AfterOperable);
    }
}
