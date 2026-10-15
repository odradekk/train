using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Settlements;

namespace Train.Core.Tests.Settlements;

[TestClass]
public sealed class PopulationStockTargetsTests
{
    private static readonly GoodId Staple = Good("a-staple");
    private static readonly GoodId Clothing = Good("b-clothing");
    private static readonly GoodId Prepared = Good("c-prepared");
    private static readonly GoodId Consumer = Good("d-consumer");
    private static readonly GoodId Electronics = Good("e-electronics");
    private static readonly GoodId Medical = Good("f-medical");

    [TestMethod]
    public void Fractional_needs_round_up_before_capacity_ties_use_stable_goods_order()
    {
        var staple = Good("z-staple");
        var clothing = Good("a-clothing");
        var original = Rule(.1, .9);
        var rule = original with
        {
            ConsumptionGoods = original.ConsumptionGoods.Select(g => g with
            {
                Good = g.Good == Staple ? staple : g.Good == Clothing ? clothing : g.Good
            }).ToImmutableArray()
        };
        AssertTargets(Calculate(rule, 2), new(clothing, 1), new(staple, 1));
        AssertTargets(Calculate(rule, 1), new GoodsQuantity(clothing, 1));
        AssertTargets(Calculate(rule with { ConsumptionGoods = rule.ConsumptionGoods.Reverse().ToImmutableArray() }, 1), new GoodsQuantity(clothing, 1));
    }

    [TestMethod]
    public void Capacity_uses_integer_proportions_and_largest_remainders()
    {
        AssertTargets(Calculate(Rule(5, 3), 5), new(Staple, 3), new(Clothing, 2));
        AssertTargets(Calculate(Rule(3, 1), 2), new GoodsQuantity(Staple, 2));
    }

    [TestMethod]
    public void Ordinary_inventory_does_not_turn_stock_target_into_purchase_gap()
    {
        AssertTargets(Calculate(Rule(10, 10), 100, [new(Staple, 4), new(Clothing, 20)]),
            new(Staple, 10), new(Clothing, 10));
    }

    [TestMethod]
    public void Prepared_stock_covers_food_once_and_is_retained_in_basic_budget()
    {
        var rule = Rule(100, 1, 100);
        ImmutableArray<GoodsQuantity> stock = [new(Staple, 20), new(Prepared, 80)];
        ImmutableArray<PopulationConsumptionRemainder> remainder = [new(Clothing, 1)];
        AssertTargets(Calculate(rule, 100, stock, remainder), new(Staple, 20), new(Prepared, 80));
        AssertTargets(Calculate(rule, 100, stock, needClass: PopulationNeedClass.Improvement), new GoodsQuantity(Prepared, 20));
        AssertTargets(Calculate(rule, 50, stock, remainder), new(Staple, 10), new(Prepared, 40));
        AssertTargets(Calculate(rule, 0, stock, needClass: PopulationNeedClass.Improvement));
    }

    [TestMethod]
    public void Staple_inventory_has_priority_over_prepared_inventory_for_basic_food()
    {
        AssertTargets(Calculate(Rule(10, 1, 10), 100, [new(Staple, 10), new(Prepared, 10)],
            [new(Clothing, 1)]), new GoodsQuantity(Staple, 10));
    }

    [TestMethod]
    public void Physical_remainders_reduce_future_targets_without_becoming_saleable_stock()
    {
        var rule = Rule(10, 5, 10);
        ImmutableArray<PopulationConsumptionRemainder> remainders = [new(Staple, .5), new(Prepared, .5), new(Clothing, .5)];
        AssertTargets(Calculate(rule, 100, [new(Prepared, 9)], remainders), new(Clothing, 5), new(Prepared, 9));
        AssertTargets(Calculate(rule, 100, [new(Prepared, 9)], remainders, PopulationNeedClass.Improvement), new GoodsQuantity(Prepared, 1));
    }

