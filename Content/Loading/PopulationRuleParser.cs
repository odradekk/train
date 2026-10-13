using System.Collections.Immutable;
using System.Text.Json;
using Train.Content.Designs;
using static Train.Content.Loading.DefinitionJson;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal static partial class RuleParser
{
    private static ParsedRule.Population Population(JsonElement root, DefinitionJson json, K.RuleDefinitionId id, string path)
    {
        Object(root, "", "schema_version", "id", "simulation");
        var sim = Required(root, "simulation", "");
        const string field = "/simulation";
        Object(sim, field, "needs", "consumption_goods", "labor_ratio", "consumption_interval_ticks", "statistics_period_ticks",
            "base_growth_per_second", "improvement_growth_per_second", "maximum_loss_per_second");
        var needObject = Required(sim, "needs", field);
        var kinds = Enum.GetValues<K.PopulationNeed>();
        Object(needObject, field + "/needs", kinds.Select(kind => DesignEvaluator.Wire(kind)).ToArray());
        var needs = ImmutableArray.CreateBuilder<K.PopulationNeedProjection>();
        foreach (var kind in kinds)
        {
            var key = DesignEvaluator.Wire(kind);
            var needField = field + "/needs/" + key;
            var need = Required(needObject, key, field + "/needs");
            Object(need, needField, "class", "amount_per_person_second", "weight");
            var category = EnumValue<K.PopulationNeedClass>(Required(need, "class", needField), needField + "/class");
            var expected = kind is K.PopulationNeed.Food or K.PopulationNeed.Clothing ? K.PopulationNeedClass.Basic : K.PopulationNeedClass.Improvement;
            if (category != expected) Fail(needField + "/class", "需求分类与固定角色不符。");
            needs.Add(new(kind, category,
                Number(Required(need, "amount_per_person_second", needField), needField + "/amount_per_person_second", positive: true),
                Number(Required(need, "weight", needField), needField + "/weight", positive: true)));
        }
        var array = Required(sim, "consumption_goods", field);
        Array(array, field + "/consumption_goods", 6);
        var goods = ImmutableArray.CreateBuilder<RuleGoodReference>();
        var consumption = ImmutableArray.CreateBuilder<K.ConsumptionGoodProjection>();
        foreach (var (value, itemField) in Items(array, field + "/consumption_goods"))
        {
            Object(value, itemField, "good", "satisfies", "selection_order");
            var good = new K.VoxelDefinitionId(json.Reference(Required(value, "good", itemField), itemField + "/good"));
            goods.Add(new(good, itemField + "/good"));
            var conversions = ImmutableArray.CreateBuilder<K.NeedConversion>();
            foreach (var (conversion, conversionField) in Items(Required(value, "satisfies", itemField), itemField + "/satisfies"))
            {
                Object(conversion, conversionField, "need", "amount_per_good_unit");
                conversions.Add(new(EnumValue<K.PopulationNeed>(Required(conversion, "need", conversionField), conversionField + "/need"),
                    Number(Required(conversion, "amount_per_good_unit", conversionField), conversionField + "/amount_per_good_unit", positive: true)));
            }
            Unique(conversions.Select(conversion => conversion.Need), itemField + "/satisfies");
            if (conversions.Count == 0) Fail(itemField + "/satisfies", "换算数组不能为空。");
            consumption.Add(new(new K.GoodId.Voxel(good), conversions.OrderBy(conversion => conversion.Need).ToImmutableArray(),
                Int32(Required(value, "selection_order", itemField), itemField + "/selection_order", 0)));
        }
        Unique(goods.Select(good => good.Id), field + "/consumption_goods");
        Unique(consumption.Select(good => good.SelectionOrder), field + "/consumption_goods");
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var good in consumption)
        {
            var role = string.Join(",", good.Satisfies.Select(conversion => conversion.Need));
            if (!roles.Add(role)) Fail(field + "/consumption_goods", "每种生活货物角色必须恰好出现一次。");
        }
        var expectedRoles = new[] { "Food", "Clothing", "Food,ImprovedDiet", "ConsumerGoods", "ConsumerElectronics", "MedicalSupplies" };
        if (!roles.SetEquals(expectedRoles)) Fail(field + "/consumption_goods", "生活货物换算必须符合六种固定角色。");
        var staple = consumption.Single(good => good.Satisfies.Length == 1 && good.Satisfies[0].Need == K.PopulationNeed.Food);
        var prepared = consumption.Single(good => good.Satisfies.Length == 2);
        if (staple.SelectionOrder >= prepared.SelectionOrder) Fail(field + "/consumption_goods", "主食必须先于加工食品。");
        var interval = Int32(Required(sim, "consumption_interval_ticks", field), field + "/consumption_interval_ticks", 1);
        var period = Int32(Required(sim, "statistics_period_ticks", field), field + "/statistics_period_ticks", 1);
        if (period <= interval || period % interval != 0)
            Fail(field + "/statistics_period_ticks", "统计期必须为消费间隔大于 1 的整数倍。");
        var projection = new K.PopulationRuleProjection(id, default, needs.ToImmutable(),
            consumption.OrderBy(good => ((K.GoodId.Voxel)good.Good).Definition.Value, StringComparer.Ordinal).ToImmutableArray(),
            Number(Required(sim, "labor_ratio", field), field + "/labor_ratio", 0, 1), interval, period,
            N("base_growth_per_second"), N("improvement_growth_per_second"), N("maximum_loss_per_second"));
        return new(projection with { Revision = Semantic("population-rule", id.Value, projection) }, path,
            goods.OrderBy(good => good.Id.Value, StringComparer.Ordinal).ToImmutableArray());
        double N(string key) => Number(Required(sim, key, field), field + "/" + key, 0);
    }
}
