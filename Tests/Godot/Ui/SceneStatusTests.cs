using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Ui;

namespace Train.Godot.Tests.Ui;

[TestClass]
public sealed class SceneStatusTests
{
    [TestMethod]
    public void DurabilityRatioClampsToTheUnitInterval()
    {
        Assert.AreEqual(1, SceneStatus.Ratio(new(100, 100)));
        Assert.AreEqual(0.4, SceneStatus.Ratio(new(40, 100)), 1e-9);
        Assert.AreEqual(1, SceneStatus.Ratio(new(150, 100)));
        Assert.AreEqual(0, SceneStatus.Ratio(new(0, 100)));
        Assert.AreEqual(0, SceneStatus.Ratio(new(-5, 100)));
        Assert.AreEqual(1, SceneStatus.Ratio(new(0, 0)));
        Assert.AreEqual(1, SceneStatus.Ratio(new(10, -1)));
    }

    [TestMethod]
    public void BarColorUsesSmokeAndFireThresholds()
    {
        Assert.AreEqual(UiStyle.Success, SceneStatus.BarColor(1));
        Assert.AreEqual(UiStyle.Success, SceneStatus.BarColor(0.5));
        Assert.AreEqual(UiStyle.Warning, SceneStatus.BarColor(Math.BitDecrement(0.5)));
        Assert.AreEqual(UiStyle.Warning, SceneStatus.BarColor(0.25));
        Assert.AreEqual(UiStyle.Error, SceneStatus.BarColor(Math.BitDecrement(0.25)));
        Assert.AreEqual(UiStyle.Error, SceneStatus.BarColor(0));
    }

    [TestMethod]
    public void TrainVehiclesUseTrainActivityAndOthersUseRelation()
    {
        var train = Train(TrainActivity.Running);
        Assert.AreEqual(new SceneStatus.Icon.Train(TrainActivity.Running),
            SceneStatus.Choose(Vehicle(train.Id, SettlementRelation.PlayerOwned, InstanceLock.Combat), train));
        Assert.AreEqual(new SceneStatus.Icon.Train(TrainActivity.Stopped),
            SceneStatus.Choose(Vehicle(train.Id, SettlementRelation.Hostile, InstanceLock.None), Train(TrainActivity.Stopped)));
        Assert.AreEqual(new SceneStatus.Icon.Relation(SettlementRelation.PlayerOwned),
            SceneStatus.Choose(Vehicle(train.Id, SettlementRelation.PlayerOwned, InstanceLock.None), null));
        Assert.AreEqual(new SceneStatus.Icon.Relation(SettlementRelation.NonHostile),
            SceneStatus.Choose(Vehicle(null, SettlementRelation.NonHostile, InstanceLock.Combat), train));
        Assert.AreEqual(new SceneStatus.Icon.Relation(SettlementRelation.Hostile),
            SceneStatus.Choose(Building(SettlementRelation.Hostile), train));
        Assert.AreEqual(new SceneStatus.Icon.Relation(SettlementRelation.PlayerOwned),
            SceneStatus.Choose(Building(SettlementRelation.PlayerOwned), null));
    }

    private static InstanceView Vehicle(TrainId? train, SettlementRelation relation, InstanceLock locks) => new(
        new InstanceId.Vehicle(new(1)), new("design"), new("composition"), new WorldLocation.Node(new(1)), relation,
        new InstanceDetails.Vehicle(SizeClass.Small, train), locks, new(40, 100), [], []);

    private static InstanceView Building(SettlementRelation relation) => new(
        new InstanceId.Building(new(2)), new("design"), new("composition"), new WorldLocation.Node(new(1)), relation,
        new InstanceDetails.Building(new(3), PlotSize.Small), InstanceLock.Combat, new(40, 100), [], []);

    private static TrainView Train(TrainActivity activity) => new(new(7),
        new TrainPosition.Node(new(1), null, null, null, null, NodeTravelPhase.Waiting, 0, null), activity,
        [new(1)], new(1), 0, 1, 1, 1, 0, 1, null, null, 0,
        new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0.5), [], null, [],
        TrainOrderFacing.FirstEndLeading, null);
}
