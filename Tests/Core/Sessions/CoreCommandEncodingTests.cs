using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;

namespace Train.Core.Tests.Sessions;

[TestClass]
public sealed class CoreCommandEncodingTests
{
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
    [DataRow("{\"kind\":\"station\",\"node\":1,\"dwell_steps\":0,\"goods_rules\":[{}]}", "/goods_rules")]
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
    public void Automatic_goods_rules_remain_unimplemented_in_recorded_inputs()
    {
        var rule = new AutomaticGoodsRule(new AutomaticGoodsAction.Load(),
            new GoodId.Voxel(new("test:ore")), new(1), 1);
        var command = new CoreCommand.ReplaceRunPlan(new(1),
            new(false, [new PlanStop.Station(new(1), 0, [rule])]));
        Assert.ThrowsExactly<NotImplementedException>(() => CoreCommandEncoding.Encode(command));
    }

    private static CoreCommand Decode(string payload)
    {
        var decoded = ((CoreResult<DecodedCommand>.Success)CoreCommandEncoding.Decode(payload)).Value;
        Assert.IsNull(decoded.InputIdentity);
        return decoded.Command!;
    }
}
