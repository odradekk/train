using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using Train.Core.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class StreamingRecorderTests
{
    private static SceneStreamingSceneId Scene => new(new(Guid.Empty), 1);
    private static SceneStreamingAttemptId Attempt(ulong value = 1) => new(new(Scene, SceneStreamingPartKind.RailwaySegment, "opaque"), value);
    private static SceneFrameStamp Stamp(ulong revision = 1) => new(Scene.Session, new(1), new(1), revision);
    private static void Change(StreamingRecorder recorder, SceneStreamingAttemptId attempt, SceneStreamingStage stage,
        ImmutableArray<SceneObjectId> objects = default) => recorder.OnEvent(new SceneStreamingEvent.Changed(attempt, stage, objects.IsDefault ? [] : objects));

    private static void Replay(StreamingRecorder recorder, StreamingHostEvent value) => recorder.Displayed(null, [value]);

    [TestMethod]
    public void ConcurrentProducerRecordsAreTimestampedAtCallbackAndDrainedWithoutLoss()
    {
        double now = 1;
        var recorder = new StreamingRecorder(() => now);
        Parallel.For(0, 100, i => Change(recorder,
            new(new(Scene, SceneStreamingPartKind.RailwaySegment, "opaque-" + i), 1), SceneStreamingStage.Needed));
        now = 2;
        recorder.OnEvent(new SceneStreamingEvent.Frame(Scene, Stamp(), [], null));
        recorder.Displayed(Stamp());
        var events = recorder.Events(0).Where(value => value.Kind == "Changed").ToArray();
        Assert.AreEqual(100, events.Length);
        Assert.IsTrue(events.All(value => value.At == 1));
        Assert.AreEqual(100, recorder.MissingCounts()["needed_not_queued"]);
    }

    [TestMethod]
    public void CancelledAttemptCannotPolluteRebuiltAttemptAndOldDisplayedFrameDoesNotCommitReplacement()
    {
        var recorder = new StreamingRecorder(() => 1);
        var old = Attempt(); var replacement = Attempt(2);
        Change(recorder, old, SceneStreamingStage.Needed);
        Change(recorder, old, SceneStreamingStage.Admitted, [new(7)]);
        recorder.OnEvent(new SceneStreamingEvent.Frame(Scene, Stamp(), [old], null));
        recorder.Displayed(Stamp());
        Change(recorder, old, SceneStreamingStage.Cancelled);
        Change(recorder, replacement, SceneStreamingStage.Needed);
        Change(recorder, replacement, SceneStreamingStage.Started);
        Change(recorder, old, SceneStreamingStage.Built);
        recorder.Displayed(Stamp());
        Assert.AreEqual("committed", recorder.Category(replacement.Part));
        Assert.AreEqual(0, recorder.MissingCounts().Count);
        Assert.AreEqual(1, recorder.RebuildingCounts()["started"]);
        Assert.IsNull(recorder.Timings(0).Single(value => value.Attempt == replacement).Committed);
    }

    [TestMethod]
    public void AdmittedObjectsJoinHostEventsOnlyThroughExactFrameMembership()
    {
        var recorder = new StreamingRecorder(() => 1);
        var attempt = Attempt();
        Change(recorder, attempt, SceneStreamingStage.Needed);
        Change(recorder, attempt, SceneStreamingStage.Admitted, [new(7), new(8), new(9)]);
        recorder.OnEvent(new SceneStreamingEvent.Frame(Scene, Stamp(), [attempt], null));
        Replay(recorder, new(1, 2, new(1, Stamp(2), new(7), SceneDisplayStage.ResourcesPrepared)));
        Assert.IsNull(recorder.Timings(0).Single().ResourcesPrepared);
        Replay(recorder, new(1, 3, new(1, Stamp(), new(7), SceneDisplayStage.ResourcesPrepared)));
        Replay(recorder, new(1, 3.5, new(1, Stamp(), new(8), SceneDisplayStage.ResourcesPrepared)));
        Replay(recorder, new(1, 3.7, new(1, Stamp(2), new(9), SceneDisplayStage.ResourcesPrepared)));
        Replay(recorder, new(1, 3.8, new(1, Stamp(), null, SceneDisplayStage.FramePrepared)));
        Assert.AreEqual("prepared_waiting_commit", recorder.Category(attempt.Part));
        Assert.IsNull(recorder.Timings(0).Single().Committed);
        Replay(recorder, new(1, 4.1, new(1, Stamp(), null, SceneDisplayStage.FrameCommitted)));
        recorder.Displayed(Stamp());
        var timing = recorder.Timings(0).Single();
        Assert.AreEqual(3.5, timing.ResourcesPrepared);
        Assert.AreEqual(4.1, timing.Committed);
        Assert.AreEqual(3.1, timing.NeededToCommittedSeconds!.Value, .000001);
    }

    [TestMethod]
    public void ReusedCandidateHasExactCommitWithoutInventingResourcePreparation()
    {
        var recorder = new StreamingRecorder(() => 1);
        var attempt = Attempt();
        Change(recorder, attempt, SceneStreamingStage.Needed);
        Change(recorder, attempt, SceneStreamingStage.Admitted, [new(7)]);
        recorder.OnEvent(new SceneStreamingEvent.Frame(Scene, Stamp(), [attempt], null));
        Replay(recorder, new(1, 2, new(1, Stamp(), null, SceneDisplayStage.FramePrepared)));
        Assert.AreEqual("prepared_waiting_commit", recorder.Category(attempt.Part));
        Replay(recorder, new(1, 2.5, new(1, Stamp(), null, SceneDisplayStage.FrameCommitted)));
        recorder.Displayed(Stamp());
        var timing = recorder.Timings(0).Single();
        Assert.AreEqual(2.5, timing.Committed);
        Assert.IsNull(timing.ResourcesPrepared);
    }

    [TestMethod]
    public void EachPartBecomesReadyBeforeTheWholeCandidateAndCancellationDoesNotCarryReadiness()
    {
        var recorder = new StreamingRecorder(() => 1);
        var first = Attempt();
        var second = new SceneStreamingAttemptId(first.Part with { Key = "second" }, 1);
        var empty = new SceneStreamingAttemptId(first.Part with { Key = "empty" }, 1);
        foreach (var part in new[] { first, second, empty }) Change(recorder, part, SceneStreamingStage.Needed);
        Change(recorder, first, SceneStreamingStage.Admitted, [new(7), new(8)]);
        Change(recorder, second, SceneStreamingStage.Admitted, [new(9)]);
        Change(recorder, empty, SceneStreamingStage.Admitted);
        recorder.OnEvent(new SceneStreamingEvent.Frame(Scene, Stamp(), [first, second, empty], null));
        ImmutableArray<SceneObjectId> required = [new(8), new(9)];
        Replay(recorder, new(1, 2, new(1, Stamp(), null, SceneDisplayStage.FramePreparationStarted, required)));
        Assert.AreEqual("prepared_waiting_commit", recorder.Category(empty.Part));
        Assert.IsNull(recorder.Timings(0).Single(value => value.Attempt == empty).ResourcesPrepared);
        Replay(recorder, new(1, 3, new(1, Stamp(), new(8), SceneDisplayStage.ResourcesPrepared)));
        Assert.AreEqual("prepared_waiting_commit", recorder.Category(first.Part));
        Assert.AreEqual("admitted_waiting_resources", recorder.Category(second.Part));
        Assert.AreEqual(3d, recorder.Timings(0).Single(value => value.Attempt == first).ResourcesPrepared);
        Replay(recorder, new(1, 4, new(1, Stamp(), null, SceneDisplayStage.HostCancelled)));
        Assert.AreEqual("admitted_waiting_resources", recorder.Category(first.Part));
        Assert.IsNull(recorder.Timings(0).Single(value => value.Attempt == first).ResourcesPrepared);
        Replay(recorder, new(1, 5, new(2, Stamp(), null, SceneDisplayStage.FramePreparationStarted, required)));
        Assert.AreEqual("admitted_waiting_resources", recorder.Category(first.Part));
        Replay(recorder, new(1, 6, new(2, Stamp(), new(8), SceneDisplayStage.ResourcesPrepared)));
        Replay(recorder, new(1, 7, new(2, Stamp(), new(9), SceneDisplayStage.ResourcesPrepared)));
        Replay(recorder, new(1, 8, new(2, Stamp(), null, SceneDisplayStage.FrameCommitted)));
        var timing = recorder.Timings(0).Single(value => value.Attempt == first);
        Assert.AreEqual(6d, timing.ResourcesPrepared);
        Assert.AreEqual(8d, timing.Committed);
    }

    [TestMethod]
    public void TraceCutoffExcludesLateBuildsAndDoesNotBorrowPreparationFromALaterCommit()
    {
        double now = 1;
        var recorder = new StreamingRecorder(() => now);
        var building = Attempt();
        var preparing = new SceneStreamingAttemptId(building.Part with { Key = "preparing" }, 1);
        Change(recorder, building, SceneStreamingStage.Needed);
        Change(recorder, building, SceneStreamingStage.Queued);
        Change(recorder, preparing, SceneStreamingStage.Needed);
        Change(recorder, preparing, SceneStreamingStage.Admitted, [new(7)]);
        recorder.OnEvent(new SceneStreamingEvent.Frame(Scene, Stamp(), [preparing], null));
        ImmutableArray<SceneObjectId> required = [new(7)];
        Replay(recorder, new(1, 2, new(1, Stamp(), null, SceneDisplayStage.FramePreparationStarted, required)));
        Replay(recorder, new(1, 3, new(1, Stamp(), new(7), SceneDisplayStage.ResourcesPrepared)));
        Assert.AreEqual(3d, recorder.Timings(0, 4).Single(value => value.Attempt == preparing).ResourcesPrepared);
        Replay(recorder, new(1, 5, new(1, Stamp(), null, SceneDisplayStage.FrameCommitted)));
        now = 6;
        Change(recorder, building, SceneStreamingStage.Built);
        Change(recorder, building, SceneStreamingStage.Cancelled);
        Change(recorder, Attempt(2), SceneStreamingStage.Needed);
        recorder.Drain();
        var timings = recorder.Timings(0, 4);
        Assert.AreEqual(2, timings.Length);
        Assert.IsTrue(timings.All(value => value.Outcome == "unfinished"));
        Assert.IsTrue(timings.All(value => value.Committed is null && value.ResourcesPrepared is null));
        Assert.IsNull(timings.Single(value => value.Attempt == building).Built);
        Assert.IsTrue(recorder.Events(0, 4).All(value => value.At <= 4));
        Assert.IsFalse(recorder.Events(0, 4).Any(value => value.Stage == SceneStreamingStage.Built));
    }

    [TestMethod]
    public void MissingCountsExcludeEndedDemandAndClosedScenes()
    {
        var recorder = new StreamingRecorder(() => 1);
        Change(recorder, Attempt(), SceneStreamingStage.Needed);
        recorder.OnEvent(new SceneStreamingEvent.Frame(Scene, Stamp(), [], null));
        recorder.Displayed(Stamp());
        Assert.AreEqual(1, recorder.MissingCounts()["needed_not_queued"]);
        Change(recorder, Attempt(), SceneStreamingStage.DemandEnded);
        recorder.Displayed(Stamp());
        Assert.AreEqual(0, recorder.MissingCounts().Count);
        Change(recorder, Attempt(), SceneStreamingStage.Needed);
        recorder.OnEvent(new SceneStreamingEvent.Closed(Scene));
        recorder.Drain();
        Assert.AreEqual(0, recorder.MissingCounts().Count);
        Assert.AreEqual("not_needed", recorder.Category(Attempt().Part));
    }

    [TestMethod]
    public void DisplayedSceneMissingCountsExcludePreviewRolesAndActivationEndsPreview()
    {
        var recorder = new StreamingRecorder(() => 1);
        var candidate = new SceneStreamingSceneId(Scene.Session, 2);
        var successor = new SceneStreamingSceneId(Scene.Session, 3);
        recorder.OnEvent(new SceneStreamingEvent.Opened(Scene, new WorldLocation.Node(new(1)), SceneStreamingRole.Active));
        recorder.OnEvent(new SceneStreamingEvent.Opened(candidate, new WorldLocation.Node(new(2)), SceneStreamingRole.Candidate));
        recorder.OnEvent(new SceneStreamingEvent.Opened(successor, new WorldLocation.Node(new(3)), SceneStreamingRole.Successor));
        foreach (var scene in new[] { Scene, candidate, successor })
            Change(recorder, Attempt() with { Part = Attempt().Part with { Scene = scene } }, SceneStreamingStage.Needed);
        recorder.OnEvent(new SceneStreamingEvent.Frame(Scene, Stamp(), [], null));
        recorder.Displayed(Stamp());
        Assert.AreEqual(1, recorder.MissingCounts()["needed_not_queued"]);
        Assert.AreEqual(1, recorder.PreviewCounts()[SceneStreamingRole.Candidate]["needed_not_queued"]);
        Assert.AreEqual(1, recorder.PreviewCounts()[SceneStreamingRole.Successor]["needed_not_queued"]);
        recorder.OnEvent(new SceneStreamingEvent.Activated(candidate));
        recorder.OnEvent(new SceneStreamingEvent.Closed(successor));
        recorder.Displayed(Stamp());
        Assert.AreEqual(0, recorder.PreviewCounts().Count);
        Assert.AreEqual(1, recorder.MissingCounts()["needed_not_queued"]);
    }

    [TestMethod]
    public void LateBuildOfCurrentCancelledAttemptKeepsCancelledCategory()
    {
        var recorder = new StreamingRecorder(() => 1);
        Change(recorder, Attempt(), SceneStreamingStage.Needed);
        Change(recorder, Attempt(), SceneStreamingStage.Cancelled);
        Change(recorder, Attempt(), SceneStreamingStage.Built);
        recorder.OnEvent(new SceneStreamingEvent.Frame(Scene, Stamp(), [], null));
        recorder.Displayed(Stamp());
        Assert.AreEqual("cancelled", recorder.Category(Attempt().Part));
        Assert.AreEqual(1, recorder.MissingCounts()["cancelled"]);
        Assert.AreEqual("cancelled", recorder.Timings(0).Single().Outcome);
    }

    [TestMethod]
    public void DrawReplaysCancelledAndReplacementCandidatesInOrderWithAbsoluteTimes()
    {
        var recorder = new StreamingRecorder(() => 10);
        Change(recorder, Attempt(), SceneStreamingStage.Needed);
        Change(recorder, Attempt(), SceneStreamingStage.Admitted, [new(7)]);
        recorder.OnEvent(new SceneStreamingEvent.Frame(Scene, Stamp(), [Attempt()], null));
        ImmutableArray<SceneObjectId> required = [new(7)];
        recorder.Displayed(Stamp(), [
            new(1, 11, new(1, Stamp(), null, SceneDisplayStage.FramePreparationStarted, required)),
            new(1, 12, new(1, Stamp(), new(7), SceneDisplayStage.ResourcesPrepared)),
            new(1, 13, new(1, Stamp(), null, SceneDisplayStage.HostCancelled)),
            new(1, 14, new(2, Stamp(), null, SceneDisplayStage.FramePreparationStarted, required)),
            new(1, 15, new(2, Stamp(), new(7), SceneDisplayStage.ResourcesPrepared)),
            new(1, 16, new(2, Stamp(), null, SceneDisplayStage.FrameCommitted))]);
        var timing = recorder.Timings(10).Single();
        Assert.AreEqual(5d, timing.ResourcesPrepared);
        Assert.AreEqual(6d, timing.Committed);
        Assert.AreEqual(0, recorder.MissingCounts().Count);
    }

    [TestMethod]
    public void AlreadyVisibleBeforeDemandKeepsTimestampsAndExcludesDemandLatency()
    {
        double now = 1;
        var recorder = new StreamingRecorder(() => now);
        Change(recorder, Attempt(), SceneStreamingStage.Admitted, [new(7)]);
        recorder.OnEvent(new SceneStreamingEvent.Frame(Scene, Stamp(), [Attempt()], null));
        Replay(recorder, new(1, 2, new(1, Stamp(), null, SceneDisplayStage.FrameCommitted)));
        now = 3;
        Change(recorder, Attempt(), SceneStreamingStage.Needed);
        recorder.Drain();
        var timing = recorder.Timings(0).Single();
        Assert.AreEqual(3d, timing.Needed);
        Assert.AreEqual(2d, timing.Committed);
        Assert.IsTrue(timing.AlreadyVisibleBeforeDemand);
        Assert.IsNull(timing.NeededToCommittedSeconds);
    }

    [TestMethod]
    public void RawEventsNeverSerializeCoverageAndDisplayedSnapshotSurvivesSceneClosure()
    {
        var recorder = new StreamingRecorder(() => 1);
        var coverage = new Coverage();
        recorder.OnEvent(new SceneStreamingEvent.Frame(Scene, Stamp(), [], coverage));
        recorder.Displayed(Stamp());
        recorder.OnEvent(new SceneStreamingEvent.Closed(Scene));
        recorder.Displayed(Stamp());
        Assert.AreSame(coverage, recorder.Coverage(Stamp()));
        var json = JsonSerializer.Serialize(recorder.Events(0));
        Assert.IsFalse(json.Contains("Coverage", StringComparison.Ordinal));
        recorder.Displayed(null);
        Assert.IsNull(recorder.Coverage(Stamp()));
    }

    private sealed class Coverage : ISceneStreamingCoverage
    {
        public ImmutableArray<SceneStreamingPartId> FindTerrainParts(double x, double z) => throw new InvalidOperationException();
    }
}
