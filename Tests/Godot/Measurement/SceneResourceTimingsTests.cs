using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;
using Train.App.Measurement;
using Train.Rendering;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class SceneResourceTimingsTests
{
    [TestMethod]
    public void SidecarRecordsEveryCallAndResetsItsCountsAtEachDraw()
    {
        var timings = new SceneResourceTimings();
        timings.Draw(1, 10, "node", "node", true, new(1));
        for (var call = 0; call < 3; call++) timings.Measure(SceneDisplayWork.Prepare, TimeSpan.Zero, TimeSpan.Zero);
        for (var call = 0; call < 2; call++) timings.Measure(SceneDisplayWork.PrepareCandidate, TimeSpan.Zero, TimeSpan.Zero);
        timings.Measure(SceneDisplayWork.Commit, TimeSpan.Zero, TimeSpan.Zero);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var frame = JsonSerializer.SerializeToElement(timings.Draw(2, 11, "node", "node", true, new(1)), options);
        Assert.AreEqual(3, frame.GetProperty("preparation_calls").GetInt32());
        Assert.AreEqual(2, frame.GetProperty("candidate_preparation_calls").GetInt32());
        var next = JsonSerializer.SerializeToElement(timings.Draw(3, 12, "node", "node", true, new(1)), options);
        Assert.AreEqual(0, next.GetProperty("preparation_calls").GetInt32());
        Assert.AreEqual(0, next.GetProperty("candidate_preparation_calls").GetInt32());
    }

    [TestMethod]
    public void ADrawSumsPreparationPausesAndResetsThemWithoutIncludingCommit()
    {
        var timings = new SceneResourceTimings();
        timings.Draw(1, 10, "node", "node", true, new(1));
        timings.Measure(SceneDisplayWork.Prepare, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(2));
        timings.Measure(SceneDisplayWork.PrepareCandidate, TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(1));
        timings.Measure(SceneDisplayWork.Prepare, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(.5));
        timings.Measure(SceneDisplayWork.Commit, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(10));
        var frame = timings.Draw(2, 11, "node", "node", true, new(1))!;
        Assert.AreEqual(.010, frame.PreparationTotalSeconds, 1e-12);
        Assert.AreEqual(.0035, frame.PreparationGcPauseSeconds, 1e-12);
        Assert.AreEqual(0, timings.Draw(3, 12, "node", "node", true, new(1))!.PreparationGcPauseSeconds);
    }

    [TestMethod]
    public void ADrawSumsEveryPreparationCallAndKeepsCommitSeparate()
    {
        var timings = new SceneResourceTimings();
        Assert.IsNull(timings.Draw(1, 10, "node", "node", true, new(1)));
        timings.Measure(SceneDisplayWork.Prepare, TimeSpan.FromMilliseconds(1), TimeSpan.Zero);
        timings.Measure(SceneDisplayWork.PrepareCandidate, TimeSpan.FromMilliseconds(.5), TimeSpan.Zero);
        timings.Measure(SceneDisplayWork.Prepare, TimeSpan.FromMilliseconds(2), TimeSpan.Zero);
        timings.Measure(SceneDisplayWork.Commit, TimeSpan.FromMilliseconds(20), TimeSpan.Zero);
        var frame = timings.Draw(2, 11, "node", "node", true, new(1))!;
        Assert.IsTrue(frame.AfterOperable);
        Assert.AreEqual(.0035, frame.PreparationTotalSeconds, 1e-12);
        Assert.AreEqual(.02, frame.CommitSeconds, 1e-12);
        Assert.AreEqual(0, timings.Draw(3, 12, "node", "node", true, new(1))!.PreparationTotalSeconds);
    }

    [TestMethod]
    public void OnlyTheFirstCommittedInteractiveDrawOfEachOpeningIsExcluded()
    {
        var timings = new SceneResourceTimings();
        timings.Draw(1, 10, "map", null, true, null);
        Assert.IsFalse(timings.Draw(2, 11, "node", "node", false, null)!.AfterOperable);
        Assert.IsFalse(timings.Draw(3, 12, "node", "node", true, new(1))!.AfterOperable);
        Assert.IsTrue(timings.Draw(4, 13, "node", "node", true, new(1))!.AfterOperable);
        Assert.IsTrue(timings.Draw(5, 14, "node", "node", false, new(1))!.AfterOperable);
        Assert.IsFalse(timings.Draw(6, 15, "edge", "edge", true, new(2))!.AfterOperable);
        Assert.IsTrue(timings.Draw(7, 16, "edge", "edge", true, new(2))!.AfterOperable);
        Assert.IsFalse(timings.Draw(8, 17, "map", null, false, null)!.AfterOperable);
    }

    [TestMethod]
    public void RoutePhaseAndLoadingDoNotRestartTheOpeningExclusion()
    {
        var timings = new SceneResourceTimings();
        timings.Draw(1, 10, "route-opening", "route", false, null);
        Assert.IsFalse(timings.Draw(2, 11, "route-opening", "route", true, new(1))!.AfterOperable);
        Assert.IsTrue(timings.Draw(3, 12, "route-follow", "route", false, new(1))!.AfterOperable);
        Assert.IsTrue(timings.Draw(4, 13, "route-follow", "route", true, new(1))!.AfterOperable);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ASpaceChangeExcludesExactlyItsDrawAfterTheOpening(bool interactive)
    {
        var timings = new SceneResourceTimings();
        timings.Draw(1, 10, "route-opening", "route", true, new(1));
        var before = timings.Draw(2, 11, "route-follow", "route", true, new(1))!;
        timings.Measure(SceneDisplayWork.Prepare, TimeSpan.FromMilliseconds(200), TimeSpan.Zero);
        var changed = timings.Draw(3, 12, "route-follow", "route", interactive, new(2))!;
        var after = timings.Draw(4, 13, "route-follow", "route", interactive, new(2))!;
        Assert.IsTrue(before.AfterOperable);
        Assert.IsFalse(before.ExcludedSpaceChange);
        Assert.IsFalse(changed.AfterOperable);
        Assert.IsTrue(changed.ExcludedSpaceChange);
        Assert.AreEqual(.2, changed.PreparationTotalSeconds);
        Assert.IsTrue(after.AfterOperable);
        Assert.IsFalse(after.ExcludedSpaceChange);
    }

    [TestMethod]
    public void MissingDisplayedFramesDoNotForgetTheLastCommittedSpace()
    {
        var timings = new SceneResourceTimings();
        timings.Draw(1, 10, "route", "route", true, new(1));
        Assert.IsTrue(timings.Draw(2, 11, "route", "route", false, null)!.AfterOperable);
        Assert.IsTrue(timings.Draw(3, 12, "route", "route", true, new(2))!.ExcludedSpaceChange);
        Assert.IsTrue(timings.Draw(4, 13, "route", "route", false, null)!.AfterOperable);
        var same = timings.Draw(5, 14, "route", "route", true, new(2))!;
        Assert.IsTrue(same.AfterOperable);
        Assert.IsFalse(same.ExcludedSpaceChange);
    }

    [TestMethod]
    public void TheOpeningFirstCommitIsNotCountedAsALaterSpaceChange()
    {
        var timings = new SceneResourceTimings();
        timings.Draw(1, 10, "node", "node", false, new(1));
        var first = timings.Draw(2, 11, "node", "node", true, new(2))!;
        Assert.IsFalse(first.AfterOperable);
        Assert.IsFalse(first.ExcludedSpaceChange);
        Assert.IsTrue(timings.Draw(3, 12, "node", "node", true, new(2))!.AfterOperable);
    }
}
