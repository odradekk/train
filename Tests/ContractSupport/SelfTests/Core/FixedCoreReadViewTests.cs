using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.ContractSupport.Tests.Core;

[TestClass]
public sealed class FixedCoreReadViewTests
{
    [TestMethod]
    public void ExplicitDefaultValueIsConfigured()
    {
        var view = new FixedCoreReadView { Position = new SimulationPosition(0, 0), InputIdentity = new("input") };
        Assert.AreEqual(new SimulationPosition(0, 0), view.Position);
        Assert.AreEqual(new CoreInputId("input"), view.InputIdentity);
    }

    [TestMethod]
    public void UnconfiguredPropertyAndQueryFailExplicitly()
    {
        var view = new FixedCoreReadView();
        AssertUnconfigured(() => _ = view.Position);
        AssertUnconfigured(() => _ = view.InputIdentity);
        AssertUnconfigured(() => _ = view.World);
        AssertUnconfigured(() => view.FindNode(new NodeId(1)));
        AssertUnconfigured(() => view.FindEdge(new EdgeId(1)));
        AssertUnconfigured(() => view.FindTrain(new TrainId(1)));
        AssertUnconfigured(() => view.FindInstance(new InstanceId.Vehicle(new(1))));
        AssertUnconfigured(() => view.FindCombat(new CombatId(1)));
        AssertUnconfigured(() => _ = view.Revision);
        AssertUnconfigured(() => _ = view.Fault);
        AssertUnconfigured(() => view.FindLocation(new WorldLocation.Node(new(1))));
    }

    [TestMethod]
    public void WorldRetainsFixedInput()
    {
        var world = new WorldSummary(42, new("railway"), 100, 100, [], [], [], [], []);
        var view = new FixedCoreReadView { World = world };
        Assert.AreSame(world, view.World);
    }

    [TestMethod]
    public void ExplicitMissingIsRetainedAndConfigurationIsCopied()
    {
        var id = new NodeId(1);
        var missing = new Lookup<NodeView>.Missing();
        var entries = new Dictionary<NodeId, Lookup<NodeView>> { [id] = missing };
        var view = new FixedCoreReadView { Nodes = entries };
        entries.Clear();
        Assert.AreSame(missing, view.FindNode(id));
        Assert.AreSame(missing, view.FindNode(id));
        AssertUnconfigured(() => view.FindNode(new NodeId(2)));
    }

    [TestMethod]
    public void FoundQueryReturnsFixedObject()
    {
        var id = new CombatId(3);
        var combat = new CombatView(id, new WorldLocation.Node(new NodeId(1)), 4, []);
        var found = new Lookup<CombatView>.Found(combat);
        var view = new FixedCoreReadView
        {
            Combats = new Dictionary<CombatId, Lookup<CombatView>> { [id] = found }
        };
        Assert.AreSame(found, view.FindCombat(id));
        Assert.AreSame(combat, ((Lookup<CombatView>.Found)view.FindCombat(id)).Value);
        AssertUnconfigured(() => _ = new FixedCoreReadView().Fault);
        AssertUnconfigured(() => view.PreviewSettlement(new NodeId(1)));
    }

    [TestMethod]
    public void ConfiguredEdgeTrainAndInstanceQueriesRetainExplicitMissing()
    {
        var edge = new Lookup<EdgeView>.Missing();
        var train = new Lookup<TrainView>.Missing();
        var instance = new Lookup<InstanceView>.Missing();
        var instanceId = new InstanceId.Vehicle(new(1));
        var view = new FixedCoreReadView
        {
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>> { [new(1)] = edge },
            Trains = new Dictionary<TrainId, Lookup<TrainView>> { [new(1)] = train },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>> { [instanceId] = instance }
        };
        Assert.AreSame(edge, view.FindEdge(new(1)));
        Assert.AreSame(train, view.FindTrain(new(1)));
        Assert.AreSame(instance, view.FindInstance(instanceId));
    }

    private static void AssertUnconfigured(Action operation)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(operation);
        StringAssert.StartsWith(error.Message, "UNCONFIGURED:");
    }
}
