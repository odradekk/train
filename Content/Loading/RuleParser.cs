using System.Collections.Immutable;
using System.Text.Json;
using static Train.Content.Loading.DefinitionJson;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal static partial class RuleParser
{
    internal static ParsedRule? Parse(byte[] bytes, LoadPackage package, string path, string kind)
    {
        using var document = ContentDocument.Parse(bytes, package, path);
        if (document is null) return null;
        C.ContentReference? subject = null;
        try
        {
            var root = document.RootElement;
            var json = new DefinitionJson(root, package, path, kind + "-rule", 1);
            var id = new K.RuleDefinitionId(json.Id);
            subject = new C.ContentReference.Core(new K.CoreDefinitionReference.Rule(id));
            return kind switch
            {
                "population" => Population(root, json, id, path),
                "market" => Market(root, json, id, path),
                "construction" => Construction(root, id, path),
                "railway" => Railway(root, json, id, path),
                "combat" => Combat(root, id, path),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }
        catch (DefinitionFormatException error)
        {
            package.Reject(error.Code, path, error.Field, error.Message, subject);
            return null;
        }
    }

    private static ParsedRule.Market Market(JsonElement root, DefinitionJson json, K.RuleDefinitionId id, string path)
    {
        Object(root, "", "schema_version", "id", "simulation");
        var sim = Required(root, "simulation", "");
        const string field = "/simulation";
        Object(sim, field, "basic_reserve_hours", "production_reserve_hours", "improvement_reserve_hours",
            "default_goods_prices", "good_prices", "vehicle_buy_multiplier_basis_points", "vehicle_sell_multiplier_basis_points",
            "vehicle_purchase_limits", "automatic_evaluation_interval_ticks", "recent_report_limit", "commission_money_per_work",
            "rent_money_per_capacity_hour", "rentable_capacity_ratio_basis_points", "lease_duration_ticks");
        var goods = ImmutableArray.CreateBuilder<RuleGoodReference>();
        var prices = ImmutableArray.CreateBuilder<K.GoodPriceOverride>();
        foreach (var (value, itemField) in Items(Required(sim, "good_prices", field), field + "/good_prices"))
        {
            Object(value, itemField, "good", "parameters");
            var good = new K.VoxelDefinitionId(json.Reference(Required(value, "good", itemField), itemField + "/good"));
            goods.Add(new(good, itemField + "/good"));
            prices.Add(new(new K.GoodId.Voxel(good), Price(Required(value, "parameters", itemField), itemField + "/parameters")));
        }
        Unique(goods.Select(good => good.Id), field + "/good_prices");
        var limits = ImmutableArray.CreateBuilder<K.VehiclePurchaseLimit>();
        var limitArray = Required(sim, "vehicle_purchase_limits", field);
        Array(limitArray, field + "/vehicle_purchase_limits", 5);
        foreach (var (value, itemField) in Items(limitArray, field + "/vehicle_purchase_limits"))
        {
            Object(value, itemField, "size", "maximum_for_sale");
            limits.Add(new(EnumValue<K.SizeClass>(Required(value, "size", itemField), itemField + "/size"),
                Int32(Required(value, "maximum_for_sale", itemField), itemField + "/maximum_for_sale", 0)));
        }
        Unique(limits.Select(limit => limit.Size), field + "/vehicle_purchase_limits");
        var projection = new K.MarketRuleProjection(id, default, N("basic_reserve_hours"), N("production_reserve_hours"),
            N("improvement_reserve_hours"), Price(Required(sim, "default_goods_prices", field), field + "/default_goods_prices"),
            prices.OrderBy(price => ((K.GoodId.Voxel)price.Good).Definition.Value, StringComparer.Ordinal).ToImmutableArray(),
            new(I("vehicle_buy_multiplier_basis_points", 1)), new(I("vehicle_sell_multiplier_basis_points", 1)),
            limits.OrderBy(limit => limit.Size).ToImmutableArray(), I("automatic_evaluation_interval_ticks", 1), I("recent_report_limit", 1),
            new(L("commission_money_per_work", 0)), new(L("rent_money_per_capacity_hour", 0)),
            new(I("rentable_capacity_ratio_basis_points", 0, 10000)), L("lease_duration_ticks", 1));
        return new(projection with { Revision = RuleSemantics.Revision(projection) }, path,
            goods.OrderBy(good => good.Id.Value, StringComparer.Ordinal).ToImmutableArray());
        double N(string key) => Number(Required(sim, key, field), field + "/" + key, 0);
        int I(string key, int minimum, int maximum = int.MaxValue) => Int32(Required(sim, key, field), field + "/" + key, minimum, maximum);
        long L(string key, long minimum) => Int64(Required(sim, key, field), field + "/" + key, minimum);
    }

    private static K.GoodsPriceParameters Price(JsonElement value, string field)
    {
        Object(value, field, "sensitivity_basis_points", "minimum_multiplier_basis_points", "maximum_multiplier_basis_points", "spread_basis_points");
        var minimum = I("minimum_multiplier_basis_points", 1);
        var maximum = I("maximum_multiplier_basis_points", 1);
        if (maximum < minimum) Fail(field + "/maximum_multiplier_basis_points", "价格上限不能小于下限。");
        return new(new(I("sensitivity_basis_points", 0)), new(minimum), new(maximum), new(I("spread_basis_points", 0, 9999)));
        int I(string key, int min, int max = int.MaxValue) => Int32(Required(value, key, field), field + "/" + key, min, max);
    }

    private static ParsedRule.Construction Construction(JsonElement root, K.RuleDefinitionId id, string path)
    {
        Object(root, "", "schema_version", "id", "simulation");
        var sim = Required(root, "simulation", "");
        const string field = "/simulation";
        Object(sim, field, "founding_duration_ticks", "construction_work_per_item", "refit_work_per_changed_item",
            "deconstruction_work_per_item", "repair_work_per_damaged_part", "power_per_work", "labor_per_work");
        var projection = new K.ConstructionRuleProjection(id, default,
            UInt64(Required(sim, "founding_duration_ticks", field), field + "/founding_duration_ticks", 1),
            L("construction_work_per_item"), L("refit_work_per_changed_item"), L("deconstruction_work_per_item"),
            L("repair_work_per_damaged_part"), N("power_per_work"), N("labor_per_work"));
        return new(projection with { Revision = RuleSemantics.Revision(projection) }, path);
        long L(string key) => Int64(Required(sim, key, field), field + "/" + key, 1);
        double N(string key) => Number(Required(sim, key, field), field + "/" + key, 0);
    }

    private static ParsedRule.Railway Railway(JsonElement root, DefinitionJson json, K.RuleDefinitionId id, string path)
    {
        Object(root, "", "schema_version", "id", "braking_deceleration", "minimum_acceleration", "minimum_gap_meters",
            "rail_units_per_meter", "build_seconds_per_meter", "repair_seconds_per_break", "rail_good", "repair_rail_units_per_break", "appearance");
        var good = new K.VoxelDefinitionId(json.Reference(Required(root, "rail_good", ""), "/rail_good"));
        var projection = new K.RailwayRuleProjection(id, default, N("braking_deceleration"), N("minimum_acceleration"),
            N("minimum_gap_meters"), good, N("rail_units_per_meter"), N("build_seconds_per_meter"),
            Int64(Required(root, "repair_rail_units_per_break", ""), "/repair_rail_units_per_break", 1), N("repair_seconds_per_break"));
        var appearance = Required(root, "appearance", "");
        Object(appearance, "/appearance", "minimum_curve_radius_meters", "maximum_grade");
        return new(projection with { Revision = RuleSemantics.Revision(projection) },
            new(id, Number(Required(appearance, "minimum_curve_radius_meters", "/appearance"), "/appearance/minimum_curve_radius_meters", positive: true),
                Number(Required(appearance, "maximum_grade", "/appearance"), "/appearance/maximum_grade", positive: true)), path, [new(good, "/rail_good")]);
        double N(string key) => Number(Required(root, key, ""), "/" + key, positive: true);
    }

    private static ParsedRule.Combat Combat(JsonElement root, K.RuleDefinitionId id, string path)
    {
        Object(root, "", "schema_version", "id", "retreat_seconds", "morale_damage_coefficient", "weapon_destroyed_morale_loss",
            "morale_recovery_per_second", "surrender_threshold", "new_lair_interval_seconds");
        var projection = new K.CombatRuleProjection(id, default, N("retreat_seconds", positive: true), N("morale_damage_coefficient"),
            N("weapon_destroyed_morale_loss", 1), N("morale_recovery_per_second"), N("surrender_threshold", 1), N("new_lair_interval_seconds", positive: true));
        return new(projection with { Revision = RuleSemantics.Revision(projection) }, path);
        double N(string key, double maximum = double.MaxValue, bool positive = false) =>
            Number(Required(root, key, ""), "/" + key, 0, maximum, positive);
    }
}
