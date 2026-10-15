using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.World;
using static Train.Core.Tests.Settlements.PopulationFixture;

namespace Train.Core.Tests.Settlements;

[TestClass]
public sealed class PopulationValidationTests
{
    [TestMethod]
    public void Positive_rates_that_underflow_during_consumption_do_not_fault_the_session()
    {
        var rule = Rule();
        rule = rule with { Needs = rule.Needs.Select(need => need with { AmountPerPersonSecond = double.Epsilon }).ToImmutableArray() };
        using var session = Start(Content(rule, Stock()));
        var report = Reports(Advance(session, 2)).Single();
        Assert.AreEqual(1d, report.BasicSatisfaction);
        Assert.AreEqual(1d, report.ImprovementSatisfaction);
    }

    [TestMethod]
    [DataRow("missing_need")]
    [DataRow("missing_good")]
    [DataRow("wrong_class")]
    [DataRow("wrong_conversion")]
    [DataRow("duplicate_order")]
    [DataRow("prepared_first")]
    [DataRow("negative_labor")]
    [DataRow("excess_labor")]
    [DataRow("nonfinite_labor")]
    [DataRow("zero_interval")]
    [DataRow("equal_period")]
    [DataRow("nonmultiple_period")]
    public void Invalid_rules_fail_before_allocating_a_command_number(string corruption)
    {
        var rule = Rule();
        var invalid = corruption switch
        {
            "missing_need" => rule with { Needs = rule.Needs.RemoveAt(0) },
            "missing_good" => rule with { ConsumptionGoods = rule.ConsumptionGoods.RemoveAt(0) },
            "wrong_class" => rule with { Needs = rule.Needs.SetItem(0, rule.Needs[0] with { Class = PopulationNeedClass.Improvement }) },
            "wrong_conversion" => WithConversion(rule, new(PopulationNeed.Clothing, 1)),
            "duplicate_order" => rule with { ConsumptionGoods = rule.ConsumptionGoods.SetItem(0,
                rule.ConsumptionGoods[0] with { SelectionOrder = rule.ConsumptionGoods[1].SelectionOrder }) },
            "prepared_first" => rule with { ConsumptionGoods = rule.ConsumptionGoods.SetItem(0,
                rule.ConsumptionGoods[0] with { SelectionOrder = 6 }) },
            "negative_labor" => rule with { LaborRatio = -.1 },
            "excess_labor" => rule with { LaborRatio = 1.1 },
            "nonfinite_labor" => rule with { LaborRatio = double.NaN },
            "zero_interval" => rule with { ConsumptionIntervalTicks = 0 },
            "equal_period" => rule with { StatisticsPeriodTicks = rule.ConsumptionIntervalTicks },
            "nonmultiple_period" => rule with { ConsumptionIntervalTicks = 2, StatisticsPeriodTicks = 3 },
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        AssertInvalidRule(invalid);
    }

    [TestMethod]
    [DataRow(0d)]
    [DataRow(-1d)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void Need_rates_weights_and_conversions_must_be_finite_and_positive(double value)
    {
        var rule = Rule();
        AssertInvalidRule(rule with { Needs = rule.Needs.SetItem(0, rule.Needs[0] with { AmountPerPersonSecond = value }) });
        AssertInvalidRule(rule with { Needs = rule.Needs.SetItem(0, rule.Needs[0] with { Weight = value }) });
        AssertInvalidRule(WithConversion(rule, new(PopulationNeed.Food, value)));
    }

    [TestMethod]
    [DataRow("period/elapsed_steps", "-1", "/period/elapsed_steps")]
    [DataRow("period/elapsed_steps", "2", "/period/elapsed_steps")]
    [DataRow("period/required", "[]", "/period")]
    [DataRow("period/satisfied", "[]", "/period")]
    [DataRow("period/rule/needs", "[]", "/period/rule")]
    [DataRow("period/required/0", "-1", "/period/required")]
    [DataRow("period/satisfied/0", "-1", "/period/satisfied")]
    [DataRow("period/satisfied/0", "1000", "/period/satisfied")]
    [DataRow("shortages", "[]", "/shortages")]
    [DataRow("consumption_remaining/0/units", "-1", "/consumption_remaining/units")]
    [DataRow("consumption_remaining/0/units", "0", "/consumption_remaining/units")]
    [DataRow("consumption_remaining/0/units", "1.1", "/consumption_remaining/units")]
    [DataRow("change_remaining", "1", "/change_remaining")]
    [DataRow("change_remaining", "-1", "/change_remaining")]
    public void Decode_rejects_invalid_frozen_population_state_at_its_path(string member, string json, string suffix)
    {
        var payload = MidperiodPayload();
        Replace(Population(payload), member, JsonNode.Parse(json));
        AssertDecodeFailure(payload, "invalid_state", "/world/settlements/population_state" + suffix);
    }

    [TestMethod]
    [DataRow("consumption_remaining/0/units", "/consumption_remaining/units")]
    [DataRow("period/required/0", "/period/required")]
    [DataRow("period/satisfied/0", "/period/satisfied")]
    [DataRow("change_remaining", "/change_remaining")]
    public void Decode_rejects_nonfinite_population_numbers(string member, string suffix)
    {
        var payload = MidperiodPayload();
        Replace(Population(payload), member, JsonNode.Parse("1e400"));
        AssertDecodeFailure(payload, "invalid_state", "/world/settlements/population_state" + suffix);
    }

    [TestMethod]
    public void Frozen_period_progress_cannot_exceed_the_saved_boundary()
    {
        var payload = MidperiodPayload();
        payload["position"]!["completed_steps"] = 0;
        AssertDecodeFailure(payload, "invalid_state");
    }

    [TestMethod]
    public void Frozen_period_anchor_cannot_be_after_the_saved_boundary()
    {
        var payload = MidperiodPayload();
        Population(payload)["period"]!["last_advanced_step"] = 2;
        AssertDecodeFailure(payload, "invalid_state");
    }

    [TestMethod]
    public void Derived_effective_progress_cannot_reach_the_unsettled_period_end()
    {
        var payload = MidperiodPayload();
        var period = Population(payload)["period"]!;
        payload["position"]!["completed_steps"] = 2;
        period["last_advanced_step"] = 1;
        period["elapsed_steps"] = 1;
        AssertDecodeFailure(payload, "invalid_state");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Before_the_first_consumption_both_accumulated_arrays_must_remain_zero(bool satisfied)
    {
        using var session = Start(Content(Rule() with { ConsumptionIntervalTicks = 2, StatisticsPeriodTicks = 4 }, Stock()));
        Advance(session, 1);
        var capture = Capture(session);
        Assert.AreEqual(capture.Encode(), WorldFixture.Success(CoreFactory.Create().DecodeState(capture.Encode())).Encode());
        var payload = JsonNode.Parse(capture.Encode())!;
        var period = Population(payload)["period"]!;
        period["required"]![0] = .1;
        if (satisfied) period["satisfied"]![0] = .1;
        AssertDecodeFailure(payload, "invalid_state");
    }

    [TestMethod]
    public void Accumulated_demand_is_validated_as_saved_state_rather_than_rederived_by_multiplication()
    {
        var payload = MidperiodPayload();
        var period = Population(payload)["period"]!;
        period["required"]![0] = .1;
        period["satisfied"]![0] = .1;
        var decoded = WorldFixture.Success(CoreFactory.Create().DecodeState(payload.ToJsonString()));
        var roundTrip = WorldFixture.Success(CoreFactory.Create().DecodeState(decoded.Encode()));
        Assert.AreEqual(decoded.Encode(), roundTrip.Encode());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Decode_rejects_last_settlement_positions_outside_the_saved_boundary(bool sequence)
    {
        using var session = Start(Content(Rule(), Stock()));
        Advance(session, 2);
        var payload = JsonNode.Parse(Capture(session).Encode())!;
        var position = Population(payload)["last_settlement"]!["position"]!;
        if (sequence) position["sequence"] = 1;
        else position["completed_steps"] = 3;
        AssertDecodeFailure(payload, "invalid_state", "/world/settlements/population_state/last_settlement/position");
    }

    [TestMethod]
    public void Prepopulation_state_format_is_not_accepted_as_the_current_format()
    {
        var payload = MidperiodPayload();
        payload["core_state_format_version"] = 5;
        AssertDecodeFailure(payload, "unsupported_format", "/core_state_format_version");
    }

    private static PopulationRuleProjection WithConversion(PopulationRuleProjection rule, NeedConversion conversion)
        => rule with { ConsumptionGoods = rule.ConsumptionGoods.SetItem(0, rule.ConsumptionGoods[0] with { Satisfies = [conversion] }) };

    private static void AssertInvalidRule(PopulationRuleProjection rule)
    {
        var content = Content(Rule(), Stock());
        using var session = Start(content);
        var before = Capture(session);
        var invalid = content with { Identity = Identity('A'), PopulationRules = [rule] };
        Assert.ThrowsExactly<ArgumentException>(() => CoreFactory.Create().PrepareNew(WorldFixture.Input, invalid, WorldFixture.Options));
        Assert.ThrowsExactly<ArgumentException>(() => session.Submit(new CoreCommand.PublishContent(invalid), CommandSource.AutomationTest));
        Assert.AreEqual(before.Encode(), Capture(session).Encode());
    }

    private static JsonNode MidperiodPayload()
    {
        using var session = Start(Content(Rule(), Stock()));
        Advance(session, 1);
        return JsonNode.Parse(Capture(session).Encode())!;
    }

    private static JsonNode Population(JsonNode payload) => payload["world"]!["settlements"]![0]!["population_state"]!;

    private static void Replace(JsonNode root, string path, JsonNode? value)
    {
        var parts = path.Split('/');
        foreach (var part in parts[..^1]) root = root is JsonArray ? root[int.Parse(part)]! : root[part]!;
        if (root is JsonArray array) array[int.Parse(parts[^1])] = value;
        else root[parts[^1]] = value;
    }

    private static void AssertDecodeFailure(JsonNode payload, string code, string? field = null)
    {
        var result = CoreFactory.Create().DecodeState(payload.ToJsonString());
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(result);
        var problem = ((CoreResult<ICoreCapture>.Failure)result).Problems.Single();
        Assert.AreEqual(code, problem.Code);
        if (field is not null) Assert.AreEqual(field, problem.Field);
    }
}
