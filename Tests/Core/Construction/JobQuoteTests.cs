using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Construction.ConstructionFixture;

namespace Train.Core.Tests.Construction;

[TestClass]
public sealed class JobQuoteTests
{
    [TestMethod]
    [DataRow(JobFunding.OwnInventory, false)]
    [DataRow(JobFunding.NpcCommission, false)]
    [DataRow(JobFunding.OwnInventory, true)]
    [DataRow(JobFunding.NpcCommission, true)]
    public void Missing_goods_quote_lists_every_shortage_in_key_order_without_consuming_partial_allocations(JobFunding funding, bool lastOnly)
    {
        GoodId first = new GoodId.Voxel(new("fixture:aa-first"));
        GoodId last = new GoodId.Voxel(new("fixture:zz-last"));
        var baseline = CommissionFixture.Content();
        var content = baseline with
        {
            Goods = [..baseline.Goods, baseline.Goods[0] with { Good = first }, baseline.Goods[0] with { Good = last }],
            Designs = baseline.Designs.Select(d => d.Design != WorldFixture.Vehicle ? d : d with
                { BillOfMaterials = [new(last, 1), new(Material, 2), new(first, 1)], BillOfMaterialsItemCount = 4 }).ToImmutableArray()
        };
        using var original = funding == JobFunding.OwnInventory ? Session(content) : CommissionFixture.Session(content);
        var cargo = ImmutableArray.Create(new GoodsQuantity(Material, funding == JobFunding.OwnInventory ? 2 : 1));
        if (funding == JobFunding.OwnInventory && lastOnly) cargo = cargo.Add(new(first, 1));
        using var session = new CoreSession(original.State with { World = original.State.World with
        {
            Inventories = original.State.World.Inventories.Select(i => i.Id == Cargo ? i with { Goods = cargo }
                : i.Id == Local ? i with { Goods = lastOnly ? [new(Material, 10), new(first, 1)] : [new(Material, 10)] } : i).ToImmutableArray(),
            Trains = original.State.World.Trains.Select(t => t with { Activity = TrainActivity.Servicing,
                Plan = new(false, [new PlanStop.Station(original.State.World.Settlements.Single().Node, 100, [])]),
                NextStopIndex = 0, RemainingDwellSteps = 100,
                CurrentRun = Train.Core.Trading.RunReports.Begin(original.State.Position) }).ToImmutableArray()
        } });
        var request = CommissionFixture.BuildRequest(content, Cargo);
        var before = session.State;
        var quote = WorldFixture.Success(session.Read().GetJobQuote(request, funding));
        Assert.AreEqual(before, session.State);
        Assert.IsNull(quote.TotalPayment);
        var blocker = quote.Blockers.Single();
        Assert.AreEqual("insufficient_goods", blocker.Code);
        Assert.AreEqual("Sources", blocker.Field);
        var expected = lastOnly ? new[] { "voxel:fixture:zz-last" }
            : new[] { "voxel:fixture:aa-first", "voxel:fixture:zz-last" };
        CollectionAssert.AreEqual(expected, blocker.RelatedIdentities.ToArray());
        CollectionAssert.AreEqual((lastOnly ? new[] { new GoodsQuantity(last, 1) }
            : new[] { new GoodsQuantity(first, 1), new GoodsQuantity(last, 1) }), quote.Shortages.ToArray());
        Assert.AreEqual(Cargo, quote.SourceConsumption.Single().Inventory);
        if (funding == JobFunding.NpcCommission)
        {
            Assert.IsTrue(quote.NpcPurchases.Any(p => p.Good == Material && p.Quantity == 1));
            Assert.IsNull(quote.ServicePayment);
        }
        else
        {
            Assert.IsEmpty(quote.NpcPurchases);
            Assert.AreEqual(new Money(0), quote.MaterialPayment);
            Assert.AreEqual(new Money(0), quote.ServicePayment);
        }
        CoreCommand command = funding == JobFunding.OwnInventory ? new CoreCommand.StartOwnedJob(request)
            : new CoreCommand.CommissionVehicleWork(request, new(long.MaxValue));
        var result = Submit(session, command);
        var rejected = (CommandDecision.Rejected)result.Receipt.Decision;
        Assert.AreEqual(blocker.Code, rejected.Problems.Single().Code);
        Assert.AreEqual(blocker.Field, rejected.Problems.Single().Field);
        CollectionAssert.AreEqual(expected, rejected.Problems.Single().RelatedIdentities.ToArray());
        Assert.AreEqual(before.World, session.State.World);
        Assert.IsEmpty(result.Events);
    }

