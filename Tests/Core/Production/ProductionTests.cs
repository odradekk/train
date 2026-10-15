using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Production;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Production.ProductionFixture;

namespace Train.Core.Tests.Production;

[TestClass]
public sealed class ProductionTests
{
    [TestMethod]
    public void Incomplete_batch_keeps_all_inputs_and_waits()
    {
        using var candidate = Prepare(Content(stock: [new(Input, 7), new(OtherProduct, 1)],
            inputs: [new(Input, 2), new(OtherProduct, 2)]));
        using var session = candidate.Activate();
        Step(session, 3);
        Assert.AreEqual(7L, Stock(session, Input));
        Assert.AreEqual(1L, Stock(session, OtherProduct));
        Assert.AreEqual(ProductionState.WaitingForInputs, Read(session).State);
        Assert.IsNull(Read(session).Batch);
    }

    [TestMethod]
    public void Starting_consumes_once_and_work_begins_on_next_step()
    {
        using var candidate = Prepare();
        using var session = candidate.Activate();
        var old = session.Read();
        Step(session);
        Assert.AreEqual(ProductionState.Running, Read(session).State);
        Assert.AreEqual(8L, Stock(session, Input));
        Assert.AreEqual(0d, Read(session).Batch!.ProgressWork);
        Assert.AreEqual(0d, Read(session).ActualWorkPerSecond);
        Step(session);
        Assert.AreEqual(8L, Stock(session, Input));
        Assert.AreEqual(1d, Read(session).Batch!.ProgressWork, 1e-12);
        var working = session.Read();
        var finished = Step(session);
        Assert.IsNull(Read(session).Batch);
        Assert.AreEqual(3L, Stock(session, Product));
        Assert.AreEqual(8L, Stock(session, Input));
        Assert.AreEqual(1, finished.Events.Count(e => e.Event is CoreEvent.ProductionCompleted));
        Assert.AreEqual(1d, Read(working).Batch!.ProgressWork, 1e-12);
        Assert.IsNull(Read(old).Batch);
        Assert.AreEqual(10L, ((Lookup<InventoryView>.Found)old.FindInventory(LocalInventory)).Value.Stacks.Single().Quantity);
        Step(session);
        Assert.AreEqual(6L, Stock(session, Input));
    }

    [TestMethod]
    public void Power_and_fuel_limit_fractional_work_and_newly_usable_inventory_fuel_resumes_batch()
    {
        var content = Content(stock: [new(Input, 2), new(Fuel, 1), new(OtherProduct, 1)], work: 2, rate: 120,
            powerPerWork: 1, power: 30);
        using var candidate = Prepare(content);
        using var session = candidate.Activate();
        Step(session);
        Step(session);
        Assert.AreEqual(.5, Read(session).Batch!.ProgressWork, 1e-12);
        Assert.AreEqual(30d, Read(session).ActualWorkPerSecond, 1e-12);
        CollectionAssert.Contains(Read(session).Limits.ToArray(), WorkLimit.Power);
        Step(session, 3);
        Assert.AreEqual(1d, Read(session).Batch!.ProgressWork, 1e-12);
        Assert.AreEqual(0d, Read(session).ActualWorkPerSecond);
        Assert.AreEqual(0L, Stock(session, Input));

        Publish(session, Changed(content) with { Goods = content.Goods.Select(g => g.Good == OtherProduct
            ? g with { FuelEnergyPerUnit = 1 } : g).ToImmutableArray() });
        Step(session, 2);
        Assert.IsNull(Read(session).Batch);
        Assert.AreEqual(3L, Stock(session, Product));
        Assert.AreEqual(0L, Stock(session, Input));

    }

