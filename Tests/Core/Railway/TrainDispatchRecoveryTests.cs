using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.World;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.Railway;

[TestClass]
public sealed class TrainDispatchRecoveryTests
{
    [TestMethod]
    [DataRow("queue")]
    [DataRow("handoff")]
    [DataRow("returning")]
    public void Saved_dispatch_state_restores_the_same_suffix(string boundary)
    {
        var state = TrainDispatchRecoveryScenario.State(boundary);
        using var continuous = new CoreSession(state);
        var capture = Success(continuous.Capture(CapturePurpose.Save));
        var decoded = Success(CoreFactory.Create().DecodeState(capture.Encode()));
        Assert.AreEqual(capture.Encode(), decoded.Encode());
        using var candidate = Success(CoreFactory.Create().PrepareRestore(decoded, state.Content, Options));
        using var resumed = candidate.Activate();
        var expected = TrainDispatchRecoveryScenario.Suffix(continuous);
        var actual = TrainDispatchRecoveryScenario.Suffix(resumed);
        CollectionAssert.AreEqual(expected.Samples, actual.Samples);
        CollectionAssert.AreEqual(expected.Events, actual.Events);
    }

    [TestMethod]
    public void Debug_break_command_and_dispatch_steps_strictly_replay()
    {
        var state = TrainDispatchRecoveryScenario.State("queue");
        using var session = new CoreSession(state);
        var events = new List<string>();
        var checkpoints = new List<CoreCheckpoint>();
        TrainRecoveryFixture.Advance(session, events);
        checkpoints.Add(Success(session.Capture(CapturePurpose.Save)).Checkpoint);
        TrainRecoveryFixture.Submit(session, new CoreCommand.DebugAddRailBreak(new(1), 1, 15), events);
        checkpoints.Add(Success(session.Capture(CapturePurpose.Save)).Checkpoint);
        for (var step = 0; step < 12; step++) TrainRecoveryFixture.Advance(session, events);
        checkpoints.Add(Success(session.Capture(CapturePurpose.Save)).Checkpoint);
        var recording = session.ReadRecording();
        var input = new CoreReplayInput(1, Options.Runtime, recording.Starts[0].State.Encode(), recording.Commands,
            checkpoints.ToImmutableArray(), recording.EndPosition, null);
        using var replay = Success(CoreFactory.Create().PrepareReplay(input, Options.Runtime, [state.Content]));
        var replayEvents = new List<string>();
        foreach (var checkpoint in checkpoints)
        {
            var result = replay.Advance(20, checkpoint.Position);
            Assert.IsNull(result.Fault);
            Assert.IsNull(result.Difference);
            replayEvents.AddRange(result.Events.Select(TrainRecoveryFixture.Event));
            Assert.IsInstanceOfType<CheckpointComparison.Equal>(Success(CoreFactory.Create().CompareCheckpoints(checkpoint, replay.CaptureBoundary().Checkpoint)));
        }
        CollectionAssert.AreEqual(events, replayEvents);
        Assert.AreEqual(ReplayStop.EndOfRecording, replay.Advance(0).Stop);
        StringAssert.Contains(recording.Commands.Single().CanonicalPayload, "rail.debug_add_break");
    }

    [TestMethod]
    public void Dispatch_queries_do_not_change_checkpoints_or_events()
    {
        var state = TrainDispatchRecoveryScenario.State("returning");
        using var observed = new CoreSession(state);
        using var unobserved = new CoreSession(state);
        var observedEvents = new List<string>();
        var unobservedEvents = new List<string>();
        for (var step = 0; step < 12; step++)
        {
            _ = TrainDispatchRecoveryScenario.Query(observed);
            TrainRecoveryFixture.Advance(observed, observedEvents);
            TrainRecoveryFixture.Advance(unobserved, unobservedEvents);
        }
        CollectionAssert.AreEqual(observedEvents, unobservedEvents);
        Assert.AreEqual(Success(observed.Capture(CapturePurpose.Save)).Checkpoint,
            Success(unobserved.Capture(CapturePurpose.Save)).Checkpoint);
    }
}

internal static class TrainDispatchRecoveryScenario
{
    internal static CoreState State(string boundary)
    {
        var state = TrainDispatchFixture.State(count: boundary == "queue" ? 2 : 1);
        var world = state.World;
        var first = world.Trains[0];
        switch (boundary)
        {
            case "queue":
                world = world with { Trains = [first with { Position = new TrainPosition.Edge(new(1), 1, TravelDirection.Forward, 10),
                    Transit = null, Activity = TrainActivity.Running }, world.Trains[1] with { Activity = TrainActivity.Waiting, QueueEnteredStep = 3 }] };
                break;
            case "handoff":
                world = world with { TrackHandoffs = [new(new(1), 1, TravelDirection.Reverse)],
                    Trains = [first with { Position = new TrainPosition.Node(new(2)), Activity = TrainActivity.Waiting,
                        QueueEnteredStep = 3, Transit = new(new(1), null), Plan = new(false, [new PlanStop.Waypoint(new(1))]) }] };
                break;
            case "returning":
                world = world with { Edges = world.Edges.SetItem(0, world.Edges[0] with { Breaks = [new(new(1), 1, 15)] }),
                    Next = world.Next with { Break = 2 }, Trains = [first with { Position = new TrainPosition.Edge(new(1), 1, TravelDirection.Reverse, 10),
                        Transit = null, Activity = TrainActivity.Running, ReturningFromBreak = new(1), OrderFacing = TrainOrderFacing.LastEndLeading }] };
                break;
            default: throw new ArgumentException("未知调度边界。", nameof(boundary));
        }
        WorldStateValidation.Validate(world);
        return state with { Position = new(5, 0), World = world };
    }

    internal static (TrainProcessSample[] Samples, string[] Events) Suffix(ICoreSession session)
    {
        var samples = new List<TrainProcessSample>();
        var events = new List<string>();
        void Sample()
        {
            var capture = Success(session.Capture(CapturePurpose.Save));
            samples.Add(new(capture.Position, capture.Encode(), capture.Checkpoint.CanonicalText, capture.Checkpoint.Sha256, Query(session)));
        }
        Sample();
        for (var step = 0; step < 12; step++)
        {
            TrainRecoveryFixture.Advance(session, events);
            if (step is 0 or 5 or 11) Sample();
        }
        return (samples.ToArray(), events.ToArray());
    }

    internal static string Query(ICoreSession session)
    {
        var read = session.Read();
        return JsonSerializer.Serialize(new { Nodes = read.World.Nodes.Select(n => new { n.Id, n.Platforms, n.OccupiedPlatforms }),
            Trains = read.World.Trains.Select(t => ((Lookup<TrainView>.Found)read.FindTrain(t.Id)).Value)
                .Select(t => new { t.Id, Position = TrainRecoveryFixture.Position(t.Position), t.Activity,
                    t.SpeedMetersPerSecond, t.OrderFacing, t.RemainingDwellSteps, t.NodeTransit, t.Blockers }),
            Edges = read.World.Edges.Select(e => new { e.Id, e.Breaks }) });
    }
}
