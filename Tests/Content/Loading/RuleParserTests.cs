using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Loading;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class RuleParserTests
{
    [TestMethod]
    public void Construction_preserves_unsigned_and_signed_integer_limits()
    {
        var json = Construction();
        var sim = json["simulation"]!.AsObject();
        sim["founding_duration_ticks"] = ulong.MaxValue;
        foreach (var key in new[] { "construction_work_per_item", "refit_work_per_changed_item", "deconstruction_work_per_item", "repair_work_per_damaged_part" })
            sim[key] = long.MaxValue;
        var rule = (ParsedRule.Construction)Parse("construction", json)!;
        Assert.AreEqual(ulong.MaxValue, rule.Projection.FoundingDurationTicks);
        Assert.AreEqual(long.MaxValue, rule.Projection.ConstructionWorkPerItem);
        Assert.AreEqual(long.MaxValue, rule.Projection.RefitWorkPerChangedItem);
        Assert.AreEqual(long.MaxValue, rule.Projection.DeconstructionWorkPerItem);
        Assert.AreEqual(long.MaxValue, rule.Projection.RepairWorkPerDamagedPart);
    }

    [TestMethod]
    public void Population_retains_weights_and_selection_values_and_canonicalizes_unordered_arrays()
    {
        var original = Population();
        original["simulation"]!["needs"]!["food"]!["weight"] = 7;
        var first = (ParsedRule.Population)Parse("population", original)!;
        Assert.AreEqual(7d, first.Projection.Needs.Single(need => need.Need == K.PopulationNeed.Food).Weight);
        var reordered = original.DeepClone().AsObject();
        var goods = reordered["simulation"]!["consumption_goods"]!.AsArray();
        reordered["simulation"]!["consumption_goods"] = new JsonArray(goods.Reverse().Select(good => good!.DeepClone()).ToArray());
        var processed = reordered["simulation"]!["consumption_goods"]!.AsArray().Single(good => good!["satisfies"]!.AsArray().Count == 2)!;
        processed["satisfies"] = new JsonArray(processed["satisfies"]!.AsArray().Reverse().Select(value => value!.DeepClone()).ToArray());
        var second = (ParsedRule.Population)Parse("population", reordered)!;
        Assert.AreEqual(first.Revision, second.Revision);
        CollectionAssert.AreEqual(first.Projection.ConsumptionGoods.Select(good => good.SelectionOrder).ToArray(),
            second.Projection.ConsumptionGoods.Select(good => good.SelectionOrder).ToArray());
        Assert.AreEqual("/simulation/consumption_goods/0/good", second.Goods.Single(good => good.Id.Value == "core:medical").Field);
    }

    [TestMethod]
    public void Market_preserves_exact_money_and_basis_points_and_covers_all_sizes()
    {
        var json = Market();
        json["simulation"]!["commission_money_per_work"] = long.MaxValue;
        json["simulation"]!["vehicle_buy_multiplier_basis_points"] = int.MaxValue;
        var rule = (ParsedRule.Market)Parse("market", json)!;
        Assert.AreEqual(long.MaxValue, rule.Projection.CommissionMoneyPerWork.Value);
        Assert.AreEqual(int.MaxValue, rule.Projection.VehicleBuyMultiplier.Value);
        Assert.AreEqual(9999, rule.Projection.DefaultGoodsPrices.Spread.Value);
        CollectionAssert.AreEqual(Enum.GetValues<K.SizeClass>(), rule.Projection.VehiclePurchaseLimits.Select(limit => limit.Size).ToArray());
    }

    [TestMethod]
    [DataRow("approach_length_meters")]
    [DataRow("departure_length_meters")]
    [DataRow("node_speed_limit_meters_per_second")]
    public void Node_rules_are_required_projected_and_part_of_business_revision(string field)
    {
        var json = Railway();
        var original = (ParsedRule.Railway)Parse("railway", json)!;
        Assert.AreEqual(80, original.Projection.ApproachLengthMeters);
        Assert.AreEqual(80, original.Projection.DepartureLengthMeters);
        Assert.AreEqual(15, original.Projection.NodeSpeedLimitMetersPerSecond);
        json[field] = 25;
        Assert.AreNotEqual(original.Revision, Parse("railway", json)!.Revision);
        json.Remove(field);
        var package = Package();
        Assert.IsNull(Parse("railway", json, package));
        Assert.AreEqual("/" + field, package.Diagnostics.Single().Field);
    }

    [TestMethod]
    public void Version_one_railway_rules_are_rejected()
    {
        var json = Railway();
        json["schema_version"] = 1;
        var package = Package();
        Assert.IsNull(Parse("railway", json, package));
        Assert.AreEqual("unsupported_format", package.Diagnostics.Single().Code);
    }

    [TestMethod]
    public void Railway_appearance_changes_do_not_change_business_revision()
    {
        var json = Railway();
        var original = (ParsedRule.Railway)Parse("railway", json)!;
        json["appearance"]!["maximum_grade"] = 0.2;
        var changed = (ParsedRule.Railway)Parse("railway", json)!;
        Assert.AreEqual(original.Revision, changed.Revision);
        Assert.AreNotEqual(original.Appearance, changed.Appearance);
        Assert.AreEqual(new K.VoxelDefinitionId("core:rail"), changed.Projection.RailGood);
        Assert.AreEqual("/rail_good", changed.Goods.Single().Field);
        json["repair_seconds_per_break"] = 20;
        Assert.AreNotEqual(changed.Revision, Parse("railway", json)!.Revision);
    }

    [TestMethod]
    [DataRow("construction", "/simulation/founding_duration_ticks", "18446744073709551616")]
    [DataRow("construction", "/simulation/construction_work_per_item", "9223372036854775808")]
    [DataRow("construction", "/simulation/repair_work_per_damaged_part", "1.5")]
    [DataRow("construction", "/simulation/power_per_work", "1e999")]
    [DataRow("population", "/simulation/statistics_period_ticks", "60")]
    [DataRow("population", "/simulation/statistics_period_ticks", "61")]
    [DataRow("population", "/simulation/consumption_interval_ticks", "2147483648")]
    [DataRow("population", "/simulation/needs/food/amount_per_person_second", "0")]
    [DataRow("population", "/simulation/needs/food/class", "\"improvement\"")]
    [DataRow("market", "/simulation/default_goods_prices/spread_basis_points", "10000")]
    [DataRow("market", "/simulation/default_goods_prices/maximum_multiplier_basis_points", "1")]
    [DataRow("market", "/simulation/rentable_capacity_ratio_basis_points", "10001")]
    [DataRow("market", "/simulation/lease_duration_ticks", "0")]
    [DataRow("railway", "/approach_length_meters", "0")]
    [DataRow("railway", "/departure_length_meters", "-1")]
    [DataRow("railway", "/node_speed_limit_meters_per_second", "1e999")]
    [DataRow("railway", "/minimum_gap_meters", "0")]
    [DataRow("railway", "/appearance/maximum_grade", "0")]
    [DataRow("combat", "/surrender_threshold", "1.1")]
    [DataRow("combat", "/retreat_seconds", "0")]
    public void Invalid_ranges_and_cross_field_contracts_are_rejected(string kind, string pointer, string replacement)
    {
        var json = Fixture(kind);
        var parts = pointer.Split('/').Skip(1).ToArray();
        JsonNode target = json;
        foreach (var part in parts[..^1]) target = target is JsonArray array ? array[int.Parse(part)]! : target[part]!;
        if (target is JsonArray finalArray) finalArray[int.Parse(parts[^1])] = JsonNode.Parse(replacement);
        else target[parts[^1]] = JsonNode.Parse(replacement);
        var package = Package();
        Assert.IsNull(Parse(kind, json, package));
        var diagnostic = package.Diagnostics.Single();
        Assert.AreEqual("invalid_field", diagnostic.Code);
        Assert.AreEqual(pointer, diagnostic.Field);
        Assert.AreEqual(new C.ContentReference.Core(new K.CoreDefinitionReference.Rule(new("core:test"))), diagnostic.Subject);
    }

    [TestMethod]
    [DataRow("duplicate-order", "/simulation/consumption_goods")]
    [DataRow("staple-order", "/simulation/consumption_goods")]
    [DataRow("missing-processed-role", "/simulation/consumption_goods")]
    [DataRow("duplicate-size", "/simulation/vehicle_purchase_limits")]
    public void Invalid_array_relationships_report_their_exact_field(string mutation, string expectedField)
    {
        var kind = mutation == "duplicate-size" ? "market" : "population";
        var json = Fixture(kind);
        switch (mutation)
        {
            case "duplicate-order": json["simulation"]!["consumption_goods"]![2]!["selection_order"] = 0; break;
            case "staple-order": json["simulation"]!["consumption_goods"]![0]!["selection_order"] = 10; break;
            case "missing-processed-role":
                json["simulation"]!["consumption_goods"]![2]!["satisfies"] =
                    new JsonArray(new JsonObject { ["need"] = "improved_diet", ["amount_per_good_unit"] = 1 });
                break;
            case "duplicate-size": json["simulation"]!["vehicle_purchase_limits"]![4]!["size"] = "small"; break;
        }
        var package = Package();
        Assert.IsNull(Parse(kind, json, package));
        Assert.AreEqual("invalid_field", package.Diagnostics.Single().Code);
        Assert.AreEqual(expectedField, package.Diagnostics.Single().Field);
    }

    [TestMethod]
    [DataRow("population")]
    [DataRow("market")]
    public void Changing_only_a_goods_reference_changes_the_rule_revision(string kind)
    {
        var json = Fixture(kind);
        if (kind == "market") json["simulation"]!["good_prices"] = new JsonArray(new JsonObject
        {
            ["good"] = "first", ["parameters"] = json["simulation"]!["default_goods_prices"]!.DeepClone()
        });
        var original = Parse(kind, json)!;
        if (kind == "population") json["simulation"]!["consumption_goods"]![0]!["good"] = "replacement";
        else json["simulation"]!["good_prices"]![0]!["good"] = "replacement";
        var changed = Parse(kind, json)!;
        Assert.AreNotEqual(original.Revision, changed.Revision);
    }

    [TestMethod]
    public void Strict_json_unknown_fields_and_mismatched_filename_fail_before_publication()
    {
        var json = Combat();
        json["unknown"] = 1;
        var package = Package();
        Assert.IsNull(Parse("combat", json, package));
        Assert.AreEqual("invalid_field", package.Diagnostics.Single().Code);
        Assert.AreEqual("/unknown", package.Diagnostics.Single().Field);
        package = Package();
        Assert.IsNull(RuleParser.Parse(Encoding.UTF8.GetBytes("{\"schema_version\":1,\"schema_version\":1}"), package, "test.combat-rule.json", "combat"));
        Assert.AreEqual("invalid_json", package.Diagnostics.Single().Code);
        Assert.AreEqual("/schema_version", package.Diagnostics.Single().Field);
        package = Package();
        Assert.IsNull(RuleParser.Parse(Encoding.UTF8.GetBytes(Combat().ToJsonString()), package, "different.combat-rule.json", "combat"));
        Assert.AreEqual("invalid_field", package.Diagnostics.Single().Code);
        Assert.AreEqual("/id", package.Diagnostics.Single().Field);
    }

    private static ParsedRule? Parse(string kind, JsonObject json, LoadPackage? package = null) =>
        RuleParser.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()), package ?? Package(), "test." + kind + "-rule.json", kind);

    private static LoadPackage Package() => new(new("builtin"), "", true) { Metadata = new(new("core"), "core", "0.0.56", [], []) };
    private static JsonObject Fixture(string kind) => kind switch
    {
        "population" => Population(), "market" => Market(), "construction" => Construction(), "railway" => Railway(), "combat" => Combat(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    internal static JsonObject Construction() => JsonNode.Parse("""
        {"schema_version":1,"id":"test","simulation":{"founding_duration_ticks":60,"construction_work_per_item":1,
        "refit_work_per_changed_item":2,"deconstruction_work_per_item":3,"repair_work_per_damaged_part":4,"power_per_work":0,"labor_per_work":0}}
        """)!.AsObject();
    internal static JsonObject Railway() => JsonNode.Parse("""
        {"schema_version":2,"id":"test","approach_length_meters":80,"departure_length_meters":80,"node_speed_limit_meters_per_second":15,"braking_deceleration":1,"minimum_acceleration":2,"minimum_gap_meters":3,
        "rail_units_per_meter":4,"build_seconds_per_meter":5,"repair_seconds_per_break":6,"rail_good":"rail",
        "repair_rail_units_per_break":7,"appearance":{"minimum_curve_radius_meters":20,"maximum_grade":0.1}}
        """)!.AsObject();
    internal static JsonObject Combat() => JsonNode.Parse("""
        {"schema_version":1,"id":"test","retreat_seconds":1,"morale_damage_coefficient":0,"weapon_destroyed_morale_loss":1,
        "morale_recovery_per_second":0,"surrender_threshold":0,"new_lair_interval_seconds":2}
        """)!.AsObject();
    internal static JsonObject Market() => JsonNode.Parse("""
        {"schema_version":1,"id":"test","simulation":{"basic_reserve_hours":1,"production_reserve_hours":2,"improvement_reserve_hours":3,
        "default_goods_prices":{"sensitivity_basis_points":0,"minimum_multiplier_basis_points":2,"maximum_multiplier_basis_points":3,"spread_basis_points":9999},
        "good_prices":[],"vehicle_buy_multiplier_basis_points":10000,"vehicle_sell_multiplier_basis_points":5000,
        "vehicle_purchase_limits":[{"size":"titan","maximum_for_sale":5},{"size":"large","maximum_for_sale":3},
        {"size":"small","maximum_for_sale":1},{"size":"extra_large","maximum_for_sale":4},{"size":"medium","maximum_for_sale":2}],
        "automatic_evaluation_interval_ticks":60,"recent_report_limit":1,"commission_money_per_work":0,"rent_money_per_capacity_hour":0,
        "rentable_capacity_ratio_basis_points":10000,"lease_duration_ticks":1}}
        """)!.AsObject();
    internal static JsonObject Population()
    {
        var needs = new JsonObject();
        var names = new[] { "food", "clothing", "improved_diet", "consumer_goods", "consumer_electronics", "medical_supplies" };
        for (var i = 0; i < names.Length; i++) needs[names[i]] = new JsonObject
        {
            ["class"] = i < 2 ? "basic" : "improvement", ["amount_per_person_second"] = 0.001, ["weight"] = 1
        };
        var goods = new JsonArray();
        var identities = new[] { "staple", "clothing", "prepared", "consumer", "electronics", "medical" };
        for (var i = 0; i < names.Length; i++)
        {
            var conversions = new JsonArray { new JsonObject { ["need"] = names[i], ["amount_per_good_unit"] = 1 } };
            if (i == 2) conversions.Add(new JsonObject { ["need"] = "food", ["amount_per_good_unit"] = 2 });
            goods.Add(new JsonObject { ["good"] = identities[i], ["satisfies"] = conversions, ["selection_order"] = i });
        }
        return new JsonObject
        {
            ["schema_version"] = 1, ["id"] = "test", ["simulation"] = new JsonObject
            {
                ["needs"] = needs, ["consumption_goods"] = goods, ["labor_ratio"] = 0.5,
                ["consumption_interval_ticks"] = 60, ["statistics_period_ticks"] = 600,
                ["base_growth_per_second"] = 0, ["improvement_growth_per_second"] = 0, ["maximum_loss_per_second"] = 0
            }
        };
    }
}
