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
    public void EveryTrainActivityHasDisplayText()
    {
        foreach (var activity in Enum.GetValues<TrainActivity>())
        {
            var text = WorldPanels.Activity(activity);
            Assert.IsFalse(string.IsNullOrWhiteSpace(text), $"{activity} 缺少显示文字。");
            Assert.AreNotEqual(activity.ToString(), text, $"{activity} 仍显示枚举名。");
        }
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
    public void TargetsRequireCurrentBindingGenerationAndObject()
    {
        var binding = new HostBinding(Guid.NewGuid());
        var generation = new ContentGeneration(3);
        var terrain = new TerrainView(new("plain"), 0, []);
        var world = new WorldSummary(1, new("rail"), 10, 10,
            [new(new(1), new(0, 0), terrain, new NodeOccupant.Empty(), [], 0, 1, null)],
            [new(new(1), new(1), new(2), 10, terrain, 1, 1, [], [])],
            [new(new(1), new TrainPosition.Node(new(1), null, null, null, null, NodeTravelPhase.Waiting, 80, null), TrainActivity.Stopped)],
            [], [new(new(1), new WorldLocation.Node(new(1)))]);
        var present = new FixedCoreReadView { World = world };
        var absent = new FixedCoreReadView { World = world with { Nodes = [], Edges = [], Trains = [], Combats = [] } };
        WorldObjectId[] objects = [new WorldObjectId.Node(new(1)), new WorldObjectId.Edge(new(1)),
            new WorldObjectId.Train(new(1)), new WorldObjectId.Combat(new(1))];
        foreach (var obj in objects)
        {
            var target = new PanelTarget(binding, generation, obj);
            Assert.IsTrue(target.IsAvailable(binding, present, true));
            Assert.IsFalse(target.IsAvailable(new(Guid.NewGuid()), present, true));
            Assert.IsFalse(target.IsAvailable(binding, present, false));
            Assert.IsFalse(target.IsAvailable(binding, absent, true));
            Assert.IsFalse(target.IsAvailable(new(Guid.NewGuid()), new FixedCoreReadView(), true));
        }
    }
    [TestMethod]
    public void EconomicTargetsRequireObjectsToRemainPresent()
    {
        var binding = new HostBinding(Guid.NewGuid());
        var generation = new ContentGeneration(3);
        var rule = new RuleStatusView(new("rule"), null, []);
        var settlement = new SettlementView(new(1), new(1), new("town"), null,
            SettlementRelation.PlayerOwned, new(2), [], [], 0, 0, 0, [], [], [],
            new(0, 0, 0, 0), new(rule, 0, 0, 0, 0, new PopulationExecution.NotStarted([]), null, []),
            new(TargetPriority.ArmedFirst, TargetScope.All), 1, false, [], [], [new(3)],
            new(rule, new(0, 0, 0, 0, 0, 0, 0), []));
        var inventory = new InventoryView(new(2), new InventoryOwner.Settlement(new(1)),
            new WorldLocation.Node(new(1)), InventoryAccess.ReadWrite, 10, 0, 0, 10, 0, [], []);

        FixedCoreReadView View(bool objectsExist) => new()
        {
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
            {
                [new(1)] = objectsExist ? new Lookup<SettlementView>.Found(settlement) : new Lookup<SettlementView>.Missing()
            },
            Inventories = new Dictionary<InventoryId, Lookup<InventoryView>>
            {
                [new(2)] = objectsExist ? new Lookup<InventoryView>.Found(inventory) : new Lookup<InventoryView>.Missing()
            }
        };
        WorldObjectId[] objects = [new WorldObjectId.Settlement(new(1)), new WorldObjectId.Inventory(new(2))];
        foreach (var obj in objects)
        {
            var target = new PanelTarget(binding, generation, obj);
            Assert.IsTrue(target.IsAvailable(binding, View(true), true));
            Assert.IsFalse(target.IsAvailable(binding, View(false), true));
            Assert.IsFalse(target.IsAvailable(new(Guid.NewGuid()), new FixedCoreReadView(), true));
            Assert.IsFalse(target.IsAvailable(binding, new FixedCoreReadView(), false));
        }
    }
}
