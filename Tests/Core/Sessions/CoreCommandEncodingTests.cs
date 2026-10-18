using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Globalization;
using Train.Core.Contracts;
using Train.Core.Sessions;

namespace Train.Core.Tests.Sessions;

[TestClass]
public sealed class CoreCommandEncodingTests
{
    [TestMethod]
    public void Rail_break_has_a_fixed_vector_and_round_trips_under_other_cultures()
    {
        const string payload = "{\"input_format_version\":2,\"kind\":\"rail.debug_add_break\",\"edge\":18446744073709551615,\"track\":2147483647,\"distance_meters\":0.12345678901234568}";
        var command = new CoreCommand.DebugAddRailBreak(new(ulong.MaxValue), int.MaxValue, 0.12345678901234568);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.AreEqual(payload, CoreCommandEncoding.Encode(command));
            Assert.AreEqual(command, Decode(payload));
            Assert.AreEqual(command, Decode("{\"distance_meters\":0.12345678901234568,\"track\":2147483647,\"edge\":18446744073709551615,\"kind\":\"rail.debug_add_break\",\"input_format_version\":2}"));
            foreach (var distance in new[] { 0, double.Epsilon, double.MaxValue })
            {
                var boundary = new CoreCommand.DebugAddRailBreak(new(1), 1, distance);
                Assert.AreEqual(boundary, Decode(CoreCommandEncoding.Encode(boundary)));
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [TestMethod]
    [DataRow("\"edge\":0,\"track\":1,\"distance_meters\":0", "/edge")]
    [DataRow("\"edge\":-1,\"track\":1,\"distance_meters\":0", "/edge")]
    [DataRow("\"edge\":18446744073709551616,\"track\":1,\"distance_meters\":0", "/edge")]
    [DataRow("\"edge\":1.5,\"track\":1,\"distance_meters\":0", "/edge")]
    [DataRow("\"track\":1,\"distance_meters\":0", "/edge")]
    [DataRow("\"edge\":1,\"distance_meters\":0", "/track")]
    [DataRow("\"edge\":1,\"track\":0,\"distance_meters\":0", "/track")]
    [DataRow("\"edge\":1,\"track\":-1,\"distance_meters\":0", "/track")]
    [DataRow("\"edge\":1,\"track\":1.5,\"distance_meters\":0", "/track")]
    [DataRow("\"edge\":1,\"track\":2147483648,\"distance_meters\":0", "/track")]
    [DataRow("\"edge\":1,\"track\":1", "/distance_meters")]
    [DataRow("\"edge\":1,\"track\":1,\"distance_meters\":-1", "/distance_meters")]
    [DataRow("\"edge\":1,\"track\":1,\"distance_meters\":1e309", "/distance_meters")]
    [DataRow("\"edge\":1,\"track\":1,\"distance_meters\":\"NaN\"", "/distance_meters")]
    [DataRow("\"edge\":1,\"track\":1,\"distance_meters\":null", "/distance_meters")]
    [DataRow("\"edge\":1,\"edge\":2,\"track\":1,\"distance_meters\":0", "/edge")]
    [DataRow("\"edge\":1,\"track\":1,\"distance_meters\":0,\"extra\":0", "/extra")]
    public void Rail_break_payload_fields_are_strict(string fields, string field)
    {
        var payload = "{\"input_format_version\":2,\"kind\":\"rail.debug_add_break\"," + fields + "}";
        var failure = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(payload);
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual(field, failure.Problems.Single().Field);
    }

    [TestMethod]
    public void Invalid_rail_break_values_cannot_be_encoded()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(new CoreCommand.DebugAddRailBreak(new(0), 1, 0)));
        foreach (var track in new[] { 0, -1 })
            Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(new CoreCommand.DebugAddRailBreak(new(1), track, 0)));
        foreach (var distance in new[] { -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(new CoreCommand.DebugAddRailBreak(new(1), 1, distance)));
    }

