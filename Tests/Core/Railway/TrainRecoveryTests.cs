using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.Railway;

[TestClass]
public sealed class TrainRecoveryTests
{
    [TestMethod]
    [DataRow("approaching", NodeTravelPhase.Approaching)]
    [DataRow("waiting", NodeTravelPhase.Waiting)]
    [DataRow("docked", NodeTravelPhase.Docked)]
    [DataRow("departing", NodeTravelPhase.Departing)]
    public void Node_phase_capture_restores_and_strictly_replays_the_same_continuation(string boundary, NodeTravelPhase phase)
    {
        using var continuous = TrainRecoveryFixture.New(boundary: boundary);
        TrainRecoveryFixture.Configure(continuous);
        TrainRecoveryFixture.Boundary(continuous, boundary);
        Assert.AreEqual(phase, ((TrainPosition.Node)TrainRecoveryFixture.Train(continuous).Position).Phase);
        var capture = Success(continuous.Capture(CapturePurpose.Save));
        var decoded = Success(CoreFactory.Create().DecodeState(capture.Encode()));
        Assert.AreEqual(capture.Encode(), decoded.Encode());
        using var candidate = Success(CoreFactory.Create().PrepareRestore(decoded, TrainRecoveryFixture.ContentFor(boundary), Options));
        using var resumed = candidate.Activate();
        Assert.AreEqual(TrainRecoveryFixture.Query(continuous), TrainRecoveryFixture.Query(resumed));
        var events = new List<string>();
        var resumedEvents = new List<string>();
        if (boundary == "waiting")
        {
            TrainRecoveryFixture.Submit(continuous, new CoreCommand.PublishContent(TrainRecoveryFixture.Content), events);
            TrainRecoveryFixture.Submit(resumed, new CoreCommand.PublishContent(TrainRecoveryFixture.Content), resumedEvents);
        }
        for (var step = 0; step < 24; step++)
        {
            TrainRecoveryFixture.Advance(continuous, events);
            TrainRecoveryFixture.Advance(resumed, resumedEvents);
            Assert.AreEqual(TrainRecoveryFixture.Query(continuous), TrainRecoveryFixture.Query(resumed));
            Assert.AreEqual(Success(continuous.Capture(CapturePurpose.Save)).Checkpoint,
                Success(resumed.Capture(CapturePurpose.Save)).Checkpoint);
        }
        CollectionAssert.AreEqual(events, resumedEvents);
        var end = Success(continuous.Capture(CapturePurpose.Save));
        var recording = continuous.ReadRecording();
        var input = new CoreReplayInput(2, TrainRecoveryFixture.Runtime, recording.Starts[0].State.Encode(), recording.Commands,
            [capture.Checkpoint, end.Checkpoint], recording.EndPosition, null);
        ImmutableArray<CoreContentProjection> history = boundary == "waiting"
            ? [TrainRecoveryFixture.ContentFor(boundary), TrainRecoveryFixture.Content] : [TrainRecoveryFixture.Content];
        using var replay = Success(CoreFactory.Create().PrepareReplay(input, TrainRecoveryFixture.Runtime, history));
        var prefix = replay.Advance(2000, capture.Position);
        Assert.IsNull(prefix.Difference);
        Assert.IsNull(prefix.Fault);
        Assert.AreEqual(capture.Checkpoint, replay.CaptureBoundary().Checkpoint);
        var suffix = replay.Advance(24, end.Position);
        Assert.IsNull(suffix.Difference);
        Assert.IsNull(suffix.Fault);
        Assert.AreEqual(end.Checkpoint, replay.CaptureBoundary().Checkpoint);
        CollectionAssert.AreEqual(events, suffix.Events.Select(TrainRecoveryFixture.Event).ToList());
    }

