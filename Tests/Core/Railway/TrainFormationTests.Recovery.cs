using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.Railway;

public sealed partial class TrainFormationTests
{
    [TestMethod]
    public void Replacement_accepted_and_rejected_commands_restore_and_strictly_replay()
    {
        var input = Input with { Configuration = Input.Configuration with
        { WidthMeters = 8, HeightMeters = 2, MinimumNodeSpacingMeters = 2, SettlementRatio = 1,
            LairRatio = 0, HostileSettlementRatio = 0 },
            Start = Input.Start with { Trains = [Input.Start.Trains[0] with { Vehicles = [Vehicle, Vehicle] }] } };
        using var initial = Success(CoreFactory.Create().PrepareNew(input, TrainRecoveryFixture.Content,
            new(TrainRecoveryFixture.Runtime, false, 32)));
        using var session = initial.Activate();
        var original = TrainRecoveryFixture.Train(session);
        var destination = session.Read().World.Nodes.First(n => n.Id != ((TrainPosition.Node)original.Position).Id).Id;
        Accepted(session, new CoreCommand.ReplaceRunPlan(original.Id, new(false, [new PlanStop.Station(destination, 3, [])])));
        Accepted(session, new CoreCommand.ReplaceTrainFormation(original.Id, [original.Vehicles[1]]));
        CollectionAssert.AreEqual(new[] { original.Vehicles[1] }, TrainRecoveryFixture.Train(session).Vehicles.ToArray());
        var removed = ((Lookup<InstanceView>.Found)session.Read().FindInstance(new InstanceId.Vehicle(original.Vehicles[0]))).Value;
        Assert.IsNull(((InstanceDetails.Vehicle)removed.Details).Train);
        Assert.AreEqual(new WorldLocation.Node(((TrainPosition.Node)original.Position).Id), removed.Location);
        var rejected = (SubmissionResult.Completed)session.Submit(
            new CoreCommand.ReplaceTrainFormation(original.Id, []), CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Rejected>(rejected.Receipt.Decision);
        var capture = Success(session.Capture(CapturePurpose.Save));
        var decoded = Success(CoreFactory.Create().DecodeState(capture.Encode()));
        using var candidate = Success(CoreFactory.Create().PrepareRestore(decoded, TrainRecoveryFixture.Content, Options));
        using var restored = candidate.Activate();
        Assert.AreEqual(TrainRecoveryFixture.Query(session), TrainRecoveryFixture.Query(restored));
        Assert.AreEqual(capture.Checkpoint, Success(restored.Capture(CapturePurpose.Save)).Checkpoint);
        Accepted(session, new CoreCommand.StartTrain(original.Id));
        Accepted(restored, new CoreCommand.StartTrain(original.Id));
        for (var i = 0; i < 12; i++)
        {
            TrainRecoveryFixture.Advance(session);
            TrainRecoveryFixture.Advance(restored);
            Assert.AreEqual(TrainRecoveryFixture.Query(session), TrainRecoveryFixture.Query(restored));
        }
        var end = Success(session.Capture(CapturePurpose.Save));
        var recording = session.ReadRecording();
        var replayInput = new CoreReplayInput(2, TrainRecoveryFixture.Runtime, recording.Starts[0].State.Encode(), recording.Commands,
            [capture.Checkpoint, end.Checkpoint], recording.EndPosition, null);
        using var replay = Success(CoreFactory.Create().PrepareReplay(replayInput, TrainRecoveryFixture.Runtime, [TrainRecoveryFixture.Content]));
        foreach (var checkpoint in replayInput.Checkpoints)
        {
            var result = replay.Advance(1000, checkpoint.Position);
            Assert.IsNull(result.Difference);
            Assert.IsNull(result.Fault);
            Assert.AreEqual(checkpoint, replay.CaptureBoundary().Checkpoint);
        }
        Assert.AreEqual(ReplayStop.EndOfRecording, replay.Advance(0).Stop);
    }
}

