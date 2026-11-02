using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Production.ProductionFixture;

namespace Train.Core.Tests.Settlements;

public sealed partial class SettlementNetChangeTests
{
    [TestMethod]
    public void Population_uses_continuous_consumption_then_current_remainders_with_basic_before_improvement()
    {
        var rule = PopulationFixture.Rule() with
        {
            ConsumptionIntervalTicks = 60, StatisticsPeriodTicks = 600,
            Needs = PopulationFixture.Rule().Needs.Select(n => n with
            { AmountPerPersonSecond = n.Need == PopulationNeed.Food ? .1 : n.Need == PopulationNeed.Clothing ? .05 : .2 }).ToImmutableArray(),
            ConsumptionGoods = PopulationFixture.Rule().ConsumptionGoods.Select(g => g.Good != PopulationFixture.PreparedFood ? g : g with
            { Satisfies = [new(PopulationNeed.Food, 2), new(PopulationNeed.ImprovedDiet, 3)] }).ToImmutableArray()
        };
        var content = PopulationFixture.Content(rule, PopulationFixture.Stock(1, 1, 1));
        using var original = PopulationFixture.Start(content);
        var missing = Changed(content) with { MarketRules = [], MissingReferences =
            [new(new CoreDefinitionReference.Rule(WorldFixture.Rules.Market), "market")] };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(
            PopulationFixture.Capture(original), missing, WorldFixture.Options));
        using var session = (CoreSession)candidate.Activate();
        var retained = SettlementView(session.Read());
        AssertRate(session, PopulationFixture.Food, -1);
        AssertRate(session, PopulationFixture.Clothing, -.5);
        AssertRate(session, PopulationFixture.PreparedFood, -2d / 3);
        Step(session, 60);
        Assert.AreEqual(0L, PopulationFixture.Quantity(session.Read(), PopulationFixture.Food));
        Assert.AreEqual(0L, PopulationFixture.Quantity(session.Read(), PopulationFixture.PreparedFood));
        // Only 1/3 prepared food and 1/2 clothing remain after the real interval; they remain consumable.
        AssertRate(session, PopulationFixture.Food, 0);
        AssertRate(session, PopulationFixture.Clothing, -.5);
        AssertRate(session, PopulationFixture.PreparedFood, -1d / 3);
        Assert.AreEqual(-2d / 3, retained.Market.Goods.Single(g => g.Good == PopulationFixture.PreparedFood)
            .ExpectedNetChangePerSecond, 1e-12);
        var beforeWorld = session.State.World;
        var beforeSave = PopulationFixture.Capture(session).Encode();
        for (var i = 0; i < 3; i++) AssertRate(session, PopulationFixture.PreparedFood, -1d / 3);
        Assert.AreSame(beforeWorld, session.State.World);
        Assert.AreEqual(beforeSave, PopulationFixture.Capture(session).Encode());
        var changed = Changed(content, 'E') with { PopulationRules = [rule with
        {
            Revision = new(new string('A', 64)),
            Needs = rule.Needs.Select(n => n with { AmountPerPersonSecond = n.AmountPerPersonSecond / 2 }).ToImmutableArray()
        }] };
        Publish(session, changed);
        // A running period keeps its frozen rule, so 1/2 clothing still satisfies the next interval.
        AssertRate(session, PopulationFixture.Clothing, -.5);
    }

    [TestMethod]
    public void Produced_food_and_population_consumption_merge_before_display_rounding()
    {
        var food = PopulationFixture.Food;
        var content = Content(work: 10, rate: 2, stock: [new(Input, 10), new(food, 10)], outputs: [new(food, 3)]);
        content = content with { PopulationRules = [content.PopulationRules[0] with
        {
            ConsumptionIntervalTicks = 120, StatisticsPeriodTicks = 600,
            Needs = content.PopulationRules[0].Needs.Select(n => n with { AmountPerPersonSecond = .01 }).ToImmutableArray()
        }] };
        using var session = Owned(content);
        Step(session, 2);
        // Production 2 * 3/10 = .6/s; ten residents consume .1/s.
        AssertRate(session, food, .5);
        AssertRate(session, Input, -.4);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Population_missing_required_content_has_no_flow(bool missingRule)
    {
        var content = PopulationFixture.Content(PopulationFixture.Rule() with { StatisticsPeriodTicks = 600 }, PopulationFixture.Stock());
        using var original = PopulationFixture.Start(content);
        Step(original);
        var reference = missingRule ? (CoreDefinitionReference)new CoreDefinitionReference.Rule(WorldFixture.Rules.Population)
            : new CoreDefinitionReference.Voxel(((GoodId.Voxel)PopulationFixture.Food).Definition);
        var missing = Changed(content) with
        {
            PopulationRules = missingRule ? [] : content.PopulationRules,
            Goods = missingRule ? content.Goods : content.Goods.Where(g => g.Good != PopulationFixture.Food).ToImmutableArray(),
            MissingReferences = [new(reference, "fixture")]
        };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(PopulationFixture.Capture(original), missing, WorldFixture.Options));
        using var session = candidate.Activate();
        Assert.IsInstanceOfType<PopulationExecution.Disabled>(SettlementView(session.Read()).Population.Execution);
        AssertRate(session, PopulationFixture.Clothing, 0);
        AssertRate(session, PopulationFixture.PreparedFood, 0);
    }
}
