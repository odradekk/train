using System.Collections.Immutable;
using System.Numerics;
using Train.Core.Contracts;
using Train.Core.Inventory;
using Train.Core.Production;
using Train.Core.World;

namespace Train.Core.Settlements;

internal sealed class MarketStockTargets
{
    private readonly SettlementState _settlement;
    private readonly PopulationRuleProjection? _population;
    private readonly long _capacity;
    private readonly GoodDefinitionProjection? _fuel;
    private readonly ImmutableArray<(FacilityView View, ProcessProjection? Process, double Rate)> _production;
    private readonly Dictionary<GoodId, long> _stock;
    private readonly long _available;
    internal MarketRuleProjection? Rule { get; }
    internal GoodId? Staple { get; }
    internal GoodId? Prepared { get; }
    internal long StapleStock { get; }
    internal long PreparedStock { get; }
    internal ImmutableArray<GoodsQuantity> Current { get; }
    internal ImmutableArray<FacilityView> Facilities => _production.Select(p => p.View).ToImmutableArray();

    internal MarketStockTargets(WorldState world, CoreContentProjection content, SettlementState settlement, ulong step)
    {
        _settlement = settlement;
        var id = world.Source.Configuration.Rules.Market;
        Rule = content.MissingReferences.Any(m => m.Reference == new CoreDefinitionReference.Rule(id))
            ? null : content.MarketRules.SingleOrDefault(r => r.Id == id);
        var inputs = PopulationOperations.ReadInputs(world, content);
        _population = PopulationOperations.Problems(world, settlement, inputs).IsEmpty
            ? settlement.PopulationState.Period?.Rule ?? inputs.Current : null;
        Staple = _population?.ConsumptionGoods.Single(g => g.Satisfies.Any(c => c.Need == PopulationNeed.Food)
            && !g.Satisfies.Any(c => c.Need == PopulationNeed.ImprovedDiet)).Good;
        Prepared = _population?.ConsumptionGoods.Single(g => g.Satisfies.Any(c => c.Need == PopulationNeed.ImprovedDiet)).Good;
        var inventory = world.Inventories.Single(i => i.Id == settlement.Inventory);
        _stock = inventory.Goods.ToDictionary(g => g.Good, g => g.Quantity);
        StapleStock = Staple is null ? 0 : _stock.GetValueOrDefault(Staple);
        PreparedStock = Prepared is null ? 0 : _stock.GetValueOrDefault(Prepared);
        _capacity = InventoryStorage.Settlement(world, content, settlement, step).View.NpcCapacity;
        _available = InventoryStorage.Read(world, content, step, inventory).Available;
        _fuel = content.Goods.Where(g => g.Good is GoodId.Voxel && g.FuelEnergyPerUnit is not null
                && !WorldGoods.DefinitionMissing(world, content, g.Good))
            .OrderBy(g => WorldReferences.GoodKey(g.Good), StringComparer.Ordinal).FirstOrDefault();
        _production = ProductionQueries.Potential(world, content, settlement);
        Current = Calculate(StapleStock, PreparedStock);
    }

    // Only the two substitutable foods make targets depend on tradable inventory.
    internal ImmutableArray<GoodsQuantity> Calculate(long stapleStock, long preparedStock)
    {
        if (Rule is null) return [];
        var targets = new Dictionary<GoodId, long>();
        var budget = _capacity;
        Add(PopulationStockTargets.Calculate(_population, _settlement.Population,
            Rule.BasicReserveHours, Rule.ImprovementReserveHours, stapleStock, preparedStock,
            _settlement.PopulationState.ConsumptionRemaining, PopulationNeedClass.Basic, budget));
        var fuelRemaining = _settlement.FuelEnergyRemaining;
        var accepted = new List<(ProcessProjection Process, ProductionBatchState? Batch, double Rate, double Energy)>();
        foreach (var (view, process, rate) in _production)
        {
            if (view.State is not (ProductionState.Running or ProductionState.WaitingForInputs or ProductionState.OutputBlocked)) continue;
            var batch = _settlement.ProductionFacilities.Single(f => f.Facility == view.Key).Batch;
            var minimum = batch is null ? ProductionQueries.Inputs(process!.Details).ToDictionary(g => g.Good, g => new BigInteger(g.Quantity)) : [];
            var energy = batch is null ? process!.WorkRequired * process.PowerPerWork
                : (batch.FrozenWorkRequired - batch.ProgressWork) * batch.FrozenPowerPerWork;
            var trialFuel = Fuel(minimum, energy, fuelRemaining);
            var size = minimum.Values.Aggregate(BigInteger.Zero, (sum, q) => sum + q);
            if (size > budget || energy > fuelRemaining && _fuel is null) continue;
            Add(minimum.Select(g => new GoodsQuantity(g.Key, checked((long)g.Value))).ToImmutableArray());
            fuelRemaining = trialFuel;
            accepted.Add((process!, batch, rate, energy));
        }
        foreach (var plan in accepted)
        {
            var extra = new Dictionary<GoodId, BigInteger>();
            var seconds = Rule.ProductionReserveHours * 3600;
            foreach (var input in ProductionQueries.Inputs(plan.Process.Details))
            {
                var desired = new BigInteger(Math.Ceiling(plan.Rate / plan.Process.WorkRequired * input.Quantity * seconds));
                var minimum = plan.Batch is null ? input.Quantity : 0;
                extra[input.Good] = BigInteger.Max(0, desired - minimum);
            }
            var energy = Math.Max(0, plan.Rate * plan.Process.PowerPerWork * seconds - plan.Energy);
            var materialFuel = _fuel is null ? BigInteger.Zero : extra.GetValueOrDefault(_fuel.Good);
            Fuel(extra, energy, fuelRemaining);
            var allotted = PopulationStockTargets.Allocate(extra.Select(g => (g.Key, g.Value)), budget);
            var fuelUnits = _fuel is null ? 0 : allotted.Where(g => g.Good == _fuel.Good).Sum(g => g.Quantity)
                - checked((long)BigInteger.Min(materialFuel, allotted.Where(g => g.Good == _fuel.Good).Sum(g => g.Quantity)));
            fuelRemaining = Math.Max(0, fuelRemaining + fuelUnits * (_fuel?.FuelEnergyPerUnit ?? 0) - energy);
            Add(allotted);
        }
        Add(PopulationStockTargets.Calculate(_population, _settlement.Population,
            Rule.BasicReserveHours, Rule.ImprovementReserveHours, stapleStock, preparedStock,
            _settlement.PopulationState.ConsumptionRemaining, PopulationNeedClass.Improvement, budget));
        return targets.OrderBy(t => WorldReferences.GoodKey(t.Key), StringComparer.Ordinal)
            .Select(t => new GoodsQuantity(t.Key, t.Value)).ToImmutableArray();

        void Add(ImmutableArray<GoodsQuantity> goods)
        {
            foreach (var good in goods)
            {
                targets[good.Good] = checked(targets.GetValueOrDefault(good.Good) + good.Quantity);
                budget = checked(budget - good.Quantity);
            }
        }
    }

