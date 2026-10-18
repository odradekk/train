using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;
using static Train.Core.Tests.Inventory.InventoryFixture;

namespace Train.Core.Tests.Inventory;

[TestClass]
public sealed class InventoryCapacityTests
{
    [TestMethod]
    public void Reservations_reduce_free_space_without_double_counting_active_lease_quota()
    {
        using var original = Session();
        var lease = Rent(original);
        Service(original);
        Accept(original, new CoreCommand.UnloadGoods(new(1), lease.Settlement, lease.Inventory, Voxel, 7));
        var state = original.State;
        var settlement = state.World.Settlements.Single();
        var train = state.World.Trains.Single();
        var inventories = state.World.Inventories.Select(i => i with { Reservations = i.Owner switch
        {
            InventoryOwner.Settlement => [new(new(1), 10, [new(Voxel, 3)])],
            InventoryOwner.Lease => [new(new(2), 5, [new(Composite, 2)])],
            _ => [new(new(3), 4, [new(Assembly, 1)])]
        } }).ToImmutableArray();
        using var session = new CoreSession(state with { World = state.World with { Inventories = inventories } });
        Assert.AreEqual(8L, ReadInventory(session, lease.Inventory).Available);
        Assert.AreEqual(70L, ReadInventory(session, settlement.Inventory).Available);
        Assert.AreEqual(73L, ReadInventory(session, train.Inventory).Available);
        Assert.AreEqual(70L, WorldFixture.Success(session.Read().QuoteLease(new LeaseRequest.Rent(settlement.Id, 1, 3))).CurrentlyRentableCapacity);
        Assert.AreEqual(5L, ReadInventory(session, lease.Inventory).Reserved);
        Assert.AreEqual(Composite, ReadInventory(session, lease.Inventory).Reservations.Single().ExpectedReturns.Single().Good);
        RejectWithoutWorldChange(session, new CoreCommand.UnloadGoods(train.Id, lease.Settlement, lease.Inventory, Voxel, 9));
        Step(session); Step(session);
        Assert.AreEqual(93L, ReadInventory(session, settlement.Inventory).Capacity);
        Assert.AreEqual(78L, ReadInventory(session, settlement.Inventory).Available);
        Assert.AreEqual(78L, WorldFixture.Success(session.Read().QuoteLease(new LeaseRequest.Rent(settlement.Id, 1, 3))).CurrentlyRentableCapacity);
    }

    [TestMethod]
    public void Rentable_ratio_counts_active_quota_and_expired_goods_only_as_physical_space()
    {
        var content = Content() with { MarketRules = [Content().MarketRules[0] with { RentableCapacityRatio = new(2000) }] };
        using var session = Session(content);
        var lease = Rent(session);
        Service(session);
        Accept(session, new CoreCommand.UnloadGoods(new(1), lease.Settlement, lease.Inventory, Voxel, 7));
        var request = new LeaseRequest.Rent(lease.Settlement, 1, 3);
        Assert.AreEqual(0L, WorldFixture.Success(session.Read().QuoteLease(request)).CurrentlyRentableCapacity);
        Step(session); Step(session);
        Assert.AreEqual(20L, WorldFixture.Success(session.Read().QuoteLease(request)).CurrentlyRentableCapacity);
        Assert.AreEqual(InventoryAccess.WithdrawOnly, ReadInventory(session, lease.Inventory).Access);
    }

    [TestMethod]
    public void Hostility_freezes_local_and_expired_rented_inventory_before_other_checks()
    {
        using var original = Session(Content(duration: 1));
        var lease = Rent(original, 20, 1);
        Step(original);
        var state = original.State;
        var world = state.World;
        var settlement = world.Settlements.Single();
        world = world with
        {
            Settlements = [settlement with { Relation = SettlementRelation.Hostile }],
            Nodes = world.Nodes.Select(n => n.Id == settlement.Node ? n with
            { Occupant = new NodeOccupant.Settlement(settlement.Id, SettlementRelation.Hostile) } : n).ToImmutableArray(),
            Instances = world.Instances.Select(i => i.Id is InstanceId.Building ? i with
            { Ownership = SettlementRelation.Hostile } : i).ToImmutableArray()
        };
        using var session = new CoreSession(state with { World = world });
        Assert.IsTrue(Lease(session, lease.Id).Expired);
        Assert.AreEqual(InventoryAccess.Frozen, ReadInventory(session, lease.Inventory).Access);
        Assert.AreEqual(InventoryAccess.Frozen, ReadInventory(session, settlement.Inventory).Access);
        Assert.AreEqual(0L, ReadInventory(session, lease.Inventory).Available);
        RejectWithoutWorldChange(session, new CoreCommand.LoadGoods(new(1), settlement.Id, lease.Inventory, Voxel, 1), "access_denied");
        RejectWithoutWorldChange(session, new CoreCommand.UnloadGoods(new(1), settlement.Id, lease.Inventory, Voxel, 1), "access_denied");
        var quote = WorldFixture.Success(session.Read().QuoteLease(new LeaseRequest.Renew(lease.Id, 1)));
        Assert.IsNotNull(quote.Total);
        Assert.AreEqual("access_denied", quote.Blockers[0].Code);
    }

