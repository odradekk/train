using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Construction.ConstructionFixture;

namespace Train.Core.Tests.Construction;

[TestClass]
public sealed class CommissionTests
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
            Assert.AreEqual(quote.MaterialPayment, started.MaterialPayment);
            Assert.AreEqual(quote.ServicePayment, started.ServicePayment);
            Assert.AreEqual(quote.TotalWork, started.Job.FrozenTotalWork);
            CollectionAssert.AreEqual(quote.RequiredGoods.ToArray(), started.Job.InvestedGoods.ToArray());
        }
    }

    [TestMethod]
    public void Ordered_player_sources_reduce_each_goods_marginal_purchase_and_only_wages_are_service_expense()
    {
        GoodId food = new GoodId.Voxel(new("fixture:food"));
        GoodId clothing = new GoodId.Voxel(new("fixture:clothing"));
        var content = CommissionFixture.Content() with
        {
            Designs = CommissionFixture.Content().Designs.Select(d => d.Design != WorldFixture.Vehicle ? d : d with
                { BillOfMaterials = [new(food, 3), new(clothing, 1)], BillOfMaterialsItemCount = 4 }).ToImmutableArray(),
            MarketRules = [CommissionFixture.Content().MarketRules[0] with
            {
                BasicReserveHours = 1d / 3600,
                DefaultGoodsPrices = new(new(5000), new(5000), new(20000), new(1000)),
                RentableCapacityRatio = new(10000)
            }]
        };
        using var original = CommissionFixture.Session(content, cargo: [new(food, 1)]);
        var settlement = original.State.World.Settlements.Single();
        var acquired = Accept(original, new CoreCommand.AcquireLease(new LeaseRequest.Rent(settlement.Id, 10, 1), new(0)))
            .Events.Select(e => e.Event).OfType<CoreEvent.LeaseAcquired>().Single();
        using var session = new CoreSession(original.State with { World = original.State.World with
        {
            Inventories = original.State.World.Inventories.Select(i => i.Id == Local
                ? i with { Goods = [new(food, 13), new(clothing, 13)] }
                : i.Id == acquired.Lease.Inventory ? i with { Goods = [new(food, 1)] } : i).ToImmutableArray(),
            Trains = original.State.World.Trains.Select(t => t with { Activity = TrainActivity.Servicing,
                Plan = new(false, [new PlanStop.Station(settlement.Node, 100, [])]),
                NextStopIndex = 0, RemainingDwellSteps = 100,
                CurrentRun = Train.Core.Trading.RunReports.Begin(original.State.Position) }).ToImmutableArray()
        } });
        var request = CommissionFixture.BuildRequest(content, Cargo, acquired.Lease.Inventory);
        var quote = WorldFixture.Success(session.Read().GetJobQuote(request, JobFunding.NpcCommission));
        Assert.IsEmpty(quote.Blockers);
        CollectionAssert.AreEqual(new[] { Cargo, acquired.Lease.Inventory }, quote.SourceConsumption.Select(s => s.Inventory).ToArray());
        Assert.IsTrue(quote.SourceConsumption.All(s => s.Goods.Single() == new GoodsQuantity(food, 1)));
        // Both goods have target 10 and stock 13: midpoint 12.5 costs ceil(100 × .875 × 1.1) = 97.
        Assert.AreEqual(new Money(194), quote.MaterialPayment);
        Assert.AreEqual(new Money(12), quote.ServicePayment);
        Assert.AreEqual(new Money(206), quote.TotalPayment);
        var before = session.Read().Economy.Wallet;
        var started = Accept(session, new CoreCommand.CommissionVehicleWork(request, new(206)))
            .Events.Select(e => e.Event).OfType<CoreEvent.ConstructionStarted>().Single();
        Assert.AreEqual(new Money(before.Value - 206), session.Read().Economy.Wallet);
        Assert.AreEqual(new Money(12), session.Read().Economy.UnassignedServicePayments);
        var run = ((Lookup<TrainView>.Found)session.Read().FindTrain(new(1))).Value.CurrentRun;
        Assert.IsNotNull(run);
        Assert.AreEqual(new Money(0), run.ServicePayments);
        Assert.AreEqual(0, session.State.World.Inventories.Single(i => i.Id == Cargo).Goods.Length);
        Assert.AreEqual(0, session.State.World.Inventories.Single(i => i.Id == acquired.Lease.Inventory).Goods.Length);
        Assert.AreEqual(12L, session.State.World.Inventories.Single(i => i.Id == Local).Goods.Single(g => g.Good == food).Quantity);
        Assert.AreEqual(started.MaterialPayment, quote.MaterialPayment);
        using var abundant = new CoreSession(session.State with { World = session.State.World with
        {
            Inventories = session.State.World.Inventories.Select(i => i.Id == Cargo || i.Id == acquired.Lease.Inventory
                ? i with { Goods = [new(food, 3)] } : i).ToImmutableArray()
        } });
        var cargoFirst = WorldFixture.Success(abundant.Read().GetJobQuote(request, JobFunding.NpcCommission));
        var leaseFirst = WorldFixture.Success(abundant.Read().GetJobQuote(
            request with { Sources = [acquired.Lease.Inventory, Cargo] }, JobFunding.NpcCommission));
        Assert.AreEqual(Cargo, cargoFirst.SourceConsumption.Single().Inventory);
        Assert.AreEqual(acquired.Lease.Inventory, leaseFirst.SourceConsumption.Single().Inventory);
    }

    [TestMethod]
    public void Multiple_units_use_successive_marginal_prices_instead_of_one_bulk_price()
    {
        GoodId food = new GoodId.Voxel(new("fixture:food"));
        var baseline = CommissionFixture.Content();
        var content = baseline with
        {
            Designs = baseline.Designs.Select(d => d.Design != WorldFixture.Vehicle ? d : d with
                { BillOfMaterials = [new(food, 3)], BillOfMaterialsItemCount = 3 }).ToImmutableArray(),
            MarketRules = [baseline.MarketRules[0] with { BasicReserveHours = 1d / 3600,
                DefaultGoodsPrices = new(new(5000), new(5000), new(20000), new(1000)) }]
        };
        using var original = CommissionFixture.Session(content);
        using var session = new CoreSession(original.State with { World = original.State.World with
        { Inventories = original.State.World.Inventories.Select(i => i.Id != Local ? i : i with
            { Goods = [new(food, 13)] }).ToImmutableArray() } });
        var quote = WorldFixture.Success(session.Read().GetJobQuote(CommissionFixture.BuildRequest(content), JobFunding.NpcCommission));
        Assert.IsEmpty(quote.Blockers);
        Assert.AreEqual(new Money(97 + 102 + 108), quote.MaterialPayment);
        Assert.AreEqual(new Money(9), quote.ServicePayment);
        Accept(session, new CoreCommand.CommissionVehicleWork(quote.Request, new(316)));
        Assert.AreEqual(new Money(10000 - 316), session.Read().Economy.Wallet);
    }

    [TestMethod]
    public void Last_missing_material_and_npc_inventory_as_player_source_reject_atomically()
    {
        var baseline = CommissionFixture.Content();
        GoodId last = new GoodId.Voxel(new("fixture:zz-last"));
        var content = baseline with
        {
            Goods = baseline.Goods.Add(baseline.Goods[0] with { Good = last }),
            Designs = baseline.Designs.Select(d => d.Design != WorldFixture.Vehicle ? d : d with
                { BillOfMaterials = [new(Material, 1), new(last, 1)] }).ToImmutableArray()
        };
        using var session = CommissionFixture.Session(content);
        var request = CommissionFixture.BuildRequest(content);
        var quote = WorldFixture.Success(session.Read().GetJobQuote(request, JobFunding.NpcCommission));
        Assert.IsNull(quote.TotalPayment);
        Assert.AreEqual("insufficient_goods", quote.Blockers[0].Code);
        Reject(session, new CoreCommand.CommissionVehicleWork(request, new(long.MaxValue)), code: "insufficient_goods");
        Reject(session, CommissionFixture.Commission(content, Local), "Sources[0]", "access_denied");
    }

    [TestMethod]
    public void Wallet_and_limit_failures_preserve_computed_quote_and_leave_all_goods_and_jobs_unchanged()
    {
        var content = CommissionFixture.Content();
        using var poor = CommissionFixture.Session(content, money: 205);
        var request = CommissionFixture.BuildRequest(content);
        var quote = WorldFixture.Success(poor.Read().GetJobQuote(request, JobFunding.NpcCommission));
        Assert.AreEqual(new Money(206), quote.TotalPayment);
        Assert.AreEqual("insufficient_money", quote.Blockers.Single().Code);
        Reject(poor, new CoreCommand.CommissionVehicleWork(request, new(206)), "Wallet", "insufficient_money");
        using var rich = CommissionFixture.Session(content);
        Reject(rich, new CoreCommand.CommissionVehicleWork(request, new(205)), "MaximumPayment", "price_condition_failed");
        Accept(rich, new CoreCommand.CommissionVehicleWork(request, new(206)));
    }

    [TestMethod]
    public void Npc_reserved_goods_cannot_be_used_to_complete_the_bom()
    {
        GoodId food = new GoodId.Voxel(new("fixture:food"));
        var baseline = CommissionFixture.Content();
        var content = baseline with
        {
            Designs = baseline.Designs.Select(d => d.Design != WorldFixture.Vehicle ? d : d with
                { BillOfMaterials = [new(Material, 1), new(food, 1)] }).ToImmutableArray(),
            MarketRules = [baseline.MarketRules[0] with { BasicReserveHours = 1d / 3600 }]
        };
        using var original = CommissionFixture.Session(content);
        using var session = new CoreSession(original.State with { World = original.State.World with
        { Inventories = original.State.World.Inventories.Select(i => i.Id != Local ? i : i with
            { Goods = [new(Material, 10), new(food, 10)] }).ToImmutableArray() } });
        var quote = WorldFixture.Success(session.Read().GetJobQuote(CommissionFixture.BuildRequest(content), JobFunding.NpcCommission));
        Assert.IsNull(quote.TotalPayment);
        Assert.AreEqual("insufficient_goods", quote.Blockers[0].Code);
        Reject(session, CommissionFixture.Commission(content), code: "insufficient_goods");
    }

    [TestMethod]
    public void Cargo_source_must_be_at_a_serviced_local_platform()
    {
        var content = CommissionFixture.Content();
        using var session = CommissionFixture.Session(content, cargo: [new(Material, 10)]);
        var request = CommissionFixture.BuildRequest(content, Cargo);
        var quote = WorldFixture.Success(session.Read().GetJobQuote(request, JobFunding.NpcCommission));
        Assert.IsNull(quote.TotalPayment);
        var blocker = quote.Blockers.Single();
        Assert.AreEqual("Sources[0]", blocker.Field);
        Reject(session, new CoreCommand.CommissionVehicleWork(request, new(long.MaxValue)), blocker.Field, blocker.Code);
    }

    [TestMethod]
    public void Queue_is_fifo_even_when_later_priority_is_higher_and_head_is_resource_blocked()
    {
        var content = CommissionFixture.Content(power: 1);
        using var session = CommissionFixture.Session(content);
        var first = Start(session, CommissionFixture.Commission(content));
        var later = Start(session, new CoreCommand.CommissionVehicleWork(
            CommissionFixture.BuildRequest(content) with { Priority = int.MaxValue }, new(long.MaxValue)));
        Assert.AreEqual(ConstructionState.Queued, later.State);
        var stepped = Step(session, 3);
        Assert.AreEqual(ConstructionState.ResourceBlocked, Job(session.Read(), first.Id).State);
        var queued = Job(session.Read(), later.Id);
        Assert.AreEqual(ConstructionState.Queued, queued.State);
        Assert.AreEqual(0d, queued.ProgressWork);
        Assert.AreEqual(0d, queued.ActualWorkPerSecond);
        Assert.IsEmpty(queued.Limits);
        Assert.IsFalse(stepped.Events.Any(e => e.Event is CoreEvent.ConstructionStateChanged change && change.Job == later.Id));
    }

    [TestMethod]
    public void Completing_head_starts_next_job_and_emits_queued_to_working_transition()
    {
        var content = CommissionFixture.Content();
        using var session = CommissionFixture.Session(content);
        var first = Start(session, CommissionFixture.Commission(content));
        var second = Start(session, CommissionFixture.Commission(content));
        var events = Step(session, 5).Events.Select(e => e.Event).ToArray();
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, events.OfType<CoreEvent.ConstructionCompleted>().Select(e => e.Job).ToArray());
        Assert.IsTrue(events.OfType<CoreEvent.ConstructionStateChanged>().Any(e => e.Job == second.Id
            && e.Before == ConstructionState.Queued && e.After == ConstructionState.Working));
    }

    [TestMethod]
    public void Lost_facility_fails_working_and_queued_jobs_without_refunding_materials_or_fees()
    {
        var content = CommissionFixture.Content();
        using var original = CommissionFixture.Session(content);
        var first = Start(original, CommissionFixture.Commission(content));
        var second = Start(original, CommissionFixture.Commission(content));
        using var session = new CoreSession(original.State with { World = original.State.World with
        { Instances = original.State.World.Instances.Select(i => i.Id is not InstanceId.Building ? i : i with
            { Ownership = SettlementRelation.PlayerOwned }).ToImmutableArray() } });
        var wallet = session.Read().Economy.Wallet;
        var stocks = session.State.World.Inventories;
        var failures = Step(session).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionStateChanged>()
            .Where(e => e.After == ConstructionState.Failed).Select(e => e.Job).ToArray();
        CollectionAssert.AreEquivalent(new[] { first.Id, second.Id }, failures);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(first.Id));
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(second.Id));
        Assert.AreEqual(wallet, session.Read().Economy.Wallet);
        foreach (var inventory in stocks)
            CollectionAssert.AreEqual(inventory.Goods.ToArray(),
                session.State.World.Inventories.Single(i => i.Id == inventory.Id).Goods.ToArray());
        Assert.AreEqual(new Money(12), session.Read().Economy.UnassignedServicePayments);
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
        Assert.AreEqual(ConstructionState.Queued, job.State);
        Reject(session, new CoreCommand.CommissionVehicleWork(request, new(long.MaxValue)), "Instance", "instance_locked");
        Assert.AreEqual(InstanceLock.UnderRepair, ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value.Lock);
        Reject(session, new CoreCommand.StartTrain(new(1)), "Train", "instance_locked");
    }

    [TestMethod]
    [DataRow("owned", "invalid_state", "Facility")]
    [DataRow("hostile", "access_denied", "Facility")]
    [DataRow("market", "content_missing", "MarketRule")]
    [DataRow("construction", "content_missing", "ConstructionRule")]
    public void Quote_and_submission_report_the_same_relation_or_rule_failure(string condition, string code, string field)
    {
        var content = CommissionFixture.Content();
        using var original = condition == "owned" ? Session(content) : CommissionFixture.Session(content);
        var state = original.State;
        using var session = new CoreSession(state with
        {
            Content = condition == "market" ? content with { MarketRules = [] }
                : condition == "construction" ? content with { ConstructionRules = [] } : content,
            World = condition == "hostile" ? state.World with
                { Settlements = state.World.Settlements.Select(s => s with { Relation = SettlementRelation.Hostile }).ToImmutableArray() }
                : state.World
        });
        var quote = WorldFixture.Success(session.Read().GetJobQuote(CommissionFixture.BuildRequest(content), JobFunding.NpcCommission));
        Assert.IsNull(quote.TotalPayment);
        Assert.AreEqual(code, quote.Blockers.Single().Code);
        Assert.AreEqual(field, quote.Blockers.Single().Field);
        Reject(session, CommissionFixture.Commission(content), field, code);
    }

    [TestMethod]
    public void Expired_local_lease_can_still_supply_commission_materials()
    {
        var content = CommissionFixture.Content() with
        { MarketRules = [CommissionFixture.Content().MarketRules[0] with { RentableCapacityRatio = new(10000) }] };
        using var original = CommissionFixture.Session(content);
        var settlement = original.State.World.Settlements.Single();
        var lease = Accept(original, new CoreCommand.AcquireLease(new LeaseRequest.Rent(settlement.Id, 2, 1), new(0)))
            .Events.Select(e => e.Event).OfType<CoreEvent.LeaseAcquired>().Single().Lease;
        using var session = new CoreSession(original.State with { World = original.State.World with
        {
            Inventories = original.State.World.Inventories.Select(i => i.Id != lease.Inventory ? i : i with
                { Goods = [new(Material, 2)] }).ToImmutableArray()
        } });
        Step(session);
        Assert.IsTrue(((Lookup<LeaseView>.Found)session.Read().FindLease(lease.Id)).Value.Expired);
        var request = CommissionFixture.BuildRequest(content, lease.Inventory);
        var quote = WorldFixture.Success(session.Read().GetJobQuote(request, JobFunding.NpcCommission));
        Assert.IsEmpty(quote.Blockers);
        Assert.IsEmpty(quote.NpcPurchases);
        Assert.AreEqual(new Money(6), quote.TotalPayment);
        Accept(session, new CoreCommand.CommissionVehicleWork(request, new(6)));
        Assert.IsEmpty(((Lookup<InventoryView>.Found)session.Read().FindInventory(lease.Inventory)).Value.Stacks);
    }

    [TestMethod]
    public void Invalid_commission_forms_do_not_acquire_command_numbers()
    {
        var content = CommissionFixture.Content();
        using var session = CommissionFixture.Session(content);
        var before = session.State;
        Assert.ThrowsExactly<ArgumentException>(() => session.Submit(new CoreCommand.CommissionVehicleWork(
            CommissionFixture.BuildRequest(content), new(-1)), CommandSource.AutomationTest));
        JobRequest[] invalid = [new JobRequest.BuildBuilding(Facility, new(1), Design(content), [], 0),
            new JobRequest.BuildEquipment(Facility, Design(content), [], 0), new JobRequest.Dismantle(Facility, Vehicle, 0)];
        foreach (var request in invalid)
        {
            Assert.ThrowsExactly<ArgumentException>(() => session.Submit(new CoreCommand.CommissionVehicleWork(request, new(0)), CommandSource.AutomationTest));
            Assert.ThrowsExactly<ArgumentException>(() => session.Read().GetJobQuote(request, JobFunding.NpcCommission));
        }
        var refit = new JobRequest.Refit(Facility, Vehicle, null!, [], 0);
        Assert.ThrowsExactly<NotImplementedException>(() => session.Submit(new CoreCommand.CommissionVehicleWork(refit, new(0)), CommandSource.AutomationTest));
        Assert.ThrowsExactly<NotImplementedException>(() => session.Read().GetJobQuote(refit, JobFunding.NpcCommission));
        Assert.AreEqual(before, session.State);
        Assert.IsEmpty(session.ReadRecording().Commands);
    }
}

