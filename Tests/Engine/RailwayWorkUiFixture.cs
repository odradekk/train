using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Engine.Tests;

internal static class RailwayWorkUiFixture
{
    internal const ulong ZeroTrackEdge = 1;
    internal const ulong BreakEdge = 2;
    internal const ulong ActiveEdge = 3;
    internal const long StartedUnits = 7;
    internal const long AcceptedUnits = 11;
    internal const double ActiveLengthMeters = 400;
    internal const double RailUnitsPerMeter = 0.7;
    internal static readonly GoodId Rail = new GoodId.Voxel(new("fixture:rail"));

    internal static FixedCoreReadView Create(ulong revision = 1) =>
        Build(revision, [StartedWork(), UnstartedWork()], new(1), 18);

    internal static FixedCoreReadView WithoutAutomaticRepair(ulong revision) =>
        Build(revision, [StartedWork()], null, 18);

    internal static FixedCoreReadView WithAcceptedRepair(ulong revision) =>
        Build(revision, [StartedWork(), AcceptedRepair()], null, 7);

    internal static RailwayWorkView StartedWork() => new(new(2), RailwayWorkKind.AddTrack, 2, null, new(1),
        [new(Rail, StartedUnits)], 0.25, []);

    internal static RailwayWorkView UnstartedWork() => new(new(3), RailwayWorkKind.RepairBreak, 1, new BreakId(2), new(1),
        [], 0, [Blocker()]);

    internal static RailwayWorkView AcceptedRepair() => UnstartedWork() with { InvestedGoods = [new(Rail, AcceptedUnits)], Blockers = [] };

    internal static CoreProblem Blocker() => new("insufficient_goods", "MaterialSource.Inventory",
        new WorldObjectId.Settlement(new(1)), ["voxel:fixture:rail"]);

