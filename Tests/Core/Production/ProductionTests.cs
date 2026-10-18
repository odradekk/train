using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Construction;
using Train.Core.Contracts;
using Train.Core.Production;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;
using static Train.Core.Tests.Production.ProductionFixture;

namespace Train.Core.Tests.Production;

[TestClass]
public sealed class ProductionTests
{
    [TestMethod]
    public void Session_work_uses_current_population_labor_and_new_residents_only_on_the_next_step()
    {
        var food = new GoodId.Voxel(new("fixture:food"));
        var clothing = new GoodId.Voxel(new("fixture:clothing"));
        var content = Content(stock: [new(Input, 2), new(food, 10), new(clothing, 10)],
            work: 10, rate: 120, laborPerWork: 2);
        content = content with { PopulationRules = [WorldFixture.PopulationRule() with
        {
            StatisticsPeriodTicks = 2, BaseGrowthPerSecond = 3, LaborRatio = .4
        }] };
        using var referenceCandidate = Prepare(content);
        using var reference = referenceCandidate.Activate();
        Step(reference, 2);
        Assert.AreEqual(2, Read(reference).ActualWorkPerSecond, 1e-12);
        using var candidate = Prepare(content);
        using var session = candidate.Activate();
        Step(session);
        var changed = Changed(content) with { PopulationRules = [content.PopulationRules[0] with { LaborRatio = .8 }] };
        Publish(session, changed);
        var settled = Step(session);
        var report = settled.Events.Select(e => e.Event).OfType<CoreEvent.PopulationSettled>().Single().Report;
        Assert.AreEqual(10L, report.Before);
        Assert.AreEqual(11L, report.After);
        Assert.AreEqual(4, Read(session).ActualWorkPerSecond, 1e-12);
        Assert.AreEqual(4d / 60, Read(session).Batch!.ProgressWork, 1e-12);
        CollectionAssert.Contains(Read(session).Limits.ToArray(), WorkLimit.Labor);
        Step(session);
        Assert.AreEqual(4.4, Read(session).ActualWorkPerSecond, 1e-12);
        Assert.AreEqual(8.4 / 60, Read(session).Batch!.ProgressWork, 1e-12);
        Assert.AreEqual(0L, Stock(session, Input));
    }

    [TestMethod]
    public void Incomplete_batch_keeps_all_inputs_and_waits()
    {
        var content = Content(stock: [new(Input, 7), new(OtherProduct, 1)], inputs: [new(Input, 2), new(OtherProduct, 2)]);
        content = content with { PopulationRules = [content.PopulationRules[0] with
            { ConsumptionIntervalTicks = 120, StatisticsPeriodTicks = 720 }] };
        using var candidate = Prepare(content);
        using var session = (CoreSession)candidate.Activate();
        Step(session);
        var waiting = session.State.World;
        Step(session, 2);
        Assert.AreEqual(waiting.Settlements, session.State.World.Settlements);
        Assert.AreEqual(waiting.Inventories, session.State.World.Inventories);
        Assert.AreEqual(7L, Stock(session, Input));
        Assert.AreEqual(1L, Stock(session, OtherProduct));
        Assert.AreEqual(ProductionState.WaitingForInputs, Read(session).State);
        var blocker = Read(session).Blockers.Single();
        Assert.AreEqual("inputs_missing", blocker.Code);
        Assert.AreEqual("Facility", blocker.Field);
        Assert.AreEqual(new WorldObjectId.Instance(new InstanceId.Building(Facility.Building)), blocker.Subject);
        Assert.IsTrue(blocker.RelatedIdentities.IsEmpty);
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
        CollectionAssert.AreEqual(new[] { WorkLimit.Capability }, Read(session).Limits.ToArray());
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
        CollectionAssert.AreEqual(new[] { WorkLimit.Power }, Read(session).Limits.ToArray());
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
        var start = AdvanceProduction(state.World, state.Content, Settlement, 30, 1).World;
        var limited = AdvanceProduction(start, state.Content, Settlement, 30, 2).World;
        var batch = limited.Settlements.Single().ProductionFacilities.Single().Batch!;
        Assert.AreEqual(.25, batch.ProgressWork, 1e-12);
        Assert.AreEqual(15d, limited.Settlements.Single().ProductionFacilities.Single().LastActualWorkPerSecond, 1e-12);
        Assert.AreEqual(30d, limited.Settlements.Single().ProductionFacilities.Single().LastAllocatedLabor);
        CollectionAssert.AreEqual(new[] { WorkLimit.Labor }, limited.Settlements.Single().ProductionFacilities.Single().LastLimits.ToArray());
        var paused = AdvanceProduction(limited, state.Content, Settlement, 0, 3).World;
        Assert.AreEqual(.25, paused.Settlements.Single().ProductionFacilities.Single().Batch!.ProgressWork, 1e-12);
        Assert.AreEqual(0d, paused.Settlements.Single().ProductionFacilities.Single().LastAllocatedLabor);
        var finished = AdvanceProduction(paused, state.Content, Settlement, 240, 4).World;
        Assert.IsNull(finished.Settlements.Single().ProductionFacilities.Single().Batch);
        Assert.AreEqual(210d, finished.Settlements.Single().ProductionFacilities.Single().LastAllocatedLabor);
        Assert.AreEqual(3L, finished.Inventories.Single(i => i.Id == LocalInventory).Goods.Single(g => g.Good == Product).Quantity);
        var idle = AdvanceProduction(finished, state.Content, Settlement, 240, 5).World;
        Assert.AreEqual(0d, idle.Settlements.Single().ProductionFacilities.Single().LastAllocatedLabor);
    }