    [TestMethod]
    public void Labor_limits_work_and_releasing_the_limit_preserves_progress()
    {
        var state = State(Content(work: 2, rate: 120, laborPerWork: 2));
        var start = ProductionOperations.Advance(state.World, state.Content, Settlement, 30, 1).World;
        var limited = ProductionOperations.Advance(start, state.Content, Settlement, 30, 2).World;
        var batch = limited.Settlements.Single().ProductionFacilities.Single().Batch!;
        Assert.AreEqual(.25, batch.ProgressWork, 1e-12);
        Assert.AreEqual(15d, limited.Settlements.Single().ProductionFacilities.Single().LastActualWorkPerSecond, 1e-12);
        CollectionAssert.Contains(limited.Settlements.Single().ProductionFacilities.Single().LastLimits.ToArray(), WorkLimit.Labor);
        var paused = ProductionOperations.Advance(limited, state.Content, Settlement, 0, 3).World;
        Assert.AreEqual(.25, paused.Settlements.Single().ProductionFacilities.Single().Batch!.ProgressWork, 1e-12);
        var finished = ProductionOperations.Advance(paused, state.Content, Settlement, 240, 4).World;
        Assert.IsNull(finished.Settlements.Single().ProductionFacilities.Single().Batch);
        Assert.AreEqual(3L, finished.Inventories.Single(i => i.Id == LocalInventory).Goods.Single(g => g.Good == Product).Quantity);
    }

    [TestMethod]
    public void Publishing_new_specification_finishes_frozen_batch_then_starts_current_specification()
    {
        var content = Content();
        using var candidate = Prepare(content);
        using var session = candidate.Activate();
        Step(session);
        var updated = Changed(content) with { Processes = [content.Processes[0] with
        {
            Revision = new(new string('A', 64)), WorkRequired = 4,
            Details = new ProcessDetails.Production(new("fixture:production"), [new(Input, 3)], [new(Product, 7)])
        }] };
        Publish(session, updated);
        Assert.IsTrue(Read(session).Batch!.CurrentSpecificationDiffers);
        Assert.AreEqual(2L, Read(session).Batch!.FrozenWorkRequired);
        Step(session, 2);
        Assert.AreEqual(3L, Stock(session, Product));
        Step(session);
        Assert.AreEqual(5L, Stock(session, Input));
        Assert.AreEqual(4L, Read(session).Batch!.FrozenWorkRequired);
        Assert.AreEqual(updated.Processes[0].Revision, Read(session).Batch!.StartedFromRevision);
        Step(session, 4);
        Assert.AreEqual(10L, Stock(session, Product));
    }

    [TestMethod]
    public void Recovery_uses_the_same_batch_work_and_output_buffer()
    {
        using var candidate = Prepare(Content(stock: [new(Input, 2)], work: 1, capacity: 1, recovery: true));
        using var session = candidate.Activate();
        Step(session);
        Assert.AreEqual(Recovery, Read(session).Batch!.Process);
        Assert.AreEqual(0L, Stock(session, Input));
        var completed = Step(session);
        Assert.AreEqual(1L, Stock(session, Product));
        Assert.AreEqual(new GoodsQuantity(Product, 2), Read(session).OutputBuffer.Single());
        Assert.AreEqual(ProductionState.OutputBlocked, Read(session).State);
        var change = completed.Events.Select(e => e.Event).OfType<CoreEvent.ProductionStateChanged>().Single();
        Assert.AreEqual(ProductionState.Running, change.Before);
        Assert.AreEqual(ProductionState.OutputBlocked, change.After);
    }

    [TestMethod]
    public void Active_batch_keeps_frozen_power_and_labor_intensities()
    {
        var content = Content(stock: [new(Input, 4), new(Fuel, 2)], powerPerWork: 1);
        using var candidate = Prepare(content);
        using var session = candidate.Activate();
        Step(session, 2);
        Assert.AreEqual(1d, Read(session).Batch!.ProgressWork, 1e-12);
        Publish(session, Changed(content) with { Processes = [content.Processes[0] with
        { Revision = new(new string('A', 64)), PowerPerWork = 2, LaborPerWork = 1 }] });
        Step(session);
        Assert.AreEqual(3L, Stock(session, Product));
        Assert.IsNull(Read(session).Batch);
        Step(session, 2);
        Assert.AreEqual(0d, Read(session).Batch!.ProgressWork);
        Assert.AreEqual(0d, Read(session).ActualWorkPerSecond);
        CollectionAssert.Contains(Read(session).Limits.ToArray(), WorkLimit.Labor);
    }

