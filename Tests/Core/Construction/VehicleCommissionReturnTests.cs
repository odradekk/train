using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Construction.ConstructionFixture;

namespace Train.Core.Tests.Construction;

[TestClass]
public sealed class VehicleCommissionReturnTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Missing_return_lease_blocks_quote_and_command_without_business_changes(bool undersizedSource)
    {
        var content = ReturnContent();
        using var original = CommissionFixture.Session(content, money: 0);
        var lease = undersizedSource ? Rent(original, 3) : null;
        using var session = new CoreSession(original.State with { World = original.State.World with
        {
            Inventories = original.State.World.Inventories.Select(i => i.Id != lease?.Inventory ? i : i with
                { Goods = [new(Material, 1)] }).ToImmutableArray()
        } });
        var request = Removal(content, lease is null ? [] : [lease.Inventory]);
        var before = session.State.World;
        var quote = WorldFixture.Success(session.Read().GetJobQuote(request, JobFunding.NpcCommission));
        Assert.AreEqual("insufficient_capacity", quote.Blockers.Single().Code);
        Assert.AreEqual("ReturnInventory.Capacity", quote.Blockers.Single().Field);
        Assert.AreEqual(before, session.State.World);
        Reject(session, new CoreCommand.CommissionVehicleWork(request, new(0)),
            "ReturnInventory.Capacity", "insufficient_capacity");
    }

    [TestMethod]
    public void Destroyed_parts_and_rounded_down_structure_need_no_return_lease()
    {
        var content = ReturnContent();
        using var original = CommissionFixture.Session(content, damaged: true);
        using var session = new CoreSession(original.State with { World = original.State.World with
        {
            Instances = original.State.World.Instances.Select(i => i.Id != Vehicle ? i : i with
                { StructureDurability = 1 }).ToImmutableArray()
        } });
        var command = new CoreCommand.CommissionVehicleWork(Removal(content), new(115));
        var quote = WorldFixture.Success(session.Read().GetJobQuote(command.Request, JobFunding.NpcCommission));
        Assert.IsEmpty(quote.Blockers);
        var job = Start(session, command);
        Assert.IsNull(session.State.World.ConstructionJobs.Single().ReturnInventory);
        Assert.IsEmpty(session.State.World.Leases);
        var completed = Step(session, 5).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();
        Assert.AreEqual(job.Id, completed.Job);
        Assert.IsEmpty(completed.ReturnedGoods);
    }

    [TestMethod]
    public void Return_destination_skips_insufficient_first_lease_and_orders_by_inventory_identity()
    {
        var content = ReturnContent();
        using var original = CommissionFixture.Session(content);
        var first = Rent(original, 3);
        var second = Rent(original, 4);
        var third = Rent(original, 4);
        var world = original.State.World;
        using var session = new CoreSession(original.State with { World = world with
        {
            Leases = [world.Leases[2] with { Id = first.Id }, world.Leases[1], world.Leases[0] with { Id = third.Id }],
            Inventories = world.Inventories.Reverse().Select(i => i.Id == first.Inventory
                ? i with { Owner = new InventoryOwner.Lease(third.Id) }
                : i.Id == third.Inventory ? i with { Owner = new InventoryOwner.Lease(first.Id) } : i).ToImmutableArray()
        } });
        var job = Start(session, new CoreCommand.CommissionVehicleWork(Removal(content), new(long.MaxValue)));
        Assert.AreEqual(second.Inventory, session.State.World.ConstructionJobs.Single().ReturnInventory);
        Assert.AreEqual(4L, Inventory(session, second.Inventory).Reserved);
        Assert.AreEqual(job.Id, Inventory(session, second.Inventory).Reservations.Single().Job);
        Assert.IsEmpty(Inventory(session, first.Inventory).Reservations);
        Assert.IsEmpty(Inventory(session, third.Inventory).Reservations);
    }

    [TestMethod]
    public void Player_material_consumption_releases_capacity_before_return_lease_selection()
    {
        var content = ReturnContent();
        using var original = CommissionFixture.Session(content);
        var lease = Rent(original, 4);
        using var session = new CoreSession(original.State with { World = original.State.World with
        {
            Inventories = original.State.World.Inventories.Select(i => i.Id != lease.Inventory ? i : i with
                { Goods = [new(Material, 1)] }).ToImmutableArray()
        } });
        Assert.AreEqual(3L, Inventory(session, lease.Inventory).Available);
        var request = Removal(content, lease.Inventory);
        var quote = WorldFixture.Success(session.Read().GetJobQuote(request, JobFunding.NpcCommission));
        Assert.IsEmpty(quote.Blockers);
        Assert.AreEqual(new Money(15), quote.TotalPayment);
        Start(session, new CoreCommand.CommissionVehicleWork(request, new(15)));
        Assert.AreEqual(0L, Stock(session, lease.Inventory));
        Assert.AreEqual(4L, Inventory(session, lease.Inventory).Reserved);
        Assert.AreEqual(0L, Inventory(session, lease.Inventory).Available);
    }

    [TestMethod]
    public void Expired_lease_cannot_accept_a_new_return_reservation()
    {
        var content = ReturnContent();
        using var session = CommissionFixture.Session(content);
        var lease = Rent(session, 4, 1);
        Step(session);
        Assert.AreEqual(InventoryAccess.WithdrawOnly, Inventory(session, lease.Inventory).Access);
        var quote = WorldFixture.Success(session.Read().GetJobQuote(Removal(content), JobFunding.NpcCommission));
        Assert.AreEqual("insufficient_capacity", quote.Blockers.Single().Code);
        Assert.AreEqual("ReturnInventory.Capacity", quote.Blockers.Single().Field);
        Reject(session, new CoreCommand.CommissionVehicleWork(Removal(content), new(long.MaxValue)),
            "ReturnInventory.Capacity", "insufficient_capacity");
    }

    [TestMethod]
    public void Accepted_returns_survive_expiry_restore_and_strict_replay_then_release_reservation()
    {
        var content = ReturnContent();
        using var session = CommissionFixture.Session(content);
        var command = new CoreCommand.CommissionVehicleWork(Removal(content), new(115));
        Reject(session, command, "ReturnInventory.Capacity", "insufficient_capacity");
        var lease = Rent(session, 4, 1);
        var job = Start(session, command);
        // One added item and four removed items need five work units; all removed parts are intact.
        Assert.AreEqual(5L, job.FrozenTotalWork);
        Assert.AreEqual(9885L, session.Read().Economy.Wallet.Value);
        Step(session);
        Assert.AreEqual(InventoryAccess.WithdrawOnly, Inventory(session, lease.Inventory).Access);
        Assert.AreEqual(4L, Inventory(session, lease.Inventory).Reserved);
        var settlement = ((Lookup<SettlementView>.Found)session.Read().FindSettlement(lease.Settlement)).Value;
        Assert.AreEqual(4L, settlement.Market.Storage.TotalReserved);
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var factory = CoreFactory.Create();
        using var candidate = WorldFixture.Success(factory.PrepareRestore(
            WorldFixture.Success(factory.DecodeState(saved.Encode())), content, WorldFixture.Options));
        using var restored = candidate.Activate();
        Assert.AreEqual(saved.Encode(), WorldFixture.Success(restored.Capture(CapturePurpose.Save)).Encode());
        var completed = Step(session, 4).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();
        Step(restored, 4);
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 4) }, completed.ReturnedGoods.ToArray());
        Assert.AreEqual(4L, Stock(session, lease.Inventory));
        Assert.AreEqual(0L, Inventory(session, lease.Inventory).Reserved);
        Assert.IsEmpty(Inventory(session, lease.Inventory).Reservations);
        var final = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        Assert.AreEqual(final.Encode(), WorldFixture.Success(restored.Capture(CapturePurpose.Save)).Encode());
        var recording = session.ReadRecording();
        Assert.IsInstanceOfType<CommandDecision.Rejected>(recording.Commands[0].Result.Decision);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(recording.Commands[2].Result.Decision);
        using var replay = WorldFixture.Success(factory.PrepareReplay(new(2, WorldFixture.Options.Runtime,
            recording.Starts[0].State.Encode(), recording.Commands, recording.Checkpoints, recording.EndPosition, null),
            WorldFixture.Options.Runtime, [content]));
        var result = replay.Advance(5);
        Assert.AreEqual(ReplayStop.EndOfRecording, result.Stop);
        Assert.IsNull(result.Difference);
        Assert.IsNull(result.Fault);
        Assert.AreEqual(final.Checkpoint, replay.CaptureBoundary().Checkpoint);
    }

    [TestMethod]
    public void Frozen_lease_keeps_accepted_returns_in_city_reservation_totals()
    {
        var content = ReturnContent();
        using var original = CommissionFixture.Session(content);
        var lease = Rent(original, 4);
        var job = Start(original, new CoreCommand.CommissionVehicleWork(Removal(content), new(115)));
        var world = original.State.World;
        var settlement = world.Settlements.Single();
        using var frozen = new CoreSession(original.State with { World = world with
        {
            Settlements = [settlement with { Relation = SettlementRelation.Hostile }],
            Nodes = world.Nodes.Select(n => n.Id != settlement.Node ? n : n with
                { Occupant = new NodeOccupant.Settlement(settlement.Id, SettlementRelation.Hostile) }).ToImmutableArray(),
            Instances = world.Instances.Select(i => i.Id is not InstanceId.Building ? i : i with
                { Ownership = SettlementRelation.Hostile }).ToImmutableArray()
        } });
        var inventory = Inventory(frozen, lease.Inventory);
        Assert.AreEqual(InventoryAccess.Frozen, inventory.Access);
        Assert.AreEqual(4L, inventory.Reserved);
        Assert.AreEqual(job.Id, inventory.Reservations.Single().Job);
        Assert.AreEqual(4L, ((Lookup<SettlementView>.Found)frozen.Read().FindSettlement(settlement.Id))
            .Value.Market.Storage.TotalReserved);
        var saved = WorldFixture.Success(frozen.Capture(CapturePurpose.Save));
        var factory = CoreFactory.Create();
        using var candidate = WorldFixture.Success(factory.PrepareRestore(
            WorldFixture.Success(factory.DecodeState(saved.Encode())), content, WorldFixture.Options));
        using var restored = candidate.Activate();
        Assert.AreEqual(saved.Encode(), WorldFixture.Success(restored.Capture(CapturePurpose.Save)).Encode());
        Assert.AreEqual(InventoryAccess.Frozen, Inventory(restored, lease.Inventory).Access);
        Assert.AreEqual(job.Id, Inventory(restored, lease.Inventory).Reservations.Single().Job);
        var recovered = ((CoreSession)restored).State;
        using var thawed = new CoreSession(recovered with { World = recovered.World with
        {
            Settlements = world.Settlements, Nodes = world.Nodes, Instances = world.Instances
        } });
        var completed = Step(thawed, 5).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Single();
        CollectionAssert.AreEqual(new[] { new GoodsQuantity(Material, 4) }, completed.ReturnedGoods.ToArray());
        Assert.AreEqual(4L, Stock(thawed, lease.Inventory));
        Assert.AreEqual(0L, Inventory(thawed, lease.Inventory).Reserved);
        Assert.IsEmpty(Inventory(thawed, lease.Inventory).Reservations);
    }

    private static LeaseView Rent(CoreSession session, long capacity, ulong duration = 1) =>
        Accept(session, new CoreCommand.AcquireLease(new LeaseRequest.Rent(
            session.State.World.Settlements.Single().Id, capacity, duration), new(long.MaxValue)))
            .Events.Select(e => e.Event).OfType<CoreEvent.LeaseAcquired>().Single().Lease;

    private static InventoryView Inventory(ICoreSession session, InventoryId id) =>
        ((Lookup<InventoryView>.Found)session.Read().FindInventory(id)).Value;

    private static CoreContentProjection ReturnContent() => CommissionFixture.Content() with
    {
        MarketRules = [CommissionFixture.Content().MarketRules[0] with { RentableCapacityRatio = new(10000), LeaseDurationTicks = 1, RentMoneyPerCapacityHour = new(0) }],
        Designs = CommissionFixture.Content().Designs.Select(d => d.Design != WorldFixture.Vehicle ? d : d with
        {
            StructuralGoods = [new(Material, 3)], BillOfMaterials = [new(Material, 4)], BillOfMaterialsItemCount = 4
        }).ToImmutableArray()
    };

    private static JobRequest.Refit Removal(CoreContentProjection content, params InventoryId[] sources)
    {
        var input = Design(content);
        var target = input with { Design = input.Design with
        {
            Design = new(new string('9', 64)), Composition = new(new string('8', 64)),
            Parts = [], StructuralGoods = [new(Material, 1)],
            BillOfMaterials = [new(Material, 1)], BillOfMaterialsItemCount = 1
        } };
        return new(Facility, Vehicle, new(input.Design.Design, input.Design.Composition, target,
            [], [new(Material, 1)], [new(Material, 1, [new(0)], [], []),
                new(Material, 1, [], [new(Material, 1)], []), new(Material, 2, [], [new(Material, 2)], [])], 5), [..sources], 0);
    }
}





