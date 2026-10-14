using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Inventory.InventoryFixture;

namespace Train.Core.Tests.Inventory;

[TestClass]
public sealed class InventoryLeaseTests
{
    [TestMethod]
    public void Half_hour_quote_rounds_up_and_business_rejections_still_show_price()
    {
        using var session = Session(Content(108000));
        var settlement = session.State.World.Settlements.Single().Id;
        var request = new LeaseRequest.Rent(settlement, 3, 108000);
        var quote = WorldFixture.Success(session.Read().QuoteLease(request));
        Assert.AreEqual(new Money(8), quote.Total);
        Assert.IsTrue(quote.Blockers.IsEmpty);
        RejectWithoutWorldChange(session, new CoreCommand.AcquireLease(request, new(7)));
        var accepted = Accept(session, new CoreCommand.AcquireLease(request, new(8)));
        Assert.AreEqual(new Money(8), accepted.Events.Select(e => e.Event).OfType<CoreEvent.LeaseAcquired>().Single().ActualPayment);
        var tooLarge = WorldFixture.Success(session.Read().QuoteLease(new LeaseRequest.Rent(settlement, 100, 108000)));
        Assert.AreEqual(new Money(250), tooLarge.Total);
        Assert.IsFalse(tooLarge.Blockers.IsEmpty);
    }

