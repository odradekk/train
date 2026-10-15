using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Railway;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Railway;

[TestClass]
public sealed class TrainRoutingTests
{
    private static readonly TerrainView Terrain = new(new("test:plain"), 0, []);

    [TestMethod]
    public void Route_uses_shortest_travel_time_in_both_directions()
    {
        ImmutableArray<NodeMapItem> nodes = [Node(1), Node(2), Node(3)];
        ImmutableArray<EdgeMapItem> edges = [Edge(10, 1, 3, 12), Edge(20, 1, 2, 3), Edge(30, 2, 3, 4)];

        AssertRoute(TrainRouting.FindRoute(nodes, edges, new(1), new(3), 2), 7, 20, 30);
        AssertRoute(TrainRouting.FindRoute(nodes, edges, new(3), new(1), 2), 7, 30, 20);
        AssertRoute(TrainRouting.FindRoute(nodes, edges, new(2), new(2), 2), 0);
        var blockedEdges = edges.SetItem(1, edges[1] with { Tracks = 0 });
        AssertRoute(TrainRouting.FindRoute(nodes, blockedEdges, new(1), new(3), 2), 12, 10);
    }

    [TestMethod]
    public void Equal_time_routes_compare_the_whole_edge_sequence_independent_of_input_order()
    {
        ImmutableArray<NodeMapItem> nodes = [Node(1), Node(2), Node(3), Node(4), Node(5)];
        ImmutableArray<EdgeMapItem> edges =
        [
            Edge(1, 1, 2, 1), Edge(30, 2, 3, 1), Edge(2, 3, 5, 1),
            Edge(20, 2, 4, 1), Edge(90, 4, 5, 1)
        ];

        AssertRoute(TrainRouting.FindRoute(nodes, edges, new(1), new(5), 2), 3, 1, 20, 90);
        AssertRoute(TrainRouting.FindRoute(nodes.Reverse().ToImmutableArray(),
            edges.Reverse().ToImmutableArray(), new(1), new(5), 2), 3, 1, 20, 90);
    }

    [TestMethod]
    public void Track_selection_excludes_breaks_and_repairs_and_chooses_the_smallest_remaining_number()
    {
        var edge = Edge(1, 1, 2, 5) with
        {
            Tracks = 4,
            Breaks = [new(new(1), 1, 2)],
            Works = [Work(RailwayWorkKind.RepairBreak, 2), Work(RailwayWorkKind.AddTrack, 3)]
        };

        Assert.AreEqual(3, TrainRouting.FindTrack(edge));
        Assert.AreEqual(1, TrainRouting.FindTrack(edge with { Breaks = [] }));
        Assert.AreEqual(1, TrainRouting.FindTrack(Edge(1, 1, 2, 5) with
        {
            Works = [Work(RailwayWorkKind.AddTrack, 1)]
        }));
    }

    [TestMethod]
    public void Routes_require_at_least_one_enterable_track()
    {
        ImmutableArray<NodeMapItem> nodes = [Node(1), Node(2)];
        var edge = Edge(1, 1, 2, 5) with { Tracks = 0 };
        Assert.IsNull(TrainRouting.FindTrack(edge));
        Assert.IsNull(TrainRouting.FindRoute(nodes, [edge], new(1), new(2), 2));

        edge = edge with
        {
            Tracks = 2,
            Breaks = [new(new(1), 1, 2)],
            Works = [Work(RailwayWorkKind.RepairBreak, 2)]
        };
        Assert.IsNull(TrainRouting.FindTrack(edge));
        Assert.IsNull(TrainRouting.FindRoute(nodes, [edge], new(1), new(2), 2));

        edge = edge with { Tracks = 3 };
        AssertRoute(TrainRouting.FindRoute(nodes, [edge], new(1), new(2), 2), 5, 1);
    }

