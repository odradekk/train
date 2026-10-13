using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;

namespace Train.Godot.Tests.Ui;

[TestClass]
public sealed class UiInputTests
{
    [TestMethod]
    public void UneditedDwellPreservesFractionalSecondSteps()
    {
        Assert.IsTrue(UiInput.TryPlan(new(1), false, [new(new(2), true, "1.016667", 61)], out var command, out _));
        Assert.AreEqual(61UL, ((PlanStop.Station)command!.Plan.Stops[0]).DwellSteps);
        Assert.IsTrue(UiInput.TryPlan(new(1), false, [new(new(2), true, "2")], out command, out _));
        Assert.AreEqual(120UL, ((PlanStop.Station)command!.Plan.Stops[0]).DwellSteps);
    }
    [TestMethod]
    public void TimeChangesAtMinuteHourAndDayBoundaries()
    {
        var minute = 60UL * (ulong)SimulationTime.StepsPerSecond;
        Assert.AreEqual("第1天 00:00", UiInput.FormatTime(minute - 1));
        Assert.AreEqual("第1天 00:01", UiInput.FormatTime(minute));
        Assert.AreEqual("第1天 00:59", UiInput.FormatTime(60 * minute - 1));
        Assert.AreEqual("第1天 01:00", UiInput.FormatTime(60 * minute));
        Assert.AreEqual("第1天 23:59", UiInput.FormatTime(1440 * minute - 1));
        Assert.AreEqual("第2天 00:00", UiInput.FormatTime(1440 * minute));
    }

    [TestMethod]
    public void PlanPreservesRowsAndIgnoresWaypointDwellInput()
    {
        foreach (var loop in new[] { false, true })
        {
            Assert.IsTrue(UiInput.TryPlan(new(7), loop,
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
            Assert.IsFalse(UiInput.TryPlan(new(1), false,
                [new(new(2), false, ""), new(new(3), true, seconds)], out var command, out var error));
            Assert.IsNull(command);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));
        }
        Assert.IsFalse(UiInput.TryPlan(new(0), false, [new(new(1), false, "")], out _, out _));
        Assert.IsFalse(UiInput.TryPlan(new(1), false, [], out _, out _));
        Assert.IsFalse(UiInput.TryPlan(new(1), false, [new(new(0), false, "")], out _, out _));
    }

    [TestMethod]
    public void TargetsRequireCurrentBindingGenerationAndObject()
    {
        var binding = new HostBinding(Guid.NewGuid());
        var generation = new ContentGeneration(3);
        var terrain = new TerrainView(new("plain"), 0, []);
        var world = new WorldSummary(1, new("rail"), 10, 10,
            [new(new(1), new(0, 0), terrain, new NodeOccupant.Empty(), [], 0, 1, null)],
            [new(new(1), new(1), new(2), 10, terrain, 1, 1, [], [])],
            [new(new(1), new TrainPosition.Node(new(1)), TrainActivity.Stopped)], [], []);
        var present = new FixedCoreReadView { World = world };
        var absent = new FixedCoreReadView { World = world with { Nodes = [], Edges = [], Trains = [] } };
        WorldObjectId[] objects = [new WorldObjectId.Node(new(1)), new WorldObjectId.Edge(new(1)), new WorldObjectId.Train(new(1))];
        foreach (var obj in objects)
        {
            var target = new PanelTarget(binding, generation, obj);
            Assert.IsTrue(target.IsAvailable(binding, generation, present));
            Assert.IsFalse(target.IsAvailable(new(Guid.NewGuid()), generation, present));
            Assert.IsFalse(target.IsAvailable(binding, new(4), present));
            Assert.IsFalse(target.IsAvailable(binding, generation, absent));
            Assert.IsFalse(target.IsAvailable(new(Guid.NewGuid()), generation, new FixedCoreReadView()));
        }
    }
}
