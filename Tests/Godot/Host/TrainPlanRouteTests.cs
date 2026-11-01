using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.Core.Contracts;

namespace Train.Godot.Tests.Host;

[TestClass]
public sealed class TrainPlanRouteTests
{
    [TestMethod]
    public void ConnectedNodesReturnTheirLineInBothDirections()
    {
        var world = World([Node(1), Node(2)], [Edge(7, 1, 2, 100)]);

        CollectionAssert.AreEqual(new[] { new EdgeId(7) }, TrainPlanRoute.Find(world, new(1), new(2))!.Value.ToArray());
        CollectionAssert.AreEqual(new[] { new EdgeId(7) }, TrainPlanRoute.Find(world, new(2), new(1))!.Value.ToArray());
    }

    [TestMethod]
    public void RouteUsesShortestTotalLengthAcrossSeveralLinesAndCycles()
    {
        var world = World([Node(1), Node(2), Node(3), Node(4)],
            [Edge(9, 1, 4, 20), Edge(8, 1, 3, 12), Edge(7, 3, 4, 2),
                Edge(3, 1, 2, 3), Edge(4, 2, 3, 4)]);

        CollectionAssert.AreEqual(new[] { new EdgeId(3), new EdgeId(4), new EdgeId(7) },
            TrainPlanRoute.Find(world, new(1), new(4))!.Value.ToArray());
    }

    [TestMethod]
    public void ExistingSameNodeReturnsAnEmptyRouteButMissingEndpointsReturnNull()
    {
        var world = World([Node(1), Node(2)], []);

        Assert.IsTrue(TrainPlanRoute.Find(world, new(1), new(1))!.Value.IsEmpty);
        Assert.IsNull(TrainPlanRoute.Find(world, new(1), new(2)));
        Assert.IsNull(TrainPlanRoute.Find(world, new(3), new(3)));
        Assert.IsNull(TrainPlanRoute.Find(world, new(3), new(1)));
        Assert.IsNull(TrainPlanRoute.Find(world, new(1), new(3)));
        Assert.IsNull(TrainPlanRoute.Find(World([], []), new(1), new(1)));
    }

    [TestMethod]
    [DataRow(0, new int[] { }, false)]
    [DataRow(1, new int[] { }, true)]
    [DataRow(1, new[] { 1 }, false)]
    [DataRow(2, new[] { 1 }, true)]
    [DataRow(2, new[] { 2 }, true)]
    [DataRow(2, new[] { 1, 2 }, false)]
    [DataRow(2, new[] { 1, 1 }, true)]
    [DataRow(3, new[] { 1, 2 }, true)]
    public void LineIsUsableExactlyWhenItHasAnIntactTrack(int tracks, int[] brokenTracks, bool reachable)
    {
        var world = World([Node(1), Node(2)], [Edge(7, 1, 2, 100, tracks, brokenTracks)]);

        var route = TrainPlanRoute.Find(world, new(1), new(2));

        if (reachable) CollectionAssert.AreEqual(new[] { new EdgeId(7) }, route!.Value.ToArray());
        else Assert.IsNull(route);
    }

    [TestMethod]
    public void HostileSettlementsAndLairsCanBeEndpointsButCannotBeTransitNodes()
    {
        NodeOccupant[] occupants = [new NodeOccupant.Lair(new(1)),
            new NodeOccupant.Settlement(new(1), SettlementRelation.Hostile)];
        foreach (var occupant in occupants)
        {
            var world = World([Node(1), Node(2, occupant), Node(3)],
                [Edge(1, 1, 2, 1), Edge(2, 2, 3, 1), Edge(3, 1, 3, 10)]);

            CollectionAssert.AreEqual(new[] { new EdgeId(3) }, TrainPlanRoute.Find(world, new(1), new(3))!.Value.ToArray());
            CollectionAssert.AreEqual(new[] { new EdgeId(1) }, TrainPlanRoute.Find(world, new(1), new(2))!.Value.ToArray());
            CollectionAssert.AreEqual(new[] { new EdgeId(2) }, TrainPlanRoute.Find(world, new(2), new(3))!.Value.ToArray());
            Assert.IsNull(TrainPlanRoute.Find(world with { Edges = world.Edges.RemoveAt(2) }, new(1), new(3)));
        }
    }

    [TestMethod]
    [DataRow(SettlementRelation.PlayerOwned)]
    [DataRow(SettlementRelation.NonHostile)]
    public void FriendlySettlementsAllowTransit(SettlementRelation relation)
    {
        var world = World([Node(1), Node(2, new NodeOccupant.Settlement(new(1), relation)), Node(3)],
            [Edge(1, 1, 2, 1), Edge(2, 2, 3, 1)]);

        CollectionAssert.AreEqual(new[] { new EdgeId(1), new EdgeId(2) },
            TrainPlanRoute.Find(world, new(1), new(3))!.Value.ToArray());
    }

    [TestMethod]
    public void EqualLengthRoutesUseTheEntireEdgeSequenceRegardlessOfMapOrder()
    {
        var world = World([Node(1), Node(2), Node(3), Node(4), Node(5)],
            [Edge(1, 1, 2, 1), Edge(30, 2, 3, 1), Edge(5, 3, 5, 1),
                Edge(20, 2, 4, 1), Edge(90, 4, 5, 1)]);
        var reordered = world with { Nodes = world.Nodes.Reverse().ToImmutableArray(), Edges = world.Edges.Reverse().ToImmutableArray() };

        foreach (var map in new[] { world, reordered })
            CollectionAssert.AreEqual(new[] { new EdgeId(1), new EdgeId(20), new EdgeId(90) },
                TrainPlanRoute.Find(map, new(1), new(5))!.Value.ToArray());
    }

    private static WorldSummary World(ImmutableArray<NodeMapItem> nodes, ImmutableArray<EdgeMapItem> edges) =>
        new(1, new("test:railway"), 100, 100, nodes, edges, [], [], []);

    private static NodeMapItem Node(ulong id, NodeOccupant? occupant = null) =>
        new(new(id), new(0, 0), new(new("test:plain"), 0, []), occupant ?? new NodeOccupant.Empty(), [], 0, 1, null);

    private static EdgeMapItem Edge(ulong id, ulong start, ulong end, double length, int tracks = 1,
        params int[] brokenTracks) =>
        new(new(id), new(start), new(end), length, new(new("test:plain"), 0, []), tracks, tracks,
            [.. brokenTracks.Select((track, index) => new TrackBreakView(new((ulong)index + 1), track, 10))], []);
}