    [TestMethod]
    public void Hostile_settlements_and_lairs_are_only_entered_as_the_current_target_and_can_be_departed()
    {
        NodeOccupant[] occupants =
        [
            new NodeOccupant.Settlement(new(1), SettlementRelation.Hostile),
            new NodeOccupant.Lair(new(1))
        ];
        ImmutableArray<EdgeMapItem> edges = [Edge(1, 1, 2, 1), Edge(2, 2, 3, 1), Edge(3, 1, 3, 10)];

        foreach (var occupant in occupants)
        {
            ImmutableArray<NodeMapItem> nodes = [Node(1), Node(2, occupant), Node(3)];
            AssertRoute(TrainRouting.FindRoute(nodes, edges, new(1), new(3), 2), 10, 3);
            Assert.IsNull(TrainRouting.FindRoute(nodes, edges.RemoveAt(2), new(1), new(3), 2));
            AssertRoute(TrainRouting.FindRoute(nodes, edges, new(1), new(2), 2), 1, 1);
            AssertRoute(TrainRouting.FindRoute(nodes, edges, new(2), new(3), 2), 1, 2);
        }
    }

    [TestMethod]
    public void Friendly_settlements_allow_transit()
    {
        ImmutableArray<EdgeMapItem> edges = [Edge(1, 1, 2, 1), Edge(2, 2, 3, 1)];
        foreach (var relation in new[] { SettlementRelation.PlayerOwned, SettlementRelation.NonHostile })
        {
            ImmutableArray<NodeMapItem> nodes =
                [Node(1), Node(2, new NodeOccupant.Settlement(new(1), relation)), Node(3)];
            AssertRoute(TrainRouting.FindRoute(nodes, edges, new(1), new(3), 2), 2, 1, 2);
        }
    }

    [TestMethod]
    public void Prediction_accumulates_waypoints_until_station_or_the_last_stop_of_a_single_run()
    {
        ImmutableArray<NodeMapItem> nodes = [Node(1), Node(2), Node(3), Node(4)];
        ImmutableArray<EdgeMapItem> edges = [Edge(1, 1, 2, 2), Edge(2, 2, 3, 3), Edge(3, 3, 4, 4)];
        var plan = new RunPlan(false, [Waypoint(2), Waypoint(3), Station(4)]);

        Assert.AreEqual(19.0, Predict(nodes, edges, new(1), plan, 0));
        Assert.AreEqual(14.0, Predict(nodes, edges, new(2), plan, 1));
        Assert.AreEqual(19.0, Predict(nodes, edges, new(1),
            new(false, [Waypoint(2), Waypoint(3), Waypoint(4)]), 0));
        Assert.AreEqual(6.0, Predict(nodes, edges, new(1),
            new(true, [Station(2), Waypoint(4)]), 0));
    }

    [TestMethod]
    public void Prediction_wraps_loop_index_and_consumes_repeated_targets()
    {
        ImmutableArray<NodeMapItem> nodes = [Node(1), Node(2), Node(3)];
        ImmutableArray<EdgeMapItem> edges = [Edge(1, 1, 2, 2), Edge(2, 2, 3, 3)];

        Assert.AreEqual(7.0, Predict(nodes, edges, new(3),
            new(true, [Station(2), Waypoint(3)]), 1));
        Assert.AreEqual(12.0, Predict(nodes, edges, new(1),
            new(false, [Waypoint(1), Waypoint(2), Waypoint(2), Station(3)]), 0));
        Assert.AreEqual(1.0, Predict(nodes, edges, new(1), new(false, [Station(1)]), 0));
    }

    [TestMethod]
    public void Waypoint_only_loop_stops_at_reversal_but_not_on_a_cycle()
    {
        ImmutableArray<NodeMapItem> nodes = [Node(1), Node(2), Node(3)];
        ImmutableArray<EdgeMapItem> edges = [Edge(1, 1, 2, 2), Edge(2, 2, 3, 3)];

        Assert.AreEqual(12.0, Predict(nodes, edges, new(1), new(true, [Waypoint(2), Waypoint(3)]), 0));
        Assert.AreEqual(7.0, Predict(nodes, edges, new(3), new(true, [Waypoint(2), Waypoint(3)]), 1));
        Assert.IsNull(Predict(nodes, edges.Add(Edge(3, 3, 1, 4)), new(1),
            new(true, [Waypoint(2), Waypoint(3), Waypoint(1)]), 0));
    }

