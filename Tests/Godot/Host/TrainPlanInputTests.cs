using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.Core.Contracts;

namespace Train.Godot.Tests.Host;

[TestClass]
public sealed class TrainPlanInputTests
{
    [TestMethod]
    public void AutomaticRulesPreserveOrderPricesTargetsAndExactDwell()
    {
        GoodId ore = new GoodId.Voxel(new("test:ore"));
        GoodId equipment = new GoodId.Composite(new("test:equipment"));
        GoodId clothing = new GoodId.Voxel(new("test:clothing"));
        GoodId wood = new GoodId.Voxel(new("test:wood"));
        Assert.IsTrue(TrainPlanInput.TryPlan(new(7), true,
            [new(new(2), true, "1.016667", 61, [
                new(TradeAction.Unload, equipment, new(11), "0", "ignored"),
                new(TradeAction.Buy, ore, new(12), "9007199254740993", "0"),
                new(TradeAction.Sell, clothing, new(11), "9223372036854775807", "9223372036854775807"),
                new(TradeAction.Load, wood, new(12), "1", "")])], out var command, out _));
        var station = (PlanStop.Station)command!.Plan.Stops[0];
        Assert.AreEqual(61UL, station.DwellSteps);
        AutomaticGoodsRule[] expected = [
            new(new AutomaticGoodsAction.Unload(), equipment, new(11), 0),
            new(new AutomaticGoodsAction.Buy(new(0)), ore, new(12), 9007199254740993),
            new(new AutomaticGoodsAction.Sell(new(long.MaxValue)), clothing, new(11), long.MaxValue),
            new(new AutomaticGoodsAction.Load(), wood, new(12), 1)];
        CollectionAssert.AreEqual(expected, station.GoodsRules.ToArray());
        Assert.IsTrue(TrainPlanInput.TryPlan(new(7), false,
            [new(new(2), true, "0", GoodsRules: [new(TradeAction.Buy, ore, new(1), "0", "")])],
            out command, out _));
        Assert.IsNull(((AutomaticGoodsAction.Buy)((PlanStop.Station)command!.Plan.Stops[0]).GoodsRules[0].Action).MaximumUnitPrice);
    }

    [TestMethod]
    public void RuleNumbersRejectNegativeFractionalAndOverflowValuesWithoutPartialCommand()
    {
        GoodId ore = new GoodId.Voxel(new("test:ore"));
        foreach (var text in new[] { "", "-1", "+1", "1.5", " 1", "9223372036854775808" })
        {
            Assert.IsFalse(TrainPlanInput.TryPlan(new(1), false,
                [new(new(2), true, "0", GoodsRules: [new(TradeAction.Load, ore, new(1), text, "")])],
                out var command, out _));
            Assert.IsNull(command);
            if (text.Length == 0) continue;
            Assert.IsFalse(TrainPlanInput.TryPlan(new(1), false,
                [new(new(2), true, "0", GoodsRules: [new(TradeAction.Sell, ore, new(1), "0", text)])],
                out command, out _));
            Assert.IsNull(command);
        }
        Assert.IsFalse(TrainPlanInput.TryPlan(new(1), false,
            [new(new(2), false, "", GoodsRules: [new(TradeAction.Load, ore, new(1), "0", "")])], out _, out _));
    }

    [TestMethod]
    public void UneditedDwellPreservesFractionalSecondSteps()
    {
        Assert.IsTrue(TrainPlanInput.TryPlan(new(1), false, [new(new(2), true, "1.016667", 61)], out var command, out _));
        Assert.AreEqual(61UL, ((PlanStop.Station)command!.Plan.Stops[0]).DwellSteps);
        Assert.IsTrue(TrainPlanInput.TryPlan(new(1), false, [new(new(2), true, "2")], out command, out _));
        Assert.AreEqual(120UL, ((PlanStop.Station)command!.Plan.Stops[0]).DwellSteps);
    }

    [TestMethod]
    public void PlanPreservesRowsAndIgnoresWaypointDwellInput()
    {
        foreach (var loop in new[] { false, true })
        {
            Assert.IsTrue(TrainPlanInput.TryPlan(new(7), loop,
                [new(new(4), true, "3"), new(new(8), false, "ignored"), new(new(4), true, "0")],
                out var command, out var error));
            Assert.IsNull(error);
            Assert.AreEqual(new TrainId(7), command!.Train);
            Assert.AreEqual(loop, command.Plan.Loop);
            Assert.AreEqual(3, command.Plan.Stops.Length);
            var first = (PlanStop.Station)command.Plan.Stops[0];
            Assert.AreEqual(new NodeId(4), first.Node);
            Assert.AreEqual(3UL * (ulong)SimulationTime.StepsPerSecond, first.DwellSteps);
            Assert.IsTrue(first.GoodsRules.IsEmpty);
            Assert.AreEqual(new NodeId(8), ((PlanStop.Waypoint)command.Plan.Stops[1]).Node);
            var last = (PlanStop.Station)command.Plan.Stops[2];
            Assert.AreEqual(new NodeId(4), last.Node);
            Assert.AreEqual(0UL, last.DwellSteps);
            Assert.IsTrue(last.GoodsRules.IsEmpty);
        }
    }

    [TestMethod]
    public void InvalidPlanNeverReturnsPartialCommand()
    {
        foreach (var seconds in new[] { "", "-1", "+1", "1.5", " 1", "18446744073709551615", "18446744073709551616" })
        {
            Assert.IsFalse(TrainPlanInput.TryPlan(new(1), false,
                [new(new(2), false, ""), new(new(3), true, seconds)], out var command, out var error));
            Assert.IsNull(command);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));
        }
        Assert.IsFalse(TrainPlanInput.TryPlan(new(0), false, [new(new(1), false, "")], out _, out _));
        Assert.IsFalse(TrainPlanInput.TryPlan(new(1), false, [], out _, out _));
        foreach (var station in new[] { false, true })
            Assert.IsFalse(TrainPlanInput.TryPlan(new(1), false, [new(new(0), station, "0")], out _, out _));
    }
}
