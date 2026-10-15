using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Settlements;
using static Train.Core.Tests.Settlements.SettlementFixture;

namespace Train.Core.Tests.Settlements;

[TestClass]
public sealed class SettlementResourcesTests
{
    [TestMethod]
    public void Shuffled_facilities_resolve_by_priority_then_building_then_capability()
    {
        var state = State([new(Fuel, 1)], power: 60);
        FacilityRequest[] requests = [Work(1, priority: 1, rate: 30), Work(2),
            Work(1, CapabilityKind.Construction), Work(1, CapabilityKind.Extraction)];
        foreach (var order in new[] { requests, requests.Reverse().ToArray(), [requests[2], requests[0], requests[3], requests[1]] })
        {
            var result = Resolve(state, order.ToImmutableArray(), labor: 90);
            CollectionAssert.AreEqual(new FacilityKey[] { new(new(1), CapabilityKind.Extraction),
                new(new(1), CapabilityKind.Construction), new(new(2), CapabilityKind.Production),
                new(new(1), CapabilityKind.Production) }, result.Work.Select(w => w.Facility).ToArray());
            CollectionAssert.AreEqual(new double[] { 60, 30, 0, 0 }, result.Work.Select(w => w.WorkPerSecond).ToArray());
            Assert.AreEqual(.5, Remainder(result.World), 1e-12);
            Assert.AreEqual(0L, Stock(result.World, Fuel));
        }
        Assert.AreEqual(1L, Stock(state.World, Fuel));
        Assert.AreEqual(0, Remainder(state.World));
    }

    [TestMethod]
    public void Power_limited_work_does_not_hold_labor_it_cannot_use()
    {
        var result = Resolve(State([new(Fuel, 1)]), [Work(power: 2), Work(2, power: 0)], labor: 90);
        Assert.AreEqual(30, result.Work[0].WorkPerSecond, 1e-12);
        Assert.AreEqual(60, result.Work[1].WorkPerSecond, 1e-12);
        Assert.AreEqual(1, Remainder(result.World), 1e-12);
    }

    [TestMethod]
    public void Labor_limited_work_leaves_power_for_a_labor_free_request()
    {
        var result = Resolve(State([new(Fuel, 1)]), [Work(labor: 2), Work(2, labor: 0)], labor: 10);
        Assert.AreEqual(5, result.Work[0].WorkPerSecond, 1e-12);
        Assert.AreEqual(55, result.Work[1].WorkPerSecond, 1e-12);
        Assert.AreEqual(1, Remainder(result.World), 1e-12);
    }

    [TestMethod]
    public void Zero_coefficients_are_unlimited_and_caller_caps_useful_work()
    {
        var state = State();
        var result = Resolve(state, [Work(rate: 123, power: 0, labor: 0), Work(2, power: 0, labor: 2)], labor: 10);
        Assert.AreEqual(123, result.Work[0].WorkPerSecond);
        Assert.AreEqual(5, result.Work[1].WorkPerSecond);
        result = Resolve(State([new(Fuel, 1)]), [Work(rate: 15), Work(2)], labor: 100);
        Assert.AreEqual(15, result.Work[0].WorkPerSecond);
        Assert.AreEqual(45, result.Work[1].WorkPerSecond, 1e-12);
    }

    [TestMethod]
    public void Basic_then_facilities_then_improvement_share_the_same_goods()
    {
        var result = Resolve(State([new(Fuel, 4)]), [Work(2, priority: 1), Batch(1, 0, new GoodsQuantity(Fuel, 1))],
            basic: [new(Fuel, 1)], improvement: [new(Fuel, 2)]);
        CollectionAssert.AreEqual(new long[] { 1 }, result.BasicGrantedUnits.ToArray());
        Assert.HasCount(1, result.AcceptedBatches);
        Assert.AreEqual(60, result.Work.Single().WorkPerSecond);
        CollectionAssert.AreEqual(new long[] { 1 }, result.ImprovementGrantedUnits.ToArray());
        Assert.AreEqual(0L, Stock(result.World, Fuel));
        Assert.AreEqual(1, Remainder(result.World));
    }

    [TestMethod]
    public void Basic_consumption_takes_the_only_unit_before_a_batch_can_claim_it()
    {
        var result = Resolve(State([new(Material, 1)]), [Batch(1, 0, new GoodsQuantity(Material, 1))],
            basic: [new(Material, 1)]);
        Assert.AreEqual(1L, result.BasicGrantedUnits.Single());
        Assert.IsEmpty(result.AcceptedBatches);
        Assert.AreEqual(0L, Stock(result.World, Material));
    }

    [TestMethod]
    public void Consumption_is_partial_and_preserves_the_given_order()
    {
        var result = Resolve(State([new(Material, 3)]), [], basic: [new(Material, 2), new(Material, 2)],
            improvement: [new(Material, 1)]);
        CollectionAssert.AreEqual(new long[] { 2, 1 }, result.BasicGrantedUnits.ToArray());
        CollectionAssert.AreEqual(new long[] { 0 }, result.ImprovementGrantedUnits.ToArray());
    }

