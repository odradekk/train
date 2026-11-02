using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;

namespace Train.Godot.Tests.Ui;

[TestClass]
public sealed class WorldListTests
{
    [TestMethod]
    public void AttentionIncludesMissingPlanBlockedAndStoppedButNotOrdinaryWaiting()
    {
        var plan = new RunPlan(true, [new PlanStop.Waypoint(new(91))]);
        foreach (var activity in Enum.GetValues<TrainActivity>())
        {
            Assert.IsTrue(WorldListDrawer.NeedsAttention(Train(10, activity)));
            Assert.AreEqual(activity is TrainActivity.Stopped or TrainActivity.NoRoute,
                WorldListDrawer.NeedsAttention(Train(10, activity) with { Plan = plan }), activity.ToString());
        }
    }

    [TestMethod]
    public void FleetFiltersIntersectWithNameSearchAndKeepEachTrainOnce()
    {
        var plan = new RunPlan(true, [new PlanStop.Waypoint(new(91))]);
        var trains = new[]
        {
            Train(42, TrainActivity.Running) with { Plan = plan },
            Train(43, TrainActivity.NoRoute) with { Plan = plan },
            Train(44) with { Plan = plan },
            Train(45, TrainActivity.Waiting) with { Plan = plan },
            Train(46, TrainActivity.Servicing) with { Plan = plan },
            Train(47, TrainActivity.NoRoute),
            Train(48, TrainActivity.Running),
            Train(49, TrainActivity.Combat) with { Plan = plan },
            Train(50, TrainActivity.Retreating) with { Plan = plan }
        };
        var view = View(trains);
        var content = new FixedContentView();
        ulong[] Ids(int filter, string search = "") => WorldListDrawer.Rows(WorldListKind.Fleet, view, content, search, filter)
            .Select(row => ((WorldObjectId.Train)row.Object!).Id.Value).ToArray();
        CollectionAssert.AreEqual(new ulong[] { 42, 43, 44, 45, 46, 47, 48, 49, 50 }, Ids(0));
        CollectionAssert.AreEqual(new ulong[] { 43, 44, 47, 48 }, Ids(1));
        CollectionAssert.AreEqual(new ulong[] { 42, 43, 45, 46, 47, 48, 49, 50 }, Ids(2),
            "运行中包括装卸、等待和受阻，只有已停车列车被排除。");
        CollectionAssert.AreEqual(new ulong[] { 44 }, Ids(3));
        CollectionAssert.AreEquivalent(Enum.GetValues<TrainActivity>(), trains.Select(train => train.Activity).Distinct().ToArray());
        Assert.IsFalse(Ids(2).Intersect(Ids(3)).Any());
        CollectionAssert.AreEquivalent(Ids(0), Ids(2).Concat(Ids(3)).ToArray());
        CollectionAssert.AreEqual(new ulong[] { 48 }, Ids(1, "  列车 48  "));
        Assert.AreEqual(0, Ids(1, "列车 42").Length);
        Assert.AreEqual(0, Ids(0, "运行").Length);
        Assert.AreEqual(0, WorldListDrawer.Rows(WorldListKind.Fleet, View([]), content, "", 0).Count);
    }

    private static FixedCoreReadView View(TrainView[] trains) => new()
    {
        World = new(1, new("fixture:rail"), 1000, 600, [], [],
            [.. trains.Select(train => new TrainMapItem(train.Id, train.Position, train.Activity))], [], []),
        Trains = trains.ToDictionary(train => train.Id, train => (Lookup<TrainView>)new Lookup<TrainView>.Found(train))
    };

    [TestMethod]
    public void FleetRowsShowNextStopAndLatestCashFlowWithoutTreatingNoReportAsZero()
    {
        var train = Train(42, TrainActivity.Running) with
        {
            Plan = new(false, [new PlanStop.Station(new(91), 60, []), new PlanStop.Waypoint(new(92))]),
            NextStopIndex = 0,
            RecentReports = [Report(-77, 1), Report(9007199254740993, 2)]
        };
        var content = Content();
        string Text(TrainView value) => WorldListDrawer.Rows(WorldListKind.Fleet, WithPlaces(value), content, "", 0).Single().Text;
        var text = Text(train);
        StringAssert.Contains(text, "列车 42");
        StringAssert.Contains(text, "青川");
        Assert.IsFalse(text.Contains("白石"));
        StringAssert.Contains(text, "+9007199254740993");
        Assert.IsFalse(text.Contains("-77"));
        var waypoint = Text(train with { NextStopIndex = 1 });
        StringAssert.Contains(waypoint, "白石");
        Assert.IsFalse(waypoint.Contains("青川"));
        var samePlace = train with { Plan = new(false, [new PlanStop.Station(new(91), 60, []), new PlanStop.Waypoint(new(91))]) };
        Assert.AreNotEqual(Text(samePlace), Text(samePlace with { NextStopIndex = 1 }),
            "在同一地点停靠和途经必须有不同的去向说明。");
        foreach (var withoutDestination in new[] { train with { NextStopIndex = null }, train with { Activity = TrainActivity.Retreating } })
        {
            Assert.IsFalse(Text(withoutDestination).Contains("青川"));
            Assert.IsFalse(Text(withoutDestination).Contains("白石"));
        }
        var noReport = Text(train with { Plan = null, NextStopIndex = null, RecentReports = [] });
        StringAssert.Contains(Text(train with { RecentReports = [Report(-234, 3)] }), "-234");
        var zeroReport = Text(train with { Plan = null, NextStopIndex = null, RecentReports = [Report(0, 4)] });
        Assert.AreNotEqual(noReport, zeroReport);
        StringAssert.Contains(zeroReport, "0");
    }