    [TestMethod]
    public void Formation_preserves_vehicle_order_and_has_a_fixed_vector()
    {
        const string payload = "{\"input_format_version\":2,\"kind\":\"train.form\",\"ordered_vehicles\":[9,2,7]}";
        Assert.AreEqual(payload, CoreCommandEncoding.Encode(new CoreCommand.FormTrain([new(9), new(2), new(7)])));
        var command = (CoreCommand.FormTrain)Decode(payload);
        CollectionAssert.AreEqual(new ulong[] { 9, 2, 7 }, command.OrderedVehicles.Select(v => v.Value).ToArray());
    }

    [TestMethod]
    public void Plan_preserves_stop_order_loop_and_station_dwell()
    {
        const string payload = "{\"input_format_version\":2,\"kind\":\"train.replace_run_plan\",\"train\":3,\"plan\":{\"loop\":true,\"stops\":[{\"kind\":\"waypoint\",\"node\":8},{\"kind\":\"station\",\"node\":4,\"dwell_steps\":18446744073709551615,\"goods_rules\":[]}]}}";
        var plan = new RunPlan(true, [new PlanStop.Waypoint(new(8)), new PlanStop.Station(new(4), ulong.MaxValue, [])]);
        Assert.AreEqual(payload, CoreCommandEncoding.Encode(new CoreCommand.ReplaceRunPlan(new(3), plan)));
        var command = (CoreCommand.ReplaceRunPlan)Decode(payload);
        Assert.AreEqual(new TrainId(3), command.Train);
        Assert.IsTrue(command.Plan.Loop);
        Assert.AreEqual(new PlanStop.Waypoint(new(8)), command.Plan.Stops[0]);
        var station = (PlanStop.Station)command.Plan.Stops[1];
        Assert.AreEqual(new NodeId(4), station.Node);
        Assert.AreEqual(ulong.MaxValue, station.DwellSteps);
        Assert.IsTrue(station.GoodsRules.IsEmpty);
    }

    [TestMethod]
    [DataRow("disband")]
    [DataRow("start")]
    [DataRow("stop")]
    public void Single_train_commands_accept_reordered_fields(string kind)
    {
        var command = Decode("{\"train\":18446744073709551615,\"kind\":\"train." + kind + "\",\"input_format_version\":2}");
        CoreCommand expected = kind switch
        {
            "disband" => new CoreCommand.DisbandTrain(new(ulong.MaxValue)),
            "start" => new CoreCommand.StartTrain(new(ulong.MaxValue)),
            _ => new CoreCommand.StopTrain(new(ulong.MaxValue))
        };
        Assert.AreEqual(expected, command);
        Assert.AreEqual("{\"input_format_version\":2,\"kind\":\"train." + kind + "\",\"train\":18446744073709551615}", CoreCommandEncoding.Encode(command));
    }

    [TestMethod]
    [DataRow("{\"input_format_version\":2,\"kind\":\"train.start\",\"train\":0}", "/train")]
    [DataRow("{\"input_format_version\":2,\"kind\":\"train.start\",\"train\":-1}", "/train")]
    [DataRow("{\"input_format_version\":2,\"kind\":\"train.start\",\"train\":18446744073709551616}", "/train")]
    [DataRow("{\"input_format_version\":2,\"kind\":\"train.start\",\"train\":1.5}", "/train")]
    [DataRow("{\"input_format_version\":2,\"kind\":\"train.start\",\"train\":1,\"train\":2}", "/train")]
    [DataRow("{\"input_format_version\":2,\"kind\":\"train.start\",\"train\":1,\"extra\":2}", "/extra")]
    [DataRow("{\"input_format_version\":2,\"kind\":\"train.form\",\"ordered_vehicles\":[1,0]}", "/ordered_vehicles/1")]
    [DataRow("{\"input_format_version\":2,\"kind\":\"train.form\",\"ordered_vehicles\":null}", "/ordered_vehicles")]
    [DataRow("{\"input_format_version\":2,\"kind\":\"train.start\"}", "/train")]
    public void Invalid_command_fields_are_preparation_failures(string payload, string field)
    {
        var failure = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(payload);
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual(field, failure.Problems.Single().Field);
    }