    [TestMethod]
    public void Rejected_batch_keeps_all_inputs_available_for_later_requests()
    {
        var result = Resolve(State([new(Material, 1), new(Fuel, 1)]),
            [Batch(1, 0, new(Fuel, 1), new(Material, 2)), Work(2, priority: 1)], improvement: [new(Material, 1)]);
        Assert.IsEmpty(result.AcceptedBatches);
        Assert.AreEqual(60, result.Work.Single().WorkPerSecond);
        Assert.AreEqual(1L, result.ImprovementGrantedUnits.Single());
    }

    [TestMethod]
    [DataRow(-1, 60, 0)]
    [DataRow(1, 0, 1)]
    public void Fuel_for_power_competes_with_batch_inputs_in_one_facility_pass(int workPriority, int rate, int batches)
    {
        var result = Resolve(State([new(Fuel, 1)]), [Batch(1, 0, new GoodsQuantity(Fuel, 1)), Work(2, priority: workPriority)]);
        Assert.AreEqual((double)rate, result.Work.Single().WorkPerSecond);
        Assert.HasCount(batches, result.AcceptedBatches);
        Assert.AreEqual(0L, Stock(result.World, Fuel));
    }

    [TestMethod]
    public void Idle_generation_costs_nothing_and_one_whole_unit_is_shared_across_steps()
    {
        var state = State([new(Fuel, 1)]);
        var idle = Resolve(state, []);
        Assert.AreEqual(1L, Stock(idle.World, Fuel));
        Assert.AreEqual(0, Remainder(idle.World));
        for (var step = 0; step < 5; step++)
        {
            var result = Resolve(state, [Work(rate: 30)]);
            Assert.AreEqual(step < 4 ? 30d : 0d, result.Work.Single().WorkPerSecond);
            Assert.AreEqual(step < 4 ? 1.5 - step * .5 : 0, Remainder(result.World), 1e-12);
            Assert.AreEqual(0L, Stock(result.World, Fuel));
            state = state with { World = result.World };
        }
    }

    [TestMethod]
    public void Multiple_fuels_use_ordinal_voxel_ids_and_only_take_needed_whole_units()
    {
        var state = State([new(OtherFuel, 2), new(Fuel, 1)], power: 150);
        state = state with { Content = state.Content with { Goods = state.Content.Goods.Reverse().ToImmutableArray() } };
        var result = Resolve(state, [Work(rate: 270, labor: 0)]);
        Assert.AreEqual(270, result.Work.Single().WorkPerSecond);
        Assert.AreEqual(0L, Stock(result.World, Fuel));
        Assert.AreEqual(1L, Stock(result.World, OtherFuel));
        Assert.AreEqual(.5, Remainder(result.World), 1e-12);
    }

    [TestMethod]
    public void Train_and_lease_fuel_are_neither_supplied_nor_consumed()
    {
        var state = State();
        var world = state.World;
        var leaseInventory = world.Next.Inventory;
        world = world with
        {
            Inventories = world.Inventories.Select(i => i.Owner is InventoryOwner.Train ? i with
                { Goods = [new(Fuel, 10)] } : i).Append(new(new(leaseInventory), new InventoryOwner.Lease(new(1)), [new(Fuel, 20)], [])).ToImmutableArray(),
            Leases = [new(new(1), world.Settlements.Single().Id, new(leaseInventory), 20, 10, new(0))],
            Next = world.Next with { Inventory = leaseInventory + 1, Lease = 2 }
        };
        var result = Resolve(state with { World = world }, [Work()], basic: [new(Fuel, 1)], improvement: [new(Fuel, 1)]);
        Assert.AreEqual(0, result.Work.Single().WorkPerSecond);
        Assert.AreEqual(0L, result.BasicGrantedUnits.Single());
        Assert.AreEqual(0L, result.ImprovementGrantedUnits.Single());
        CollectionAssert.AreEqual(world.Inventories.Where(i => i.Owner is not InventoryOwner.Settlement).ToArray(),
            result.World.Inventories.Where(i => i.Owner is not InventoryOwner.Settlement).ToArray());
    }

    [TestMethod]
    public void Missing_voxels_and_fuel_valued_composites_cannot_supply_power()
    {
        var state = State([new(Fuel, 1), new(OtherFuel, 1)]);
        var composite = new GoodId.Composite(new("fixture:composite"));
        var content = state.Content with
        {
            Goods = state.Content.Goods.Select(g => g.Good == OtherFuel ? g with { Good = composite } : g).ToImmutableArray(),
            MissingReferences = [new(new CoreDefinitionReference.Voxel(((GoodId.Voxel)Fuel).Definition), "fixture")]
        };
        var world = state.World with { Inventories = state.World.Inventories.Select(i => i with
            { Goods = i.Goods.Select(g => g.Good == OtherFuel ? g with { Good = composite } : g).ToImmutableArray() }).ToImmutableArray() };
        var result = Resolve(state with { World = world, Content = content }, [Work()]);
        Assert.AreEqual(0, result.Work.Single().WorkPerSecond);
        Assert.AreEqual(1L, Stock(result.World, Fuel));
        Assert.AreEqual(1L, Stock(result.World, composite));
    }
}