internal static class CommissionFixture
{
    internal static CoreContentProjection Content(double rate = 60, long stock = 10, double power = 0, double labor = 0)
    {
        var content = ConstructionFixture.Content(rate: rate, stock: stock, power: power, labor: labor);
        return content with
        {
            Goods = content.Goods.Select(g => g with { BaseValue = new(100) }).ToImmutableArray(),
            MarketRules = [content.MarketRules[0] with { CommissionMoneyPerWork = new(3) }]
        };
    }

    internal static CoreSession Session(CoreContentProjection? content = null, bool damaged = false,
        long money = 10000, ImmutableArray<GoodsQuantity> cargo = default)
    {
        using var initial = ConstructionFixture.Session(content ?? Content(), damaged);
        var state = initial.State;
        var settlement = state.World.Settlements.Single();
        return new(state with { World = state.World with
        {
            Money = new(money),
            Settlements = [settlement with { Relation = SettlementRelation.NonHostile }],
            Nodes = state.World.Nodes.Select(n => n.Id != settlement.Node ? n : n with
                { Occupant = new NodeOccupant.Settlement(settlement.Id, SettlementRelation.NonHostile) }).ToImmutableArray(),
            Instances = state.World.Instances.Select(i => i.Id is not InstanceId.Building ? i : i with
                { Ownership = SettlementRelation.NonHostile }).ToImmutableArray(),
            Inventories = state.World.Inventories.Select(i => i.Id != Cargo ? i : i with
                { Goods = cargo.IsDefault ? [] : cargo }).ToImmutableArray()
        } });
    }

    internal static JobRequest.BuildVehicle BuildRequest(CoreContentProjection content, params InventoryId[] sources)
        => new(Facility, Design(content), [..sources], 0);
    internal static CoreCommand Commission(CoreContentProjection content, params InventoryId[] sources)
        => new CoreCommand.CommissionVehicleWork(BuildRequest(content, sources), new(long.MaxValue));
}