    [TestMethod]
    public void Missing_storage_part_definition_removes_capacity_without_deleting_goods()
    {
        using var original = Session();
        var saved = WorldFixture.Success(original.Capture(CapturePurpose.Save));
        var decoded = WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode()));
        var content = Changed(Content(), '5') with { Goods = Content().Goods.Where(g => g.Good != Voxel).ToImmutableArray() };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(decoded, content, WorldFixture.Options));
        using var session = candidate.Activate();
        var train = ((Lookup<TrainView>.Found)session.Read().FindTrain(new(1))).Value;
        Assert.AreEqual(0.0, train.TractionKilonewtons);
        Assert.AreEqual(0.0, train.MaximumSpeedMetersPerSecond);
        Assert.IsTrue(train.Blockers.Any(p => p.Code == "control_missing"));
        var inventory = ReadInventory(session, train.Inventory);
        Assert.AreEqual(0L, inventory.Capacity);
        Assert.AreEqual(30L, inventory.Used);
        Assert.AreEqual(0L, inventory.Available);
        Assert.IsTrue(inventory.Stacks.Single(s => s.Good == Voxel).DefinitionMissing);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public void Restored_missing_parts_preserve_other_capabilities_and_saved_damage(bool composite, bool survivingControl)
    {
        var content = Content();
        var missingGood = composite ? Composite : Voxel;
        var knownGood = composite ? Voxel : Composite;
        content = content with { Designs = content.Designs.Select(d => d.Design == WorldFixture.Vehicle ? d with
        {
            Parts = [d.Parts[0] with { Definition = WorldReferences.GoodReference(missingGood)!, RepairGood = missingGood },
                d.Parts[0] with { Id = new(1), Definition = WorldReferences.GoodReference(knownGood)!, RepairGood = knownGood,
                    Capabilities = survivingControl
                        ? [new PartCapability.Control(), new PartCapability.Propulsion(30, 20), new PartCapability.Storage(40)]
                        : [new PartCapability.Propulsion(30, 20), new PartCapability.Storage(40)] }]
        } : d).ToImmutableArray() };
        using var generated = Session(content);
        var state = generated.State;
        using var original = new CoreSession(state with { World = state.World with
        {
            Instances = state.World.Instances.Select(i => i.Id is InstanceId.Vehicle ? i with
            { StructureDurability = 62, Parts = [new(new(0), 55), new(new(1), 75)] } : i).ToImmutableArray()
        } });
        var originalInstance = ((Lookup<InstanceView>.Found)original.Read().FindInstance(new InstanceId.Vehicle(new(1)))).Value;
        var saved = WorldFixture.Success(original.Capture(CapturePurpose.Save));
        var payload = saved.Encode();
        var decoded = WorldFixture.Success(CoreFactory.Create().DecodeState(payload));
        var missing = Changed(content, '5') with { Goods = content.Goods.Where(g => g.Good != missingGood).ToImmutableArray() };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(decoded, missing, WorldFixture.Options));
        using var session = candidate.Activate();
        var train = ((Lookup<TrainView>.Found)session.Read().FindTrain(new(1))).Value;
        Assert.AreEqual(30.0, train.TractionKilonewtons);
        Assert.AreEqual(20.0, train.MaximumSpeedMetersPerSecond);
        Assert.AreEqual(!survivingControl, train.Blockers.Any(p => p.Code == "control_missing"));
        var inventory = ReadInventory(session, train.Inventory);
        Assert.AreEqual(40L, inventory.Capacity);
        Assert.AreEqual(30L, inventory.Used);
        Assert.AreEqual(10L, inventory.Available);
        var instance = ((Lookup<InstanceView>.Found)session.Read().FindInstance(new InstanceId.Vehicle(new(1)))).Value;
        Assert.AreEqual(new DurabilityView(62, 100), instance.Structure);
        CollectionAssert.AreEqual(new PartDamageView[]
        {
            new(new(0), new(55, 100), true, false),
            new(new(1), new(75, 100), false, false)
        }, instance.Parts.ToArray());
        var adapted = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        Assert.AreEqual(saved.Checkpoint, adapted.Checkpoint);
        Assert.AreEqual(payload, saved.Encode());
        Assert.AreEqual(payload, decoded.Encode());
        var placeholder = WorldFixture.Success(CoreFactory.Create().DecodeState(adapted.Encode()));
        using var repairedCandidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(placeholder, content, WorldFixture.Options));
        using var repaired = repairedCandidate.Activate();
        var repairedTrain = ((Lookup<TrainView>.Found)repaired.Read().FindTrain(new(1))).Value;
        Assert.AreEqual(130.0, repairedTrain.TractionKilonewtons);
        Assert.AreEqual(10.0, repairedTrain.MaximumSpeedMetersPerSecond);
        Assert.IsEmpty(repairedTrain.Blockers);
        Assert.AreEqual(140L, ReadInventory(repaired, repairedTrain.Inventory).Capacity);
        var repairedInstance = ((Lookup<InstanceView>.Found)repaired.Read().FindInstance(new InstanceId.Vehicle(new(1)))).Value;
        Assert.AreEqual(originalInstance.Structure, repairedInstance.Structure);
        Assert.IsTrue(repairedInstance.Parts.All(p => !p.DefinitionMissing));
        CollectionAssert.AreEqual(originalInstance.Parts.ToArray(), repairedInstance.Parts.ToArray());
        Assert.AreEqual(saved.Checkpoint, WorldFixture.Success(repaired.Capture(CapturePurpose.Save)).Checkpoint);
    }
}
