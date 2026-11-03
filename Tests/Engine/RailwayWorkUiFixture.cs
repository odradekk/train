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
    internal const long AcceptedUnits = 5;
    internal const double ActiveLengthMeters = 400;
    internal const double RailUnitsPerMeter = 0.7;
    internal static readonly GoodId Rail = new GoodId.Voxel(new("fixture:rail"));

    internal static FixedCoreReadView Create(ulong revision = 1) =>
        Build(revision, [StartedWork(), UnstartedWork()], new(1), 18);

    internal static FixedCoreReadView WithManyBreaks(ulong revision = 1) =>
        Build(revision, [StartedWork(), UnstartedWork()], new(1), 18, activeBreaks: 60);

    internal static FixedCoreReadView WithAcceptedRepair(ulong revision) =>
        Build(revision, [StartedWork(), AcceptedRepair()], null, 18 - AcceptedUnits);

    internal static FixedCoreReadView MissingRules() => Build(1, [], null, 18, missingRules: true);

    internal static FixedCoreReadView ReadyToRepair() => Build(1, [StartedWork()], null, 18);

    internal static FixedCoreReadView WithStock(long stock, bool automaticRepair = true) =>
        Build(1, [], automaticRepair ? new(1) : null, stock);

    internal static FixedCoreReadView WithoutSource() => Build(1, [], null, 18, ownedSource: false);

    internal static FixedCoreReadView AtTrackLimit() => Build(1, [], null, 18, activeTracks: 2);

    internal static RailwayWorkView StartedWork() => new(new(2), RailwayWorkKind.AddTrack, 2, null, new(1),
        [new(Rail, StartedUnits)], 0.25, []);

    internal static RailwayWorkView UnstartedWork() => new(new(3), RailwayWorkKind.RepairBreak, 1, new BreakId(2), new(1),
        [], 0, [Blocker()]);

    internal static RailwayWorkView AcceptedRepair() => UnstartedWork() with { InvestedGoods = [new(Rail, AcceptedUnits)], Blockers = [] };

    internal static CoreProblem Blocker() => new("insufficient_goods", "MaterialSource.Inventory",
        new WorldObjectId.Settlement(new(1)), ["voxel:fixture:rail"]);

    internal static FixedContentView Content(bool missingGood = false, ulong generation = 1)
    {
        var type = new SettlementTypeId("fixture:town-type");
        var template = new SettlementTemplateId("fixture:town");
        var symbol = new AssetRevision("fixture:town-symbol");
        var scene = new DesignSceneProjection(null, null, null, true, null, null, [], [], [], [], []);
        SettlementLayoutProjection Layout(ulong id, string name) => new(new(id), template,
            new(new("fixture:town-model"), scene, new(name, "")), [], new(name, ""));
        var projection = HostContentScenario.CreateProjection(HostCoreScenario.Input) with
        {
            RailwayRules = [new RailwayRuleProjection(new("fixture:railway"), new("fixture-railway-v1"),
                0.7, 0.15, 28, new("fixture:rail"), RailUnitsPerMeter, 3, 18, 48, 80, 80, 15)],
            SettlementTemplates = [new(template, type, 1, [], [], [], [], 40)],
            Goods = [new(Rail, new("fixture-good-v1"), new Money(1), 0.028, true, null)]
        };
        var png = ImmutableArray.CreateRange(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNoOODwHwAFxAKAPuJ4AwAAAABJRU5ErkJggg=="));
        return new(projection, new(generation))
        {
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>>
            {
                [Rail] = missingGood ? new ContentLookup<GoodPresentation>.NotFound() : new ContentLookup<GoodPresentation>.Found(new(Rail, new(new("夹具钢轨", ""), null, null, false), null, false))
            },
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
            {
                [new(1)] = new ContentLookup<SettlementLayoutProjection>.Found(Layout(1, "自有镇")),
                [new(2)] = new ContentLookup<SettlementLayoutProjection>.Found(Layout(2, "他镇"))
            },
            DefinitionDisplays = new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
            {
                [new CoreDefinitionReference.Terrain(new("core:plains"))] = new ContentLookup<ContentDisplay>.NotFound(),
                [new CoreDefinitionReference.SettlementType(type)] = new ContentLookup<ContentDisplay>.Found(
                    new(new("镇", ""), null, symbol, false))
            },
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>
            {
                [symbol] = new ContentLookup<ContentAsset>.Found(new(symbol, png, new AssetMetadata.Png(1, 1), []))
            }
        };
    }

    private static FixedCoreReadView Build(ulong revision, ImmutableArray<RailwayWorkView> activeWorks, SettlementId? autoRepair, long railQuantity,
        int activeBreaks = 1, bool ownedSource = true, int activeTracks = 1, bool missingRules = false)
    {
        var terrain = new TerrainView(new("core:plains"), 0, []);
        NodeMapItem Node(ulong id, double x, double y, NodeOccupant occupant) => new(new(id), new(x, y), terrain, occupant, [], 0, 1, null);
        var nodes = new[]
        {
            Node(1, 500, 350, new NodeOccupant.Settlement(new(1), ownedSource ? SettlementRelation.PlayerOwned : SettlementRelation.NonHostile)),
            Node(2, 1500, 350, new NodeOccupant.Settlement(new(2), SettlementRelation.NonHostile)),
            Node(3, 1500, 850, new NodeOccupant.Empty()),
            Node(4, 500, 850, new NodeOccupant.Empty())
        };
        var unstartedRepair = new RailwayWorkView(new(1), RailwayWorkKind.RepairBreak, 1, new BreakId(1), new(1), [], 0, [Blocker()]);
        EdgeView Edge(ulong id, ulong start, ulong end, double length, int tracks, ImmutableArray<TrackBreakView> breaks,
            ImmutableArray<RailwayWorkView> works, SettlementId? auto)
        {
            var map = new EdgeMapItem(new(id), new(start), new(end), length, terrain, tracks, 2, breaks, works);
            var trackViews = Enumerable.Range(1, tracks).Select(number => new TrackView(number, null, id == ActiveEdge && number == 1 ? [new(987654)] : [])).ToImmutableArray();
            return new(map, trackViews, auto);
        }
        var edges = new[]
        {
            Edge(ZeroTrackEdge, 1, 2, 800, 0, [], [], null),
            Edge(BreakEdge, 2, 3, 500, 1, [new(new(1), 1, 120)], [unstartedRepair], new(1)),
            Edge(ActiveEdge, 3, 4, ActiveLengthMeters, activeTracks,
                [.. Enumerable.Range(0, activeBreaks).Select(index => new TrackBreakView(new((ulong)index + 2), 1, 80 + index * 5))],
                activeWorks, autoRepair)
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
        var scene = UiWorldFixture.Scene();
        var train = ((Lookup<TrainView>.Found)scene.FindTrain(new(7))).Value with
        {
            Id = new(987654), Inventory = new(3), Activity = TrainActivity.Running,
            Position = new TrainPosition.Edge(new(ActiveEdge), 1, TravelDirection.Forward, 35)
        };
        var location = new WorldLocation.Edge(new(ActiveEdge), 35);
        var vehicle = ((Lookup<InstanceView>.Found)scene.FindInstance(new InstanceId.Vehicle(new(9)))).Value with
        {
            Location = location,
            Details = new InstanceDetails.Vehicle(SizeClass.Medium, train.Id)
        };
        var trainInventory = ((Lookup<InventoryView>.Found)scene.FindInventory(new(1))).Value with
        {
            Id = train.Inventory, Owner = new InventoryOwner.Train(train.Id), Location = location
        };
        return new(new(0, revision), HostCoreScenario.Input, revision: new(revision))
        {
            RailwayWorkPreviews = Previews(edges, railQuantity, missingRules),
            Trains = new Dictionary<TrainId, Lookup<TrainView>>
            {
                [train.Id] = new Lookup<TrainView>.Found(train)
            },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
            { [vehicle.Id] = new Lookup<InstanceView>.Found(vehicle) },
            Economy = UiWorldFixture.Economy,
            World = new(7, new("fixture:railway"), 2000, 1200, nodes.ToImmutableArray(),
                edges.Select(edge => edge.Map).ToImmutableArray(),
                [new(new(987654), new TrainPosition.Edge(new(ActiveEdge), 1, TravelDirection.Forward, 35), TrainActivity.Running)], [], []),
            Nodes = nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(new(node, [], 80, 40, 80))),
            Edges = edges.ToDictionary(edge => edge.Map.Id, edge => (Lookup<EdgeView>)new Lookup<EdgeView>.Found(edge)),
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
            {
                [new(1)] = new Lookup<SettlementView>.Found(Settlement(1, 1, ownedSource ? SettlementRelation.PlayerOwned : SettlementRelation.NonHostile, 1)),
                [new(2)] = new Lookup<SettlementView>.Found(Settlement(2, 2, SettlementRelation.NonHostile, 2))
            },
            Inventories = new Dictionary<InventoryId, Lookup<InventoryView>>
            {
                [new(1)] = new Lookup<InventoryView>.Found(Inventory(1, 1, railQuantity)),
                [new(2)] = new Lookup<InventoryView>.Found(Inventory(2, 2, 0)),
                [train.Inventory] = new Lookup<InventoryView>.Found(trainInventory)
            }
        };
    }
    private static IReadOnlyDictionary<(RailwayWorkRequest Request, SettlementId? MaterialSource), CoreResult<RailwayWorkPreview>>
        Previews(IEnumerable<EdgeView> edges, long stock, bool missingRules)
    {
        var result = new Dictionary<(RailwayWorkRequest, SettlementId?), CoreResult<RailwayWorkPreview>>();
        foreach (var edge in edges)
        {
            var requests = new RailwayWorkRequest[] { new RailwayWorkRequest.AddTrack(edge.Map.Id) }
                .Concat(edge.Map.Breaks.Select(item => (RailwayWorkRequest)new RailwayWorkRequest.RepairBreak(edge.Map.Id, item.Id)));
            foreach (var request in requests)
            foreach (SettlementId? source in new SettlementId?[] { null, new(1) })
            {
                if (missingRules)
                {
                    result[(request, source)] = new CoreResult<RailwayWorkPreview>.Success(new([], 0,
                        [new("content_missing", "RailwayRule", new WorldObjectId.Edge(edge.Map.Id), ["fixture:railway"])]));
                    continue;
                }
                var blockers = ImmutableArray.CreateBuilder<CoreProblem>();
                if (request is RailwayWorkRequest.AddTrack && edge.Map.Tracks +
                    edge.Map.Works.Count(work => work.Kind == RailwayWorkKind.AddTrack) >= edge.Map.TrackLimit)
                    blockers.Add(new("invalid_state", "Edge.Tracks", new WorldObjectId.Edge(edge.Map.Id), []));
                if (request is RailwayWorkRequest.RepairBreak repair && edge.Map.Works.Any(work => work.RepairingBreak == repair.Break))
                    blockers.Add(new("busy", "Break", new WorldObjectId.Edge(edge.Map.Id), []));
                var amount = request is RailwayWorkRequest.RepairBreak ? 11L : 17L;
                if (source is null) blockers.Add(new("invalid_state", "MaterialSource", null, []));
                else if (stock < amount) blockers.Add(Blocker());
                result[(request, source)] = new CoreResult<RailwayWorkPreview>.Success(new([new(Rail, amount)],
                    request is RailwayWorkRequest.RepairBreak ? 48UL : 90000UL, blockers.ToImmutable()));
            }
        }
        return result;
    }

}
