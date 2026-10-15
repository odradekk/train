using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Inventory;

internal static class InventoryStorage
{
    internal static long Used(InventoryState inventory) => inventory.Goods.Sum(g => g.Quantity);
    internal static long Reserved(InventoryState inventory) => inventory.Reservations.Sum(r => r.Capacity);

    private static long InstanceCapacity(WorldState world, CoreContentProjection content, InstanceId id)
    {
        var instance = world.Instances.Single(i => i.Id == id);
        long capacity = 0;
        foreach (var part in InstanceCapabilities.EffectiveParts(world, content, instance))
        {
            foreach (var storage in part.Capabilities.OfType<PartCapability.Storage>())
                capacity = checked(capacity + storage.Capacity);
        }
        return capacity;
    }

    internal static (SettlementStorageView View, long Uncommitted) Settlement(WorldState world, CoreContentProjection content,
        SettlementState settlement, ulong step, LeaseId? exclude = null)
    {
        var physical = settlement.Plots.Where(p => p.Building is not null)
            .Sum(p => InstanceCapacity(world, content, new InstanceId.Building(p.Building!.Value)));
        var own = world.Inventories.Single(i => i.Id == settlement.Inventory);
        long used = Used(own), reserved = Reserved(own), active = 0, expired = 0;
        long committed = checked(used + reserved);
        foreach (var lease in world.Leases.Where(l => l.Settlement == settlement.Id && l.Id != exclude))
        {
            var inventory = world.Inventories.Single(i => i.Id == lease.Inventory);
            var goods = Used(inventory);
            var reservation = Reserved(inventory);
            used = checked(used + goods);
            reserved = checked(reserved + reservation);
            if (step < lease.ExpiresAtStep)
            {
                active = checked(active + lease.Capacity);
                committed = checked(committed + Math.Max(lease.Capacity, checked(goods + reservation)));
            }
            else
            {
                expired = checked(expired + goods);
                committed = checked(committed + goods + reservation);
            }
        }
        var rule = content.MarketRules.SingleOrDefault(r => r.Id == world.Source.Configuration.Rules.Market);
        var limit = rule is null ? 0 : checked((long)((Int128)physical * rule.RentableCapacityRatio.Value / 10000));
        var free = Math.Max(0, checked(physical - committed));
        return (new(physical, used, reserved, active, expired,
            Math.Max(0, checked(physical - active - expired)),
            Math.Min(Math.Max(0, checked(limit - active)), free)), free);
    }

    internal static InventoryView Read(WorldState world, CoreContentProjection content, ulong step, InventoryState inventory)
    {
        long capacity, shared = long.MaxValue;
        WorldLocation location;
        var access = InventoryAccess.ReadWrite;
        if (inventory.Owner is InventoryOwner.Train owner)
        {
            var train = world.Trains.Single(t => t.Id == owner.Id);
            capacity = train.Vehicles.Sum(v => InstanceCapacity(world, content, new InstanceId.Vehicle(v)));
            location = train.Position switch
            {
                TrainPosition.Node n => new WorldLocation.Node(n.Id),
                TrainPosition.Edge e => new WorldLocation.Edge(e.Id, e.DistanceMeters),
                _ => throw new InvalidOperationException("列车位置无效。")
            };
        }
        else
        {
            var lease = inventory.Owner is InventoryOwner.Lease l ? world.Leases.Single(x => x.Id == l.Id) : null;
            var settlement = world.Settlements.Single(s => s.Id == (lease?.Settlement ?? ((InventoryOwner.Settlement)inventory.Owner).Id));
            var storage = Settlement(world, content, settlement, step).View;
            capacity = lease?.Capacity ?? storage.NpcCapacity;
            shared = Math.Max(0, checked(storage.PhysicalCapacity - storage.TotalUsed - storage.TotalReserved));
            location = new WorldLocation.Node(settlement.Node);
            if (settlement.Relation == SettlementRelation.Hostile) access = InventoryAccess.Frozen;
            else if (lease is not null && step >= lease.ExpiresAtStep) access = InventoryAccess.WithdrawOnly;
        }
        var used = Used(inventory);
        var reserved = Reserved(inventory);
        var available = access == InventoryAccess.ReadWrite
            ? Math.Min(Math.Max(0, checked(capacity - used - reserved)), shared) : 0;
        double mass = 0;
        foreach (var stack in inventory.Goods)
            if (!WorldGoods.DefinitionMissing(world, content, stack.Good)) mass += WorldGoods.Definition(world, content, stack.Good)!.MassTons * stack.Quantity;
        return new(inventory.Id, inventory.Owner, location, access, capacity, used, reserved, available, mass,
            inventory.Goods.Select(g => new InventoryStackView(g.Good, g.Quantity, WorldGoods.DefinitionMissing(world, content, g.Good))).ToImmutableArray(),
            inventory.Reservations);
    }
}
