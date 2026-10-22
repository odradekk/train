using Train.Core.Tests.World;
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
    [DataRow("opposing")]
    [DataRow("following")]
    [DataRow("replan")]
    public void Saved_dispatch_state_restores_the_same_suffix(string boundary)
    {
        var state = TrainDispatchRecoveryScenario.State(boundary);
        using var continuous = new CoreSession(state);
        var capture = Success(continuous.Capture(CapturePurpose.Save));
        var decoded = Success(CoreFactory.Create().DecodeState(capture.Encode()));
        Assert.AreEqual(capture.Encode(), decoded.Encode());
        using var candidate = Success(CoreFactory.Create().PrepareRestore(decoded, state.Content, Options));
        using var resumed = candidate.Activate();
        var expected = TrainDispatchRecoveryScenario.Suffix(continuous, boundary);
        var actual = TrainDispatchRecoveryScenario.Suffix(resumed, boundary);
        CollectionAssert.AreEqual(expected.Samples, actual.Samples);
        CollectionAssert.AreEqual(expected.Events, actual.Events);
        // 恢复后的后缀必须真的走过这个边界：对向等待的老车最终发车，跟车保持次序，换计划的列车离站。
        var trains = resumed.Read();
        TrainPosition Position(ulong id) => ((Lookup<TrainView>.Found)trains.FindTrain(new(id))).Value.Position;
        switch (boundary)
        {
            case "opposing":
                Assert.IsFalse(Position(2) is TrainPosition.Node { Id.Value: 1 }, "同向老车在对向队列之后仍未发车。");
                break;
            case "following":
                Assert.IsTrue(Position(1) is TrainPosition.Node { Id.Value: 2 }, "前车没有到达节点 2。");
                Assert.IsFalse(Position(2) is TrainPosition.Edge { DistanceMeters: <= 30 }, "后车没有跟着前进。");
                break;
            case "replan":
                Assert.IsFalse(Position(1) is TrainPosition.Node { Id.Value: 1 }, "换计划后列车 1 仍未离开节点 1。");
                break;
        }
    }

    [TestMethod]
    public void Different_node_entry_and_exit_tracks_restore_and_strictly_replay_the_same_continuation()
    {
        var state = TrainDispatchFixture.State(count: 1, tracks: 2);
        var train = state.World.Trains[0] with
        {
            Position = new TrainPosition.Node(new(2), new(1), 2, new(1), 1,
                NodeTravelPhase.Departing, WorldFixture.ApproachLength, null),
            SpeedMetersPerSecond = 1, Activity = TrainActivity.Running,
            Plan = new(false, [new PlanStop.Waypoint(new(1))])
        };
        state = state with { World = state.World with { Trains = [train] } };
        using var continuous = new CoreSession(state);
        var capture = Success(continuous.Capture(CapturePurpose.Save));
        var decoded = Success(CoreFactory.Create().DecodeState(capture.Encode()));
        Assert.AreEqual(capture.Encode(), decoded.Encode());
        using var candidate = Success(CoreFactory.Create().PrepareRestore(decoded, state.Content, Options));
        using var resumed = candidate.Activate();
        var restored = (TrainPosition.Node)TrainRecoveryFixture.Train(resumed).Position;
        Assert.AreEqual(2, restored.EnteredTrack);
        Assert.AreEqual(1, restored.NextTrack);
        Assert.AreEqual(TrainDispatchRecoveryScenario.Query(continuous), TrainDispatchRecoveryScenario.Query(resumed));
        var checkpoints = new List<CoreCheckpoint> { capture.Checkpoint };
        var events = new List<string>();
        var resumedEvents = new List<string>();
        for (var step = 0; step < 12; step++)
        {
            TrainRecoveryFixture.Advance(continuous, events);
            TrainRecoveryFixture.Advance(resumed, resumedEvents);
            Assert.AreEqual(TrainDispatchRecoveryScenario.Query(continuous), TrainDispatchRecoveryScenario.Query(resumed));
            var checkpoint = Success(continuous.Capture(CapturePurpose.Save)).Checkpoint;
            Assert.AreEqual(checkpoint, Success(resumed.Capture(CapturePurpose.Save)).Checkpoint);
            checkpoints.Add(checkpoint);
        }
        CollectionAssert.AreEqual(events, resumedEvents);
        Assert.IsTrue(TrainRecoveryFixture.Train(continuous).Position is TrainPosition.Edge { Track: 1 });
        var recording = continuous.ReadRecording();
        var input = new CoreReplayInput(2, Options.Runtime, capture.Encode(), recording.Commands,
            checkpoints.ToImmutableArray(), recording.EndPosition, null);
        using var replay = Success(CoreFactory.Create().PrepareReplay(input, Options.Runtime, [state.Content]));
        var replayEvents = new List<string>();
        foreach (var checkpoint in checkpoints)
        {
            var result = replay.Advance(12, checkpoint.Position);
            Assert.IsNull(result.Fault);
            Assert.IsNull(result.Difference);
            Assert.AreEqual(checkpoint, replay.CaptureBoundary().Checkpoint);
            replayEvents.AddRange(result.Events.Select(TrainRecoveryFixture.Event));
        }
        CollectionAssert.AreEqual(events, replayEvents);
        Assert.AreEqual(ReplayStop.EndOfRecording, replay.Advance(0).Stop);
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
        var input = new CoreReplayInput(2, Options.Runtime, recording.Starts[0].State.Encode(), recording.Commands,
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
        var state = boundary switch
        {
            "queue" or "replan" => TrainDispatchFixture.State(count: 2),
            "opposing" => TrainDispatchFixture.State(count: 3),
            "following" => TrainDispatchFixture.State(count: 2, length: 100),
            _ => TrainDispatchFixture.State(count: 1)
        };
        var world = state.World;
        var first = world.Trains[0];
        switch (boundary)
        {
            case "opposing":
                world = world with { Trains = [TrainDispatchFixture.OnEdge(first, 19.99, 2),
                    TrainDispatchFixture.Queued(world.Trains[1], 1, 1, 2), TrainDispatchFixture.Queued(world.Trains[2], 2, 2, 1)] };
                break;
            case "following":
                world = world with { Trains = [TrainDispatchFixture.OnEdge(first, 40, 1), TrainDispatchFixture.OnEdge(world.Trains[1], 30, 8)] };
                break;
            case "replan":
                // 列车 1 在站台服务，列车 2 排队；边界之后由后缀把列车 1 换成直达计划。
                var station = state with { World = world with { Trains = world.Trains.Select(train => train with
                    { Activity = TrainActivity.Running, Plan = new(false, [new PlanStop.Station(new(1), 2, []), new PlanStop.Waypoint(new(2))]) })
                    .ToImmutableArray() } };
                using (var session = new CoreSession(station))
                {
                    TrainDispatchFixture.Until(session, () => TrainDispatchFixture.View(session, 1).Activity == TrainActivity.Servicing);
                    return session.State;
                }
            case "queue":
                world = world with { Trains = [first with { Position = new TrainPosition.Edge(new(1), 1, TravelDirection.Forward, 10),
                    Activity = TrainActivity.Running }, world.Trains[1] with { Activity = TrainActivity.Waiting, QueueEnteredStep = 3 }] };
                break;
            case "handoff":
                world = world with { TrackHandoffs = [new(new(1), 1, TravelDirection.Reverse)],
                    Trains = [first with { Position = WorldFixture.Node(new(2), NodeTravelPhase.Departing, WorldFixture.ApproachLength + WorldFixture.DepartureLength, enteredFrom: new(1), nextEdge: new(1)), Activity = TrainActivity.Waiting,
                        QueueEnteredStep = 3, Plan = new(false, [new PlanStop.Waypoint(new(1))]) }] };
                break;
            case "returning":
                world = world with { Edges = world.Edges.SetItem(0, world.Edges[0] with { Breaks = [new(new(1), 1, 15)] }),
                    Next = world.Next with { Break = 2 }, Trains = [first with { Position = new TrainPosition.Edge(new(1), 1, TravelDirection.Reverse, 10),
                        Activity = TrainActivity.Running, ReturningFromBreak = new(1), OrderFacing = TrainOrderFacing.LastEndLeading }] };
                break;
            default: throw new ArgumentException("未知调度边界。", nameof(boundary));
        }
        WorldStateValidation.Validate(world);
        return state with { Position = new(5, 0), World = world };
    }

    internal static (TrainProcessSample[] Samples, string[] Events) Suffix(ICoreSession session, string boundary)
    {
        var samples = new List<TrainProcessSample>();
        var events = new List<string>();
        void Sample()
        {
            var capture = Success(session.Capture(CapturePurpose.Save));
            samples.Add(new(capture.Position, capture.Encode(), capture.Checkpoint.CanonicalText, capture.Checkpoint.Sha256, Query(session)));
        }
        Sample();
        if (boundary == "replan") TrainRecoveryFixture.Submit(session, new CoreCommand.ReplaceRunPlan(new(1), TrainDispatchFixture.Plan(2)), events);
        // 原有三个边界只看紧接着的几步；新边界要走到对向发车、跟车拉开和离站。
        int[] sampled = boundary is "queue" or "handoff" or "returning" ? [0, 5, 11] : [99, 199, 299, 399, 499, 599, 699, 799, 899];
        for (var step = 0; step <= sampled[^1]; step++)
        {
            TrainRecoveryFixture.Advance(session, events);
            if (sampled.Contains(step)) Sample();
        }
        return (samples.ToArray(), events.ToArray());
    }

    internal static string Query(ICoreSession session)
    {
        var read = session.Read();
        return JsonSerializer.Serialize(new { Nodes = read.World.Nodes.Select(n => new { n.Id, n.Platforms, n.OccupiedPlatforms }),
            Trains = read.World.Trains.Select(t => ((Lookup<TrainView>.Found)read.FindTrain(t.Id)).Value)
                .Select(t => new { t.Id, Position = TrainRecoveryFixture.Position(t.Position), t.Activity,
                    t.SpeedMetersPerSecond, t.OrderFacing, t.RemainingDwellSteps, t.Blockers }),
            Edges = read.World.Edges.Select(e => new { e.Id, e.Breaks }) });
    }
}
