using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Engine.Tests;

internal static class WorldListUiFixture
{
    private static readonly GoodId Rail = new GoodId.Voxel(new("fixture:list-rail"));

    internal static FixedContentView Content()
    {
        var scene = new DesignSceneProjection(null, null, null, true, null, null, [], [], [], [], []);
        var model = new SceneModelProjection(new("fixture:list-model"), scene, new("列表模型", ""));
        var icon = new AssetRevision("fixture-list-symbol-png");
        var bytes = ImmutableArray.CreateRange(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNoOODwHwAFxAKAPuJ4AwAAAABJRU5ErkJggg=="));
        var projection = HostContentScenario.CreateProjection(HostCoreScenario.Input) with
        {
            SettlementTemplates = [new(new("fixture:town"), new("fixture:town"), 1, [], [], [], [], 80)]
        };
        return new(projection, new(1))
        {
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>>
            { [Rail] = new ContentLookup<GoodPresentation>.Found(new(Rail, new(new("钢轨", ""), null, null, false), null, false)) },
            DefinitionDisplays = new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
            {
                [new CoreDefinitionReference.Terrain(new("fixture:long-plain"))] = new ContentLookup<ContentDisplay>.Found(
                    new(new("北方长名称河谷与山间运输枢纽平原", "列表窄窗口检查"), icon, icon, false)),
                [new CoreDefinitionReference.SettlementType(new("fixture:town"))] = new ContentLookup<ContentDisplay>.Found(
                    new(new("聚居地", ""), icon, icon, true))
            },
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
            {
                [new(1)] = new ContentLookup<SettlementLayoutProjection>.Found(new(new(1), new("fixture:town"), model, [], new("青川", ""))),
                [new(2)] = new ContentLookup<SettlementLayoutProjection>.Found(new(new(2), new("fixture:town"), model, [], new("南渡", ""))),
                [new(3)] = new ContentLookup<SettlementLayoutProjection>.Found(new(new(3), new("fixture:town"), model, [], new("赤岭", "")))
            },
            Lairs = new Dictionary<LairTypeId, ContentLookup<LairAppearanceProjection>>
            { [new("fixture:nest")] = new ContentLookup<LairAppearanceProjection>.Found(new(new("fixture:nest"), model,
                new(new("石甲兽巢穴", ""), icon, icon, true))) },
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>
            { [icon] = new ContentLookup<ContentAsset>.Found(new(icon, bytes, new AssetMetadata.Png(1, 1), [])) }
        };
    }

    internal static FixedCoreReadView View(ulong revision = 1, TrainView? precedingTrain = null, long runningCashFlow = 250)
    {
        var basis = UiWorldFixture.Map();
        var first = basis.World.Nodes[0] with { Terrain = new(new("fixture:long-plain"), 0, []) };
        var second = basis.World.Nodes[1] with { Terrain = first.Terrain };
        var owned = first with { Id = new(3), Position = new(200, 100), Occupant = new NodeOccupant.Settlement(new(1), SettlementRelation.PlayerOwned) };
        var friendly = first with { Id = new(4), Position = new(500, 100), Occupant = new NodeOccupant.Settlement(new(2), SettlementRelation.NonHostile) };
        var hostile = first with { Id = new(5), Position = new(800, 100), Occupant = new NodeOccupant.Settlement(new(3), SettlementRelation.Hostile) };
        var lair = first with { Id = new(6), Position = new(500, 500), Occupant = new NodeOccupant.Lair(new(1)) };
        var nodes = new[] { first, second, owned, friendly, hostile, lair };
        var prototype = ((Lookup<TrainView>.Found)basis.FindTrain(new(7))).Value;
        var plan = new RunPlan(true, [new PlanStop.Waypoint(first.Id), new PlanStop.Waypoint(second.Id)]);
        var trains = Enumerable.Range(7, 36).Select(id => prototype with
        {
            Id = new((ulong)id),
            Activity = id == 8 ? TrainActivity.Running : id == 9 ? TrainActivity.NoRoute : id == 10 ? TrainActivity.Retreating : TrainActivity.Stopped,
            Plan = id == 7 ? null : plan,
            NextStopIndex = id == 7 ? null : 1,
            RecentReports = id is 8 or 9 ? [new(new((ulong)id), new(0, 1), RunEndReason.LoopCompleted,
                new(new(0, 0), new(500), new(id == 8 ? 200 : 600), new(50), id == 8 ? runningCashFlow : -150, 30, .6, []))] : []
        }).ToArray();
        if (precedingTrain is not null) trains = [precedingTrain, .. trains];
        var edges = new[]
        {
            new EdgeMapItem(new(1), first.Id, second.Id, 500, first.Terrain, 1, 1, [], []),
            new EdgeMapItem(new(2), first.Id, second.Id, 500, first.Terrain, 1, 1, [new(new(1), 1, 100)], []),
            new EdgeMapItem(new(3), first.Id, second.Id, 500, first.Terrain, 0, 1, [], [])
        };
        var town = ((Lookup<SettlementView>.Found)EconomyFixture.Create().FindSettlement(new(1))).Value;
        return new(new(0, revision), basis.InputIdentity, revision: new(revision))
        {
            RailwayWorkPreviews = new Dictionary<(RailwayWorkRequest, SettlementId?), CoreResult<RailwayWorkPreview>>
            {
                [(new RailwayWorkRequest.AddTrack(new(1)), new SettlementId(1))] =
                    new CoreResult<RailwayWorkPreview>.Success(new([new(Rail, 17)], 90000, [new("invalid_state", "Edge.Tracks", null, [])])),
                [(new RailwayWorkRequest.AddTrack(new(2)), new SettlementId(1))] =
                    new CoreResult<RailwayWorkPreview>.Success(new([new(Rail, 17)], 90000, [new("invalid_state", "Edge.Tracks", null, [])])),
                [(new RailwayWorkRequest.AddTrack(new(3)), new SettlementId(1))] =
                    new CoreResult<RailwayWorkPreview>.Success(new([new(Rail, 17)], 90000, [])),
                [(new RailwayWorkRequest.RepairBreak(new(2), new(1)), new SettlementId(1))] =
                    new CoreResult<RailwayWorkPreview>.Success(new([new(Rail, 11)], 48, []))
            },
            Economy = basis.Economy,
            Inventories = new Dictionary<InventoryId, Lookup<InventoryView>>
                {
                    [prototype.Inventory] = basis.FindInventory(prototype.Inventory),
                    [town.Inventory] = new Lookup<InventoryView>.Found(new(town.Inventory,
                        new InventoryOwner.Settlement(new(1)), new WorldLocation.Node(owned.Id),
                        InventoryAccess.ReadWrite, 100, 18, 0, 82, 0.504, [new(Rail, 18, false)], []))
                },
            World = basis.World with { Nodes = [.. nodes], Edges = [.. edges],
                Combats = [new(new(1), new WorldLocation.Node(first.Id))],
                Trains = [.. trains.Select(train => new TrainMapItem(train.Id, train.Position, train.Activity))] },
            Nodes = nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(
                new(node, [.. edges.Select(edge => edge.Id)], 80, 0, 80))),
            Lairs = new Dictionary<LairId, Lookup<LairView>>
            { [new(1)] = new Lookup<LairView>.Found(new(new(1), lair.Id, new("fixture:nest"), [])) },
            Settlements = nodes.Where(node => node.Occupant is NodeOccupant.Settlement).ToDictionary(
                node => ((NodeOccupant.Settlement)node.Occupant).Id, node => (Lookup<SettlementView>)new Lookup<SettlementView>.Found(
                    town with
                    { Id = ((NodeOccupant.Settlement)node.Occupant).Id, Node = node.Id, Relation = ((NodeOccupant.Settlement)node.Occupant).Relation })),
            Edges = edges.ToDictionary(edge => edge.Id, edge => (Lookup<EdgeView>)new Lookup<EdgeView>.Found(new(edge, [], null))),
            Combats = new Dictionary<CombatId, Lookup<CombatView>>
            { [new(1)] = new Lookup<CombatView>.Found(new(new(1), new WorldLocation.Node(first.Id), 3, [])) },
            Trains = trains.ToDictionary(train => train.Id, train => (Lookup<TrainView>)new Lookup<TrainView>.Found(train)),
            Locations = nodes.ToDictionary(node => (WorldLocation)new WorldLocation.Node(node.Id),
                node => (Lookup<LocationView>)new Lookup<LocationView>.Found(new(new WorldLocation.Node(node.Id), [], [], [], [], node.Occupant)))
        };
    }
}
