using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Railway;
using Train.Core.Sessions;
using Train.Core.World;

namespace Train.Core.Inventory;

internal static class InventoryCommands
{
    internal static void Validate(CoreCommand command)
    {
        if (command is CoreCommand.AcquireLease acquire)
        {
            InventoryLeases.Validate(acquire.Request);
            return;
        }
        var (train, settlement, inventory, good, quantity, _) = Transfer(command);
        if (train.Value == 0 || settlement.Value == 0 || inventory.Value == 0 || quantity <= 0)
            throw new ArgumentException("装卸须提供非零身份与正数量。", nameof(command));
        WorldInputs.ValidateGood(good);
    }

    private static (TrainId Train, SettlementId Settlement, InventoryId Inventory, GoodId Good, long Quantity, bool Load)
        Transfer(CoreCommand command) => command switch
        {
            CoreCommand.LoadGoods c => (c.Train, c.Settlement, c.LocalInventory, c.Good, c.Quantity, true),
            CoreCommand.UnloadGoods c => (c.Train, c.Settlement, c.LocalInventory, c.Good, c.Quantity, false),
            _ => throw new InvalidOperationException("装卸命令无效。")
        };

    internal static CommandEffect Execute(WorldState world, CoreContentProjection content, CoreCommand command, ulong step)
    {
        if (command is CoreCommand.AcquireLease acquire) return InventoryLeases.Execute(world, content, acquire, step);
        var (trainId, settlementId, inventoryId, good, quantity, load) = Transfer(command);
        var train = world.Trains.FirstOrDefault(t => t.Id == trainId);
        var settlement = world.Settlements.FirstOrDefault(s => s.Id == settlementId);
        var local = world.Inventories.FirstOrDefault(i => i.Id == inventoryId);
        if (train is null) return Reject("object_missing", "Train", new WorldObjectId.Train(trainId));
        if (settlement is null) return Reject("object_missing", "Settlement", new WorldObjectId.Settlement(settlementId));
        if (local is null) return Reject("object_missing", "LocalInventory", new WorldObjectId.Inventory(inventoryId));
        if (train.Vehicles.Any(v => world.Instances.Single(i => i.Id == new InstanceId.Vehicle(v)).Ownership != SettlementRelation.PlayerOwned))
            return Reject("not_owned", "Train", new WorldObjectId.Train(trainId));
        if (train.Position is not TrainPosition.Node position || position.Id != settlement.Node)
            return Reject("train_not_at_settlement", "Train.Position", new WorldObjectId.Train(trainId));
        var lease = local.Owner is InventoryOwner.Lease l ? world.Leases.Single(x => x.Id == l.Id) : null;
        if (local.Owner != new InventoryOwner.Settlement(settlementId) && lease?.Settlement != settlementId)
            return Reject("invalid_state", "LocalInventory", new WorldObjectId.Inventory(inventoryId));
        if (settlement.Relation == SettlementRelation.Hostile) return Reject("access_denied", "LocalInventory", new WorldObjectId.Inventory(inventoryId));
        if (lease is null && settlement.Relation != SettlementRelation.PlayerOwned) return Reject("not_owned", "LocalInventory", new WorldObjectId.Inventory(inventoryId));
        if (!load && lease is not null && step >= lease.ExpiresAtStep) return Reject("access_denied", "LocalInventory", new WorldObjectId.Inventory(inventoryId));
        if (!TrainDispatch.HoldsPlatform(world, train)) return Reject("platform_required", "Train.Position", new WorldObjectId.Train(trainId));
        if (InventoryStorage.DefinitionMissing(world, content, good)) return Reject("content_missing", "Good", new WorldObjectId.Inventory(inventoryId));
        var cargo = world.Inventories.Single(i => i.Id == train.Inventory);
        var source = load ? local : cargo;
        var destination = load ? cargo : local;
        if ((source.Goods.FirstOrDefault(g => g.Good == good)?.Quantity ?? 0) < quantity)
            return Reject("insufficient_goods", "Quantity", new WorldObjectId.Inventory(source.Id));
        if (InventoryStorage.Read(world, content, step, destination).Available < quantity)
            return Reject("insufficient_capacity", load ? "Train.Inventory.Capacity" : "LocalInventory.Capacity", new WorldObjectId.Inventory(destination.Id));
        var after = world with { Inventories = world.Inventories.Select(i => i.Id == source.Id ? Move(i, -quantity)
            : i.Id == destination.Id ? Move(i, quantity) : i).ToImmutableArray() };
        after = after with { DefinitionDependencies = WorldReferences.Dependencies(after, content) };
        return new CommandEffect.Accepted(content,
            [new CoreEvent.GoodsTransferred(load ? TradeAction.Load : TradeAction.Unload, good, quantity,
                source.Id, destination.Id, new(0), trainId, settlementId)], after);

        InventoryState Move(InventoryState inventory, long amount)
        {
            var count = checked((inventory.Goods.FirstOrDefault(g => g.Good == good)?.Quantity ?? 0) + amount);
            var goods = inventory.Goods.Where(g => g.Good != good);
            if (count > 0) goods = goods.Append(new(good, count));
            return inventory with { Goods = goods.OrderBy(g => WorldReferences.GoodKey(g.Good), StringComparer.Ordinal).ToImmutableArray() };
        }
    }

    private static CommandEffect.Rejected Reject(string code, string field, WorldObjectId subject)
        => new([new(code, field, subject, [])]);
}
