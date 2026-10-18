using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Railway;
using Train.Core.Sessions;
using Train.Core.Settlements;
using Train.Core.Trading;
using Train.Core.World;
using Train.Core.Construction;

namespace Train.Core.Inventory;

internal static class InventoryCommands
{
    internal static void Validate(CoreCommand command)
    {
        if (command is CoreCommand.AcquireLease acquire)
        {
            InventoryLeases.Validate(acquire.Request);
            if (acquire.MaximumPayment.Value < 0) throw new ArgumentException("付款上限必须非负。", nameof(command));
            return;
        }
        var (request, condition) = Request(command);
        Validate(request);
        if (condition is { Value: < 0 }) throw new ArgumentException("价格条件必须非负。", nameof(command));
    }

    internal static void Validate(TradeQuoteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Train.Value == 0 || request.Settlement.Value == 0 || request.LocalInventory.Value == 0
            || request.Quantity <= 0 || !Enum.IsDefined(request.Action))
            throw new ArgumentException("货物操作须提供非零身份、正数量和有效动作。", nameof(request));
        WorldInputs.ValidateGood(request.Good);
    }

    private static (TradeQuoteRequest Request, Money? Condition) Request(CoreCommand command) => command switch
    {
        CoreCommand.LoadGoods c => (new(c.Train, c.Settlement, c.LocalInventory, TradeAction.Load, c.Good, c.Quantity), null),
        CoreCommand.UnloadGoods c => (new(c.Train, c.Settlement, c.LocalInventory, TradeAction.Unload, c.Good, c.Quantity), null),
        CoreCommand.BuyGoods c => (new(c.Train, c.Settlement, c.LocalInventory, TradeAction.Buy, c.Good, c.Quantity), c.MaximumPayment),
        CoreCommand.SellGoods c => (new(c.Train, c.Settlement, c.LocalInventory, TradeAction.Sell, c.Good, c.Quantity), c.MinimumReceipt),
        _ => throw new InvalidOperationException("货物操作无效。")
    };

    internal static TradeQuote Quote(WorldState world, CoreContentProjection content, ulong step,
        CoreRevision revision, TradeQuoteRequest request, Money? condition = null, AutomaticGoodsAction? automatic = null)
    {
        var trade = request.Action is TradeAction.Buy or TradeAction.Sell;
        var incoming = request.Action is TradeAction.Buy or TradeAction.Load;
        Money? total = trade ? null : new(0);
        long executable = 0;
        var train = world.Trains.FirstOrDefault(t => t.Id == request.Train);
        var settlement = world.Settlements.FirstOrDefault(s => s.Id == request.Settlement);
        var local = world.Inventories.FirstOrDefault(i => i.Id == request.LocalInventory);
        if (train is null) return Reject("object_missing", "Train", new WorldObjectId.Train(request.Train));
        if (settlement is null) return Reject("object_missing", "Settlement", new WorldObjectId.Settlement(request.Settlement));
        if (local is null) return Reject("object_missing", "LocalInventory", new WorldObjectId.Inventory(request.LocalInventory));
        if (TrainAccess(world, settlement, train) is { } access)
            return new(request, revision, executable, total, [access], []);
        var lease = local.Owner is InventoryOwner.Lease l ? world.Leases.Single(x => x.Id == l.Id) : null;
        if (local.Owner != new InventoryOwner.Settlement(settlement.Id) && (trade || lease?.Settlement != settlement.Id))
            return Reject("invalid_state", "LocalInventory", new WorldObjectId.Inventory(local.Id));
        if (trade && settlement.Relation == SettlementRelation.PlayerOwned)
            return Reject("invalid_state", "Settlement", new WorldObjectId.Settlement(settlement.Id));
        if (settlement.Relation == SettlementRelation.Hostile)
            return Reject("access_denied", "LocalInventory", new WorldObjectId.Inventory(local.Id));
        if (!trade && lease is null && settlement.Relation != SettlementRelation.PlayerOwned)
            return Reject("not_owned", "LocalInventory", new WorldObjectId.Inventory(local.Id));
        if (!trade && !incoming && lease is not null && step >= lease.ExpiresAtStep)
            return Reject("access_denied", "LocalInventory", new WorldObjectId.Inventory(local.Id));
        var rule = MarketPrices.Rule(world, content);
        if (trade && rule is null) return Reject("content_missing", "MarketRule");
        if (PlatformAccess(world, train) is { } platform)
            return new(request, revision, executable, total, [platform], []);
        if (WorldGoods.DefinitionMissing(world, content, request.Good))
            return Reject("content_missing", "Good", new WorldObjectId.Inventory(local.Id));
        var cargo = world.Inventories.Single(i => i.Id == train.Inventory);
        var source = incoming ? local : cargo;
        var destination = incoming ? cargo : local;
        var stock = source.Goods.FirstOrDefault(g => g.Good == request.Good)?.Quantity ?? 0;
        var space = InventoryStorage.Read(world, content, step, destination).Available;
        var localStock = local.Goods.FirstOrDefault(g => g.Good == request.Good)?.Quantity ?? 0;
        MarketStockTargets? market = trade || automatic is AutomaticGoodsAction.Load && lease is null
            ? new(world, content, settlement, step) : null;
        var marketQuantity = market?.Quantity(request.Good, localStock, Math.Min(request.Quantity, stock), incoming)
            ?? Math.Min(request.Quantity, stock);
        executable = Math.Min(marketQuantity, space);
        if (automatic is not null)
        {
            CoreProblem? incomplete = null;
            if (stock < request.Quantity)
                incomplete = new("insufficient_goods", "Quantity", new WorldObjectId.Inventory(source.Id), []);
            if (marketQuantity < Math.Min(request.Quantity, stock))
                incomplete = new(incoming ? "insufficient_goods" : "insufficient_demand", "Quantity", new WorldObjectId.Inventory(local.Id), []);
            if (space < marketQuantity)
                incomplete = new("insufficient_capacity", incoming ? "Train.Inventory.Capacity" : "LocalInventory.Capacity",
                    new WorldObjectId.Inventory(destination.Id), []);
            long amount = 0, completed = 0;
            if (trade)
            {
                var limit = automatic switch
                {
                    AutomaticGoodsAction.Buy buy => buy.MaximumUnitPrice,
                    AutomaticGoodsAction.Sell sell => sell.MinimumUnitPrice,
                    _ => null
                };
                foreach (var (price, quantity) in MarketPrices.Goods(world, content, request.Good, incoming, market!, localStock, executable))
                {
                    if (limit is { } unitLimit && (incoming ? price.Value > unitLimit.Value : price.Value < unitLimit.Value))
                    {
                        incomplete = new("price_condition_failed", incoming ? "MaximumUnitPrice" : "MinimumUnitPrice", null, []);
                        break;
                    }
                    var available = incoming ? Math.Min(quantity, (world.Money.Value - amount) / price.Value) : quantity;
                    amount = checked(amount + price.Value * available);
                    completed += available;
                    if (available < quantity)
                    {
                        incomplete = new("insufficient_money", "Wallet", null, []);
                        break;
                    }
                }
                if (!incoming) _ = checked(world.Money.Value + amount);
            }
            else completed = executable;
            return new(request, revision, completed, new(amount), incomplete is null ? [] : [incomplete], []);
        }
        if (stock < request.Quantity) return Reject("insufficient_goods", "Quantity", new WorldObjectId.Inventory(source.Id));
        if (marketQuantity < request.Quantity)
            return Reject(incoming ? "insufficient_goods" : "insufficient_demand", "Quantity", new WorldObjectId.Inventory(local.Id));
        if (space < request.Quantity)
            return Reject("insufficient_capacity", incoming ? "Train.Inventory.Capacity" : "LocalInventory.Capacity",
                new WorldObjectId.Inventory(destination.Id));
        if (trade)
        {
            long amount = 0, affordable = 0;
            foreach (var (price, quantity) in MarketPrices.Goods(world, content, request.Good, incoming, market!, localStock, request.Quantity))
            {
                if (amount <= world.Money.Value)
                    affordable += Math.Min(quantity, (world.Money.Value - amount) / price.Value);
                amount = checked(amount + price.Value * quantity);
            }
            total = new(amount);
            if (condition is { } limit && (incoming ? amount > limit.Value : amount < limit.Value))
                return Reject("price_condition_failed", incoming ? "MaximumPayment" : "MinimumReceipt");
            if (incoming && amount > world.Money.Value)
            {
                executable = affordable;
                return Reject("insufficient_money", "Wallet");
            }
            if (!incoming) _ = checked(world.Money.Value + amount);
        }
        var warnings = ImmutableArray.CreateBuilder<CoreProblem>();
        if (request.Action == TradeAction.Load && lease is null && rule is not null)
        {
            market = new(world, content, settlement, step);
            var after = market.Calculate(request.Good == market.Staple ? localStock - request.Quantity : market.StapleStock,
                request.Good == market.Prepared ? localStock - request.Quantity : market.PreparedStock);
            foreach (var target in after)
            {
                var beforeStock = local.Goods.Where(g => g.Good == target.Good).Sum(g => g.Quantity);
                var afterStock = beforeStock - (target.Good == request.Good ? request.Quantity : 0);
                var beforeTarget = market.Current.Where(g => g.Good == target.Good).Sum(g => g.Quantity);
                if (Math.Max(0, target.Quantity - afterStock) > Math.Max(0, beforeTarget - beforeStock))
                    warnings.Add(new("restock_shortage", "LocalInventory.Goods", new WorldObjectId.Inventory(local.Id), [WorldReferences.GoodKey(target.Good)]));
            }
        }
        return new(request, revision, request.Quantity, total, [], warnings.ToImmutable());

        TradeQuote Reject(string code, string field, WorldObjectId? subject = null)
            => new(request, revision, executable, total, [new(code, field, subject, [])], []);
    }

    internal static CoreProblem? TrainAccess(WorldState world, SettlementState settlement, TrainState train)
    {
        if (train.Vehicles.Any(v => world.Instances.Single(i => i.Id == new InstanceId.Vehicle(v)).Ownership != SettlementRelation.PlayerOwned))
            return new("not_owned", "Train", new WorldObjectId.Train(train.Id), []);
        if (train.Position is not TrainPosition.Node node || node.Id != settlement.Node)
            return new("train_not_at_settlement", "Train.Position", new WorldObjectId.Train(train.Id), []);
        if (ConstructionJobs.TrainLocked(world, train))
            return new("instance_locked", "Train", new WorldObjectId.Train(train.Id), []);
        return null;
    }

    internal static CoreProblem? PlatformAccess(WorldState world, TrainState train)
        => !TrainDispatch.HoldsPlatform(world, train) || train.Activity != TrainActivity.Servicing
            || train.Position is not TrainPosition.Node { Phase: NodeTravelPhase.Docked } || train.SpeedMetersPerSecond != 0
            ? new("platform_required", "Train.Position", new WorldObjectId.Train(train.Id), []) : null;

    internal static CommandEffect Execute(WorldState world, CoreContentProjection content, CoreCommand command, ulong step)
    {
        if (command is CoreCommand.AcquireLease acquire) return InventoryLeases.Execute(world, content, acquire, step);
        var (request, condition) = Request(command);
        var quote = Quote(world, content, step, new(0), request, condition);
        if (!quote.Blockers.IsEmpty) return new CommandEffect.Rejected(quote.Blockers);
        return Apply(world, content, request, quote.Total!.Value);
    }

    internal static CommandEffect.Accepted Apply(WorldState world, CoreContentProjection content, TradeQuoteRequest request, Money amount)
    {
        var train = world.Trains.Single(t => t.Id == request.Train);
        var incoming = request.Action is TradeAction.Buy or TradeAction.Load;
        var source = incoming ? request.LocalInventory : train.Inventory;
        var destination = incoming ? train.Inventory : request.LocalInventory;
        var money = request.Action switch
        {
            TradeAction.Buy => checked(world.Money.Value - amount.Value),
            TradeAction.Sell => checked(world.Money.Value + amount.Value),
            _ => world.Money.Value
        };
        var after = world with
        {
            Money = new(money),
            Trains = world.Trains.Select(t => t.Id == train.Id ? RunReports.Transferred(t, request.Action, request.Quantity, amount) : t).ToImmutableArray(),
            Inventories = world.Inventories.Select(i => i.Id == source ? Move(i, -request.Quantity)
                : i.Id == destination ? Move(i, request.Quantity) : i).ToImmutableArray()
        };
        after = after with { DefinitionDependencies = WorldReferences.Dependencies(after, content) };
        return new CommandEffect.Accepted(content,
            [new CoreEvent.GoodsTransferred(request.Action, request.Good, request.Quantity,
                source, destination, amount, train.Id, request.Settlement)], after);

        InventoryState Move(InventoryState inventory, long difference)
        {
            var quantity = checked((inventory.Goods.FirstOrDefault(g => g.Good == request.Good)?.Quantity ?? 0) + difference);
            var goods = inventory.Goods.Where(g => g.Good != request.Good);
            if (quantity > 0) goods = goods.Append(new(request.Good, quantity));
            return inventory with { Goods = goods.OrderBy(g => WorldReferences.GoodKey(g.Good), StringComparer.Ordinal).ToImmutableArray() };
        }
    }
}
