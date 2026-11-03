using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using static Train.Core.Tests.Production.ProductionFixture;

namespace Train.Core.Tests.Production;

[TestClass]
public sealed class TodayProductionTests
{
    [TestMethod]
    public void Completed_batches_count_buffered_outputs_once_and_keep_old_views()
    {
        using var session = Owned(Content(capacity: 10, work: 1));
        var old = session.Read();
        Step(session, 2);
        Assert.AreEqual(3L, Today(session));
        Assert.IsTrue(Read(session).OutputBuffer.Sum(g => g.Quantity) > 0);
        var completed = session.Read();
        Step(session);
        Assert.AreEqual(3L, Today(session));
        Publish(session, Changed(Content(capacity: 100, work: 1)));
        Step(session);
        Assert.IsEmpty(Read(session).OutputBuffer);
        Assert.AreEqual(3L, Today(session));
        Assert.IsEmpty(((Lookup<SettlementView>.Found)old.FindSettlement(Settlement)).Value.TodayProduction);
        Assert.AreEqual(3L, ((Lookup<SettlementView>.Found)completed.FindSettlement(Settlement)).Value.TodayProduction.Single().Quantity);
    }

    [TestMethod]
    public void Other_settlements_hide_production_and_idle_owned_settlements_reset_at_midnight()
    {
        using var other = Owned(Content(work: 1), SettlementRelation.NonHostile);
        Step(other, 2);
        Assert.IsEmpty(((Lookup<SettlementView>.Found)other.Read().FindSettlement(Settlement)).Value.TodayProduction);
        using var initial = Owned(Content(work: 1));
        using var session = new CoreSession(initial.State with { Position = new(5_183_996, 0) });
        Step(session, 2);
        Assert.AreEqual(3L, Today(session));
        Inventory.InventoryFixture.Accept(session, new CoreCommand.SetProductionPlan(Facility, Process, false, 0));
        Step(session, 2);
        Assert.IsEmpty(((Lookup<SettlementView>.Found)session.Read().FindSettlement(Settlement)).Value.TodayProduction);
    }

    [TestMethod]
    public void Midnight_clears_the_previous_day_before_its_completing_batch_and_restore_keeps_the_new_day()
    {
        using var initial = Owned(Content(work: 1));
        using var session = new CoreSession(initial.State with { Position = new(5_183_996, 0) });
        Step(session, 3);
        Assert.AreEqual(3L, Today(session));
        Step(session);
        Assert.AreEqual(3L, Today(session));
        var capture = Train.Core.Tests.World.WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var decoded = Train.Core.Tests.World.WorldFixture.Success(CoreFactory.Create().DecodeState(capture.Encode()));
        using var candidate = Train.Core.Tests.World.WorldFixture.Success(CoreFactory.Create().PrepareRestore(decoded, session.State.Content,
            Train.Core.Tests.World.WorldFixture.Options));
        using var restored = candidate.Activate();
        Assert.AreEqual(3L, Today(restored));
        Step(restored);
        Assert.AreEqual(3L, Today(restored));
    }

    private static long Today(ICoreSession session)
        => ((Lookup<SettlementView>.Found)session.Read().FindSettlement(Settlement)).Value.TodayProduction.Single().Quantity;
}