    private static RunReport Report(long cash, ulong step) => new(new(42), new(step, 0), RunEndReason.LoopCompleted,
        new(default, new(0), new(0), new(0), cash, 0, null, []));

    [TestMethod]
    public void EdgeFiltersSeparateConstructionFromBreaksAndSearchEndpointNames()
    {
        var edges = new EdgeMapItem[]
        {
            new(new(71), new(91), new(92), 100, Terrain, 0, 2, [], []),
            new(new(72), new(92), new(91), 200, Terrain, 1, 2, [], []),
            new(new(73), new(91), new(92), 300, Terrain, 2, 2, [new(new(9), 1, 120)], [])
        };
        var basis = WithPlaces();
        var view = new FixedCoreReadView { World = basis.World with { Edges = [.. edges] } };
        var content = Content();
        ulong[] Ids(int filter, string search = "") => WorldListDrawer.Rows(WorldListKind.Edges, view, content, search, filter)
            .Select(row => ((WorldObjectId.Edge)row.Object!).Id.Value).ToArray();
        CollectionAssert.AreEqual(new ulong[] { 71, 72, 73 }, Ids(0));
        CollectionAssert.AreEqual(new ulong[] { 72, 73 }, Ids(1));
        CollectionAssert.AreEqual(new ulong[] { 73 }, Ids(2));
        CollectionAssert.AreEqual(new ulong[] { 71 }, Ids(3, " 白石 "));
        Assert.AreEqual(0, Ids(2, "不存在").Length);
        var rows = WorldListDrawer.Rows(WorldListKind.Edges, view, content, "", 0);
        StringAssert.Contains(rows[0].Text, "青川");
        StringAssert.Contains(rows[0].Text, "白石");
        StringAssert.Contains(rows[0].Text, "100");
        StringAssert.Contains(rows[2].Text, "2/2");
        StringAssert.Contains(rows[2].Text, "300");
        Assert.IsFalse(rows[0].Text.Contains("71"));
    }

    [TestMethod]
    public void NodesGroupByRelationThenEmptyAndLairAndCountOnlyNameMatches()
    {
        var nodes = new NodeMapItem[]
        {
            Places[0] with { Id = new(95), Occupant = new NodeOccupant.Lair(new(21)) },
            Places[0] with { Id = new(94), Occupant = new NodeOccupant.Empty() },
            Places[0] with { Id = new(93), Occupant = new NodeOccupant.Settlement(new(13), SettlementRelation.Hostile) },
            Places[1], Places[0], Places[0] with { Id = new(96), Occupant = new NodeOccupant.Settlement(new(14), SettlementRelation.PlayerOwned) }
        };
        var view = new FixedCoreReadView
        {
            World = View([]).World with { Nodes = [.. nodes] },
            Lairs = new Dictionary<LairId, Lookup<LairView>>
            {
                [new(21)] = new Lookup<LairView>.Found(new(new(21), new(95), new("fixture:missing-lair"), []))
            }
        };
        var content = Content();
        var rows = WorldListDrawer.Rows(WorldListKind.Nodes, view, content, "", 0);
        var groups = rows.Where(row => row.Object is null).Select(row => Group(row.Text)).ToArray();
        CollectionAssert.AreEqual(new[] { "自有聚居地", "非敌对聚居地", "敌对聚居地", "空节点", "生物聚落" },
            groups.Select(group => group.Name).ToArray());
        CollectionAssert.AreEqual(new[] { 2, 1, 1, 1, 1 }, groups.Select(group => group.Count).ToArray());
        CollectionAssert.AreEqual(new ulong[] { 91, 96, 92, 93, 94, 95 },
            rows.Where(row => row.Object is not null).Select(row => ((WorldObjectId.Node)row.Object!).Id.Value).ToArray());
        StringAssert.Contains(rows[^1].Text, "平原");
        Assert.IsFalse(rows[^1].Text.Contains("fixture:missing-lair"));
        var searched = WorldListDrawer.Rows(WorldListKind.Nodes, view, content, "  eastGATE  ", 0);
        Assert.AreEqual(2, searched.Count);
        Assert.AreEqual(("敌对聚居地", 1), Group(searched[0].Text));
        Assert.AreEqual(new WorldObjectId.Node(new(93)), searched[1].Object);
        var partial = WorldListDrawer.Rows(WorldListKind.Nodes, view, content, "南站", 0);
        Assert.AreEqual(2, partial.Count);
        var partialGroup = Group(partial[0].Text);
        Assert.AreEqual("自有聚居地", partialGroup.Name);
        Assert.AreEqual(1, partialGroup.Count);
        Assert.IsTrue(partialGroup.Count < groups[0].Count);
        Assert.AreEqual(new WorldObjectId.Node(new(96)), partial[1].Object);
        Assert.AreEqual(0, WorldListDrawer.Rows(WorldListKind.Nodes, view, content, "不存在", 0).Count);
    }

