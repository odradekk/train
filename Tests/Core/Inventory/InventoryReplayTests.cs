using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.World;
using static Train.Core.Tests.Inventory.InventoryFixture;

namespace Train.Core.Tests.Inventory;

[TestClass]
public sealed class InventoryReplayTests
{
    [TestMethod]
    public void Public_commands_replay_success_rejection_renewal_and_commandless_expiration_tail()
    {
        using var session = Session();
        var lease = Rent(session);
        Service(session);
        Accept(session, new CoreCommand.UnloadGoods(new(1), lease.Settlement, lease.Inventory, Composite, 2));
        var samples = new List<ICoreCapture> { WorldFixture.Success(session.Capture(CapturePurpose.Save)) };
        RejectWithoutWorldChange(session, new CoreCommand.LoadGoods(new(1), lease.Settlement, lease.Inventory, Composite, 3));
        samples.Add(WorldFixture.Success(session.Capture(CapturePurpose.Save)));
        Accept(session, new CoreCommand.AcquireLease(new LeaseRequest.Renew(lease.Id, 3), new(1000)));
        samples.Add(WorldFixture.Success(session.Capture(CapturePurpose.Save)));
        while (session.Read().Position.CompletedSteps < 6)
        {
            Step(session);
            samples.Add(WorldFixture.Success(session.Capture(CapturePurpose.Save)));
        }
        var recording = session.ReadRecording();
        Assert.IsTrue(recording.Commands.Any(c => c.Result.Decision is CommandDecision.Rejected));
        Assert.IsTrue(recording.EndPosition.CompletedSteps > recording.Commands.Last().Result.After.CompletedSteps);
        var input = new CoreReplayInput(2, WorldFixture.Options.Runtime, recording.Starts[0].State.Encode(),
            recording.Commands, samples.Select(s => s.Checkpoint).ToImmutableArray(), recording.EndPosition, null);
        using var replay = WorldFixture.Success(CoreFactory.Create().PrepareReplay(input, WorldFixture.Options.Runtime, [Content()]));
        var expired = new List<LeaseId>();
        foreach (var sample in samples)
        {
            var result = replay.Advance(10, sample.Position);
            Assert.IsNull(result.Difference);
            Assert.IsNull(result.Fault);
            Assert.AreEqual(sample.Position, result.Position);
            Assert.AreEqual(sample.Encode(), replay.CaptureBoundary().Encode());
            expired.AddRange(result.Events.Select(e => e.Event).OfType<CoreEvent.LeaseExpired>().Select(e => e.Lease));
        }
        CollectionAssert.AreEqual(new[] { lease.Id }, expired);
        Assert.AreEqual(ReplayStop.EndOfRecording, replay.Advance(0).Stop);
    }
}