    [TestMethod]
    public void Five_train_commands_replay_real_running_and_servicing_checkpoints()
    {
        var (input, _, events) = Record();
        using var replay = Success(CoreFactory.Create().PrepareReplay(input, TrainRecoveryFixture.Runtime, [TrainRecoveryFixture.Content]));
        var replayEvents = new List<string>();
        foreach (var checkpoint in input.Checkpoints)
        {
            var result = replay.Advance(1000, checkpoint.Position);
            Assert.IsNull(result.Difference);
            Assert.IsNull(result.Fault);
            Assert.AreEqual(checkpoint.Position, result.Position);
            replayEvents.AddRange(result.Events.Select(TrainRecoveryFixture.Event));
            Assert.IsInstanceOfType<CheckpointComparison.Equal>(Success(CoreFactory.Create().CompareCheckpoints(checkpoint, replay.CaptureBoundary().Checkpoint)));
        }
        CollectionAssert.AreEqual(events, replayEvents);
        Assert.AreEqual(ReplayStop.EndOfRecording, replay.Advance(0).Stop);
    }

    [TestMethod]
    public void Recording_from_a_saved_edge_prefix_replays_every_remaining_checkpoint()
    {
        var (input, prefix, _) = Record();
        var suffix = input with { InitialStatePayload = prefix.Encode(),
            Commands = input.Commands.Where(command => Later(command.Result.Before, prefix.Position) || command.Result.Before == prefix.Position).ToImmutableArray(),
            Checkpoints = input.Checkpoints.Where(checkpoint => Later(checkpoint.Position, prefix.Position) || checkpoint.Position == prefix.Position).ToImmutableArray() };
        using var prefixReplay = Success(CoreFactory.Create().PrepareReplay(suffix, TrainRecoveryFixture.Runtime, [TrainRecoveryFixture.Content]));
        foreach (var checkpoint in suffix.Checkpoints)
        {
            var result = prefixReplay.Advance(1000, checkpoint.Position);
            Assert.IsNull(result.Difference);
            Assert.IsNull(result.Fault);
            Assert.IsInstanceOfType<CheckpointComparison.Equal>(Success(CoreFactory.Create().CompareCheckpoints(checkpoint, prefixReplay.CaptureBoundary().Checkpoint)));
        }
        Assert.AreEqual(ReplayStop.EndOfRecording, prefixReplay.Advance(0).Stop);
    }

    private static (CoreReplayInput Input, ICoreCapture Prefix, List<string> Events) Record()
    {
        using var session = TrainRecoveryFixture.New();
        var checkpoints = new List<CoreCheckpoint>();
        var events = new List<string>();
        void Checkpoint() => checkpoints.Add(Success(session.Capture(CapturePurpose.Save)).Checkpoint);
        TrainRecoveryFixture.Configure(session, events);
        TrainRecoveryFixture.Boundary(session, "edge", events);
        Checkpoint();
        var prefix = Success(session.Capture(CapturePurpose.Save));
        TrainRecoveryFixture.Boundary(session, "docked", events);
        Assert.AreEqual(TrainActivity.Servicing, TrainRecoveryFixture.Train(session).Activity);
        Checkpoint();
        TrainRecoveryFixture.Submit(session, new CoreCommand.StopTrain(TrainRecoveryFixture.Train(session).Id), events);
        Checkpoint();
        TrainRecoveryFixture.Submit(session, new CoreCommand.StartTrain(TrainRecoveryFixture.Train(session).Id), events);
        Checkpoint();
        for (var step = 0; step < 10; step++) { TrainRecoveryFixture.Advance(session, events); }
        Checkpoint();
        var recording = session.ReadRecording();
        CollectionAssert.AreEquivalent(new[] { "train.form", "train.disband", "train.replace_run_plan", "train.start", "train.stop" },
            recording.Commands.Select(command =>
        {
            using var json = System.Text.Json.JsonDocument.Parse(command.CanonicalPayload);
            return json.RootElement.GetProperty("kind").GetString();
        }).Distinct().ToArray());
        Assert.IsTrue(recording.Commands.All(command => command.Result.Decision is CommandDecision.Accepted));
        var input = new CoreReplayInput(2, TrainRecoveryFixture.Runtime, recording.Starts[0].State.Encode(), recording.Commands,
            checkpoints.ToImmutableArray(), recording.EndPosition, null);
        return (input, prefix, events);
    }

    private static bool Later(SimulationPosition first, SimulationPosition second) =>
        first.CompletedSteps > second.CompletedSteps || first.CompletedSteps == second.CompletedSteps && first.Sequence > second.Sequence;
}
