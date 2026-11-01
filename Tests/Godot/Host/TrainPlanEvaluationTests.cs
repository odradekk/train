using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Godot.Tests.Host;

[TestClass]
public sealed class TrainPlanEvaluationTests
{
    private static readonly GoodId Grain = new GoodId.Voxel(new("test:grain"));

    [TestMethod]
    public void ValidDraftReturnsTheCommandThatWasChecked()
    {
        var view = View([Node(2, new NodeOccupant.Settlement(new(3), SettlementRelation.NonHostile))]);
        var result = TrainPlanInput.Evaluate(new(7), false, [new(new(2), true, "1.016667", 61)], view, new FixedContentView());
        Assert.AreEqual(0, result.Problems.Count);
        Assert.IsNotNull(result.Command);
        Assert.AreEqual(new TrainId(7), result.Command.Train);
        Assert.IsFalse(result.Command.Plan.Loop);
        Assert.AreEqual(61UL, ((PlanStop.Station)result.Command.Plan.Stops.Single()).DwellSteps);
    }

    [TestMethod]
    public void ProblemsKeepEveryStopAndRuleLocationAndPreventSaving()
    {
        var result = TrainPlanInput.Evaluate(new(7), false,
            [new(new(2), true, "0", GoodsRules:
                [new(TradeAction.Buy, null, new(10), "0", ""), new(TradeAction.Sell, null, new(10), "0", "")]),
             new(new(99), false, ""),
             new(new(2), true, "0", GoodsRules: [new(TradeAction.Buy, Grain, null, "0", "")])],
            TradeView(SettlementRelation.NonHostile), Content());

        Assert.IsNull(result.Command);
        Assert.IsTrue(result.Problems.Any(problem => problem.Stop == 0 && problem.Rule == 0));
        Assert.IsTrue(result.Problems.Any(problem => problem.Stop == 0 && problem.Rule == 1));
        Assert.IsTrue(result.Problems.Any(problem => problem.Stop == 1 && problem.Rule is null));
        Assert.IsTrue(result.Problems.Any(problem => problem.Stop == 2 && problem.Rule == 0));
        Assert.IsTrue(result.Problems.All(problem => !string.IsNullOrWhiteSpace(problem.Message)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EmptyDraftHasAGlobalProblemAndCannotBeSaved(bool loop)
    {
        var result = TrainPlanInput.Evaluate(new(7), loop, [], View([]), new FixedContentView());

        Assert.IsNull(result.Command);
        Assert.IsTrue(result.Problems.Any(problem => problem.Stop is null && problem.Rule is null));
        Assert.AreEqual(0, result.Legs.Count);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void LoopNeedsAtLeastTwoDistinctPlaces(int count)
    {
        var result = TrainPlanInput.Evaluate(new(7), true,
            Enumerable.Repeat(new PlanRowInput(new(2), false, ""), count).ToArray(),
            View([Node(2)]), new FixedContentView());

        Assert.IsNull(result.Command);
        Assert.IsTrue(result.Problems.Any(problem => problem.Stop is null && problem.Rule is null));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingPlaceIsAnErrorForEitherStopType(bool station)
    {
        var result = TrainPlanInput.Evaluate(new(7), false, [new(new(99), station, "0")],
            View([]), new FixedContentView());

        Assert.IsNull(result.Command);
        Assert.IsTrue(result.Problems.Any(problem => problem.Stop == 0 && problem.Rule is null));
    }

    [TestMethod]
    public void EmptyHostileAndCreaturePlacesMustBeWaypoints()
    {
        NodeOccupant[] occupants = [new NodeOccupant.Empty(), new NodeOccupant.Lair(new(4)),
            new NodeOccupant.Settlement(new(3), SettlementRelation.Hostile)];
        foreach (var occupant in occupants)
        {
            var view = View([Node(2, occupant)]);
            var station = TrainPlanInput.Evaluate(new(7), false, [new(new(2), true, "0")], view, new FixedContentView());
            Assert.IsNull(station.Command);
            Assert.IsTrue(station.Problems.Any(problem => problem.Stop == 0 && problem.Rule is null));

            var waypoint = TrainPlanInput.Evaluate(new(7), false, [new(new(2), false, "")], view, new FixedContentView());
            Assert.AreEqual(0, waypoint.Problems.Count);
            Assert.IsInstanceOfType<PlanStop.Waypoint>(waypoint.Command!.Plan.Stops.Single());
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PreviewLegsFollowEachAdjacentPairAndOnlyLoopsReturnToTheFirstStop(bool loop)
    {
        var view = View([Node(1), Node(2), Node(3), Node(4)],
            [Edge(10, 1, 2), Edge(20, 2, 3), Edge(30, 3, 4)]);
        var result = TrainPlanInput.Evaluate(new(7), loop,
            [new(new(1), false, ""), new(new(3), false, ""), new(new(4), false, "")], view, new FixedContentView());

        Assert.AreEqual(0, result.Problems.Count);
        Assert.IsNotNull(result.Command);
        Assert.AreEqual(loop ? 3 : 2, result.Legs.Count);
        AssertLeg(result.Legs[0], 1, 3, 10, 20);
        AssertLeg(result.Legs[1], 3, 4, 30);
        if (loop) AssertLeg(result.Legs[2], 4, 1, 30, 20, 10);
    }

    [TestMethod]
    public void BrokenAdjacentAndReturnLegsHaveNullEdgesAndProblemsAtTheirDepartureStops()
    {
        var view = View([Node(1), Node(2), Node(3)],
            [Edge(10, 1, 2), Edge(20, 2, 3) with { Breaks = [new(new(1), 1, 10)] }]);
        var result = TrainPlanInput.Evaluate(new(7), true,
            [new(new(1), false, ""), new(new(2), false, ""), new(new(3), false, "")], view, new FixedContentView());

        Assert.IsNull(result.Command);
        Assert.AreEqual(3, result.Legs.Count);
        AssertLeg(result.Legs[0], 1, 2, 10);
        Assert.AreEqual(new NodeId(2), result.Legs[1].From);
        Assert.AreEqual(new NodeId(3), result.Legs[1].To);
        Assert.IsNull(result.Legs[1].Edges);
        Assert.AreEqual(new NodeId(3), result.Legs[2].From);
        Assert.AreEqual(new NodeId(1), result.Legs[2].To);
        Assert.IsNull(result.Legs[2].Edges);
        CollectionAssert.AreEquivalent(new[] { 1, 2 }, result.Problems.Select(problem => problem.Stop!.Value).ToArray());
        Assert.IsTrue(result.Problems.All(problem => problem.Rule is null));
    }

    [TestMethod]
    [DataRow(SettlementRelation.NonHostile, TradeAction.Buy, TradeAction.Sell)]
    [DataRow(SettlementRelation.PlayerOwned, TradeAction.Load, TradeAction.Unload)]
    public void SettlementInventoryOffersOnlyItsPermittedActions(SettlementRelation relation, TradeAction first, TradeAction second)
    {
        var view = TradeView(relation);
        CollectionAssert.AreEquivalent(new[] { (first, new InventoryId(10)), (second, new InventoryId(10)) },
            TrainPlanInput.Choices(view, new(2)).ToArray());
        foreach (var action in new[] { first, second })
        {
            var result = EvaluateRule(view, action, new(10));
            Assert.AreEqual(0, result.Problems.Count);
            Assert.IsNotNull(result.Command);
        }
        foreach (var action in Enum.GetValues<TradeAction>().Except([first, second]))
        {
            var result = EvaluateRule(view, action, new(10));
            Assert.IsNull(result.Command);
            Assert.IsTrue(result.Problems.Any(problem => problem.Stop == 0 && problem.Rule == 0));
        }
    }

    [TestMethod]
    public void HostileSettlementOffersNoActionsEvenWithALocalLease()
    {
        var view = TradeView(SettlementRelation.Hostile, lease: Lease());

        Assert.AreEqual(0, TrainPlanInput.Choices(view, new(2)).Count);
        foreach (var action in Enum.GetValues<TradeAction>())
        {
            var result = EvaluateRule(view, action, new(10));
            Assert.IsNull(result.Command);
            Assert.IsTrue(result.Problems.Any(problem => problem.Stop == 0 && problem.Rule == 0));
        }
    }

    [TestMethod]
    [DataRow(99UL, true)]
    [DataRow(100UL, false)]
    [DataRow(101UL, false)]
    public void LocalLeaseAllowsLoadingAfterExpiryButUnloadingOnlyBeforeExpiry(ulong step, bool canUnload)
    {
        var view = TradeView(SettlementRelation.NonHostile, step, Lease() with { Expired = !canUnload });
        var expected = new List<(TradeAction, InventoryId)>
        {
            (TradeAction.Buy, new(10)), (TradeAction.Sell, new(10)), (TradeAction.Load, new(20))
        };
        if (canUnload) expected.Add((TradeAction.Unload, new(20)));
        CollectionAssert.AreEquivalent(expected.ToArray(), TrainPlanInput.Choices(view, new(2)).ToArray());
        Assert.IsNotNull(EvaluateRule(view, TradeAction.Load, new(20)).Command);
        var unload = EvaluateRule(view, TradeAction.Unload, new(20));
        Assert.AreEqual(canUnload, unload.Command is not null);
        if (!canUnload) Assert.IsTrue(unload.Problems.Any(problem => problem.Stop == 0 && problem.Rule == 0));
    }

    [TestMethod]
    public void InventoryFromAnotherPlaceIsRejectedAtItsRule()
    {
        var result = EvaluateRule(TradeView(SettlementRelation.NonHostile), TradeAction.Buy, new(99));

        Assert.IsNull(result.Command);
        Assert.IsTrue(result.Problems.Any(problem => problem.Stop == 0 && problem.Rule == 0));
    }

    [TestMethod]
    [DataRow(10UL, TradeAction.Buy)]
    [DataRow(20UL, TradeAction.Load)]
    public void MissingInventoryIsExcludedFromChoicesAndPreventsSaving(ulong inventory, TradeAction action)
    {
        var view = TradeView(SettlementRelation.NonHostile, lease: Lease(), missingInventory: new(inventory));

        Assert.IsFalse(TrainPlanInput.Choices(view, new(2)).Any(choice => choice.Inventory == new InventoryId(inventory)));
        var result = EvaluateRule(view, action, new(inventory));
        Assert.IsNull(result.Command);
        Assert.IsTrue(result.Problems.Any(problem => problem.Stop == 0 && problem.Rule == 0));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingGoodIsReportedAtItsRule(bool missingPresentation)
    {
        var content = new FixedContentView
        {
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>>
            {
                [Grain] = missingPresentation ? Present(Grain, true) : new ContentLookup<GoodPresentation>.NotFound()
            }
        };
        var result = TrainPlanInput.Evaluate(new(7), false,
            [new(new(2), true, "0", GoodsRules: [new(TradeAction.Buy, Grain, new(10), "0", "")])],
            TradeView(SettlementRelation.NonHostile), content);

        Assert.IsNull(result.Command);
        Assert.IsTrue(result.Problems.Any(problem => problem.Stop == 0 && problem.Rule == 0));
    }

    [TestMethod]
    public void DuplicateGoodIsRejectedAtTheLaterRuleButMayAppearAtDifferentStops()
    {
        var buy = new GoodsRuleInput(TradeAction.Buy, Grain, new(10), "10", "");
        var sell = new GoodsRuleInput(TradeAction.Sell, Grain, new(10), "0", "");
        var view = TradeView(SettlementRelation.NonHostile);
        var duplicate = TrainPlanInput.Evaluate(new(7), false,
            [new(new(2), true, "0", GoodsRules: [buy, sell])], view, Content());

        Assert.IsNull(duplicate.Command);
        Assert.AreEqual(1, duplicate.Problems.Count);
        Assert.AreEqual(0, duplicate.Problems[0].Stop);
        Assert.AreEqual(1, duplicate.Problems[0].Rule);

        var separate = TrainPlanInput.Evaluate(new(7), false,
            [new(new(2), true, "0", GoodsRules: [buy]), new(new(2), true, "0", GoodsRules: [sell])], view, Content());
        Assert.AreEqual(0, separate.Problems.Count);
        Assert.IsNotNull(separate.Command);
    }

    private static PlanEvaluation EvaluateRule(FixedCoreReadView view, TradeAction action, InventoryId inventory) =>
        TrainPlanInput.Evaluate(new(7), false,
            [new(new(2), true, "0", GoodsRules: [new(action, Grain, inventory, "0", "")])], view, Content());

    private static void AssertLeg(PlanLeg leg, ulong from, ulong to, params ulong[] edges)
    {
        Assert.AreEqual(new NodeId(from), leg.From);
        Assert.AreEqual(new NodeId(to), leg.To);
        Assert.IsNotNull(leg.Edges);
        CollectionAssert.AreEqual(edges.Select(edge => new EdgeId(edge)).ToArray(), leg.Edges.Value.ToArray());
    }

    private static FixedCoreReadView View(ImmutableArray<NodeMapItem> nodes, ImmutableArray<EdgeMapItem> edges = default) => new()
    {
        World = new(1, new("test:railway"), 100, 100,
            nodes, edges.IsDefault ? [] : edges, [], [], [])
    };

    private static FixedCoreReadView TradeView(SettlementRelation relation, ulong step = 0,
        LeaseView? lease = null, InventoryId? missingInventory = null)
    {
        var rule = new RuleStatusView(new("test:rule"), null, []);
        var settlement = new SettlementView(new(3), new(2), new("test:town"), null, relation, new(10),
            [], [], 0, 1, 0, [], [], [], new(0, 0, 0, 0),
            new(rule, 0, 0, 0, 0, new PopulationExecution.NotStarted([]), null, []),
            new(TargetPriority.ArmedFirst, TargetScope.All), 1, false, [], [], lease is null ? [] : [lease.Id],
            new(rule, new(0, 0, 0, 0, 0, 0, 0), []));
        var inventories = new Dictionary<InventoryId, Lookup<InventoryView>>
        {
            [new(10)] = new Lookup<InventoryView>.Found(Inventory(10, new InventoryOwner.Settlement(new(3))))
        };
        if (lease is not null)
            inventories.Add(lease.Inventory, new Lookup<InventoryView>.Found(Inventory(lease.Inventory.Value, new InventoryOwner.Lease(lease.Id))));
        if (missingInventory is { } missing) inventories[missing] = new Lookup<InventoryView>.Missing();
        return new()
        {
            Position = new(step, 0),
            World = View([Node(2, new NodeOccupant.Settlement(new(3), relation))]).World,
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>> { [new(3)] = new Lookup<SettlementView>.Found(settlement) },
            Inventories = inventories,
            Leases = lease is null ? new Dictionary<LeaseId, Lookup<LeaseView>>() :
                new Dictionary<LeaseId, Lookup<LeaseView>> { [lease.Id] = new Lookup<LeaseView>.Found(lease) }
        };
    }

    private static NodeMapItem Node(ulong id, NodeOccupant? occupant = null) =>
        new(new(id), new(0, 0), new(new("test:plain"), 0, []), occupant ?? new NodeOccupant.Empty(), [], 0, 1, null);

    private static EdgeMapItem Edge(ulong id, ulong start, ulong end) =>
        new(new(id), new(start), new(end), 100, new(new("test:plain"), 0, []), 1, 1, [], []);

    private static InventoryView Inventory(ulong id, InventoryOwner owner) =>
        new(new(id), owner, new WorldLocation.Node(new(2)), InventoryAccess.ReadWrite, 100, 0, 0, 100, 0, [], []);

    private static LeaseView Lease() => new(new(5), new(3), new(20), 100, 100, new(0), false);

    private static FixedContentView Content() => new()
    {
        Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>> { [Grain] = Present(Grain) }
    };

    private static ContentLookup<GoodPresentation> Present(GoodId good, bool missing = false) =>
        new ContentLookup<GoodPresentation>.Found(new(good, new(new("谷物", ""), null, null, false), null, missing));
}
