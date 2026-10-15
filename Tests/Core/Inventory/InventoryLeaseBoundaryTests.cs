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
    [DataRow(false, 1UL)]
    [DataRow(false, 4UL)]
    [DataRow(false, 39UL)]
    [DataRow(true, 1UL)]
    [DataRow(true, 4UL)]
    [DataRow(true, 39UL)]
    public void Rent_and_renew_reject_short_nonmultiple_and_excessive_terms_before_capacity(bool renew, ulong duration)
    {
        using var session = Session();
        var lease = Rent(session, 100);
        LeaseRequest request = renew ? new LeaseRequest.Renew(lease.Id, duration)
            : new LeaseRequest.Rent(lease.Settlement, 1, duration);
        var quote = WorldFixture.Success(session.Read().QuoteLease(request));
        Assert.IsNull(quote.Total);
        Assert.AreEqual("invalid_state", quote.Blockers.Single().Code);
        Assert.AreEqual("Request.DurationSteps", quote.Blockers.Single().Field);
        RejectWithoutWorldChange(session, new CoreCommand.AcquireLease(request, new(1000)), "invalid_state", "Request.DurationSteps");
    }

    [TestMethod]
    public void Renewal_counts_remaining_time_and_allows_the_exact_limit_and_expired_full_term()
    {
        using var session = Session();
        var lease = Rent(session, 20, 36);
        Step(session); Step(session);
        var request = new LeaseRequest.Renew(lease.Id, 3);
        var quote = WorldFixture.Success(session.Read().QuoteLease(request));
        Assert.IsNull(quote.Total);
        Assert.AreEqual("Request.DurationSteps", quote.Blockers.Single().Field);
        RejectWithoutWorldChange(session, new CoreCommand.AcquireLease(request, new(1000)), "invalid_state", "Request.DurationSteps");
        Step(session);
        var renewed = Accept(session, new CoreCommand.AcquireLease(request, new(1000)))
            .Events.Select(e => e.Event).OfType<CoreEvent.LeaseAcquired>().Single().Lease;
        Assert.AreEqual(39UL, renewed.ExpiresAtStep);
        while (session.Read().Position.CompletedSteps < 39) Step(session);
        var expiredRenewal = Accept(session, new CoreCommand.AcquireLease(new LeaseRequest.Renew(lease.Id, 36), new(1000)))
            .Events.Select(e => e.Event).OfType<CoreEvent.LeaseAcquired>().Single().Lease;
        Assert.AreEqual(75UL, expiredRenewal.ExpiresAtStep);
        Assert.IsFalse(expiredRenewal.Expired);
    }

    [TestMethod]
    [DataRow(1L, 1, ulong.MaxValue, false)]
    [DataRow(long.MaxValue, int.MaxValue, ulong.MaxValue, false)]
    [DataRow(long.MaxValue, int.MaxValue, ulong.MaxValue - 1, true)]
    [DataRow(long.MaxValue, 1, ulong.MaxValue - 1, false)]
    public void Term_validation_handles_unsigned_requests_and_rule_products_beyond_ulong(long fixedDuration, int maximumMultiple,
        ulong duration, bool valid)
    {
        var content = Content((ulong)fixedDuration, rate: 0);
        content = content with { MarketRules = [content.MarketRules[0] with { MaximumLeaseDurationMultiple = maximumMultiple }] };
        using var session = Session(content);
        var request = new LeaseRequest.Rent(session.State.World.Settlements.Single().Id, 1, duration);
        var quote = WorldFixture.Success(session.Read().QuoteLease(request));
        if (valid)
        {
            Assert.IsTrue(quote.Blockers.IsEmpty);
            Assert.AreEqual(new Money(0), quote.Total);
            var acquired = Accept(session, new CoreCommand.AcquireLease(request, new(0)))
                .Events.Select(e => e.Event).OfType<CoreEvent.LeaseAcquired>().Single().Lease;
            Assert.AreEqual(duration, acquired.ExpiresAtStep);
        }
        else
        {
            Assert.IsNull(quote.Total);
            Assert.AreEqual("invalid_state", quote.Blockers.Single().Code);
            Assert.AreEqual("Request.DurationSteps", quote.Blockers.Single().Field);
            RejectWithoutWorldChange(session, new CoreCommand.AcquireLease(request, new(0)), "invalid_state", "Request.DurationSteps");
        }
        Assert.IsNull(session.Read().Fault);
    }

    [TestMethod]
    public void Renewal_rejects_remaining_plus_duration_beyond_ulong_without_faulting()
    {
        var content = Content((ulong)long.MaxValue, rate: 0);
        content = content with { MarketRules = [content.MarketRules[0] with { MaximumLeaseDurationMultiple = 2 }] };
        using var session = Session(content);
        var lease = Rent(session, 1, (ulong)long.MaxValue);
        var request = new LeaseRequest.Renew(lease.Id, (ulong)long.MaxValue);
        var renewed = Accept(session, new CoreCommand.AcquireLease(request, new(0)))
            .Events.Select(e => e.Event).OfType<CoreEvent.LeaseAcquired>().Single().Lease;
        Assert.AreEqual(ulong.MaxValue - 1, renewed.ExpiresAtStep);
        var quote = WorldFixture.Success(session.Read().QuoteLease(request));
        Assert.IsNull(quote.Total);
        Assert.AreEqual("invalid_state", quote.Blockers.Single().Code);
        Assert.AreEqual("Request.DurationSteps", quote.Blockers.Single().Field);
        RejectWithoutWorldChange(session, new CoreCommand.AcquireLease(request, new(0)), "invalid_state", "Request.DurationSteps");
        Assert.IsNull(session.Read().Fault);
    }

    [TestMethod]
    public void Rule_changes_and_restore_preserve_historical_terms_that_exceed_the_new_limit()
    {
        using var original = Session();
        var lease = Rent(original, 20, 36);
        var content = Changed(Content(5), '6');
        content = content with { MarketRules = [content.MarketRules[0] with { MaximumLeaseDurationMultiple = 1 }] };
        Accept(original, new CoreCommand.PublishContent(content));
        Assert.AreEqual(lease, Lease(original, lease.Id));
        var saved = WorldFixture.Success(original.Capture(CapturePurpose.Save));
        var factory = CoreFactory.Create();
        var decoded = WorldFixture.Success(factory.DecodeState(saved.Encode()));
        Assert.AreEqual(saved.Checkpoint, decoded.Checkpoint);
        using var candidate = WorldFixture.Success(factory.PrepareRestore(decoded, content, WorldFixture.Options));
        using var restored = (CoreSession)candidate.Activate();
        Assert.AreEqual(lease, Lease(restored, lease.Id));
        Assert.AreEqual(saved.Checkpoint, WorldFixture.Success(restored.Capture(CapturePurpose.Save)).Checkpoint);
        while (restored.Read().Position.CompletedSteps < 35) Step(restored);
        Assert.AreEqual(lease.Id, Step(restored).Events.Select(e => e.Event).OfType<CoreEvent.LeaseExpired>().Single().Lease);
        Assert.IsTrue(Lease(restored, lease.Id).Expired);
        Assert.IsFalse(Step(restored).Events.Any(e => e.Event is CoreEvent.LeaseExpired));
    }

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
        var quote = WorldFixture.Success(session.Read().QuoteLease(new LeaseRequest.Renew(first.Id, 3)));
        Assert.IsNull(quote.Total);
        Assert.AreEqual("insufficient_capacity", quote.Blockers.Single().Code);
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