    [TestMethod]
    [DataRow("{\"kind\":\"station\",\"node\":1,\"dwell_steps\":0}", "/goods_rules")]
    [DataRow("{\"kind\":\"station\",\"node\":1,\"dwell_steps\":0,\"goods_rules\":[{}]}", "/goods_rules/0/action")]
    [DataRow("{\"kind\":\"waypoint\",\"node\":1,\"node\":2}", "/node")]
    [DataRow("{\"kind\":\"waypoint\",\"node\":1,\"goods_rules\":[]}", "/goods_rules")]
    [DataRow("{\"kind\":\"unknown\",\"node\":1}", "/kind")]
    public void Plan_fields_are_strict_at_nested_boundaries(string stop, string field)
    {
        var payload = "{\"input_format_version\":2,\"kind\":\"train.replace_run_plan\",\"train\":1,\"plan\":{\"loop\":false,\"stops\":[" + stop + "]}}";
        var failure = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(payload);
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual("/plan/stops/0" + field, failure.Problems.Single().Field);
    }

    [TestMethod]
    public void Uninitialized_arrays_and_zero_identities_cannot_be_encoded()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(new CoreCommand.FormTrain(default)));
        Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(new CoreCommand.FormTrain([new(0)])));
        Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(new CoreCommand.StartTrain(new(0))));
        Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(new CoreCommand.ReplaceRunPlan(new(1), new(false, default))));
        Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(new CoreCommand.ReplaceRunPlan(new(1), new(false, [new PlanStop.Station(new(1), 0, default)]))));
    }

    [TestMethod]
    public void Automatic_goods_rules_preserve_order_and_have_canonical_vectors()
    {
        var good = new GoodId.Voxel(new("test:ore"));
        AutomaticGoodsRule[] rules = [
            new(new AutomaticGoodsAction.Buy(new(7)), good, new(2), 0),
            new(new AutomaticGoodsAction.Sell(null), new GoodId.Composite(new("test:steel")), new(3), long.MaxValue),
            new(new AutomaticGoodsAction.Load(), new GoodId.Assembly(new(new string('A', 64))), new(4), 9),
            new(new AutomaticGoodsAction.Unload(), good, new(5), 1)];
        var command = new CoreCommand.ReplaceRunPlan(new(1), new(false, [new PlanStop.Station(new(1), 0, [.. rules])]));
        var payload = CoreCommandEncoding.Encode(command);
        StringAssert.Contains(payload, "\"action\":{\"kind\":\"buy\",\"maximum_unit_price\":7}");
        StringAssert.Contains(payload, "\"action\":{\"kind\":\"sell\",\"minimum_unit_price\":null}");
        StringAssert.Contains(payload, "\"action\":{\"kind\":\"load\"}");
        StringAssert.Contains(payload, "\"action\":{\"kind\":\"unload\"}");
        var decoded = (CoreCommand.ReplaceRunPlan)Decode(payload);
        CollectionAssert.AreEqual(rules, ((PlanStop.Station)decoded.Plan.Stops[0]).GoodsRules.ToArray());
        Assert.AreEqual(payload, CoreCommandEncoding.Encode(decoded));
        foreach (var action in new AutomaticGoodsAction[] { new AutomaticGoodsAction.Buy(null), new AutomaticGoodsAction.Sell(new(0)) })
        {
            var rule = rules[0] with { Action = action };
            var input = command with { Plan = new(false, [new PlanStop.Station(new(1), 0, [rule])]) };
            var output = (CoreCommand.ReplaceRunPlan)Decode(CoreCommandEncoding.Encode(input));
            Assert.AreEqual(rule, ((PlanStop.Station)output.Plan.Stops[0]).GoodsRules[0]);
        }
    }

    [TestMethod]
    public void Automatic_goods_rule_has_a_fixed_vector_and_accepts_reordered_nested_fields()
    {
        const string payload = """{"input_format_version":2,"kind":"train.replace_run_plan","train":1,"plan":{"loop":false,"stops":[{"kind":"station","node":2,"dwell_steps":0,"goods_rules":[{"action":{"kind":"buy","maximum_unit_price":0},"good":{"kind":"voxel","definition":"test:ore"},"local_inventory":3,"target_quantity":0}]}]}}""";
        const string reordered = """{"input_format_version":2,"kind":"train.replace_run_plan","train":1,"plan":{"stops":[{"goods_rules":[{"target_quantity":0,"local_inventory":3,"good":{"definition":"test:ore","kind":"voxel"},"action":{"maximum_unit_price":0,"kind":"buy"}}],"dwell_steps":0,"node":2,"kind":"station"}],"loop":false}}""";
        var rule = new AutomaticGoodsRule(new AutomaticGoodsAction.Buy(new(0)), new GoodId.Voxel(new("test:ore")), new(3), 0);
        var command = new CoreCommand.ReplaceRunPlan(new(1), new(false, [new PlanStop.Station(new(2), 0, [rule])]));
        Assert.AreEqual(payload, CoreCommandEncoding.Encode(command));
        Assert.AreEqual(payload, CoreCommandEncoding.Encode(Decode(reordered)));
    }

    [TestMethod]
    public void Invalid_automatic_goods_rules_cannot_be_encoded()
    {
        var valid = new AutomaticGoodsRule(new AutomaticGoodsAction.Load(), new GoodId.Voxel(new("test:ore")), new(1), 0);
        foreach (var rule in new[] { valid with { Action = null! }, valid with { Good = new GoodId.Voxel(new("bad id")) },
            valid with { LocalInventory = new(0) }, valid with { TargetQuantity = -1 },
            valid with { Action = new AutomaticGoodsAction.Buy(new(-1)) }, valid with { Action = new AutomaticGoodsAction.Sell(new(-1)) } })
            Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(new CoreCommand.ReplaceRunPlan(new(1),
                new(false, [new PlanStop.Station(new(1), 0, [rule])]))));
    }

    [TestMethod]
    [DataRow("\"action\":{\"kind\":\"buy\",\"maximum_unit_price\":null}", "\"action\":{\"kind\":\"buy\"}", "/action/maximum_unit_price")]
    [DataRow("\"maximum_unit_price\":null", "\"maximum_unit_price\":-1", "/action/maximum_unit_price")]
    [DataRow("\"maximum_unit_price\":null", "\"maximum_unit_price\":1.5", "/action/maximum_unit_price")]
    [DataRow("\"maximum_unit_price\":null", "\"maximum_unit_price\":9223372036854775808", "/action/maximum_unit_price")]
    [DataRow("\"kind\":\"buy\"", "\"kind\":\"unknown\"", "/action/kind")]
    [DataRow("\"maximum_unit_price\":null", "\"maximum_unit_price\":null,\"extra\":0", "/action/extra")]
    [DataRow("\"maximum_unit_price\":null", "\"maximum_unit_price\":null,\"maximum_unit_price\":0", "/action/maximum_unit_price")]
    [DataRow("\"local_inventory\":1", "\"local_inventory\":0", "/local_inventory")]
    [DataRow("\"target_quantity\":0", "\"target_quantity\":-1", "/target_quantity")]
    [DataRow("\"target_quantity\":0", "\"target_quantity\":0,\"extra\":0", "/extra")]
    [DataRow("\"definition\":\"test:ore\"", "\"definition\":\"bad id\"", "/good/definition")]
    public void Automatic_goods_rule_fields_are_strict(string from, string to, string field)
    {
        const string rule = "{\"action\":{\"kind\":\"buy\",\"maximum_unit_price\":null},\"good\":{\"kind\":\"voxel\",\"definition\":\"test:ore\"},\"local_inventory\":1,\"target_quantity\":0}";
        var payload = "{\"input_format_version\":2,\"kind\":\"train.replace_run_plan\",\"train\":1,\"plan\":{\"loop\":false,\"stops\":[{\"kind\":\"station\",\"node\":1,\"dwell_steps\":0,\"goods_rules\":[" + rule.Replace(from, to, StringComparison.Ordinal) + "]}]}}";
        var failure = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(payload);
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual("/plan/stops/0/goods_rules/0" + field, failure.Problems.Single().Field);
    }

    [TestMethod]
    public void Trades_have_fixed_canonical_vectors_and_accept_reordered_fields()
    {
        var good = new GoodId.Voxel(new("fixture:ore"));
        (CoreCommand Command, string Kind, string Fields)[] vectors = [
            (new CoreCommand.BuyGoods(new(1), new(2), new(3), good, 4, new(5)), "market.buy_goods", "\"train\":1,\"settlement\":2,\"local_inventory\":3,\"good\":{\"kind\":\"voxel\",\"definition\":\"fixture:ore\"},\"quantity\":4,\"maximum_payment\":5"),
            (new CoreCommand.SellGoods(new(1), new(2), new(3), good, 4, new(5)), "market.sell_goods", "\"train\":1,\"settlement\":2,\"local_inventory\":3,\"good\":{\"kind\":\"voxel\",\"definition\":\"fixture:ore\"},\"quantity\":4,\"minimum_receipt\":5"),
            (new CoreCommand.BuyVehicle(new(2), new(3), new(5)), "market.buy_vehicle", "\"seller\":2,\"vehicle\":3,\"maximum_payment\":5"),
            (new CoreCommand.SellVehicle(new(2), new(3), new(5)), "market.sell_vehicle", "\"buyer\":2,\"vehicle\":3,\"minimum_receipt\":5")];
        foreach (var (command, kind, fields) in vectors)
        {
            var payload = "{\"input_format_version\":2,\"kind\":\"" + kind + "\"," + fields + "}";
            Assert.AreEqual(payload, CoreCommandEncoding.Encode(command));
            Assert.AreEqual(command, Decode(payload));
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var reordered = "{" + string.Join(",", document.RootElement.EnumerateObject().Reverse()
                .Select(property => "\"" + property.Name + "\":" + property.Value.GetRawText())) + "}";
            Assert.AreEqual(command, Decode(reordered));
        }
    }

    [TestMethod]
    [DataRow("buy_goods", "\"train\":1", "\"train\":0", "/train")]
    [DataRow("sell_goods", "\"settlement\":2", "\"settlement\":-1", "/settlement")]
    [DataRow("buy_goods", "\"local_inventory\":3", "\"local_inventory\":18446744073709551616", "/local_inventory")]
    [DataRow("sell_goods", "\"quantity\":4", "\"quantity\":0", "/quantity")]
    [DataRow("buy_goods", "\"quantity\":4", "\"quantity\":-1", "/quantity")]
    [DataRow("sell_goods", "\"quantity\":4", "\"quantity\":9223372036854775808", "/quantity")]
    [DataRow("buy_goods", "\"maximum_payment\":5", "\"maximum_payment\":-1", "/maximum_payment")]
    [DataRow("sell_goods", "\"minimum_receipt\":5", "\"minimum_receipt\":1.5", "/minimum_receipt")]
    [DataRow("buy_goods", "\"definition\":\"fixture:ore\"", "\"definition\":\"bad id\"", "/good/definition")]
    [DataRow("sell_goods", "\"train\":1,", "", "/train")]
    [DataRow("buy_goods", "\"train\":1", "\"train\":1,\"train\":2", "/train")]
    [DataRow("sell_goods", "\"train\":1", "\"train\":1,\"extra\":2", "/extra")]
    [DataRow("buy_vehicle", "\"seller\":2", "\"seller\":0", "/seller")]
    [DataRow("sell_vehicle", "\"buyer\":2", "\"buyer\":1.5", "/buyer")]
    [DataRow("buy_vehicle", "\"vehicle\":3", "\"vehicle\":0", "/vehicle")]
    [DataRow("sell_vehicle", "\"minimum_receipt\":5", "\"minimum_receipt\":-1", "/minimum_receipt")]
    [DataRow("buy_vehicle", "\"maximum_payment\":5", "\"maximum_payment\":9223372036854775808", "/maximum_payment")]
    [DataRow("sell_vehicle", ",\"minimum_receipt\":5", "", "/minimum_receipt")]
    [DataRow("buy_vehicle", "\"vehicle\":3", "\"vehicle\":3,\"vehicle\":4", "/vehicle")]
    [DataRow("sell_vehicle", "\"vehicle\":3", "\"vehicle\":3,\"extra\":2", "/extra")]
    public void Trade_payload_fields_are_strict(string kind, string from, string to, string field)
    {
        var fields = kind.EndsWith("goods", StringComparison.Ordinal)
            ? "\"train\":1,\"settlement\":2,\"local_inventory\":3,\"good\":{\"kind\":\"voxel\",\"definition\":\"fixture:ore\"},\"quantity\":4"
            : (kind == "buy_vehicle" ? "\"seller\":2" : "\"buyer\":2") + ",\"vehicle\":3";
        fields += kind.StartsWith("buy", StringComparison.Ordinal) ? ",\"maximum_payment\":5" : ",\"minimum_receipt\":5";
        var payload = "{\"input_format_version\":2,\"kind\":\"market." + kind + "\"," + fields + "}";
        var failure = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(payload.Replace(from, to, StringComparison.Ordinal));
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual(field, failure.Problems.Single().Field);
    }

    [TestMethod]
    public void Production_plan_round_trips_canonical_json()
    {
        const string canonical = "{\"input_format_version\":2,\"kind\":\"production.set_plan\",\"facility\":{\"building\":2,\"capability\":\"extraction\"},\"process\":{\"kind\":\"recovery\",\"definition\":\"fixture:recovery\"},\"enabled\":false,\"priority\":-3}";
        var command = new CoreCommand.SetProductionPlan(new(new(2), CapabilityKind.Extraction),
            new ProcessRef.Recovery(new("fixture:recovery")), false, -3);
        Assert.AreEqual(canonical, CoreCommandEncoding.Encode(command));
        Assert.AreEqual(command, Decode(canonical));
        Assert.AreEqual(command, Decode("{\"priority\":-3,\"enabled\":false,\"process\":{\"definition\":\"fixture:recovery\",\"kind\":\"recovery\"},\"facility\":{\"capability\":\"extraction\",\"building\":2},\"kind\":\"production.set_plan\",\"input_format_version\":2}"));
        Assert.AreEqual(canonical, CoreCommandEncoding.Encode(Decode(canonical)));
        foreach (var priority in new[] { int.MinValue, int.MaxValue })
        {
            var boundary = new CoreCommand.SetProductionPlan(new(new(2), CapabilityKind.Production),
                new ProcessRef.Production(new("fixture:production")), true, priority);
            Assert.AreEqual(boundary, Decode(CoreCommandEncoding.Encode(boundary)));
        }
        Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(new CoreCommand.SetProductionPlan(
            new(new(2), CapabilityKind.Construction), new ProcessRef.Production(new("fixture:production")), true, 0)));
    }

    [TestMethod]
    [DataRow("\"enabled\":false,", "", "/enabled")]
    [DataRow("\"priority\":-3", "\"priority\":-3,\"extra\":1", "/extra")]
    [DataRow("\"priority\":-3", "\"priority\":-3,\"priority\":1", "/priority")]
    [DataRow("\"building\":2", "\"building\":0", "/facility/building")]
    [DataRow("\"capability\":\"extraction\"", "\"capability\":\"construction\"", "/facility/capability")]
    [DataRow("\"definition\":\"fixture:recovery\"", "\"definition\":\"BAD\"", "/process/definition")]
    [DataRow("\"process\":{\"kind\":\"recovery\"", "\"process\":{\"kind\":\"extraction\"", "/process/kind")]
    [DataRow("\"priority\":-3", "\"priority\":1.5", "/priority")]
    [DataRow("\"priority\":-3", "\"priority\":2147483648", "/priority")]
    [DataRow("\"priority\":-3", "\"priority\":-2147483649", "/priority")]
    public void Production_plan_payload_fields_are_strict(string from, string to, string field)
    {
        const string payload = "{\"input_format_version\":2,\"kind\":\"production.set_plan\",\"facility\":{\"building\":2,\"capability\":\"extraction\"},\"process\":{\"kind\":\"recovery\",\"definition\":\"fixture:recovery\"},\"enabled\":false,\"priority\":-3}";
        var failure = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(payload.Replace(from, to, StringComparison.Ordinal));
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual(field, failure.Problems.Single().Field);
    }

    private static CoreCommand Decode(string payload)
    {
        var decoded = ((CoreResult<DecodedCommand>.Success)CoreCommandEncoding.Decode(payload)).Value;
        Assert.IsNull(decoded.InputIdentity);
        return decoded.Command!;
    }
}