    [TestMethod]
    public void Invalid_duration_and_missing_market_rule_cannot_be_priced()
    {
        using var original = Session();
        var settlement = original.State.World.Settlements.Single().Id;
        var wrong = new LeaseRequest.Rent(settlement, 3, 4);
        var quote = WorldFixture.Success(original.Read().QuoteLease(wrong));
        Assert.IsNull(quote.Total);
        Assert.AreEqual("invalid_state", quote.Blockers.Single().Code);
        Assert.AreEqual("Request.DurationSteps", quote.Blockers.Single().Field);
        RejectWithoutWorldChange(original, new CoreCommand.AcquireLease(wrong, new(1000)), "invalid_state");
        var saved = WorldFixture.Success(original.Capture(CapturePurpose.Save));
        var missing = Changed(Content(), '9') with { MarketRules = [] };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(saved, missing, WorldFixture.Options));
        using var restored = (CoreSession)candidate.Activate();
        var request = new LeaseRequest.Rent(settlement, 3, 3);
        var missingQuote = WorldFixture.Success(restored.Read().QuoteLease(request));
        Assert.IsNull(missingQuote.Total);
        Assert.IsFalse(missingQuote.Blockers.IsEmpty);
        RejectWithoutWorldChange(restored, new CoreCommand.AcquireLease(request, new(1000)));
    }

    [TestMethod]
    public void Renewal_uses_current_rate_accumulates_paid_and_appends_only_while_active()
    {
        using var session = Session(Content(rate: 216000));
        var lease = Rent(session, 3);
        Assert.AreEqual(new Money(9), lease.Paid);
        Assert.AreEqual(3UL, lease.ExpiresAtStep);
        Step(session);
        Accept(session, new CoreCommand.PublishContent(Changed(Content(rate: 432000), '6')));
        var renewal = Accept(session, new CoreCommand.AcquireLease(new LeaseRequest.Renew(lease.Id, 3), new(18)))
            .Events.Select(e => e.Event).OfType<CoreEvent.LeaseAcquired>().Single();
        Assert.IsTrue(renewal.Renewal);
        Assert.AreEqual(new Money(18), renewal.ActualPayment);
        Assert.AreEqual(new Money(27), renewal.Lease.Paid);
        Assert.AreEqual(6UL, renewal.Lease.ExpiresAtStep);
        for (var i = 0; i < 6; i++) Step(session);
        Assert.IsTrue(Lease(session, lease.Id).Expired);
        var renewed = Accept(session, new CoreCommand.AcquireLease(new LeaseRequest.Renew(lease.Id, 3), new(18)))
            .Events.Select(e => e.Event).OfType<CoreEvent.LeaseAcquired>().Single().Lease;
        Assert.AreEqual(10UL, renewed.ExpiresAtStep);
        Assert.AreEqual(new Money(45), renewed.Paid);
        Assert.AreEqual(lease.Inventory, renewed.Inventory);
        Assert.IsFalse(renewed.Expired);
    }

    [TestMethod]
    public void Expiration_is_sorted_keeps_empty_inventory_and_changes_only_to_withdrawal()
    {
        using var session = Session(Content(duration: 2));
        var first = Rent(session, 20, 2);
        var second = Rent(session, 20, 2);
        Service(session, dwell: 2);
        Accept(session, new CoreCommand.UnloadGoods(new(1), first.Settlement, first.Inventory, Voxel, 7));
        var old = session.Read();
        var step = Step(session);
        var expired = step.Events.Select(e => e.Event).OfType<CoreEvent.LeaseExpired>().ToArray();
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, expired.Select(e => e.Lease).ToArray());
        Assert.IsTrue(step.Events.Take(2).All(e => e.Event is CoreEvent.LeaseExpired));
        Assert.IsTrue(step.Events.Skip(2).Any(e => e.Event is CoreEvent.TrainActivityChanged));
        Assert.AreEqual(InventoryAccess.WithdrawOnly, ReadInventory(session, first.Inventory).Access);
        Assert.AreEqual(0L, ReadInventory(session, first.Inventory).Available);
        Assert.AreEqual(0L, ReadInventory(session, second.Inventory).Used);
        Assert.IsTrue(Lease(session, second.Id).Expired);
        Assert.AreEqual(93L, ReadInventory(session, session.State.World.Settlements.Single().Inventory).Capacity);
        RejectWithoutWorldChange(session, new CoreCommand.UnloadGoods(new(1), first.Settlement, first.Inventory, Voxel, 1));
        Service(session);
        Accept(session, new CoreCommand.LoadGoods(new(1), first.Settlement, first.Inventory, Voxel, 7));
        Assert.AreEqual(100L, ReadInventory(session, session.State.World.Settlements.Single().Inventory).Capacity);
        Assert.AreEqual(InventoryAccess.ReadWrite, ((Lookup<InventoryView>.Found)old.FindInventory(first.Inventory)).Value.Access);
        Assert.IsFalse(((Lookup<LeaseView>.Found)old.FindLease(first.Id)).Value.Expired);
        Assert.IsFalse(Step(session).Events.Any(e => e.Event is CoreEvent.LeaseExpired));
    }

    [TestMethod]
    public void Active_lease_reduces_npc_quota_but_full_npc_stock_prevents_new_rental()
    {
        using var session = Session();
        Rent(session);
        Assert.AreEqual(80L, ReadInventory(session, session.State.World.Settlements.Single().Inventory).Capacity);
        using var full = Session(Content(localStock: 100));
        var settlement = full.State.World.Settlements.Single();
        var quote = WorldFixture.Success(full.Read().QuoteLease(new LeaseRequest.Rent(settlement.Id, 20, 3)));
        Assert.AreEqual(0L, quote.CurrentlyRentableCapacity);
        Assert.IsNotNull(quote.Total);
        RejectWithoutWorldChange(full, new CoreCommand.AcquireLease(quote.Request, new(1000)));
    }

    [TestMethod]
    public void Nonpositive_amounts_and_zero_duration_throw_before_allocating_command_number()
    {
        using var session = Session();
        var settlement = session.State.World.Settlements.Single();
        CoreCommand[] invalid = [
            new CoreCommand.AcquireLease(new LeaseRequest.Rent(settlement.Id, 0, 3), new(1000)),
            new CoreCommand.AcquireLease(new LeaseRequest.Rent(settlement.Id, -1, 3), new(1000)),
            new CoreCommand.AcquireLease(new LeaseRequest.Rent(settlement.Id, 3, 0), new(1000)),
            new CoreCommand.AcquireLease(new LeaseRequest.Renew(new(1), 0), new(1000)),
            new CoreCommand.LoadGoods(new(1), settlement.Id, settlement.Inventory, Voxel, 0),
            new CoreCommand.UnloadGoods(new(1), settlement.Id, settlement.Inventory, Voxel, -1)];
        var before = session.State;
        foreach (var command in invalid)
        {
            Assert.ThrowsExactly<ArgumentException>(() => session.Submit(command, CommandSource.AutomationTest));
            Assert.AreEqual(before, session.State);
        }
        Assert.IsTrue(session.ReadRecording().Commands.IsEmpty);
    }
}