    [TestMethod]
    public void Basic_and_improvement_use_their_own_hours_and_capacity_budgets()
    {
        var rule = Rule(2, 2, 2);
        AssertTargets(PopulationStockTargets.Calculate(rule, 2, 1, 2, 0, 0, [], PopulationNeedClass.Basic, 8),
            new(Staple, 4), new(Clothing, 4));
        AssertTargets(PopulationStockTargets.Calculate(rule, 2, 1, 2, 0, 0,
            [new(Consumer, 1), new(Electronics, 1), new(Medical, 1)], PopulationNeedClass.Improvement, 5),
            new GoodsQuantity(Prepared, 5));
        AssertTargets(Calculate(rule, 0));
        AssertTargets(Calculate(null, 100));
    }

    [TestMethod]
    public void Proportional_products_can_exceed_long_without_losing_capacity()
    {
        var result = Calculate(Rule(1e15, 1e15), 1_000_000_000_000_001);
        AssertTargets(result, new(Staple, 500_000_000_000_001), new(Clothing, 500_000_000_000_000));
    }

    [TestMethod]
    public void Targets_larger_than_long_are_compressed_before_converting_to_inventory_quantities()
    {
        AssertTargets(Calculate(Rule(1e24, 1e24), 10), new(Staple, 5), new(Clothing, 5));
    }

    private static ImmutableArray<GoodsQuantity> Calculate(PopulationRuleProjection? rule, long capacity,
        ImmutableArray<GoodsQuantity> inventory = default,
        ImmutableArray<PopulationConsumptionRemainder> remainders = default,
        PopulationNeedClass needClass = PopulationNeedClass.Basic) =>
        PopulationStockTargets.Calculate(rule, 1, 1, 1,
            inventory.IsDefault ? 0 : inventory.Where(g => g.Good == rule?.ConsumptionGoods.Single(g => g.Satisfies.Any(c => c.Need == PopulationNeed.Food)
                && !g.Satisfies.Any(c => c.Need == PopulationNeed.ImprovedDiet)).Good).Sum(g => g.Quantity),
            inventory.IsDefault ? 0 : inventory.Where(g => g.Good == rule?.ConsumptionGoods.Single(g => g.Satisfies.Any(c => c.Need == PopulationNeed.ImprovedDiet)).Good).Sum(g => g.Quantity),
            (remainders.IsDefault ? ImmutableArray<PopulationConsumptionRemainder>.Empty : remainders)
                .AddRange(new PopulationConsumptionRemainder[] { new(Consumer, 1), new(Electronics, 1), new(Medical, 1) }), needClass, capacity);

    private static void AssertTargets(ImmutableArray<GoodsQuantity> actual, params GoodsQuantity[] expected) =>
        CollectionAssert.AreEqual(expected, actual.ToArray());

    private static GoodId Good(string value) => new GoodId.Voxel(new("fixture:" + value));

    private static PopulationRuleProjection Rule(double food, double clothing, double improvedDiet = 1) => new(
        new("fixture:population"), new(new string('1', 64)),
        [new(PopulationNeed.Food, PopulationNeedClass.Basic, food / 3600, 1),
            new(PopulationNeed.Clothing, PopulationNeedClass.Basic, clothing / 3600, 1),
            new(PopulationNeed.ImprovedDiet, PopulationNeedClass.Improvement, improvedDiet / 3600, 1),
            new(PopulationNeed.ConsumerGoods, PopulationNeedClass.Improvement, .25 / 3600, 1),
            new(PopulationNeed.ConsumerElectronics, PopulationNeedClass.Improvement, .25 / 3600, 1),
            new(PopulationNeed.MedicalSupplies, PopulationNeedClass.Improvement, .25 / 3600, 1)],
        [new(Staple, [new(PopulationNeed.Food, 1)], 0),
            new(Clothing, [new(PopulationNeed.Clothing, 1)], 1),
            new(Prepared, [new(PopulationNeed.Food, 1), new(PopulationNeed.ImprovedDiet, 1)], 2),
            new(Consumer, [new(PopulationNeed.ConsumerGoods, 1)], 3),
            new(Electronics, [new(PopulationNeed.ConsumerElectronics, 1)], 4),
            new(Medical, [new(PopulationNeed.MedicalSupplies, 1)], 5)],
        .5, 1, 2, 0, 0, 0);
}
