using System.Numerics;
using Train.Core.Contracts;
using Train.Core.Settlements;
using Train.Core.World;

namespace Train.Core.Trading;

internal static class MarketPrices
{
    internal static IEnumerable<(Money Price, long Quantity)> Goods(WorldState world, CoreContentProjection content,
        GoodId good, bool buy, MarketStockTargets market, long stock, long count)
    {
        if (count == 0) yield break;
        var rule = market.Rule!;
        var prices = rule.GoodPrices.SingleOrDefault(p => p.Good == good)?.Parameters ?? rule.DefaultGoodsPrices;
        var value = WorldGoods.Definition(world, content, good)!.BaseValue;
        BigInteger? minimumStock = null;
        if (buy && prices.Sensitivity.Value > 0)
        {
            var sensitivity = new BigInteger(prices.Sensitivity.Value);
            // k(2s+1) >= 2T(10000+k-L) must hold for every target 0 <= T <= NPC capacity.
            var bound = (BigInteger)2 * market.Capacity * BigInteger.Max(0, 10000 + sensitivity - prices.MinimumMultiplier.Value);
            minimumStock = (bound + sensitivity - 1) / (2 * sensitivity);
        }
        var range = (Minimum: 0L, Maximum: -1L, Target: 0L);
        long completed = 0;
        while (completed < count)
        {
            var lowerStock = buy ? stock - completed - 1 : checked(stock + completed);
            if (minimumStock is { } minimum && lowerStock >= minimum)
            {
                var quantity = (long)BigInteger.Min(count - completed, lowerStock - minimum + 1);
                yield return (Good(value, prices, 0, 0, true), quantity);
                completed += quantity;
                continue;
            }
            if (lowerStock < range.Minimum || lowerStock > range.Maximum) range = market.TargetRange(good, lowerStock);
            var price = Good(value, prices, range.Target, (BigInteger)lowerStock * 2 + 1, buy);
            var maximum = count - completed;
            var distance = buy ? lowerStock - range.Minimum : range.Maximum - lowerStock;
            if (maximum - 1 > distance) maximum = distance + 1;
            // With a fixed target, price is monotonic in the midpoint, so equal endpoint prices cover the entire segment.
            long low = 1, high = maximum;
            while (low < high)
            {
                var middle = low + (high - low) / 2 + 1;
                if (SamePrice(middle)) low = middle;
                else high = middle - 1;
            }
            yield return (price, low);
            completed += low;

            bool SamePrice(long quantity)
            {
                var midpoint = (BigInteger)lowerStock * 2 + 1 + (buy ? -2 : 2) * new BigInteger(quantity - 1);
                try { return Good(value, prices, range.Target, midpoint, buy) == price; }
                // A later price may overflow after an automatic trade would already have stopped.
                catch (OverflowException) { return false; }
            }
        }
    }

    internal static MarketRuleProjection? Rule(WorldState world, CoreContentProjection content)
    {
        var id = world.Source.Configuration.Rules.Market;
        return content.MissingReferences.Any(m => m.Reference == new CoreDefinitionReference.Rule(id))
            ? null : content.MarketRules.SingleOrDefault(r => r.Id == id);
    }

    internal static Money Good(Money value, GoodsPriceParameters parameters, long target, BigInteger twiceStock, bool buy)
    {
        BigInteger numerator;
        BigInteger denominator;
        if (target == 0)
        {
            numerator = (BigInteger)value.Value * parameters.MinimumMultiplier.Value * (10000 + parameters.Spread.Value);
            denominator = 100_000_000;
        }
        else
        {
            var twiceTarget = (BigInteger)target * 2;
            var multiplier = twiceTarget * 10000 + parameters.Sensitivity.Value * (twiceTarget - twiceStock);
            multiplier = BigInteger.Clamp(multiplier, twiceTarget * parameters.MinimumMultiplier.Value,
                twiceTarget * parameters.MaximumMultiplier.Value);
            numerator = value.Value * multiplier * (buy ? 10000 + parameters.Spread.Value : 10000 - parameters.Spread.Value);
            denominator = twiceTarget * 100_000_000;
        }
        return new(Math.Max(1, checked((long)Round(numerator, denominator, buy))));
    }

    internal static Money Vehicle(InstanceState instance, DesignProjection design, BasisPoints multiplier, bool ceiling)
    {
        BigInteger numerator = 0, denominator = 1;
        Add(design.StructuralBaseValue.Value, Math.Min(instance.StructureDurability, design.MaxStructureDurability), design.MaxStructureDurability);
        foreach (var part in design.Parts)
            Add(part.BaseValue.Value, Math.Min(instance.Parts.Single(p => p.Id == part.Id).Current, part.MaxDurability), part.MaxDurability);
        return new(checked((long)Round(numerator * multiplier.Value, denominator * 10000, ceiling)));

        void Add(long value, long current, long maximum)
        {
            numerator = numerator * maximum + (BigInteger)value * current * denominator;
            denominator *= maximum;
            var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
            numerator /= divisor;
            denominator /= divisor;
        }
    }

    private static BigInteger Round(BigInteger numerator, BigInteger denominator, bool ceiling)
        => BigInteger.DivRem(numerator, denominator, out var remainder) + (ceiling && remainder != 0 ? 1 : 0);
}