    [TestMethod]
    public void Multiple_outputs_fill_storage_in_ordinal_order_and_draining_buffer_does_not_restart()
    {
        var content = Content(stock: [new(Input, 4)], work: 1, capacity: 4,
            outputs: [new(OtherProduct, 3), new(Product, 3)]);
        using var candidate = Prepare(content);
        using var session = candidate.Activate();
        Step(session, 2);
        Assert.AreEqual(2L, Stock(session, Product));
        Assert.AreEqual(0L, Stock(session, OtherProduct));
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Product, 1), new GoodsQuantity(OtherProduct, 3) }, Read(session).OutputBuffer.ToArray());
        var expanded = Changed(content) with { Designs = content.Designs.Select(d => d.Design != WorldFixture.Station ? d : d with
        { Parts = d.Parts.Select(p => p with { Capabilities = p.Capabilities.Select(c => c is PartCapability.Storage
            ? new PartCapability.Storage(20) : c).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray() };
        Publish(session, expanded);
        Step(session);
        Assert.IsTrue(Read(session).OutputBuffer.IsEmpty);
        Assert.IsNull(Read(session).Batch);
        Assert.AreEqual(2L, Stock(session, Input));
        Assert.AreEqual(3L, Stock(session, Product));
        Assert.AreEqual(3L, Stock(session, OtherProduct));
        Step(session);
        Assert.AreEqual(0L, Stock(session, Input));
        Assert.IsNotNull(Read(session).Batch);
    }

    [TestMethod]
    [DataRow(1, 0)]
    [DataRow(0, 3)]
    public void Output_does_not_use_reserved_or_overcapacity_storage(int reserved, int extraStock)
    {
        var state = State(Content(stock: [new(Input, 2)], work: 1, capacity: 1));
        var started = ProductionOperations.Advance(state.World, state.Content, Settlement, 0, 1).World;
        var world = started with { Inventories = started.Inventories.Select(i => i.Id != LocalInventory ? i : i with
        {
            Goods = extraStock == 0 ? [] : [new(OtherProduct, extraStock)],
            Reservations = reserved == 0 ? [] : [new(new(1), reserved, [])]
        }).ToImmutableArray() };
        using var session = new CoreSession(state with { World = world });
        Step(session);
        Assert.AreEqual(0L, Stock(session, Product));
        Assert.AreEqual(new GoodsQuantity(Product, 3), Read(session).OutputBuffer.Single());
        Step(session);
        Assert.AreEqual(0L, Stock(session, Product));
    }

    [TestMethod]
    public void Disabled_and_zero_capability_precede_conditions_and_buffer_blocking()
    {
        var content = Content(stock: [new(Input, 2)], work: 1, capacity: 1);
        using var candidate = Prepare(content);
        using var session = candidate.Activate();
        Step(session, 2);
        Assert.AreEqual(ProductionState.OutputBlocked, Read(session).State);
        var wrongTerrain = Changed(content) with { Processes = [content.Processes[0] with { AllowedTerrainTypes = [WorldFixture.Hills] }] };
        Publish(session, wrongTerrain);
        Assert.AreEqual(ProductionState.ConditionsNotMet, Read(session).State);
        var zero = Changed(wrongTerrain, 'A') with { Designs = wrongTerrain.Designs.Select(d => d.Design != WorldFixture.FactoryBuilding ? d : d with
        { Parts = d.Parts.Select(p => p with { Capabilities = [] }).ToImmutableArray() }).ToImmutableArray() };
        Publish(session, zero);
        Assert.AreEqual(ProductionState.ZeroCapability, Read(session).State);
        Assert.AreEqual(3L, Read(session).OutputBuffer.Sum(g => g.Quantity) + Stock(session, Product));

        var disabledContent = Content(enabled: false);
        disabledContent = disabledContent with
        {
            Designs = disabledContent.Designs.Select(d => d.Design != WorldFixture.FactoryBuilding ? d : d with
            { Parts = d.Parts.Select(p => p with { Capabilities = [] }).ToImmutableArray() }).ToImmutableArray(),
            Processes = [disabledContent.Processes[0] with { AllowedTerrainTypes = [WorldFixture.Hills] }]
        };
        using var disabled = Prepare(disabledContent);
        using var stopped = disabled.Activate();
        Step(stopped);
        Assert.AreEqual(ProductionState.Disabled, Read(stopped).State);
        Assert.IsNull(Read(stopped).Batch);
        Assert.AreEqual(10L, Stock(stopped, Input));
    }

    [TestMethod]
    public void Changed_conditions_allow_existing_batch_to_finish()
    {
        var content = Content();
        using var candidate = Prepare(content);
        using var session = candidate.Activate();
        Step(session);
        var published = Publish(session, Changed(content) with { Processes = [content.Processes[0] with { AllowedTerrainTypes = [WorldFixture.Hills] }] });
        var change = published.Events.Select(e => e.Event).OfType<CoreEvent.ProductionStateChanged>().Single();
        Assert.AreEqual(ProductionState.Running, change.Before);
        Assert.AreEqual(ProductionState.ConditionsNotMet, change.After);
        Step(session, 2);
        Assert.AreEqual(3L, Stock(session, Product));
        Assert.AreEqual(ProductionState.ConditionsNotMet, Read(session).State);
        Step(session, 2);
        Assert.IsNull(Read(session).Batch);
        Assert.AreEqual(8L, Stock(session, Input));
    }

    [TestMethod]
    public void Facility_lookup_distinguishes_unsupported_capabilities_and_missing_buildings()
    {
        using var candidate = Prepare();
        using var session = candidate.Activate();
        Assert.IsInstanceOfType<Lookup<FacilityView>.Missing>(session.Read().FindFacility(new(new(999), CapabilityKind.Production)));
        Assert.IsInstanceOfType<Lookup<FacilityView>.Missing>(session.Read().FindFacility(new(new(1), CapabilityKind.Production)));
        Assert.ThrowsExactly<NotImplementedException>(() => session.Read().FindFacility(Facility with { Capability = CapabilityKind.Extraction }));
        Assert.ThrowsExactly<NotImplementedException>(() => session.Read().FindFacility(Facility with { Capability = CapabilityKind.Construction }));
        Assert.ThrowsExactly<NotImplementedException>(() => session.Submit(new CoreCommand.SetProductionPlan(Facility, Process, false, 1), CommandSource.AutomationTest));
    }

    [TestMethod]
    public void Declared_production_capability_remains_discoverable_without_a_plan_when_parts_are_destroyed()
    {
        var content = Content();
        content = content with { SettlementTemplates = [content.SettlementTemplates[0] with { ProductionPlans = [] }] };
        var state = State(content);
        var world = state.World with { Instances = state.World.Instances.Select(i => i.Id != new InstanceId.Building(Facility.Building)
            ? i : i with { Parts = i.Parts.Select(p => p with { Current = 0 }).ToImmutableArray() }).ToImmutableArray() };
        using var session = new CoreSession(state with { World = world });
        Assert.AreEqual(0d, Read(session).CapabilityWorkPerSecond);
        Assert.AreEqual(ProductionState.Disabled, Read(session).State);
        Assert.IsNull(Read(session).Process);
    }
}