    [TestMethod]
    [DataRow(JobFunding.OwnInventory)]
    [DataRow(JobFunding.NpcCommission)]
    public void Partial_purchase_reports_remaining_quantity_and_rejects_atomically(JobFunding funding)
    {
        var content = CommissionFixture.Content(stock: 1);
        using var session = funding == JobFunding.OwnInventory ? Session(content) : CommissionFixture.Session(content);
        var request = CommissionFixture.BuildRequest(content, funding == JobFunding.OwnInventory ? [Local] : []);
        var before = session.State;
        var quote = WorldFixture.Success(session.Read().GetJobQuote(request, funding));
        Assert.AreEqual(before, session.State);
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 1) }, quote.Shortages.ToArray());
        Assert.AreEqual("insufficient_goods", quote.Blockers.Single().Code);
        if (funding == JobFunding.NpcCommission)
        {
            CollectionAssert.AreEqual(new[] { new JobPurchaseView(Material, 1, new(100)) }, quote.NpcPurchases.ToArray());
            Assert.IsNull(quote.MaterialPayment);
            Assert.IsNull(quote.ServicePayment);
        }
        else Assert.AreEqual(new Money(0), quote.MaterialPayment);
        Assert.IsNull(quote.TotalPayment);
        CoreCommand command = funding == JobFunding.OwnInventory ? new CoreCommand.StartOwnedJob(request)
            : new CoreCommand.CommissionVehicleWork(request, new(long.MaxValue));
        Reject(session, command, "Sources", "insufficient_goods");
    }

    [TestMethod]
    [DataRow(JobFunding.OwnInventory, false)]
    [DataRow(JobFunding.NpcCommission, false)]
    [DataRow(JobFunding.OwnInventory, true)]
    [DataRow(JobFunding.NpcCommission, true)]
    public void Quote_reports_only_intact_construction_work_rate(JobFunding funding, bool destroyed)
    {
        var baseline = CommissionFixture.Content(rate: 37);
        var content = baseline with
        {
            Designs = baseline.Designs.Select(design => design.Design != WorldFixture.Station ? design : design with
            { Parts = [design.Parts[0], design.Parts[0] with { Id = new(1) }] }).ToImmutableArray()
        };
        using var original = funding == JobFunding.OwnInventory ? Session(content) : CommissionFixture.Session(content);
        using var session = new CoreSession(original.State with { World = original.State.World with
        {
            Instances = original.State.World.Instances.Select(i => i.Id is not InstanceId.Building || !destroyed ? i
                : i with { Parts = i.Parts.Select(p => p.Id == new PartId(1) ? p with { Current = 0 } : p).ToImmutableArray() }).ToImmutableArray()
        } });
        var request = CommissionFixture.BuildRequest(content, funding == JobFunding.OwnInventory ? [Local] : []);
        var quote = WorldFixture.Success(session.Read().GetJobQuote(request, funding));
        Assert.AreEqual(destroyed ? 37d : 74d, quote.CapabilityWorkPerSecond);
        Assert.IsEmpty(quote.Blockers);
    }

    [TestMethod]
    public void Too_small_plot_quote_and_command_reject_with_plot_condition_without_mutation()
    {
        var baseline = Content();
        var target = baseline.Designs.Single(design => design.Design == WorldFixture.Vehicle) with
        { Design = new(new string('7', 64)), Kind = new DesignKind.Building(PlotSize.Medium, BuildingKind.Production, []) };
        var content = baseline with
        {
            Designs = baseline.Designs.Select(design => design with
            { Parts = design.Parts.Select(part => part with
                { Capabilities = part.Capabilities.Select(capability => capability is PartCapability.Construction construction
                    ? construction with { MaximumBuildingPlotSize = PlotSize.Giant } : capability).ToImmutableArray() }).ToImmutableArray() }).Append(target).ToImmutableArray(),
            SettlementTemplates = [baseline.SettlementTemplates[0] with
                { Plots = [..baseline.SettlementTemplates[0].Plots, new(2, PlotSize.Small, null)] }]
        };
        using var session = Session(content);
        var plot = session.State.World.Settlements.Single().Plots.Single(plot => plot.Building is null);
        var request = new JobRequest.BuildBuilding(Facility, plot.Id, new(content.Identity, target, []), [Local], 0);
        var before = session.State;

        var quote = WorldFixture.Success(session.Read().GetJobQuote(request, JobFunding.OwnInventory));

        Assert.AreEqual(before, session.State);
        var problem = quote.Blockers.Single();
        Assert.AreEqual("conditions_not_met", problem.Code);
        Assert.AreEqual("Plot", problem.Field);
        Reject(session, new CoreCommand.StartOwnedJob(request), "Plot", "conditions_not_met");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Plot_quote_and_command_reject_the_same_target_without_mutation(bool occupied)
    {
        var content = Content();
        using var session = Session(content);
        var plot = occupied ? session.State.World.Settlements.Single().Plots.Single(p => p.Building is not null).Id : new PlotId(999);
        var request = new JobRequest.BuildBuilding(Facility, plot, Design(content), [Local], 0);
        var before = session.State;
        var quote = WorldFixture.Success(session.Read().GetJobQuote(request, JobFunding.OwnInventory));
        Assert.AreEqual(before, session.State);
        Assert.AreEqual(0d, quote.CapabilityWorkPerSecond);
        Assert.IsEmpty(quote.Shortages);
        var problem = quote.Blockers.Single();
        Assert.AreEqual(occupied ? "plot_occupied" : "object_missing", problem.Code);
        Reject(session, new CoreCommand.StartOwnedJob(request), "Plot", problem.Code);
    }
}