    private double Fuel(Dictionary<GoodId, BigInteger> goods, double energy, double remaining)
    {
        if (energy <= remaining) return remaining - energy;
        if (_fuel is null) return 0;
        var units = new BigInteger(Math.Ceiling((energy - remaining) / _fuel.FuelEnergyPerUnit!.Value));
        goods[_fuel.Good] = goods.GetValueOrDefault(_fuel.Good) + units;
        return Math.Max(0, remaining + (double)units * _fuel.FuelEnergyPerUnit.Value - energy);
    }

    internal bool DependsOnStock(GoodId good) => Rule is { BasicReserveHours: > 0 } && (good == Staple || good == Prepared);

    internal long Target(GoodId good, long stock)
        => (DependsOnStock(good) ? Calculate(good == Staple ? stock : StapleStock,
                good == Prepared ? stock : PreparedStock) : Current)
            .Where(g => g.Good == good).Sum(g => g.Quantity);

    internal long Quantity(GoodId good, long stock, long limit, bool buy)
    {
        if (!DependsOnStock(good))
        {
            var target = Target(good, stock);
            return Math.Min(limit, buy ? Math.Max(0, stock - target) : Math.Max(0, target - stock));
        }
        var conversion = _population!.ConsumptionGoods.Single(g => g.Good == good).Satisfies.Single(c => c.Need == PopulationNeed.Food).AmountPerGoodUnit;
        var foodUnits = new BigInteger(Math.Ceiling(_population.Needs.Single(n => n.Need == PopulationNeed.Food)
            .AmountPerPersonSecond * _settlement.Population * (Rule!.BasicReserveHours * 3600) / conversion));
        var boundary = checked((long)BigInteger.Min(_capacity, foodUnits));
        long count = 0;
        while (count < limit)
        {
            var current = buy ? stock - count : checked(stock + count);
            var target = Target(good, current);
            // Above food coverage targets are constant; above capacity every target is bounded by capacity.
            if (!buy && current >= boundary)
                return checked(count + Math.Min(limit - count, Math.Max(0, target - current)));
            if (buy && current > boundary)
            {
                var surplus = Math.Min(limit - count, current - Math.Max(boundary, target));
                if (surplus > 0) { count += surplus; continue; }
            }
            if (buy ? current <= target : current >= target) break;
            count++;
        }
        return count;
    }

    internal ImmutableArray<MarketGoodView> Goods(WorldState world, CoreContentProjection content)
    {
        return content.Goods.Select(g => g.Good).Concat(world.DesignGoods.Select(g => g.Good)).Concat(_stock.Keys)
            .Distinct().OrderBy(WorldReferences.GoodKey, StringComparer.Ordinal).Select(good =>
            {
                var stock = _stock.GetValueOrDefault(good);
                var target = Current.Where(g => g.Good == good).Sum(g => g.Quantity);
                ImmutableArray<CoreProblem> problems = _settlement.Relation == SettlementRelation.PlayerOwned
                    ? [new("invalid_state", "Settlement", new WorldObjectId.Settlement(_settlement.Id), [])]
                    : _settlement.Relation == SettlementRelation.Hostile
                        ? [new("access_denied", "LocalInventory", new WorldObjectId.Inventory(_settlement.Inventory), [])]
                        : Rule is null ? [new("content_missing", "MarketRule", null, [])]
                        : WorldGoods.DefinitionMissing(world, content, good) ? [new("content_missing", "Good", null, [])] : [];
                return new MarketGoodView(good, stock, target, target,
                    problems.IsEmpty ? Quantity(good, stock, stock, true) : 0,
                    problems.IsEmpty ? Quantity(good, stock, _available, false) : 0, problems);
            }).ToImmutableArray();
    }
}
