using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Construction.ConstructionFixture;

namespace Train.Core.Tests.Construction;

[TestClass]
public sealed class VehicleDeconstructionTests
{
    [TestMethod]
    public void Parked_vehicle_dismantles_atomically_and_pause_keeps_progress_reservation_and_lock()
    {
        var content = Content() with { Designs = Content().Designs.Select(d => d.Design != WorldFixture.Vehicle ? d : d with
            { StructuralGoods = [new(Material, 1)] }).ToImmutableArray() };
        using var original = Session(content);
        using var session = new CoreSession(original.State with { World = original.State.World with
            { Inventories = original.State.World.Inventories.Select(i => i.Id != Cargo ? i : i with { Goods = [] }).ToImmutableArray() } });
        Accept(session, new CoreCommand.DisbandTrain(new(1)));
        var job = Start(session, new CoreCommand.StartOwnedJob(new JobRequest.Dismantle(Facility, Vehicle, 0)));
        Assert.AreEqual(2L, job.FrozenTotalWork);
        var before = ((Lookup<InventoryView>.Found)session.Read().FindInventory(Local)).Value;
        Assert.AreEqual(2L, before.Reserved);
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 2) }, before.Reservations.Single().ExpectedReturns.ToArray());
        Step(session);
        Assert.AreEqual(1d, Job(session.Read(), job.Id).ProgressWork);
        Accept(session, new CoreCommand.SetDeconstructionPaused(job.Id, true));
        Step(session, 3);
        var paused = Job(session.Read(), job.Id);
        Assert.AreEqual(ConstructionState.Paused, paused.State);
        Assert.AreEqual(1d, paused.ProgressWork);
        Assert.AreEqual(InstanceLock.UnderDeconstruction, ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value.Lock);
        Reject(session, new CoreCommand.FormTrain([new(1)]), "OrderedVehicles", "instance_locked");
        Assert.AreEqual(2L, ((Lookup<InventoryView>.Found)session.Read().FindInventory(Local)).Value.Reserved);
        Accept(session, new CoreCommand.SetDeconstructionPaused(job.Id, false));
        var complete = Step(session).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 2) }, complete.ReturnedGoods.ToArray());
        Assert.IsInstanceOfType<Lookup<InstanceView>.Missing>(session.Read().FindInstance(Vehicle));
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(job.Id));
        var after = ((Lookup<InventoryView>.Found)session.Read().FindInventory(Local)).Value;
        Assert.AreEqual(0L, after.Reserved);
        Assert.AreEqual(before.Used + 2, after.Used);
    }
    [TestMethod]
    public void Grouped_vehicle_cannot_be_dismantled_and_reserved_train_inventory_prevents_disbanding()
    {
        using var original = Session();
        Reject(original, new CoreCommand.StartOwnedJob(new JobRequest.Dismantle(Facility, Vehicle, 0)),
            "Instance.Details.Train", "vehicle_in_train");
        using var reserved = new CoreSession(original.State with { World = original.State.World with
        {
            Inventories = original.State.World.Inventories.Select(i => i.Id != Cargo ? i : i with
                { Goods = [], Reservations = [new(new(99), 1, [new(Material, 1)])] }).ToImmutableArray()
        } });
        Reject(reserved, new CoreCommand.DisbandTrain(new(1)), "Train.Inventory", "inventory_not_empty");
    }

    [TestMethod]
    public void Construction_capability_without_vehicle_size_limit_can_dismantle()
    {
        using var session = Parked(Content(maximum: null));
        var job = Start(session, new CoreCommand.StartOwnedJob(new JobRequest.Dismantle(Facility, Vehicle, 0)));
        Assert.AreEqual(2L, job.FrozenTotalWork);
        var completed = Step(session, 2).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 3) }, completed.ReturnedGoods.ToArray());
    }

    [TestMethod]
    public void Restored_placeholders_return_only_valid_parts_and_floor_root_structure_without_placeholder_repair_work()
    {
        var content = Content();
        var design = content.Designs.Single(d => d.Design == WorldFixture.Vehicle);
        var absent = new GoodId.Voxel(WorldFixture.Ore);
        var placeholder = design.Parts[0] with { Id = new(1), Capabilities = [],
            Definition = new CoreDefinitionReference.Voxel(WorldFixture.Ore), RepairGood = absent };
        content = content with { Goods = content.Goods.Add(content.Goods[0] with { Good = absent }),
            Designs = content.Designs.Select(d => d.Design != design.Design ? d : d with
        {
            Parts = d.Parts.Add(placeholder), StructuralGoods = [new(Material, 3)],
            BillOfMaterials = [new(Material, 4), new(absent, 1)], BillOfMaterialsItemCount = 5
        }).ToImmutableArray() };
        using var parked = Parked(content);
        using var damaged = new CoreSession(parked.State with { World = parked.State.World with
        {
            Instances = parked.State.World.Instances.Select(i => i.Id != Vehicle ? i : i with
                { StructureDurability = 50, Parts = i.Parts.Select(p => p.Id == new PartId(1) ? p with { Current = 50 } : p).ToImmutableArray() }).ToImmutableArray()
        } });
        using var session = RestoreWithoutOre(damaged);
        Assert.IsTrue(((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value.Parts
            .Single(p => p.Part == new PartId(1)).DefinitionMissing);
        var target = Content().Designs.Single(d => d.Design == WorldFixture.Vehicle) with
            { Design = new(new string('8', 64)), Composition = new(new string('9', 64)) };
        var change = new DesignChangeProjection(design.Design, design.Composition,
            new(session.State.Content.Identity, target, []), [new(new(0), new(0))], [], [], 1);
        Reject(session, new CoreCommand.StartOwnedJob(new JobRequest.Refit(Facility, Vehicle, change, [Local], 0)),
            "Change", "content_missing");
        var job = Start(session, new CoreCommand.StartOwnedJob(new JobRequest.Dismantle(Facility, Vehicle, 0)));
        Assert.AreEqual(5L, job.FrozenTotalWork);
        var inventory = ((Lookup<InventoryView>.Found)session.Read().FindInventory(Local)).Value;
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 2) }, inventory.Reservations.Single().ExpectedReturns.ToArray());
        var completed = Step(session, 5).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 2) }, completed.ReturnedGoods.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Whole_units_with_destroyed_or_missing_children_are_not_returned_and_valid_damaged_children_each_add_work(bool missing)
    {
        var content = Content();
        var design = content.Designs.Single(d => d.Design == WorldFixture.Vehicle);
        var part = design.Parts[0];
        var absent = new GoodId.Voxel(WorldFixture.Ore);
        content = content with { Goods = content.Goods.Add(content.Goods[0] with { Good = absent }), Designs = content.Designs.Select(d => d.Design != design.Design ? d : d with
        {
            Parts = [part, part with { Id = new(1), Capabilities = [] },
                part with { Id = new(2), Capabilities = [] }, part with { Id = new(3), Capabilities = [],
                    Definition = missing ? new CoreDefinitionReference.Voxel(WorldFixture.Ore) : part.Definition,
                    RepairGood = missing ? absent : Material }],
            StructuralGoods = [], BillOfMaterials = [new(Material, 2)], BillOfMaterialsItemCount = 2,
            ReturnUnits = [new(Material, 1, [new(0), new(1)], [], []), new(Material, 1, [new(2), new(3)], [], [])]
        }).ToImmutableArray() };
        using var parked = Parked(content);
        using var damaged = new CoreSession(parked.State with { World = parked.State.World with
        {
            Instances = parked.State.World.Instances.Select(i => i.Id != Vehicle ? i : i with
            {
                Parts = i.Parts.Select(p => p with { Current = p.Id.Value < 2 || p.Id.Value == 3 && missing ? 50 : p.Id.Value == 3 ? 0 : 100 }).ToImmutableArray()
            }).ToImmutableArray()
        } });
        using var session = RestoreWithoutOre(damaged);
        Assert.AreEqual(missing, ((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value.Parts
            .Single(p => p.Part == new PartId(3)).DefinitionMissing);
        var job = Start(session, new CoreCommand.StartOwnedJob(new JobRequest.Dismantle(Facility, Vehicle, 0)));
        Assert.AreEqual(4L, job.FrozenTotalWork);
        Assert.AreEqual(1L, ((Lookup<InventoryView>.Found)session.Read().FindInventory(Local)).Value.Reserved);
        var completed = Step(session, 4).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 1) }, completed.ReturnedGoods.ToArray());
    }

    [TestMethod]
    public void Restored_all_placeholder_vehicle_dismantles_without_empty_return_reservation()
    {
        var content = Content();
        var absent = new GoodId.Voxel(WorldFixture.Ore);
        content = content with { Goods = content.Goods.Add(content.Goods[0] with { Good = absent }),
            Designs = content.Designs.Select(d => d.Design != WorldFixture.Vehicle ? d : d with
        {
            Parts = d.Parts.Select(p => p with { Definition = new CoreDefinitionReference.Voxel(WorldFixture.Ore), RepairGood = absent }).ToImmutableArray(),
            StructuralGoods = [], BillOfMaterials = [new(absent, 1)], BillOfMaterialsItemCount = 1
        }).ToImmutableArray() };
        using var parked = Parked(content);
        using var session = RestoreWithoutOre(parked);
        Assert.IsTrue(((Lookup<InstanceView>.Found)session.Read().FindInstance(Vehicle)).Value.Parts.All(p => p.DefinitionMissing));
        var job = Start(session, new CoreCommand.StartOwnedJob(new JobRequest.Dismantle(Facility, Vehicle, 0)));
        Assert.AreEqual(1L, job.FrozenTotalWork);
        Assert.AreEqual(0d, job.Progress);
        Assert.IsTrue(((Lookup<InventoryView>.Found)session.Read().FindInventory(Local)).Value.Reservations.IsEmpty);
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode()));
        var completed = Step(session).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();
        Assert.IsTrue(completed.ReturnedGoods.IsEmpty);
        Assert.IsInstanceOfType<Lookup<InstanceView>.Missing>(session.Read().FindInstance(Vehicle));
    }

    [TestMethod]
    [DataRow(false, "object_missing")]
    [DataRow(true, "instance_destroyed")]
    public void Paused_job_fails_when_target_disappears_or_facility_is_destroyed_and_releases_reservation(bool facility, string reason)
    {
        using var original = Parked(Content());
        var job = Start(original, new CoreCommand.StartOwnedJob(new JobRequest.Dismantle(Facility, Vehicle, 0)));
        Accept(original, new CoreCommand.SetDeconstructionPaused(job.Id, true));
        var world = original.State.World;
        using var session = new CoreSession(original.State with { World = world with
        {
            Instances = facility ? world.Instances.Select(i => i.Id is InstanceId.Building ? i with
                { StructureDurability = 0 } : i).ToImmutableArray() : world.Instances.Where(i => i.Id != Vehicle).ToImmutableArray(),
            Settlements = facility ? world.Settlements : world.Settlements.Select(s => s with { ParkedVehicles = [] }).ToImmutableArray()
        } });
        var failed = Step(session).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionStateChanged>().Single();
        Assert.AreEqual(ConstructionState.Paused, failed.Before);
        Assert.AreEqual(ConstructionState.Failed, failed.After);
        Assert.AreEqual(reason, failed.Reasons.Single().Code);
        Assert.IsInstanceOfType<Lookup<JobView>.Missing>(session.Read().FindJob(job.Id));
        Assert.AreEqual(0L, ((Lookup<InventoryView>.Found)session.Read().FindInventory(Local)).Value.Reserved);
    }

    [TestMethod]
    public void Pausing_construction_is_rejected()
    {
        var content = Content();
        using var session = Session(content);
        var job = Start(session, Build(content, Local));
        Reject(session, new CoreCommand.SetDeconstructionPaused(job.Id, true), "Job.Kind", "invalid_state");
    }

    private static CoreSession Parked(CoreContentProjection content)
    {
        using var original = Session(content);
        var session = new CoreSession(original.State with { World = original.State.World with
        {
            Inventories = original.State.World.Inventories.Select(i => i.Id != Cargo ? i : i with { Goods = [] }).ToImmutableArray()
        } });
        Accept(session, new CoreCommand.DisbandTrain(new(1)));
        return session;
    }

    private static CoreSession RestoreWithoutOre(CoreSession original)
    {
        var saved = WorldFixture.Success(original.Capture(CapturePurpose.Save));
        var content = Changed(original.State.Content) with { Goods = original.State.Content.Goods
            .Where(g => g.Good != new GoodId.Voxel(WorldFixture.Ore)).ToImmutableArray() };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(
            WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode())), content, WorldFixture.Options));
        return (CoreSession)candidate.Activate();
    }
}
