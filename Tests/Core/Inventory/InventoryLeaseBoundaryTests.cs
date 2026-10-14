using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Inventory.InventoryFixture;

namespace Train.Core.Tests.Inventory;

[TestClass]
public sealed class InventoryLeaseBoundaryTests
{
    [TestMethod]
    public void Renewal_excludes_its_own_quota_but_cannot_reclaim_space_rented_after_expiration()
    {
        using var session = Session();
        var first = Rent(session, 40);
        Rent(session, 60);
        Service(session);
        Accept(session, new CoreCommand.UnloadGoods(new(1), first.Settlement, first.Inventory, Voxel, 7));
        var renewed = Accept(session, new CoreCommand.AcquireLease(new LeaseRequest.Renew(first.Id, 3), new(1000)))
            .Events.Select(e => e.Event).OfType<CoreEvent.LeaseAcquired>().Single().Lease;
        Assert.AreEqual(6UL, renewed.ExpiresAtStep);
        while (session.Read().Position.CompletedSteps < 6) Step(session);
        Rent(session, 90);
        var before = Lease(session, first.Id);
        RejectWithoutWorldChange(session, new CoreCommand.AcquireLease(new LeaseRequest.Renew(first.Id, 3), new(1000)), "insufficient_capacity");
        Assert.AreEqual(before, Lease(session, first.Id));
        Assert.AreEqual(7L, ReadInventory(session, first.Inventory).Used);
    }

    [TestMethod]
    public void Missing_market_rule_does_not_delay_expiration_or_disable_manual_transfers()
    {
        using var original = Session();
        var lease = Rent(original);
        Service(original);
        Accept(original, new CoreCommand.UnloadGoods(new(1), lease.Settlement, lease.Inventory, Composite, 2));
        var saved = WorldFixture.Success(original.Capture(CapturePurpose.Save));
        var content = Changed(Content(), '9') with { MarketRules = [] };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(saved, content, WorldFixture.Options));
        using var session = (CoreSession)candidate.Activate();
        Accept(session, new CoreCommand.UnloadGoods(new(1), lease.Settlement, lease.Inventory, Composite, 1));
        Step(session);
        Assert.AreEqual(lease.Id, Step(session).Events.Select(e => e.Event).OfType<CoreEvent.LeaseExpired>().Single().Lease);
        Accept(session, new CoreCommand.LoadGoods(new(1), lease.Settlement, lease.Inventory, Composite, 3));
        RejectWithoutWorldChange(session, new CoreCommand.UnloadGoods(new(1), lease.Settlement, lease.Inventory, Composite, 1), "access_denied");
        var quote = WorldFixture.Success(session.Read().QuoteLease(new LeaseRequest.Renew(lease.Id, 3)));
        Assert.IsNull(quote.Total);
        Assert.AreEqual("content_missing", quote.Blockers.Single().Code);
    }

    [TestMethod]
    public void Price_overflow_faults_submission_atomically_but_quote_does_not_freeze_session()
    {
        using var session = Session(Content(108000, long.MaxValue));
        var request = new LeaseRequest.Rent(session.State.World.Settlements.Single().Id, 100, 108000);
        var before = session.State;
        Assert.IsInstanceOfType<CoreResult<LeaseQuote>.Faulted>(session.Read().QuoteLease(request));
        Assert.AreEqual(before, session.State);
        var result = Submit(session, new CoreCommand.AcquireLease(request, new(long.MaxValue)));
        Assert.IsInstanceOfType<CommandDecision.Faulted>(result.Receipt.Decision);
        Assert.IsNotNull(session.Read().Fault);
        Assert.AreEqual(before.World, session.State.World);
        Assert.AreEqual(before.Position, session.Read().Position);
        Assert.IsTrue(result.Events.IsEmpty);
    }
}
