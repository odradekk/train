using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Measurement;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class SoakTargetsTests
{
    [TestMethod]
    public void DefaultSequenceKeepsLineBattleFirstAndEightTimesBeforeOne()
    {
        var targets = SoakTargets.Create("standard-world", new(1), new(2), null);
        Assert.IsTrue(targets.Standard);
        Assert.AreEqual(new WorldObjectId.Edge(new(15)), targets.PrimaryTarget);
        Assert.AreEqual("line-battle", targets.PrimaryView);
        CollectionAssert.AreEqual(new[] { SimulationSpeed.Eight, SimulationSpeed.One }, targets.Speeds(0));
        CollectionAssert.AreEqual(new[] { SimulationSpeed.Eight }, targets.Speeds(1));
        Assert.AreEqual(new EdgeId(23), SoakTargets.Create("standard-world", new(1), new(2), new(23)).Edge);
    }

    [TestMethod]
    [DataRow("crowded-station", "settlement")]
    [DataRow("busy-combat", "node-battle")]
    [DataRow("long-consist", "follow")]
    public void HeavySequenceUsesTheExplicitPrimaryTargetAndOneTimesFirst(string profile, string view)
    {
        var targets = new SoakTargets(profile, new(21), new(2), new(3));
        Assert.IsFalse(targets.Standard);
        Assert.AreEqual(view, targets.PrimaryView);
        Assert.AreEqual(profile == "long-consist" ? (WorldObjectId)new WorldObjectId.Train(new(21)) : new WorldObjectId.Node(new(2)),
            targets.PrimaryTarget);
        CollectionAssert.AreEqual(new[] { SimulationSpeed.One, SimulationSpeed.Eight }, targets.Speeds(0));
        CollectionAssert.AreEqual(new[] { SimulationSpeed.Eight }, targets.Speeds(1));
    }

    [TestMethod]
    [DataRow("crowded-station", TrainActivity.Servicing)]
    [DataRow("busy-combat", TrainActivity.Combat)]
    [DataRow("long-consist", TrainActivity.Running)]
    public void MatchingTargetsPassButWrongActivityReportsTheTarget(string profile, TrainActivity activity)
    {
        var targets = new SoakTargets(profile, new(21), new(2), new(3));
        targets.ValidateHeavy(World(activity));
        var error = Assert.Throws<InvalidDataException>(() => targets.ValidateHeavy(World(TrainActivity.Stopped)));
        StringAssert.Contains(error.Message, profile);
        StringAssert.Contains(error.Message, "21");
        StringAssert.Contains(error.Message, "Stopped");
    }

    [TestMethod]
    public void MissingExplicitTargetsAndLongConsistWithoutTheSelectedWaypointFail()
    {
        var world = World(TrainActivity.Running);
        Assert.Throws<InvalidDataException>(() => new SoakTargets("long-consist", new(22), new(2), new(3)).ValidateHeavy(world));
        Assert.Throws<InvalidDataException>(() => new SoakTargets("long-consist", new(21), new(99), new(3)).ValidateHeavy(world));
        Assert.Throws<InvalidDataException>(() => new SoakTargets("long-consist", new(21), new(2), new(99)).ValidateHeavy(world));
        Assert.Throws<InvalidDataException>(() => new SoakTargets("long-consist", new(21), new(2), new(3)).ValidateHeavy(
            World(TrainActivity.Running, plan: new(true, [new PlanStop.Waypoint(new(7)), new PlanStop.Waypoint(new(8))]))));
        Assert.Throws<ArgumentException>(() => SoakTargets.Create("other", new(21), new(2), null));
    }

    [TestMethod]
    public void EmptyConsistAndNonPlayerSettlementAreRejected()
    {
        var world = World(TrainActivity.Servicing);
        var targets = new SoakTargets("crowded-station", new(21), new(2), new(3));
        var train = ((Lookup<TrainView>.Found)world.FindTrain(new(21))).Value;
        Assert.Throws<InvalidDataException>(() => targets.ValidateHeavy(new FixedCoreReadView
        {
            World = world.World, Trains = new Dictionary<TrainId, Lookup<TrainView>>
                { [train.Id] = new Lookup<TrainView>.Found(train with { Vehicles = [] }) }
        }));
        Assert.Throws<InvalidDataException>(() => targets.ValidateHeavy(new FixedCoreReadView
        {
            World = world.World with { Nodes = [world.World.Nodes[0] with
                { Occupant = new NodeOccupant.Settlement(new(2), SettlementRelation.NonHostile) }] }, Trains = new Dictionary<TrainId, Lookup<TrainView>> { [new(21)] = world.FindTrain(new(21)) }
        }));
    }

    [TestMethod]
    public void LongConsistRequiresTheFullSizeAndAConnectedBuiltLine()
    {
        var world = World(TrainActivity.Running);
        var targets = new SoakTargets("long-consist", new(21), new(2), new(3));
        var train = ((Lookup<TrainView>.Found)world.FindTrain(new(21))).Value;
        foreach (var wrong in new[] { train with { LengthMeters = 1279 }, train with { Vehicles = train.Vehicles.RemoveAt(0) } })
            Assert.Throws<InvalidDataException>(() => targets.ValidateHeavy(new FixedCoreReadView
            {
                World = world.World, Trains = new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = new Lookup<TrainView>.Found(wrong) }
            }));
        foreach (var wrong in new[] { world.World.Edges[0] with { Tracks = 0 }, world.World.Edges[0] with { Start = new(9) } })
            Assert.Throws<InvalidDataException>(() => targets.ValidateHeavy(new FixedCoreReadView
            {
                World = world.World with { Edges = [wrong] }, Trains = new Dictionary<TrainId, Lookup<TrainView>> { [new(21)] = world.FindTrain(new(21)) }
            }));
        var combat = World(TrainActivity.Combat);
        Assert.Throws<InvalidDataException>(() => new SoakTargets("busy-combat", new(21), new(2), new(3)).ValidateHeavy(
            new FixedCoreReadView { World = combat.World with { Combats = [] }, Trains = new Dictionary<TrainId, Lookup<TrainView>> { [new(21)] = combat.FindTrain(new(21)) } }));
    }

    private static FixedCoreReadView World(TrainActivity activity, RunPlan? plan = null)
    {
        var terrain = new TerrainView(new("plain"), 0, []);
        var position = activity == TrainActivity.Running
            ? (TrainPosition)new TrainPosition.Edge(new(3), 1, TravelDirection.Forward, 10)
            : new TrainPosition.Node(new(2), null, null, null, null, NodeTravelPhase.Waiting, 0, null);
        var train = new TrainView(new(21), position, activity,
            Enumerable.Range(1, 32).Select(i => new VehicleId((ulong)i)).ToImmutableArray(), new(1), 0, 1280, 1, 1, 1, 10,
            plan ?? new(true, [new PlanStop.Waypoint(new(2)), new PlanStop.Waypoint(new(7))]), 0, 0,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0), [], null, [], TrainOrderFacing.FirstEndLeading, null);
        return new FixedCoreReadView { World = new(1, new("rail"), 1, 1,
            [new(new(2), new(0, 0), terrain, new NodeOccupant.Settlement(new(2), SettlementRelation.PlayerOwned), [], 4, 4, null)],
            [new(new(3), new(2), new(6), 100, terrain, 1, 1, [], [])],
            [new(train.Id, train.Position, train.Activity)], [],
            activity == TrainActivity.Combat ? [new(new(1), new WorldLocation.Node(new(2)))] : []),
            Trains = new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = new Lookup<TrainView>.Found(train),
                [new(22)] = new Lookup<TrainView>.Missing() } };
    }
}