    private static (string Name, int Count) Group(string text)
    {
        var count = System.Text.RegularExpressions.Regex.Match(text, @"\d+\s*$");
        Assert.IsTrue(count.Success, text);
        var name = new[] { "自有聚居地", "非敌对聚居地", "敌对聚居地", "空节点", "生物聚落" }
            .Single(title => text.StartsWith(title, StringComparison.Ordinal));
        return (name, int.Parse(count.Value));
    }

    [TestMethod]
    public void CombatSearchUsesTheReadablePlaceAndDoesNotAddGroups()
    {
        var view = new FixedCoreReadView { World = WithPlaces().World with
        {
            Edges = [new(new(71), new(91), new(92), 100, Terrain, 1, 1, [], [])],
            Combats = [new(new(80), new WorldLocation.Node(new(91))), new(new(81), new WorldLocation.Edge(new(71), 50))]
        } };
        var rows = WorldListDrawer.Rows(WorldListKind.Combats, view, Content(), "白石", 0);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(new WorldObjectId.Combat(new(81)), rows[0].Object);
        StringAssert.Contains(rows[0].Text, "青川—白石");
        Assert.IsFalse(rows[0].Text.Contains("71"));
    }

    private static FixedCoreReadView WithPlaces(params TrainView[] trains)
    {
        var basis = View(trains);
        return new() { World = basis.World with { Nodes = Places },
            Trains = trains.ToDictionary(train => train.Id, train => (Lookup<TrainView>)new Lookup<TrainView>.Found(train)) };
    }

    private static readonly TerrainView Terrain = new(new("fixture:plain"), 0, []);
    private static readonly ImmutableArray<NodeMapItem> Places =
    [
        new(new(91), new(0, 0), Terrain, new NodeOccupant.Settlement(new(11), SettlementRelation.PlayerOwned), [], 0, 1, null),
        new(new(92), new(100, 100), Terrain, new NodeOccupant.Settlement(new(12), SettlementRelation.NonHostile), [], 0, 1, null)
    ];

    private static FixedContentView Content() => new()
    {
        Lairs = new Dictionary<LairTypeId, ContentLookup<LairAppearanceProjection>>
        {
            [new("fixture:missing-lair")] = new ContentLookup<LairAppearanceProjection>.Missing([])
        },
        SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
        {
            [new(11)] = Layout(11, "青川"), [new(12)] = Layout(12, "白石"),
            [new(13)] = Layout(13, "Eastgate"), [new(14)] = Layout(14, "青川南站")
        },
        DefinitionDisplays = new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
        {
            [new CoreDefinitionReference.Terrain(Terrain.TypeId)] = new ContentLookup<ContentDisplay>.Found(
                new(new("平原", ""), null, null, false))
        }
    };

    private static ContentLookup<SettlementLayoutProjection> Layout(ulong id, string name) =>
        new ContentLookup<SettlementLayoutProjection>.Found(new(new(id), new("fixture:town"),
            new(new("fixture:ground"), new(null, null, null, true, null, null, [], [], [], [], []), new("地面", "")), [], new(name, "")));

    private static TrainView Train(ulong id, TrainActivity activity = TrainActivity.Stopped) => new(new(id),
        new TrainPosition.Node(new(91), null, null, null, null, NodeTravelPhase.Waiting, 0, null), activity,
        [], new(1), 0, 1, 1, 1, 0, 1, null, null, 0,
        new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, .2), [], null, [],
        TrainOrderFacing.FirstEndLeading, null);
}
