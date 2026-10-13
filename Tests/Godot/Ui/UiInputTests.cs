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
