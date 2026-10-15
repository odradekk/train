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
    public void Goods_trades_record_acceptance_and_rejection_and_restore_every_replayed_boundary()
    {
        using var session = InventoryProcessDriver.TradeSession();
        var town = session.State.World.Settlements.Single();
        var samples = new List<ICoreCapture>();
        Accept(session, new CoreCommand.BuyGoods(new(1), town.Id, town.Inventory, Voxel, 1, new(1)));
        samples.Add(WorldFixture.Success(session.Capture(CapturePurpose.Save)));
        RejectWithoutWorldChange(session, new CoreCommand.BuyGoods(new(1), town.Id, town.Inventory, Voxel, 3, new(1000)), "insufficient_goods");
        samples.Add(WorldFixture.Success(session.Capture(CapturePurpose.Save)));
        Accept(session, new CoreCommand.SellGoods(new(1), town.Id, town.Inventory, InventoryTradeTests.Clothing, 1, new(1)));
        samples.Add(WorldFixture.Success(session.Capture(CapturePurpose.Save)));
        RejectWithoutWorldChange(session, new CoreCommand.SellGoods(new(1), town.Id, town.Inventory, InventoryTradeTests.Clothing, 3, new(0)), "insufficient_goods");
        samples.Add(WorldFixture.Success(session.Capture(CapturePurpose.Save)));
        var factory = CoreFactory.Create();
        var content = InventoryProcessDriver.TradeContent();
        foreach (var sample in samples)
        {
            using var candidate = WorldFixture.Success(factory.PrepareRestore(WorldFixture.Success(factory.DecodeState(sample.Encode())), content, WorldFixture.Options));
            using var restored = candidate.Activate();
            Assert.AreEqual(sample.Encode(), WorldFixture.Success(restored.Capture(CapturePurpose.Save)).Encode());
        }
        var recording = session.ReadRecording();
        var input = new CoreReplayInput(2, WorldFixture.Options.Runtime, recording.Starts[0].State.Encode(), recording.Commands,
            samples.Select(s => s.Checkpoint).ToImmutableArray(), recording.EndPosition, null);
        using var replay = WorldFixture.Success(factory.PrepareReplay(input, WorldFixture.Options.Runtime, [content]));
        foreach (var sample in samples)
        {
            var result = replay.Advance(0, sample.Position);
            Assert.IsNull(result.Difference);
            Assert.IsNull(result.Fault);
            Assert.AreEqual(sample.Encode(), replay.CaptureBoundary().Encode());
        }
        Assert.AreEqual(new Money(10000), session.Read().Economy.Wallet);
    }
    [TestMethod]
    public void Vehicle_sales_and_purchases_preserve_captures_and_replay_accepted_and_rejected_boundaries()
    {
        var content = VehicleTradeTests.VehicleContent();
        using var session = VehicleTradeTests.Parked(content);
        var town = session.State.World.Settlements.Single().Id;
        var samples = new List<ICoreCapture> { WorldFixture.Success(session.Capture(CapturePurpose.Save)) };
        RejectWithoutWorldChange(session, new CoreCommand.BuyVehicle(town, new(1), new(1000)), "not_for_sale");
        samples.Add(WorldFixture.Success(session.Capture(CapturePurpose.Save)));
        Accept(session, new CoreCommand.SellVehicle(town, new(1), new(126)));
        samples.Add(WorldFixture.Success(session.Capture(CapturePurpose.Save)));
        RejectWithoutWorldChange(session, new CoreCommand.SellVehicle(town, new(1), new(0)), "not_owned");
        samples.Add(WorldFixture.Success(session.Capture(CapturePurpose.Save)));
        Accept(session, new CoreCommand.BuyVehicle(town, new(1), new(154)));
        samples.Add(WorldFixture.Success(session.Capture(CapturePurpose.Save)));

        var factory = CoreFactory.Create();
        foreach (var sample in samples)
        {
            var payload = sample.Encode();
            var decoded = WorldFixture.Success(factory.DecodeState(payload));
            Assert.AreEqual(payload, decoded.Encode());
            using var candidate = WorldFixture.Success(factory.PrepareRestore(decoded, content, WorldFixture.Options));
            using var restored = candidate.Activate();
            Assert.AreEqual(payload, WorldFixture.Success(restored.Capture(CapturePurpose.Save)).Encode());
        }

        var recording = session.ReadRecording();
        foreach (var kind in new[] { "market.buy_vehicle", "market.sell_vehicle" })
        {
            var commands = recording.Commands.Where(c => c.CanonicalPayload.Contains($"\"kind\":\"{kind}\"", StringComparison.Ordinal)).ToArray();
            Assert.AreEqual(2, commands.Length);
            Assert.AreEqual(1, commands.Count(c => c.Result.Decision is CommandDecision.Accepted));
            Assert.AreEqual(1, commands.Count(c => c.Result.Decision is CommandDecision.Rejected));
        }
        var input = new CoreReplayInput(2, WorldFixture.Options.Runtime, recording.Starts[0].State.Encode(),
            recording.Commands, samples.Select(s => s.Checkpoint).ToImmutableArray(), recording.EndPosition, null);
        using var replay = WorldFixture.Success(factory.PrepareReplay(input, WorldFixture.Options.Runtime, [content]));
        var traded = new List<CoreEvent.VehicleTraded>();
        foreach (var sample in samples)
        {
            var result = replay.Advance(10, sample.Position);
            Assert.IsNull(result.Difference);
            Assert.IsNull(result.Fault);
            Assert.AreEqual(sample.Position, result.Position);
            Assert.AreEqual(sample.Encode(), replay.CaptureBoundary().Encode());
            traded.AddRange(result.Events.Select(e => e.Event).OfType<CoreEvent.VehicleTraded>());
        }
        CollectionAssert.AreEqual(new[]
        {
            new CoreEvent.VehicleTraded(new(1), town, VehicleTradeAction.Sell, new(126)),
            new CoreEvent.VehicleTraded(new(1), town, VehicleTradeAction.Buy, new(154))
        }, traded.ToArray());
        Assert.AreEqual(ReplayStop.EndOfRecording, replay.Advance(0).Stop);
    }

    [TestMethod]
    public void Public_commands_replay_success_rejection_renewal_and_commandless_expiration_tail()
    {
        using var session = Session();
        var lease = Rent(session, duration: 6);
        Service(session);
        Accept(session, new CoreCommand.UnloadGoods(new(1), lease.Settlement, lease.Inventory, Composite, 2));
        var samples = new List<ICoreCapture> { WorldFixture.Success(session.Capture(CapturePurpose.Save)) };
        RejectWithoutWorldChange(session, new CoreCommand.LoadGoods(new(1), lease.Settlement, lease.Inventory, Composite, 3));
        samples.Add(WorldFixture.Success(session.Capture(CapturePurpose.Save)));
        foreach (var duration in new[] { 4UL, 39UL, 36UL })
        {
            RejectWithoutWorldChange(session, new CoreCommand.AcquireLease(new LeaseRequest.Renew(lease.Id, duration), new(1000)),
                "invalid_state", "Request.DurationSteps");
            samples.Add(WorldFixture.Success(session.Capture(CapturePurpose.Save)));
        }
        Accept(session, new CoreCommand.AcquireLease(new LeaseRequest.Renew(lease.Id, 3), new(1000)));
        samples.Add(WorldFixture.Success(session.Capture(CapturePurpose.Save)));
        while (session.Read().Position.CompletedSteps < 9)
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
