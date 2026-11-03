using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Construction.ConstructionFixture;

namespace Train.Core.Tests.Construction;

public sealed partial class CommissionTests
{
    [TestMethod]
    public void Both_funding_quotes_match_the_accepted_job_and_quote_does_not_mutate_state()
    {
        var content = CommissionFixture.Content();
        foreach (var funding in Enum.GetValues<JobFunding>())
        {
            using var session = funding == JobFunding.OwnInventory ? Session(content) : CommissionFixture.Session(content);
            var request = CommissionFixture.BuildRequest(content, funding == JobFunding.OwnInventory ? [Local] : []);
            var before = session.State;
            var quote = WorldFixture.Success(session.Read().GetJobQuote(request, funding));
            Assert.AreEqual(before, session.State);
            Assert.IsEmpty(quote.Blockers);
            Assert.AreEqual(2L, quote.TotalWork);
            Assert.AreEqual(new Money(funding == JobFunding.OwnInventory ? 0 : 200), quote.MaterialPayment);
            Assert.AreEqual(new Money(funding == JobFunding.OwnInventory ? 0 : 6), quote.ServicePayment);
            if (funding == JobFunding.OwnInventory) Assert.IsEmpty(quote.NpcPurchases);
            var command = funding == JobFunding.OwnInventory
                ? (CoreCommand)new CoreCommand.StartOwnedJob(request)
                : new CoreCommand.CommissionVehicleWork(request, quote.TotalPayment!.Value);
            var started = Accept(session, command).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionStarted>().Single();
            Assert.AreEqual(funding, started.Funding);
            if (funding == JobFunding.OwnInventory) Assert.IsEmpty(session.Read().Economy.CashFlows);
            else Assert.AreEqual(new CashFlowEntry(session.Read().Position, CashFlowKind.Construction, -206, started.Job.Settlement, null, null, 0), session.Read().Economy.CashFlows.Single());
            Assert.AreEqual(quote.MaterialPayment, started.MaterialPayment);
            Assert.AreEqual(quote.ServicePayment, started.ServicePayment);
            Assert.AreEqual(quote.TotalWork, started.Job.FrozenTotalWork);
            CollectionAssert.AreEqual(quote.RequiredGoods.ToArray(), started.Job.InvestedGoods.ToArray());
        }
    }

    [TestMethod]
    public void Commission_repair_locks_the_player_vehicle_and_rejects_a_second_repair()
    {
        var content = CommissionFixture.Content();
        using var session = CommissionFixture.Session(content, damaged: true);
        Start(session, CommissionFixture.Commission(content));
        var request = new JobRequest.Repair(Facility, Vehicle, [], 0);
        var quote = WorldFixture.Success(session.Read().GetJobQuote(request, JobFunding.NpcCommission));
        Assert.IsEmpty(quote.Blockers);
        var job = Start(session, new CoreCommand.CommissionVehicleWork(request, quote.TotalPayment!.Value));
        Assert.AreEqual(ConstructionKind.Repair, job.Kind);
        var flow = session.Read().Economy.CashFlows.Last();
        Assert.AreEqual(CashFlowKind.Repair, flow.Kind);
        Assert.AreEqual(-quote.TotalPayment!.Value.Value, flow.Amount);
        Assert.AreEqual(new TrainId(1), flow.Train);
        Assert.AreEqual(ConstructionState.Queued, job.State);
        Reject(session, new CoreCommand.CommissionVehicleWork(request, new(long.MaxValue)), "Instance", "instance_locked");
        Assert.AreEqual(InstanceLock.UnderRepair, ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value.Lock);
        Reject(session, new CoreCommand.StartTrain(new(1)), "Train", "instance_locked");
    }

}
