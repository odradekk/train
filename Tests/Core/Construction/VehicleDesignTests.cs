using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Construction.ConstructionFixture;

namespace Train.Core.Tests.Construction;

[TestClass]
public sealed class VehicleDesignTests
{
    [TestMethod]
    public void Refit_return_limits_remain_per_good_when_later_damage_and_content_change_actual_returns()
    {
        GoodId ore = new GoodId.Voxel(WorldFixture.Ore);
        var content = RemovalContent(10);
        content = content with
        {
            Goods = content.Goods.Add(content.Goods.Single(g => g.Good == Material) with { Good = ore }),
            Designs = content.Designs.Select(d => d.Design != WorldFixture.Vehicle ? d : d with
            {
                Parts = [d.Parts.Single() with { RepairGood = ore }],
                BillOfMaterials = [new(Material, 3), new(ore, 1)]
            }).ToImmutableArray()
        };
        using var initial = Session(content);
        using var session = WithDamage(initial, 50, 40);
        var change = RemovalChange(content);
        change = change with { RemovedItems = change.RemovedItems.Select(item => item.Parts.IsEmpty
            ? item : item with { Good = ore }).ToImmutableArray() };
        Start(session, Refit(change));
        var reservation = Inventory(session).Reservations.Single();
        Assert.AreEqual(2L, reservation.Capacity);
        Assert.AreEqual(1L, reservation.ExpectedReturns.Single(g => g.Good == Material).Quantity);
        Assert.AreEqual(1L, reservation.ExpectedReturns.Single(g => g.Good == ore).Quantity);
        using var damaged = WithDamage(session, 50, 0);
        var updated = Changed(content) with { Designs = damaged.State.World.Designs.Select(d => d.Design != WorldFixture.Vehicle
            ? d : d with { MaxStructureDurability = 50 }).ToImmutableArray() };
        Accept(damaged, new CoreCommand.PublishContent(updated));

        var completed = Step(damaged, 6).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();

        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 1) }, completed.ReturnedGoods.ToArray());
        Assert.AreEqual(10L, Stock(damaged, Local));
        Assert.IsFalse(Inventory(damaged).Stacks.Any(g => g.Good == ore));
        Assert.AreEqual(0L, Inventory(damaged).Reserved);
        Assert.IsTrue(Inventory(damaged).Reservations.IsEmpty);
    }

    [TestMethod]
    public void Refit_without_initial_returns_creates_no_reservation_or_later_returns_after_content_update()
    {
        var content = RemovalContent(10);
        using var initial = Session(content);
        using var session = WithDamage(initial, 1, 0);
        Start(session, Refit(RemovalChange(content)));
        Assert.AreEqual(0L, Inventory(session).Reserved);
        Assert.IsTrue(Inventory(session).Reservations.IsEmpty);
        var updated = Changed(content) with { Designs = session.State.World.Designs.Select(d => d.Design != WorldFixture.Vehicle
            ? d : d with { MaxStructureDurability = 1 }).ToImmutableArray() };
        Accept(session, new CoreCommand.PublishContent(updated));
        Assert.IsTrue(Inventory(session).Reservations.IsEmpty);

        var completed = Step(session, 5).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();

        Assert.IsTrue(completed.ReturnedGoods.IsEmpty);
        Assert.AreEqual(9L, Stock(session, Local));
        Assert.IsTrue(Inventory(session).Reservations.IsEmpty);
    }

    [TestMethod]
    public void Refit_structure_ratio_uses_exact_integer_arithmetic_at_long_maximum()
    {
        var content = Content();
        content = content with { Designs = content.Designs.Select(d => d.Design != WorldFixture.Vehicle
            ? d : d with { MaxStructureDurability = 3 }).ToImmutableArray() };
        using var initial = Session(content);
        using var session = WithDamage(initial, 2, 40);
        var change = Change(content, compositionChanged: true);
        change = change with { Target = change.Target with { Design = change.Target.Design with
            { MaxStructureDurability = long.MaxValue } } };
        Start(session, Refit(change));

        Step(session);

        var after = ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value;
        Assert.AreEqual(new DurabilityView(6_148_914_691_236_517_205L, long.MaxValue), after.Structure);
    }

    [TestMethod]
    public void Content_update_does_not_make_refit_returns_exceed_reserved_capacity()
    {
        var content = RemovalContent(99);
        using var initial = Session(content);
        using var session = WithDamage(initial, 50, 40);
        Start(session, Refit(RemovalChange(content)));
        Assert.AreEqual(2L, Inventory(session).Reserved);
        var updated = Changed(content) with { Designs = session.State.World.Designs.Select(d => d.Design != WorldFixture.Vehicle
            ? d : d with { MaxStructureDurability = 50 }).ToImmutableArray() };
        Accept(session, new CoreCommand.PublishContent(updated));
        Assert.AreEqual(2L, Inventory(session).Reserved);
        var completed = Step(session, 6).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 2) }, completed.ReturnedGoods.ToArray());
        var inventory = Inventory(session);
        Assert.AreEqual(100L, inventory.Used);
        Assert.AreEqual(0L, inventory.Reserved);
        Assert.IsTrue(inventory.Reservations.IsEmpty);
    }

    [TestMethod]
    public void Immediate_application_rejects_stale_invalid_missing_and_changed_composition_atomically()
    {
        var content = Content();
        using var session = Session(content);
        var change = Change(content);
        Reject(session, new CoreCommand.ApplyInstanceDesign(Vehicle, change with
            { Target = change.Target with { InputIdentity = new(new string('7', 64)) } }), "Change.Target.InputIdentity", "content_changed");
        Reject(session, new CoreCommand.ApplyInstanceDesign(Vehicle, change with
            { Before = new(new string('6', 64)) }), "Change.Before", "invalid_state");
        Reject(session, new CoreCommand.ApplyInstanceDesign(Vehicle, change with
            { Target = change.Target with { Design = change.Target.Design with { CanConstruct = false } } }), "Change.Target", "invalid_design");
        Reject(session, new CoreCommand.ApplyInstanceDesign(Vehicle, change with
            { Target = change.Target with { Design = change.Target.Design with
                { Parts = [change.Target.Design.Parts[0] with { Missing = true }] } } }), "Change", "content_missing");
        Reject(session, new CoreCommand.ApplyInstanceDesign(Vehicle, change with
            { Target = change.Target with { Design = change.Target.Design with { Composition = new(new string('8', 64)) } } }), "Change.Target", "invalid_design");
        var unavailable = content with { Designs = content.Designs.Select(d => d.Design == change.Before
            ? d with { CanConstruct = false } : d).ToImmutableArray() };
        using var oldInvalid = Session(unavailable);
        Reject(oldInvalid, new CoreCommand.ApplyInstanceDesign(Vehicle, change), "Change.Target", "invalid_design");
        var before = session.State;
        var commandCount = session.ReadRecording().Commands.Length;
        Assert.ThrowsExactly<NotImplementedException>(() => session.Submit(
            new CoreCommand.ApplyInstanceDesign(new InstanceId.Building(new(1)), change), CommandSource.AutomationTest));
        Assert.AreEqual(before, session.State);
        Assert.AreEqual(commandCount, session.ReadRecording().Commands.Length);
    }

    [TestMethod]
    public void Removed_damaged_part_and_root_structure_reserve_aggregate_floor_returns_then_release_capacity()
    {
        var content = RemovalContent(99);
        using var initial = Session(content, damaged: true);
        using var session = WithDamage(initial, 50, 40);
        var change = RemovalChange(content);
        var job = Start(session, Refit(change));
        Assert.AreEqual(6L, job.FrozenTotalWork);
        Assert.AreEqual(98L, Stock(session, Local));
        var inventory = Inventory(session);
        Assert.AreEqual(2L, inventory.Reserved);
        Assert.AreEqual(0L, inventory.Available);
        var reservation = inventory.Reservations.Single();
        Assert.AreEqual(job.Id, reservation.Job);
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 2) }, reservation.ExpectedReturns.ToArray());
        var completed = Step(session, 6).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 2) }, completed.ReturnedGoods.ToArray());
        Assert.AreEqual(100L, Stock(session, Local));
        Assert.AreEqual(0L, Inventory(session).Reserved);
        Assert.IsTrue(Inventory(session).Reservations.IsEmpty);
    }

    [TestMethod]
    public void Refit_rejects_insufficient_return_capacity_material_and_target_size_without_mutation()
    {
        var content = RemovalContent(100);
        using var initial = Session(content, damaged: true);
        using var full = WithDamage(initial, 50, 40);
        Reject(full, Refit(RemovalChange(content)), "ReturnInventory.Capacity", "insufficient_capacity");
        var emptyContent = RemovalContent(0);
        using var empty = Session(emptyContent);
        Reject(empty, Refit(RemovalChange(emptyContent)), "Sources", "insufficient_goods");
        var normal = Content();
        using var small = Session(normal);
        var change = Change(normal, compositionChanged: true);
        change = change with { Target = change.Target with { Design = change.Target.Design with
            { Kind = new DesignKind.Vehicle(SizeClass.Medium, 1) } } };
        Reject(small, Refit(change), "Facility", "conditions_not_met");
        Reject(small, Refit(Change(normal, compositionChanged: true) with { RetainedParts = [] }),
            "Change.RetainedParts", "invalid_state");
    }

    [TestMethod]
    public void Zero_work_refit_is_a_job_and_finishes_on_next_step_with_finite_progress()
    {
        var content = Content();
        using var session = Session(content);
        var change = Change(content, compositionChanged: true);
        var job = Start(session, Refit(change));
        Assert.AreEqual(0L, job.FrozenTotalWork);
        Assert.AreEqual(0d, job.Progress);
        Assert.IsTrue(double.IsFinite(Job(session.Read(), job.Id).Progress));
        Assert.AreEqual(change.Before, ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value.Design);
        var events = Step(session).Events.Select(e => e.Event).ToArray();
        Assert.AreEqual(job.Id, events.OfType<CoreEvent.ConstructionCompleted>().Single().Job);
        Assert.AreEqual(change.Target.Design.Design, events.OfType<CoreEvent.InstanceDesignChanged>().Single().After);
    }

    [TestMethod]
    [DataRow(33L, 67L)]
    [DataRow(100L, 201L)]
    public void Refit_rounds_structure_up_and_retains_latest_absolute_part_durability(long structure, long expected)
    {
        var content = Content();
        using var initial = Session(content);
        using var session = WithDamage(initial, structure, 40);
        var change = Change(content, compositionChanged: true);
        change = change with { Target = change.Target with { Design = change.Target.Design with { MaxStructureDurability = 201 } } };
        var job = Start(session, Refit(change));
        using var changedWhileWorking = WithDamage(session, structure, 17);
        Step(changedWhileWorking);
        var after = ((Lookup<InstanceView>.Found)changedWhileWorking.Read().FindInstance(Vehicle)).Value;
        Assert.AreEqual(new DurabilityView(expected, 201), after.Structure);
        Assert.AreEqual(17L, after.Parts.Single().Durability.EffectiveCurrent);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(changedWhileWorking.Read().FindJob(job.Id));
    }

    private static DesignChangeProjection Change(CoreContentProjection content, bool compositionChanged = false)
    {
        var input = Design(content);
        return new(input.Design.Design, input.Design.Composition, input with { Design = input.Design with
        {
            Design = new(new string('9', 64)),
            Composition = compositionChanged ? new(new string('8', 64)) : input.Design.Composition
        } }, [new(new(0), new(0))], [], [], 0);
    }

    private static CoreCommand Refit(DesignChangeProjection change) =>
        new CoreCommand.StartOwnedJob(new JobRequest.Refit(Facility, Vehicle, change, [Local], 0));

    private static CoreContentProjection RemovalContent(long stock) => Content(stock: stock) with
    {
        Designs = Content(stock: stock).Designs.Select(d => d.Design != WorldFixture.Vehicle ? d : d with
        {
            StructuralGoods = [new(Material, 3)], BillOfMaterials = [new(Material, 4)], BillOfMaterialsItemCount = 4
        }).ToImmutableArray()
    };

    private static DesignChangeProjection RemovalChange(CoreContentProjection content)
    {
        var change = Change(content, compositionChanged: true);
        return change with
        {
            Target = change.Target with { Design = change.Target.Design with
                { Parts = [], StructuralGoods = [new(Material, 1)], BillOfMaterials = [new(Material, 1)], BillOfMaterialsItemCount = 1 } },
            RetainedParts = [], AddedGoods = [new(Material, 1)], ChangedItemCount = 5,
            RemovedItems = [new(Material, 1, [new(0)], [], []),
                new(Material, 1, [], [new(Material, 1)], []), new(Material, 2, [], [new(Material, 2)], [])]
        };
    }

    private static CoreSession WithDamage(CoreSession session, long structure, long part) => new(session.State with
    {
        World = session.State.World with { Instances = session.State.World.Instances.Select(i => i.Id != Vehicle ? i : i with
        {
            StructureDurability = structure, Parts = i.Parts.Select(p => p with { Current = part }).ToImmutableArray()
        }).ToImmutableArray() }
    });

    private static InventoryView Inventory(ICoreSession session) =>
        ((Lookup<InventoryView>.Found)session.Read().FindInventory(Local)).Value;

    [TestMethod]
    public void Owned_vehicle_refit_consumes_only_added_goods_and_preserves_retained_damage()
    {
        var content = Content();
        using var original = Session(content, damaged: true);
        using var session = new CoreSession(original.State with { World = original.State.World with
        {
            Instances = original.State.World.Instances.Select(instance => instance.Id != Vehicle ? instance : instance with
            {
                Parts = instance.Parts.Select(part => part with { Current = 40 }).ToImmutableArray()
            }).ToImmutableArray()
        } });
        var before = ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value;
        var input = Design(content);
        var oldPart = input.Design.Parts.Single();
        var target = input with { Design = input.Design with
        {
            Design = new(new string('9', 64)),
            Composition = new(new string('8', 64)),
            MaxStructureDurability = 200,
            Parts = [oldPart with { Id = new(1) }, oldPart with { Id = new(0), Capabilities = [] }],
            BillOfMaterials = [new(Material, 3)],
            BillOfMaterialsItemCount = 3
        } };
        var change = new DesignChangeProjection(before.Design, before.Composition, target,
            [new(new(0), new(1))], [new(Material, 1)], [], 1);
        var stockBefore = Stock(session, Local);

        var job = Start(session, new CoreCommand.StartOwnedJob(new JobRequest.Refit(Facility, Vehicle, change, [Local], 0)));

        Assert.AreEqual(stockBefore - 1, Stock(session, Local));
        var working = ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value;
        Assert.AreEqual(before.Design, working.Design);
        Assert.AreEqual(InstanceLock.UnderRefit, working.Lock);
        Assert.AreEqual(1L, job.FrozenTotalWork);
        var events = Step(session).Events.Select(e => e.Event).ToArray();

        var completed = events.OfType<CoreEvent.ConstructionCompleted>().Single();
        Assert.AreEqual(job.Id, completed.Job);
        Assert.AreEqual(new CoreEvent.InstanceDesignChanged(Vehicle, before.Design, target.Design.Design),
            events.OfType<CoreEvent.InstanceDesignChanged>().Single());
        var after = ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value;
        Assert.AreEqual(target.Design.Design, after.Design);
        Assert.AreEqual(target.Design.Composition, after.Composition);
        Assert.AreEqual(new DurabilityView(100, 200), after.Structure);
        Assert.AreEqual(40L, after.Parts.Single(part => part.Part == new PartId(1)).Durability.EffectiveCurrent);
        Assert.AreEqual(oldPart.MaxDurability, after.Parts.Single(part => part.Part == new PartId(0)).Durability.EffectiveCurrent);
        Assert.AreEqual(InstanceLock.None, after.Lock);
        Assert.AreEqual(stockBefore - 1, Stock(session, Local));
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(job.Id));
    }

    [TestMethod]
    public void Same_composition_design_applies_immediately_and_preserves_damage_train_and_inventory()
    {
        var content = Content();
        using var session = Session(content, damaged: true);
        var before = ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value;
        var trainId = ((InstanceDetails.Vehicle)before.Details).Train!.Value;
        var trainBefore = ((Lookup<TrainView>.Found)session.Read().FindTrain(trainId)).Value;
        var localStock = Stock(session, Local);
        var cargoStock = Stock(session, Cargo);
        var target = Design(content) with
        {
            Design = Design(content).Design with { Design = new(new string('9', 64)) }
        };
        var change = new DesignChangeProjection(before.Design, before.Composition, target,
            before.Parts.Select(part => new PartCorrespondence(part.Part, part.Part)).ToImmutableArray(),
            [], [], 0);
        Assert.IsTrue(before.Structure.EffectiveCurrent < before.Structure.Maximum);
        Assert.IsTrue(before.Parts.Any(part => part.Durability.EffectiveCurrent < part.Durability.Maximum));

        var result = Accept(session, new CoreCommand.ApplyInstanceDesign(Vehicle, change));

        var changed = result.Events.Select(e => e.Event).OfType<CoreEvent.InstanceDesignChanged>().Single();
        Assert.AreEqual(new CoreEvent.InstanceDesignChanged(Vehicle, before.Design, target.Design.Design), changed);
        Assert.IsFalse(result.Events.Any(e => e.Event is CoreEvent.ConstructionStarted));
        var after = ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value;
        Assert.AreEqual(target.Design.Design, after.Design);
        Assert.AreEqual(before.Composition, after.Composition);
        Assert.AreEqual(before.Structure, after.Structure);
        CollectionAssert.AreEqual(before.Parts.ToArray(), after.Parts.ToArray());
        Assert.AreEqual(before.Details, after.Details);
        Assert.AreEqual(before.Location, after.Location);
        Assert.AreEqual(before.Lock, after.Lock);
        var trainAfter = ((Lookup<TrainView>.Found)session.Read().FindTrain(trainId)).Value;
        Assert.AreEqual(trainBefore.Position, trainAfter.Position);
        Assert.AreEqual(trainBefore.Activity, trainAfter.Activity);
        Assert.AreEqual(trainBefore.SpeedMetersPerSecond, trainAfter.SpeedMetersPerSecond);
        Assert.AreEqual(trainBefore.Plan, trainAfter.Plan);
        Assert.AreEqual(trainBefore.NextStopIndex, trainAfter.NextStopIndex);
        Assert.AreEqual(trainBefore.RemainingDwellSteps, trainAfter.RemainingDwellSteps);
        CollectionAssert.AreEqual(trainBefore.Vehicles.ToArray(), trainAfter.Vehicles.ToArray());
        Assert.AreEqual(localStock, Stock(session, Local));
        Assert.AreEqual(cargoStock, Stock(session, Cargo));
    }
}
