using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeCacheDependencyBudgetTests
{
    [TestMethod]
    public void DependencyFingerprintsCountTowardTheIdleNodeBudget()
    {
        var snapshot = NodeFixtures.Snapshot(trains: []);
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var dependencies = RailwayDependencies.Capture(snapshot, path, 0, [], [], [], [], false, false);
        var boundary = new RailwayTerrain.BoundaryData(dependencies, new([]),
            ImmutableDictionary<GridColumn, double>.Empty, default, []);
        var cache = new NodeScene.Cache(idleBudget: boundary.Grid.ArrayBytes);
        var owner = cache.Open(layout.Node);
        cache.Publish(layout.Node, owner, new(layout, [],
            ImmutableDictionary<EdgeId, RailwayTerrain.BoundaryData>.Empty.Add(path.Edge.Id, boundary)));
        cache.Close(layout.Node, owner);

        Assert.IsNull(cache.Read(layout.Node), "A budget spent on the grid cannot also retain dependency fingerprints for free.");
    }
}
