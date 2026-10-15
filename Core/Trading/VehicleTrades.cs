using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.World;

namespace Train.Core.Trading;

internal static class VehicleTrades
{
    internal static void Validate(VehicleTradeQuoteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Settlement.Value == 0 || request.Vehicle.Value == 0 || !Enum.IsDefined(request.Action))
            throw new ArgumentException("现车交易须提供非零身份和有效方向。", nameof(request));
    }

    internal static void Validate(CoreCommand command)
    {
        var (request, condition) = Request(command);
        Validate(request);
        if (condition.Value < 0) throw new ArgumentException("价格条件必须非负。", nameof(command));
    }

    internal static ImmutableArray<VehicleId> ForSale(WorldState world, SettlementState settlement)
        => settlement.Relation == SettlementRelation.PlayerOwned ? [] : settlement.ParkedVehicles
            .Where(v => world.Instances.Single(i => i.Id == new InstanceId.Vehicle(v)).Ownership != SettlementRelation.PlayerOwned)
            .OrderBy(v => v.Value).ToImmutableArray();

    internal static ImmutableArray<VehiclePurchaseCapacity> PurchaseCapacities(WorldState world,
        CoreContentProjection content, SettlementState settlement)
    {
        var rule = Rule(world, content);
        if (settlement.Relation == SettlementRelation.PlayerOwned || rule is null) return [];
        var forSale = ForSale(world, settlement);
        return Enum.GetValues<SizeClass>().Select(size =>
        {
            var count = forSale.Count(v => ((InstanceDetails.Vehicle)world.Instances.Single(i => i.Id == new InstanceId.Vehicle(v)).Details).Size == size);
            var limit = rule.VehiclePurchaseLimits.FirstOrDefault(l => l.Size == size)?.MaximumForSale ?? 0;
            var available = settlement.Relation == SettlementRelation.Hostile || !Supports(world, content, settlement, size)
                ? 0 : Math.Max(0, limit - count);
            return new VehiclePurchaseCapacity(size, count, limit, available);
        }).ToImmutableArray();
    }

    internal static VehicleTradeQuote Quote(WorldState world, CoreContentProjection content, CoreRevision revision,
        VehicleTradeQuoteRequest request, Money? priceCondition = null)
    {
        var buy = request.Action == VehicleTradeAction.Buy;
        var party = buy ? "Seller" : "Buyer";
        var settlement = world.Settlements.FirstOrDefault(s => s.Id == request.Settlement);
        var instance = world.Instances.FirstOrDefault(i => i.Id == new InstanceId.Vehicle(request.Vehicle));
        Money? total = null;
        if (settlement is null) return Reject("object_missing", party, new WorldObjectId.Settlement(request.Settlement));
        if (instance is null) return Reject("object_missing", "Vehicle", new WorldObjectId.Instance(new InstanceId.Vehicle(request.Vehicle)));
        if (settlement.Relation == SettlementRelation.Hostile) return Reject("access_denied", party, new WorldObjectId.Settlement(settlement.Id));
        if (settlement.Relation == SettlementRelation.PlayerOwned) return Reject("invalid_state", party, new WorldObjectId.Settlement(settlement.Id));
        if (instance.Ownership != (buy ? settlement.Relation : SettlementRelation.PlayerOwned))
            return Reject(buy ? "not_for_sale" : "not_owned", "Vehicle");
        var trainId = ((InstanceDetails.Vehicle)instance.Details).Train;
        var train = trainId is { } id ? world.Trains.Single(t => t.Id == id) : null;
        if (train is null ? !settlement.ParkedVehicles.Contains(request.Vehicle)
            : train.Position is not TrainPosition.Node node || node.Id != settlement.Node)
            return Reject("vehicles_not_colocated", "Vehicle.Location");
        if (trainId is not null) return Reject("vehicle_in_train", "Vehicle.Details.Train");
        var design = world.Designs.Single(d => d.Design == instance.Design);
        if (!design.MissingReferences.IsEmpty || design.Parts.Any(p => p.Missing
                || content.MissingReferences.Any(m => m.Reference == p.Definition)
                || p.Definition is CoreDefinitionReference.Voxel voxel && WorldGoods.DefinitionMissing(world, content, new GoodId.Voxel(voxel.Id))
                || p.Definition is CoreDefinitionReference.Composite composite && WorldGoods.DefinitionMissing(world, content, new GoodId.Composite(composite.Id)))
            || design.ReturnUnits.Any(u => !u.MissingReferences.IsEmpty)
            || WorldReferences.DesignGoods(design).Any(g => WorldGoods.DefinitionMissing(world, content, g.Good)))
            return Reject("content_missing", "Vehicle");
        var rule = Rule(world, content);
        if (rule is null) return Reject("content_missing", "MarketRule", null);
        if (!buy)
        {
            var size = ((InstanceDetails.Vehicle)instance.Details).Size;
            if (!Supports(world, content, settlement, size)) return Reject("capability_zero", "Buyer", new WorldObjectId.Settlement(settlement.Id));
            var capacity = PurchaseCapacities(world, content, settlement).Single(c => c.Size == size);
            if (capacity.Available == 0) return Reject("insufficient_capacity", "Buyer.VehiclePurchaseCapacity", new WorldObjectId.Settlement(settlement.Id));
        }
        total = MarketPrices.Vehicle(instance, design, buy ? rule.VehicleBuyMultiplier : rule.VehicleSellMultiplier, buy);
        if (priceCondition is { } condition && (buy ? total.Value.Value > condition.Value : total.Value.Value < condition.Value))
            return Reject("price_condition_failed", buy ? "MaximumPayment" : "MinimumReceipt", null);
        if (buy && total.Value.Value > world.Money.Value) return Reject("insufficient_money", "Wallet", null);
        if (!buy) _ = checked(world.Money.Value + total.Value.Value);
        return new(request, revision, total, []);

        VehicleTradeQuote Reject(string code, string field, WorldObjectId? subject = null)
            => new(request, revision, total, [new(code, field, subject ?? (field.StartsWith("Vehicle", StringComparison.Ordinal)
                ? new WorldObjectId.Instance(new InstanceId.Vehicle(request.Vehicle)) : null), [])]);
    }

    internal static CommandEffect Execute(WorldState world, CoreContentProjection content, CoreCommand command)
    {
        var (request, condition) = Request(command);
        var quote = Quote(world, content, new(0), request, condition);
        if (!quote.Blockers.IsEmpty) return new CommandEffect.Rejected(quote.Blockers);
        var buy = request.Action == VehicleTradeAction.Buy;
        var amount = quote.Total!.Value;
        var owner = buy ? SettlementRelation.PlayerOwned : world.Settlements.Single(s => s.Id == request.Settlement).Relation;
        return new CommandEffect.Accepted(content,
            [new CoreEvent.VehicleTraded(request.Vehicle, request.Settlement, request.Action, amount)], world with
            {
                Money = new(checked(buy ? world.Money.Value - amount.Value : world.Money.Value + amount.Value)),
                Instances = world.Instances.Select(i => i.Id == new InstanceId.Vehicle(request.Vehicle)
                    ? i with { Ownership = owner } : i).ToImmutableArray()
            });
    }

    private static bool Supports(WorldState world, CoreContentProjection content, SettlementState settlement, SizeClass size)
        => settlement.Plots.Where(p => p.Building is not null).Any(p => InstanceCapabilities.EffectiveParts(world, content,
                world.Instances.Single(i => i.Id == new InstanceId.Building(p.Building!.Value)))
            .SelectMany(part => part.Capabilities).OfType<PartCapability.Construction>()
            .Any(c => c.WorkPerSecond > 0 && c.MaximumVehicleSize is { } maximum && maximum >= size));

    private static MarketRuleProjection? Rule(WorldState world, CoreContentProjection content)
    {
        var id = world.Source.Configuration.Rules.Market;
        return content.MissingReferences.Any(m => m.Reference == new CoreDefinitionReference.Rule(id))
            ? null : content.MarketRules.SingleOrDefault(r => r.Id == id);
    }

    private static (VehicleTradeQuoteRequest Request, Money Condition) Request(CoreCommand command) => command switch
    {
        CoreCommand.BuyVehicle c => (new(c.Seller, c.Vehicle, VehicleTradeAction.Buy), c.MaximumPayment),
        CoreCommand.SellVehicle c => (new(c.Buyer, c.Vehicle, VehicleTradeAction.Sell), c.MinimumReceipt),
        _ => throw new InvalidOperationException("现车交易命令无效。")
    };
}