    internal static FixedContentView Content(bool railwayRule = true)
    {
        var type = new SettlementTypeId("fixture:town-type");
        var template = new SettlementTemplateId("fixture:town");
        var symbol = new AssetRevision("fixture:town-symbol");
        var scene = new DesignSceneProjection(null, null, null, true, null, null, [], [], [], [], []);
        SettlementLayoutProjection Layout(ulong id, string name) => new(new(id), template,
            new(new("fixture:town-model"), scene, new(name, "")), [], new(name, ""));
        var projection = HostContentScenario.CreateProjection(HostCoreScenario.Input) with
        {
            RailwayRules = railwayRule ? [new RailwayRuleProjection(new("fixture:railway"), new("fixture-railway-v1"),
                0.7, 0.15, 28, new("fixture:rail"), RailUnitsPerMeter, 3, 18, 48, 80, 80, 15)] : [],
            SettlementTemplates = [new(template, type, 1, [], [], [], [], 40)],
            Goods = [new(Rail, new("fixture-good-v1"), new Money(1), 0.028, true, null)]
        };
        var png = ImmutableArray.CreateRange(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNoOODwHwAFxAKAPuJ4AwAAAABJRU5ErkJggg=="));
        return new(projection, new(1))
        {
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>>
            {
                [Rail] = new ContentLookup<GoodPresentation>.Found(new(Rail, new(new("夹具钢轨", ""), null, null, false), null, false))
            },
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
            {
                [new(1)] = new ContentLookup<SettlementLayoutProjection>.Found(Layout(1, "自有镇")),
                [new(2)] = new ContentLookup<SettlementLayoutProjection>.Found(Layout(2, "他镇"))
            },
            DefinitionDisplays = new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
            {
                [new CoreDefinitionReference.SettlementType(type)] = new ContentLookup<ContentDisplay>.Found(
                    new(new("镇", ""), null, symbol, false))
            },
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>
            {
                [symbol] = new ContentLookup<ContentAsset>.Found(new(symbol, png, new AssetMetadata.Png(1, 1), []))
            }
        };
    }

    private static FixedCoreReadView Build(ulong revision, ImmutableArray<RailwayWorkView> activeWorks, SettlementId? autoRepair, long railQuantity)
    {
        var terrain = new TerrainView(new("core:plains"), 0, []);
        NodeMapItem Node(ulong id, double x, double y, NodeOccupant occupant) => new(new(id), new(x, y), terrain, occupant, [], 0, 1, null);
        var nodes = new[]
        {
            Node(1, 500, 350, new NodeOccupant.Settlement(new(1), SettlementRelation.PlayerOwned)),
            Node(2, 1500, 350, new NodeOccupant.Settlement(new(2), SettlementRelation.NonHostile)),
            Node(3, 1500, 850, new NodeOccupant.Empty()),
            Node(4, 500, 850, new NodeOccupant.Empty())
        };
        var pending = new RailwayWorkView(new(1), RailwayWorkKind.AddTrack, 1, null, new(1), [], 0, [Blocker()]);
        EdgeView Edge(ulong id, ulong start, ulong end, double length, int tracks, ImmutableArray<TrackBreakView> breaks,
            ImmutableArray<RailwayWorkView> works, SettlementId? auto)
        {
            var map = new EdgeMapItem(new(id), new(start), new(end), length, terrain, tracks, 2, breaks, works);
            var trackViews = Enumerable.Range(1, tracks).Select(number => new TrackView(number, null, [])).ToImmutableArray();
            return new(map, trackViews, auto);
        }
        var edges = new[]
        {
            Edge(ZeroTrackEdge, 1, 2, 800, 0, [], [pending], null),
            Edge(BreakEdge, 2, 3, 500, 1, [new(new(1), 1, 120)], [], null),
            Edge(ActiveEdge, 3, 4, ActiveLengthMeters, 1, [new(new(2), 1, 80)], activeWorks, autoRepair)
        };
        var rule = new RuleStatusView(new("fixture:economy"), null, []);
        SettlementView Settlement(ulong id, ulong node, SettlementRelation relation, ulong inventory) => new(new(id), new(node),
            new("fixture:town-type"), new("fixture:town"), relation, new(inventory), [], [], 4, 1, 0, [], [], [],
            new(0, 0, 0, 0), new(rule, 10, 10, 0, 10, new PopulationExecution.NotStarted([]), null, []),
            new(TargetPriority.ArmedFirst, TargetScope.All), 1, false, [], [], [],
            new(rule, new(0, 0, 0, 0, 0, 0, 0), []));
        InventoryView Inventory(ulong id, ulong settlement, long quantity)
        {
            ImmutableArray<InventoryStackView> stacks = quantity == 0 ? [] : [new(Rail, quantity, false)];
            return new(new(id), new InventoryOwner.Settlement(new(settlement)), new WorldLocation.Node(new(settlement)),
                InventoryAccess.ReadWrite, 100, quantity, 0, 100, 0.028 * quantity, stacks, []);
        }
        return new(new(0, revision), HostCoreScenario.Input, revision: new(revision))
        {
            World = new(7, new("fixture:railway"), 2000, 1200, nodes.ToImmutableArray(),
                edges.Select(edge => edge.Map).ToImmutableArray(), [], [], []),
            Nodes = nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(new(node, [], 80, 40, 80))),
            Edges = edges.ToDictionary(edge => edge.Map.Id, edge => (Lookup<EdgeView>)new Lookup<EdgeView>.Found(edge)),
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
            {
                [new(1)] = new Lookup<SettlementView>.Found(Settlement(1, 1, SettlementRelation.PlayerOwned, 1)),
                [new(2)] = new Lookup<SettlementView>.Found(Settlement(2, 2, SettlementRelation.NonHostile, 2))
            },
            Inventories = new Dictionary<InventoryId, Lookup<InventoryView>>
            {
                [new(1)] = new Lookup<InventoryView>.Found(Inventory(1, 1, railQuantity)),
                [new(2)] = new Lookup<InventoryView>.Found(Inventory(2, 2, 0))
            }
        };
    }
}