    [TestMethod]
    [DataRow(30d, 15d, true)]
    [DataRow(20d, 10d, false)]
    public void Simultaneous_power_and_labor_demands_limit_work(double labor, double expectedRate, bool powerAlsoBinds)
    {
        var state = State(Content(stock: [new(Input, 2), new(Fuel, 2)], work: 4, rate: 120,
            powerPerWork: 2, laborPerWork: 2, power: 30));
        var started = AdvanceProduction(state.World, state.Content, Settlement, labor, 0).World;
        var advanced = AdvanceProduction(started, state.Content, Settlement, labor, 1).World;
        var runtime = advanced.Settlements.Single().ProductionFacilities.Single();
        Assert.AreEqual(expectedRate, runtime.LastActualWorkPerSecond, 1e-12);
        Assert.AreEqual(expectedRate / 60, runtime.Batch!.ProgressWork, 1e-12);
        Assert.AreEqual(240d, runtime.LastPowerDemand);
        Assert.AreEqual(expectedRate * 2, runtime.LastActualSupply, 1e-12);
        CollectionAssert.AreEqual(powerAlsoBinds ? new[] { WorkLimit.Power, WorkLimit.Labor } : new[] { WorkLimit.Labor },
            runtime.LastLimits.ToArray());
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
        var finished = Step(session, 2);
        var completed = finished.Events.Select(e => e.Event).OfType<CoreEvent.ProductionCompleted>().Single();
        Assert.AreEqual(Facility, completed.Facility);
        Assert.AreEqual(Process, completed.Process);
        Assert.AreEqual(content.Processes[0].Revision, completed.StartedFromRevision);
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Product, 3) }, completed.Outputs.ToArray());
        Assert.IsFalse(completed.Buffered);
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
        var production = completed.Events.Select(e => e.Event).OfType<CoreEvent.ProductionCompleted>().Single();
        Assert.AreEqual(Facility, production.Facility);
        Assert.AreEqual(Recovery, production.Process);
        Assert.AreEqual(WorldFixture.Revision, production.StartedFromRevision);
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Product, 3) }, production.Outputs.ToArray());
        Assert.IsTrue(production.Buffered);
        Assert.AreEqual(1L, Stock(session, Product));
        Assert.AreEqual(new GoodsQuantity(Product, 2), Read(session).OutputBuffer.Single());
        Assert.AreEqual(ProductionState.OutputBlocked, Read(session).State);
        var change = completed.Events.Select(e => e.Event).OfType<CoreEvent.ProductionStateChanged>().Single();
        Assert.AreEqual(ProductionState.Running, change.Before);
        Assert.AreEqual(ProductionState.OutputBlocked, change.After);
        CollectionAssert.AreEqual(new[] { "output_blocked" }, change.Reasons.Select(r => r.Code).ToArray());
        CollectionAssert.AreEqual(new[] { "output_blocked" }, Read(session).Blockers.Select(r => r.Code).ToArray());
    }

    [TestMethod]
    public void Active_batch_keeps_frozen_power_and_labor_intensities()
    {
        var content = Content(stock: [new(Input, 4), new(Fuel, 2)], powerPerWork: 1);
        content = content with { PopulationRules = [content.PopulationRules[0] with { LaborRatio = 0 }] };
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
        Step(session);
        Assert.IsFalse(Read(session).Batch!.CurrentSpecificationDiffers);
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Product, 3), new GoodsQuantity(OtherProduct, 3) },
            Read(session).Batch!.FrozenOutputs.ToArray());
        Step(session);
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
        var started = AdvanceProduction(state.World, state.Content, Settlement, 0, 1).World;
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
        var expanded = Changed(zero, 'B') with { Designs = zero.Designs.Select(d => d.Design != WorldFixture.Station ? d : d with
        { Parts = d.Parts.Select(p => p with { Capabilities = p.Capabilities.Select(c => c is PartCapability.Storage
            ? new PartCapability.Storage(10) : c).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray() };
        Publish(session, expanded);
        Step(session);
        Assert.AreEqual(ProductionState.ZeroCapability, Read(session).State);
        Assert.IsTrue(Read(session).OutputBuffer.IsEmpty);
        Assert.AreEqual(3L, Stock(session, Product));
        Assert.IsNull(Read(session).Batch);

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
    [DataRow(false)]
    [DataRow(true)]
    public void Changed_conditions_allow_existing_batch_to_finish(bool changeTag)
    {
        var content = Content();
        using var candidate = Prepare(content);
        using var session = candidate.Activate();
        Step(session);
        var process = changeTag ? content.Processes[0] with { RequiredFacilityTag = FacilityTag.Assembly }
            : content.Processes[0] with { AllowedTerrainTypes = [WorldFixture.Hills] };
        var published = Publish(session, Changed(content) with { Processes = [process] });
        var change = published.Events.Select(e => e.Event).OfType<CoreEvent.ProductionStateChanged>().Single();
        Assert.AreEqual(ProductionState.Running, change.Before);
        Assert.AreEqual(ProductionState.ConditionsNotMet, change.After);
        CollectionAssert.AreEqual(new[] { "conditions_not_met" }, change.Reasons.Select(r => r.Code).ToArray());
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
        Assert.IsInstanceOfType<Lookup<FacilityView>.Missing>(session.Read().FindFacility(Facility with { Capability = CapabilityKind.Extraction }));
        Assert.ThrowsExactly<NotImplementedException>(() => session.Read().FindFacility(Facility with { Capability = CapabilityKind.Construction }));
    }

    [TestMethod]
    public void State_changes_recalculate_capability_when_part_durability_changes()
    {
        var content = Content();
        var before = State(content).World;
        var damaged = before with { Instances = before.Instances.Select(i =>
            i.Id != new InstanceId.Building(Facility.Building) ? i : i with
            {
                Parts = i.Parts.Select(p => p with { Current = 0 }).ToImmutableArray()
            }).ToImmutableArray() };
        var change = (CoreEvent.ProductionStateChanged)ProductionQueries.StateChanges(before, content,
            damaged, content, before.Settlements.Single().Id).Single();
        Assert.AreEqual(Facility, change.Facility);
        Assert.AreEqual(ProductionState.Running, change.Before);
        Assert.AreEqual(ProductionState.ZeroCapability, change.After);
        Assert.AreEqual("capability_zero", change.Reasons.Single().Code);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void State_changes_consider_plans_on_both_sides_of_the_boundary(bool plannedBefore, bool plannedAfter)
    {
        var content = Content();
        var planned = State(content).World;
        var unplanned = planned with { Settlements = planned.Settlements.Select(s => s with
        {
            Template = s.Template with { ProductionPlans = [] }
        }).ToImmutableArray() };
        var events = ProductionQueries.StateChanges(plannedBefore ? planned : unplanned, content,
            plannedAfter ? planned : unplanned, content, planned.Settlements.Single().Id);
        if (plannedBefore == plannedAfter) Assert.IsEmpty(events);
        else
        {
            var change = (CoreEvent.ProductionStateChanged)events.Single();
            Assert.AreEqual(Facility, change.Facility);
            Assert.AreEqual(plannedBefore ? ProductionState.Running : ProductionState.Disabled, change.Before);
            Assert.AreEqual(plannedAfter ? ProductionState.Running : ProductionState.Disabled, change.After);
            Assert.IsEmpty(change.Reasons);
        }
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

    [TestMethod]
    public void Malformed_production_plan_throws_before_a_command_number()
    {
        using var session = (CoreSession)Prepare().Activate();
        var position = session.State.Position;
        var number = session.State.NextCommandNumber;
        Assert.ThrowsExactly<ArgumentNullException>(() => session.Submit(new CoreCommand.SetProductionPlan(null!, Process, true, 0), CommandSource.AutomationTest));
        Assert.ThrowsExactly<ArgumentNullException>(() => session.Submit(new CoreCommand.SetProductionPlan(Facility, null!, true, 0), CommandSource.AutomationTest));
        Assert.ThrowsExactly<ArgumentException>(() => session.Submit(Plan(Process, building: new(0)), CommandSource.AutomationTest));
        Assert.ThrowsExactly<ArgumentException>(() => session.Submit(Plan(Process, capability: CapabilityKind.Construction), CommandSource.AutomationTest));
        Assert.ThrowsExactly<ArgumentException>(() => session.Submit(Plan(Process, capability: (CapabilityKind)99), CommandSource.AutomationTest));
        Assert.ThrowsExactly<ArgumentException>(() => session.Submit(Plan(new ProcessRef.Production(new("BAD"))), CommandSource.AutomationTest));
        Assert.AreEqual(position, session.State.Position);
        Assert.AreEqual(number, session.State.NextCommandNumber);
    }

    [TestMethod]
    public void Production_plan_rejections_follow_ownership_and_leave_the_world_unchanged()
    {
        using var unowned = (CoreSession)Prepare().Activate();
        Rejects(unowned, Plan(Process, building: new(999)), "object_missing", "Facility");
        Rejects(unowned, Plan(Process), "not_owned", "Facility");

        using var hostile = Owned(relation: SettlementRelation.Hostile);
        Rejects(hostile, Plan(Process), "not_owned", "Facility");

        using var split = Owned();
        var foreignBuilding = split.State.World with
        {
            Instances = split.State.World.Instances.Select(i => i.Id != new InstanceId.Building(Facility.Building)
                ? i : i with { Ownership = SettlementRelation.NonHostile }).ToImmutableArray()
        };
        using var unownedBuilding = new CoreSession(split.State with { World = foreignBuilding });
        Rejects(unownedBuilding, Plan(Process), "not_owned", "Facility");

        using var intact = Owned();
        var destroyed = intact.State.World with
        {
            Instances = intact.State.World.Instances.Select(i => i.Id != new InstanceId.Building(Facility.Building)
                ? i : i with { StructureDurability = 0 }).ToImmutableArray()
        };
        using var ruined = new CoreSession(intact.State with { World = destroyed });
        Rejects(ruined, Plan(new ProcessRef.Production(new("fixture:absent"))), "instance_destroyed", "Facility");

        using var capable = Owned();
        Rejects(capable, Plan(new ProcessRef.Production(new("fixture:absent")), capability: CapabilityKind.Extraction), "conditions_not_met", "Facility.Capability");
        Rejects(capable, Plan(new ProcessRef.Production(new("fixture:absent"))), "content_missing", "Process");

        var mismatched = WithProcess(new ProcessDetails.Extraction(new("fixture:extract"), new(Input, 1)), FacilityTag.Assembly);
        using var wrongKind = Owned(mismatched);
        Rejects(wrongKind, Plan(new ProcessRef.Production(new("fixture:extract"))), "conditions_not_met", "Process");

        using var wrongTag = Owned(Content() with
        {
            Processes = [Content().Processes[0] with { RequiredFacilityTag = FacilityTag.Assembly }]
        });
        Rejects(wrongTag, Plan(Process), "conditions_not_met", "Facility.Tags");
    }

    [TestMethod]
    public void Setting_a_plan_writes_only_that_settlement_copy_and_keeps_runtime_rows()
    {
        var content = WithAlternate();
        using var created = Owned(content);
        var settlement = created.State.World.Settlements.Single();
        var otherPlan = new TemplateProductionPlan(2, CapabilityKind.Production, Recovery, false, 9);
        var other = settlement with
        {
            Id = new(99),
            Plots = settlement.Plots.Select(p => p with { Building = null }).ToImmutableArray(),
            Template = settlement.Template with { ProductionPlans = [otherPlan] },
            ProductionFacilities = []
        };
        var cleared = created.State.World with
        {
            Settlements = [settlement with
            {
                Template = settlement.Template with { ProductionPlans = [] },
                ProductionFacilities = []
            }, other]
        };
        using var session = new CoreSession(created.State with { World = cleared });
        var templates = session.State.Content.SettlementTemplates;
        var accepted = Accept(session, Plan(Process, priority: int.MaxValue));
        var createdState = accepted.Events.Select(e => e.Event).OfType<CoreEvent.ProductionStateChanged>().Single();
        Assert.AreEqual(ProductionState.Disabled, createdState.Before);
        Assert.AreEqual(ProductionState.Running, createdState.After);
        var plans = session.State.World.Settlements.Single(s => s.Id == Settlement).Template.ProductionPlans;
        Assert.AreEqual(new TemplateProductionPlan(2, CapabilityKind.Production, Process, true, int.MaxValue), plans.Single());
        Assert.AreEqual(new ProductionFacilityState(Facility, null, [], 0, [], 0, 0, 0),
            session.State.World.Settlements.Single(s => s.Id == Settlement).ProductionFacilities.Single());
        Assert.AreEqual(otherPlan, session.State.World.Settlements.Single(s => s.Id == new SettlementId(99)).Template.ProductionPlans.Single());
        Assert.AreEqual(templates, session.State.Content.SettlementTemplates);
        Accept(session, Plan(Alternate, enabled: false, priority: 3));
        Assert.AreEqual(new TemplateProductionPlan(2, CapabilityKind.Production, Alternate, false, 3),
            session.State.World.Settlements.Single(s => s.Id == Settlement).Template.ProductionPlans.Single());

        using var running = Owned(Content());
        Step(running);
        var runtime = running.State.World.Settlements.Single().ProductionFacilities.Single();
        var priority = Accept(running, Plan(Process, priority: int.MinValue));
        Assert.IsFalse(priority.Events.Any(e => e.Event is CoreEvent.ProductionStateChanged));
        Assert.AreEqual(runtime, running.State.World.Settlements.Single().ProductionFacilities.Single());
        Assert.AreEqual(8L, Stock(running, Input));
        var disabled = Accept(running, Plan(Process, enabled: false, priority: int.MinValue));
        Assert.AreEqual(ProductionState.Disabled, ((CoreEvent.ProductionStateChanged)disabled.Events.Select(e => e.Event).OfType<CoreEvent.ProductionStateChanged>().Single()).After);
        Step(running, 2);
        Assert.AreEqual(3L, Stock(running, Product));
        Assert.AreEqual(8L, Stock(running, Input));
        Assert.IsNull(Read(running).Batch);
        Step(running);
        Assert.IsNull(Read(running).Batch);
        Assert.AreEqual(8L, Stock(running, Input));

        using var bufferedSession = Owned(WithAlternate());
        var bufferedWorld = bufferedSession.State.World;
        var bufferedSettlement = bufferedWorld.Settlements.Single();
        var buffered = bufferedWorld with
        {
            Settlements = [bufferedSettlement with
            {
                ProductionFacilities = [bufferedSettlement.ProductionFacilities.Single() with
                {
                    Batch = null, OutputBuffer = [new(Product, 1)]
                }]
            }]
        };
        using var holding = new CoreSession(bufferedSession.State with { World = buffered });
        Accept(holding, Plan(Alternate));
        Assert.AreEqual(new GoodsQuantity(Product, 1), holding.State.World.Settlements.Single().ProductionFacilities.Single().OutputBuffer.Single());
        Assert.IsNull(holding.State.World.Settlements.Single().ProductionFacilities.Single().Batch);
        Assert.AreEqual(10L, Stock(holding, Input));
    }

    [TestMethod]
    public void Damaged_declared_capability_and_terrain_mismatch_still_accept_a_plan()
    {
        var content = Content();
        using var owned = Owned(content);
        var damaged = owned.State.World with
        {
            Instances = owned.State.World.Instances.Select(i => i.Id != new InstanceId.Building(Facility.Building) ? i : i with
            {
                Parts = i.Parts.Select(p => p with { Current = 0 }).ToImmutableArray()
            }).ToImmutableArray()
        };
        using var session = new CoreSession(owned.State with { World = damaged });
        Accept(session, Plan(Process, priority: 2));
        Assert.AreEqual(ProductionState.ZeroCapability, Read(session).State);

        var hills = new ProcessRef.Production(new("fixture:hills"));
        var terrain = WorldInputs.NormalizeContent(content with
        {
            Processes = content.Processes.Add(content.Processes[0] with
            {
                Details = new ProcessDetails.Production(new("fixture:hills"), [new(Input, 2)], [new(Product, 3)]),
                AllowedTerrainTypes = [WorldFixture.Hills]
            })
        });
        using var plain = Owned(terrain);
        Accept(plain, Plan(hills));
        Assert.AreEqual(ProductionState.ConditionsNotMet, Read(plain).State);
        Step(plain);
        Assert.AreEqual(10L, Stock(plain, Input));
        Assert.IsNull(Read(plain).Batch);
    }

    [TestMethod]
    public void Adding_a_plan_to_a_locked_building_enters_the_locked_state()
    {
        using var owned = Owned();
        var world = owned.State.World;
        var settlement = world.Settlements.Single();
        var building = new InstanceId.Building(Facility.Building);
        var job = new ConstructionJobState(new(world.Next.Job), settlement.Id, Facility,
            world.Instances.Single(i => i.Id == building).Design, building, null, 0,
            world.Source.Configuration.Rules.Construction, WorldFixture.Revision, 1, 0, 0, [], [], 0,
            ConstructionState.Working, 0, [], [])
        { Funding = JobFunding.OwnInventory, Kind = ConstructionKind.Repair };
        var locked = world with
        {
            Settlements = [settlement with
            {
                Template = settlement.Template with { ProductionPlans = [] },
                ProductionFacilities = []
            }],
            ConstructionJobs = [job]
        };
        using var session = new CoreSession(owned.State with { World = locked });
        Assert.AreEqual(ProductionState.Disabled, Read(session).State);
        var result = Accept(session, Plan(Process));
        Assert.AreEqual(ProductionState.Locked, Read(session).State);
        var change = result.Events.Select(e => e.Event).OfType<CoreEvent.ProductionStateChanged>().Single();
        Assert.AreEqual(ProductionState.Disabled, change.Before);
        Assert.AreEqual(ProductionState.Locked, change.After);
    }

    [TestMethod]
    public void Switching_process_during_a_batch_finishes_the_old_batch_and_consumes_the_new_inputs_once()
    {
        var content = WithAlternate();
        using var session = Owned(content);
        Step(session);
        Assert.AreEqual(8L, Stock(session, Input));
        var result = Accept(session, Plan(Alternate, priority: 4));
        Assert.IsFalse(result.Events.Any(e => e.Event is CoreEvent.ProductionStateChanged));
        Assert.AreEqual(Alternate, Read(session).Process);
        Assert.AreEqual(Process, Read(session).Batch!.Process);
        Assert.IsFalse(Read(session).Batch!.CurrentSpecificationDiffers);
        Assert.AreEqual(0d, Read(session).Batch!.ProgressWork);
        Assert.AreEqual(8L, Stock(session, Input));
        Assert.AreEqual(4, Read(session).Priority);
        var potential = ((Lookup<SettlementView>.Found)session.Read().FindSettlement(Settlement)).Value.Facilities
            .Single(f => f.Key == Facility).PotentialConsumption.Single();
        Assert.AreEqual(Input, potential.Good);
        Assert.AreEqual(150d, potential.UnitsPerSecond, 1e-9);

        Step(session);
        Assert.AreEqual(1d, Read(session).Batch!.ProgressWork, 1e-12);
        var finished = Step(session);
        var completed = finished.Events.Select(e => e.Event).OfType<CoreEvent.ProductionCompleted>().Single();
        Assert.AreEqual(Process, completed.Process);
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Product, 3) }, completed.Outputs.ToArray());
        Assert.AreEqual(3L, Stock(session, Product));
        Assert.AreEqual(8L, Stock(session, Input));
        Assert.AreEqual(0L, Stock(session, OtherProduct));
        Step(session);
        Assert.AreEqual(Alternate, Read(session).Batch!.Process);
        Assert.AreEqual(0d, Read(session).Batch!.ProgressWork);
        Assert.AreEqual(3L, Stock(session, Input));
        Assert.AreEqual(0L, Stock(session, OtherProduct));
        Assert.AreEqual(3L, Stock(session, Product));
    }

    [TestMethod]
    public void Current_specification_differs_follows_the_batch_process_after_a_switch()
    {
        var content = WithAlternate();
        using var session = Owned(content);
        Step(session);
        Accept(session, Plan(Alternate));
        Assert.IsFalse(Read(session).Batch!.CurrentSpecificationDiffers);
        var updated = Changed(content) with
        {
            Processes = content.Processes.Select(p => p.Details is ProcessDetails.Production { Id.Value: "fixture:production" }
                ? p with
                {
                    Revision = new(new string('A', 64)),
                    Details = new ProcessDetails.Production(new("fixture:production"), [new(Input, 2)], [new(Product, 9)])
                }
                : p).ToImmutableArray()
        };
        Publish(session, updated);
        Assert.AreEqual(Alternate, Read(session).Process);
        Assert.AreEqual(Process, Read(session).Batch!.Process);
        Assert.IsTrue(Read(session).Batch!.CurrentSpecificationDiffers);
        Assert.AreEqual(0d, Read(session).Batch!.ProgressWork);
        Assert.AreEqual(8L, Stock(session, Input));
    }

    [TestMethod]
    public void Missing_batch_process_pauses_a_switched_batch()
    {
        var content = WithAlternate();
        using var session = Owned(content);
        Step(session);
        Accept(session, Plan(Alternate));
        Step(session);
        Assert.AreEqual(1d, Read(session).Batch!.ProgressWork, 1e-12);
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var missing = Changed(content) with
        {
            Processes = content.Processes.Where(p => p.Details is not ProcessDetails.Production { Id.Value: "fixture:production" }).ToImmutableArray(),
            MissingReferences = [new(new CoreDefinitionReference.Process(Process), "process")]
        };
        using var restoredCandidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(
            WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode())), missing, WorldFixture.Options));
        using var restored = restoredCandidate.Activate();
        Assert.AreEqual(ProductionState.ProcessMissing, Read(restored).State);
        Assert.AreEqual(Alternate, Read(restored).Process);
        Assert.AreEqual(1d, Read(restored).Batch!.ProgressWork, 1e-12);
        Step(restored, 3);
        Assert.AreEqual(ProductionState.ProcessMissing, Read(restored).State);
        Assert.AreEqual(1d, Read(restored).Batch!.ProgressWork, 1e-12);
        Assert.AreEqual(0d, Read(restored).ActualWorkPerSecond);
        Assert.AreEqual(8L, Stock(restored, Input));
    }

    private static CoreCommand.SetProductionPlan Plan(ProcessRef process, bool enabled = true, int priority = 0,
        CapabilityKind capability = CapabilityKind.Production, BuildingId? building = null)
        => new(new(building ?? Facility.Building, capability), process, enabled, priority);

    private static CoreContentProjection WithProcess(ProcessDetails details, FacilityTag tag)
    {
        var content = Content();
        return WorldInputs.NormalizeContent(content with
        {
            Processes = content.Processes.Add(content.Processes[0] with { Details = details, RequiredFacilityTag = tag })
        });
    }

    private static void Rejects(CoreSession session, CoreCommand.SetProductionPlan command, string code, string field)
    {
        var world = session.State.World;
        var number = session.State.NextCommandNumber;
        var result = (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);
        var problem = ((CommandDecision.Rejected)result.Receipt.Decision).Problems.Single();
        Assert.AreEqual(code, problem.Code);
        Assert.AreEqual(field, problem.Field);
        Assert.AreEqual(new WorldObjectId.Instance(new InstanceId.Building(command.Facility.Building)), problem.Subject);
        Assert.AreEqual(0, result.Events.Length);
        Assert.AreEqual(world, session.State.World);
        Assert.AreEqual(number, result.Receipt.Number);
        Assert.AreEqual(checked(number + 1), session.State.NextCommandNumber);
    }

    private static SubmissionResult.Completed Accept(CoreSession session, CoreCommand command)
    {
        var result = (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision, result.Diagnostic?.Message);
        return result;
    }
}