    [TestMethod]
    public void Prediction_stops_at_the_last_reachable_target_when_a_later_leg_has_no_route()
    {
        ImmutableArray<NodeMapItem> nodes = [Node(1), Node(2), Node(3)];
        ImmutableArray<EdgeMapItem> edges = [Edge(1, 1, 2, 2)];

        Assert.AreEqual(6.0, Predict(nodes, edges, new(1), new(false, [Waypoint(2), Station(3)]), 0));
        Assert.AreEqual(6.0, Predict(nodes, edges, new(1), new(true, [Waypoint(2), Waypoint(3)]), 0));
        Assert.AreEqual(1.0, Predict(nodes, edges, new(1), new(false, [Station(3)]), 0));
    }

    [TestMethod]
    public void Prediction_checks_the_closing_leg_of_a_waypoint_loop()
    {
        ImmutableArray<NodeMapItem> nodes =
            [Node(1), Node(2, new NodeOccupant.Settlement(new(1), SettlementRelation.Hostile)), Node(3)];
        ImmutableArray<EdgeMapItem> edges = [Edge(1, 1, 2, 1), Edge(2, 2, 3, 1)];

        Assert.AreEqual(9.0, Predict(nodes, edges, new(1), new(true, [Waypoint(1), Waypoint(2), Waypoint(3)]), 0));

        nodes = [Node(1), Node(2), Node(3, new NodeOccupant.Settlement(new(1), SettlementRelation.Hostile)), Node(4)];
        edges = [Edge(1, 1, 2, 2), Edge(2, 2, 3, 0.1), Edge(3, 3, 4, 0.1)];
        Assert.AreEqual(12.2, Predict(nodes, edges, new(1), new(true, [Waypoint(2), Waypoint(3), Waypoint(4)]), 0));
    }

    private static double? Predict(ImmutableArray<NodeMapItem> nodes, ImmutableArray<EdgeMapItem> edges,
        NodeId start, RunPlan plan, int index)
    {
        var state = WorldFixture.State();
        var content = state.Content with { RailwayRules = [state.Content.RailwayRules[0] with
            { ApproachLengthMeters = 1, DepartureLengthMeters = 2 }] };
        var train = state.World.Trains[0] with { Position = WorldFixture.Node(start), Plan = plan, NextStopIndex = index };
        var world = state.World with { Nodes = nodes, Edges = edges, Settlements = [], Trains = [train] };
        return TrainOperations.PredictStopDistance(world, content, train, start, null, new TrainDispatch(world, content), 0);
    }

    private static NodeMapItem Node(ulong id, NodeOccupant? occupant = null) =>
        new(new(id), new(0, 0), Terrain, occupant ?? new NodeOccupant.Empty(), [], 0, 0, null);

    private static EdgeMapItem Edge(ulong id, ulong start, ulong end, double length) =>
        new(new(id), new(start), new(end), length, Terrain, 1, 4, [], []);

    private static RailwayWorkView Work(RailwayWorkKind kind, int track) =>
        new(new((ulong)track), kind, track,
            kind == RailwayWorkKind.RepairBreak ? new BreakId((ulong)track) : null, new(1), [], 0, []);

    private static PlanStop Waypoint(ulong node) => new PlanStop.Waypoint(new(node));

    private static PlanStop Station(ulong node) => new PlanStop.Station(new(node), 0, []);

    private static void AssertRoute((ImmutableArray<EdgeId> Edges, double DistanceMeters)? route,
        double distance, params ulong[] edges)
    {
        Assert.IsNotNull(route);
        var actual = route!.Value;
        Assert.AreEqual(distance, actual.DistanceMeters);
        CollectionAssert.AreEqual(edges, actual.Edges.Select(edge => edge.Value).ToArray());
    }
}
