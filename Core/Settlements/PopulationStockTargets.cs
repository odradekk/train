using System.Collections.Immutable;
using System.Numerics;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Settlements;

internal static class PopulationStockTargets
{
    // 调用方依基本、生产、改善顺序分配容量；基本目标被压缩时，改善预算必须为零。
    internal static ImmutableArray<GoodsQuantity> Calculate(PopulationRuleProjection? rule,
        long residents, double basicReserveHours, double improvementReserveHours,
        long stapleStock, long preparedStock, ImmutableArray<PopulationConsumptionRemainder> remainders,
        PopulationNeedClass needClass, long capacityBudget)
    {
        if (rule is null || capacityBudget == 0) return [];

        var targets = new Dictionary<GoodId, double>();
        var staple = rule.ConsumptionGoods.Single(g => g.Satisfies.Any(c => c.Need == PopulationNeed.Food)
            && !g.Satisfies.Any(c => c.Need == PopulationNeed.ImprovedDiet));
        var prepared = rule.ConsumptionGoods.Single(g => g.Satisfies.Any(c => c.Need == PopulationNeed.ImprovedDiet));
        var food = FoodTargets(rule, residents, basicReserveHours, stapleStock, preparedStock, remainders);

        if (needClass == PopulationNeedClass.Basic)
        {
            Add(staple.Good, food.Staple);
            Add(prepared.Good, food.Prepared);
        }
        else
        {
            Add(prepared.Good, Math.Max(0, Required(PopulationNeed.ImprovedDiet, improvementReserveHours)
                / Conversion(prepared, PopulationNeed.ImprovedDiet) - Remainder(prepared.Good)
                - food.Prepared));
        }

        foreach (var need in rule.Needs.Where(n => n.Class == needClass
            && n.Need is not PopulationNeed.Food and not PopulationNeed.ImprovedDiet))
        {
            var good = rule.ConsumptionGoods.Single(g => g.Satisfies.Any(c => c.Need == need.Need));
            var hours = needClass == PopulationNeedClass.Basic ? basicReserveHours : improvementReserveHours;
            Add(good.Good, Math.Max(0, Required(need.Need, hours) / Conversion(good, need.Need) - Remainder(good.Good)));
        }

        return Allocate(targets.Select(t => (Good: t.Key, Quantity: new BigInteger(Math.Ceiling(t.Value)))), capacityBudget);

        double Required(PopulationNeed need, double hours) => rule.Needs.Single(n => n.Need == need)
            .AmountPerPersonSecond * residents * (hours * 3600);
        double Remainder(GoodId good) => remainders.Where(g => g.Good == good).Sum(g => g.Units);
        void Add(GoodId good, double units) => targets[good] = targets.GetValueOrDefault(good) + units;
    }

    internal static (double Staple, double Prepared) FoodTargets(PopulationRuleProjection rule,
        long residents, double basicReserveHours, long stapleStock, long preparedStock,
        ImmutableArray<PopulationConsumptionRemainder> remainders)
    {
        var staple = rule.ConsumptionGoods.Single(g => g.Satisfies.Any(c => c.Need == PopulationNeed.Food)
            && !g.Satisfies.Any(c => c.Need == PopulationNeed.ImprovedDiet));
        var prepared = rule.ConsumptionGoods.Single(g => g.Satisfies.Any(c => c.Need == PopulationNeed.ImprovedDiet));
        var food = rule.Needs.Single(n => n.Need == PopulationNeed.Food)
            .AmountPerPersonSecond * residents * (basicReserveHours * 3600);
        food = Math.Max(0, food - Remainder(staple.Good) * Conversion(staple, PopulationNeed.Food));
        var stapleStockCoverage = stapleStock * Conversion(staple, PopulationNeed.Food);
        var preparedBasicUnits = Math.Min(preparedStock + Remainder(prepared.Good),
            Math.Max(0, food - stapleStockCoverage) / Conversion(prepared, PopulationNeed.Food));
        var preparedBasicStock = Math.Max(0, preparedBasicUnits - Remainder(prepared.Good));
        return (Math.Ceiling(Math.Max(0, food - preparedBasicUnits * Conversion(prepared, PopulationNeed.Food))
            / Conversion(staple, PopulationNeed.Food)), Math.Ceiling(preparedBasicStock));

        double Remainder(GoodId good) => remainders.Where(g => g.Good == good).Sum(g => g.Units);
    }

    internal static ImmutableArray<GoodsQuantity> Allocate(IEnumerable<(GoodId Good, BigInteger Quantity)> targets, long capacityBudget)
    {
        var integerTargets = targets.Where(t => t.Quantity > 0)
            .OrderBy(t => WorldReferences.GoodKey(t.Good), StringComparer.Ordinal).ToArray();
        BigInteger total = 0;
        foreach (var target in integerTargets) total += target.Quantity;
        if (total <= capacityBudget)
            return integerTargets.Select(t => new GoodsQuantity(t.Good, (long)t.Quantity)).ToImmutableArray();

        var scaled = new long[integerTargets.Length];
        var fractions = new BigInteger[integerTargets.Length];
        long allocated = 0;
        for (var i = 0; i < integerTargets.Length; i++)
        {
            var numerator = integerTargets[i].Quantity * capacityBudget;
            scaled[i] = checked((long)(numerator / total));
            fractions[i] = numerator % total;
            allocated = checked(allocated + scaled[i]);
        }
        foreach (var index in Enumerable.Range(0, scaled.Length).OrderByDescending(i => fractions[i])
            .ThenBy(i => i).Take(checked((int)(capacityBudget - allocated)))) scaled[index]++;
        return integerTargets.Select((t, i) => new GoodsQuantity(t.Good, scaled[i])).Where(t => t.Quantity > 0).ToImmutableArray();

    }

    private static double Conversion(ConsumptionGoodProjection good, PopulationNeed need) =>
        good.Satisfies.Single(c => c.Need == need).AmountPerGoodUnit;
}
