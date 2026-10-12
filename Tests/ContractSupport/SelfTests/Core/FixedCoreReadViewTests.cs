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
        var view = new FixedCoreReadView { Position = new SimulationPosition(0, 0) };
        Assert.AreEqual(new SimulationPosition(0, 0), view.Position);
        Assert.AreEqual(view.Position, view.Position);
    }

    [TestMethod]
    public void UnconfiguredPropertyAndQueryFailExplicitly()
    {
        var view = new FixedCoreReadView();
        AssertUnconfigured(() => _ = view.Position);
        AssertUnconfigured(() => view.FindNode(new NodeId(1)));
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
    public void FoundQueryReturnsFixedObjectAndNullFaultRequiresConfiguration()
    {
        var id = new CombatId(3);
        var combat = new CombatView(id, new WorldLocation.Node(new NodeId(1)), 4, []);
        var found = new Lookup<CombatView>.Found(combat);
        var view = new FixedCoreReadView
        {
            Fault = null,
            Combats = new Dictionary<CombatId, Lookup<CombatView>> { [id] = found }
        };
        Assert.IsNull(view.Fault);
        Assert.AreSame(found, view.FindCombat(id));
        Assert.AreSame(combat, ((Lookup<CombatView>.Found)view.FindCombat(id)).Value);
        AssertUnconfigured(() => _ = new FixedCoreReadView().Fault);
        AssertUnconfigured(() => view.PreviewSettlement(new NodeId(1)));
    }

    private static void AssertUnconfigured(Action operation)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(operation);
        StringAssert.StartsWith(error.Message, "UNCONFIGURED:");
    }
}
