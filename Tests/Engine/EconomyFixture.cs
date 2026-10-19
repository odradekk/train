using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Engine.Tests;

internal static class EconomyFixture
{
    internal enum VehicleState { ForSale, Owned, Formed }
    internal const long LargeQuantity = 9007199254740993;
    internal const ulong LeaseDuration = Train.Ui.UiInput.StepsPerDay;
    internal static GoodId Food { get; } = new GoodId.Voxel(new("core:staple-food"));
    internal static GoodId Equipment { get; } = new GoodId.Composite(new("fixture:equipment"));

    internal static FixedCoreReadView Create(ulong revision = 1,
        VehicleState vehicle = VehicleState.ForSale, int forSale = 1, int purchaseAvailable = 1,
        bool blocked = false, bool shortage = false, long foodQuantity = 4, long tradeTotal = 731, long? executableQuantity = null,
        bool missingMarketRule = false,
        IReadOnlyDictionary<LeaseRequest, CoreResult<LeaseQuote>>? leaseQuotes = null,
        bool trainAtNode = true)
    {
        var location = new WorldLocation.Node(new(1));
        var rule = new RuleStatusView(new("fixture:economy"), null, []);
        var terrain = new TerrainView(new("fixture:plain"), 0, []);
        var occupant = new NodeOccupant.Settlement(new(1), SettlementRelation.NonHostile);
        var node = new NodeMapItem(new(1), new(250, 300), terrain, occupant, [], 0, 1, null);
        TrainPosition trainPosition = trainAtNode
            ? new TrainPosition.Node(new(1), null, null, null, null, NodeTravelPhase.Waiting, 80, null)
            : new TrainPosition.Edge(new(1), 0, TravelDirection.Forward, 30);
        var train = new TrainView(new(7), trainPosition, TrainActivity.Stopped,
            [new(10)], new(12), 0, 20, 10, 100, 0, 30,
            null, null, 0, new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, .2),
            [], null, [], TrainOrderFacing.FirstEndLeading, null);
        var purchasedTrain = train with { Id = new(8), Vehicles = [new(9)], Inventory = new(13) };
        var existingVehicleId = new InstanceId.Vehicle(new(10));
        var existingVehicle = new InstanceView(existingVehicleId, new("fixture-vehicle"), new("fixture-composition"), location,
            SettlementRelation.PlayerOwned, new InstanceDetails.Vehicle(SizeClass.Medium, train.Id),
            InstanceLock.None, new(100, 100), [], []);
        InstanceId instanceId = new InstanceId.Vehicle(new(9));
        var instance = new InstanceView(instanceId, new("fixture-vehicle"), new("fixture-composition"), location,
            vehicle == VehicleState.ForSale ? SettlementRelation.NonHostile : SettlementRelation.PlayerOwned,
            new InstanceDetails.Vehicle(SizeClass.Medium, vehicle == VehicleState.Formed ? purchasedTrain.Id : null),
            InstanceLock.None, new(100, 100), [], []);
        var settlement = new SettlementView(new(1), new(1), new("fixture:town"), null,
            SettlementRelation.NonHostile, new(10), [], vehicle == VehicleState.Formed ? [] : [new(9)], 4, 1, 0,
            vehicle == VehicleState.ForSale && forSale > 0 ? [new(9)] : [],
            [new(SizeClass.Medium, forSale, 4, purchaseAvailable)], [], new(0, 0, 0, 0),
            new(rule, 10, 10, 0, 10, new PopulationExecution.NotStarted([]), null, []),
            new(TargetPriority.ArmedFirst, TargetScope.All), 1, false, [], [], [new(1)],
            new(missingMarketRule ? rule with { Problems = [
                    new("content_missing", "market.rule", new WorldObjectId.Settlement(new(1)), ["fixture:missing-market"]),
                    new("invalid_state", "market.rule", new WorldObjectId.Settlement(new(1)), ["fixture:invalid-market"])] } : rule,
                new(100, 5, 2, 20, 0, 75, 50),
                [new(Food, 42, 50, 2, 40, 20, []), new(Equipment, 3, 5, 0, 3, 2, [])]));
        InventoryView Inventory(ulong id, InventoryOwner owner) => new(new(id), owner, location, InventoryAccess.ReadWrite,
            100, 5, 2, 93, 1.25, [new(Food, foodQuantity, false), new(Equipment, 1, false)], []);
        ImmutableArray<CoreProblem> blockers = blocked
            ? [new("insufficient_money", "wallet", new WorldObjectId.Settlement(new(1)), ["fixture:quote"]),
               new("insufficient_capacity", "inventory", new WorldObjectId.Inventory(new(10)), ["fixture:capacity"])] : [];
        ImmutableArray<CoreProblem> warnings = shortage
            ? [new("insufficient_goods", "quantity", new WorldObjectId.Inventory(new(12)), ["fixture:shortage"]), new("content_missing", "goods", new WorldObjectId.Inventory(new(12)), ["fixture:missing"])] : [];
        var trades = new Dictionary<TradeQuoteRequest, CoreResult<TradeQuote>>();
        foreach (var local in new InventoryId[] { new(10), new(11) })
        foreach (var good in new[] { Food, Equipment })
        foreach (var action in new[] { TradeAction.Buy, TradeAction.Sell, TradeAction.Load, TradeAction.Unload })
        foreach (var quantity in new[] { 1L, LargeQuantity })
        {
            var request = new TradeQuoteRequest(new(7), new(1), local, action, good, quantity);
            trades.Add(request, new CoreResult<TradeQuote>.Success(new(request, new(revision), executableQuantity ?? quantity,
                action is TradeAction.Load or TradeAction.Unload ? null : new Money(tradeTotal), blockers, warnings)));
        }
        var vehicles = new Dictionary<VehicleTradeQuoteRequest, CoreResult<VehicleTradeQuote>>();
        foreach (var action in new[] { VehicleTradeAction.Buy, VehicleTradeAction.Sell })
        {
            var request = new VehicleTradeQuoteRequest(new(1), new(9), action);
            vehicles.Add(request, new CoreResult<VehicleTradeQuote>.Success(new(request, new(revision), new(917), blockers)));
        }
        var leases = new Dictionary<LeaseRequest, CoreResult<LeaseQuote>>();
        LeaseRequest[] leaseRequests = [new LeaseRequest.Rent(new(1), 3, LeaseDuration),
            new LeaseRequest.Renew(new(1), LeaseDuration), new LeaseRequest.Renew(new(1), LeaseDuration * 2),
            new LeaseRequest.Renew(new(1), LeaseDuration * 3)];
        foreach (var request in leaseRequests)
            leases.Add(request, new CoreResult<LeaseQuote>.Success(new(request, new(revision), new(419), 3, blockers)));
        return new(new(0, revision), HostCoreScenario.Input, revision: new(revision))
        {
            World = new(1, new("fixture:railway"), 1000, 600, [node], [], vehicle == VehicleState.Formed
                ? [new(train.Id, train.Position, train.Activity), new(purchasedTrain.Id, purchasedTrain.Position, purchasedTrain.Activity)]
                : [new(train.Id, train.Position, train.Activity)], [], []),
            Economy = new(new(1234567), rule, rule, rule, new(0), []),
            Nodes = new Dictionary<NodeId, Lookup<NodeView>>
            {
                [new(1)] = new Lookup<NodeView>.Found(new(node, [], 80, 0, 80)), [new(2)] = new Lookup<NodeView>.Missing()
            },
            Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
            {
                [location] = new Lookup<LocationView>.Found(new(location, vehicle == VehicleState.Formed ? [train.Id, purchasedTrain.Id] : [train.Id],
                    [instanceId, existingVehicleId], [], [], occupant)),
                [new WorldLocation.Node(new(2))] = new Lookup<LocationView>.Missing()
            },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
            {
                [instanceId] = new Lookup<InstanceView>.Found(instance),
                [existingVehicleId] = new Lookup<InstanceView>.Found(existingVehicle),
                [new InstanceId.Vehicle(new(11))] = new Lookup<InstanceView>.Missing()
            },
            Trains = new Dictionary<TrainId, Lookup<TrainView>>
            {
                [train.Id] = new Lookup<TrainView>.Found(train),
                [purchasedTrain.Id] = vehicle == VehicleState.Formed
                    ? new Lookup<TrainView>.Found(purchasedTrain) : new Lookup<TrainView>.Missing(),
                [new(9)] = new Lookup<TrainView>.Missing()
            },
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
            {
                [new(1)] = new Lookup<SettlementView>.Found(settlement), [new(2)] = new Lookup<SettlementView>.Missing()
            },
            Inventories = new Dictionary<InventoryId, Lookup<InventoryView>>
            {
                [new(10)] = new Lookup<InventoryView>.Found(Inventory(10, new InventoryOwner.Settlement(new(1)))),
                [new(11)] = new Lookup<InventoryView>.Found(Inventory(11, new InventoryOwner.Lease(new(1)))),
                [new(12)] = new Lookup<InventoryView>.Found(Inventory(12, new InventoryOwner.Train(train.Id))),
                [new(13)] = vehicle == VehicleState.Formed
                    ? new Lookup<InventoryView>.Found(Inventory(13, new InventoryOwner.Train(purchasedTrain.Id)))
                    : new Lookup<InventoryView>.Missing()
            },
            Leases = new Dictionary<LeaseId, Lookup<LeaseView>>
            {
                [new(1)] = new Lookup<LeaseView>.Found(new(new(1), new(1), new(11), 20, LeaseDuration, new(419), false)),
                [new(2)] = new Lookup<LeaseView>.Missing()
            },
            TradeQuotes = trades, VehicleQuotes = vehicles, LeaseQuotes = leaseQuotes ?? leases
        };
    }

    internal static FixedContentView Content(ulong generation = 1, bool missingMarketRule = false, int maxLeaseMultiple = 3,
        ImmutableArray<ProcessProjection> processes = default,
        IReadOnlyDictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>? definitionDisplays = null,
        BlueprintRecord? vehicleBlueprint = null, BlueprintRecord? otherBlueprint = null,
        ImmutableArray<SavedContentDraft> drafts = default)
    {
        var icon = new AssetRevision("fixture-food-png");
        var bytes = ImmutableArray.CreateRange(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNoOODwHwAFxAKAPuJ4AwAAAABJRU5ErkJggg=="));
        var market = new MarketRuleProjection(new("fixture:economy"), new("fixture-market-v1"), 1, 1, 1,
            new(new(10000), new(10000), new(10000), new(0)), [], new(10000), new(10000), [],
            60, 10, new(1), new(1), new(10000), (long)LeaseDuration, maxLeaseMultiple);
        var projection = HostContentScenario.CreateProjection(HostCoreScenario.Input) with
        {
            MarketRules = missingMarketRule ? [] : [market with { Id = new("fixture:another-market"), LeaseDurationTicks = 120 }, market],
            Processes = processes.IsDefault ? [] : processes
        };
        return new(projection, new(generation))
        {
            LocalDrafts = drafts.IsDefault ? [] : drafts,
            Blueprints = [.. new[] { vehicleBlueprint, otherBlueprint }.OfType<BlueprintRecord>().Select(record =>
                new BlueprintSummary(record.Address, record.Definition.Display,
                    ((DesignEvaluation.Complete)record.Evaluation).Design, true, []))],
            BlueprintRecords = new[] { vehicleBlueprint, otherBlueprint }.OfType<BlueprintRecord>().ToDictionary(
                record => record.Address, record => (ContentLookup<BlueprintRecord>)new ContentLookup<BlueprintRecord>.Found(record)),
            DefinitionDisplays = definitionDisplays ?? ImmutableDictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>.Empty,
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
            {
                [new(1)] = new ContentLookup<SettlementLayoutProjection>.NotFound(),
                [new(2)] = new ContentLookup<SettlementLayoutProjection>.NotFound()
            },
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>>
            {
                [Food] = new ContentLookup<GoodPresentation>.Found(new(Food, new(new("主食", "有限经济情景"), icon, null, false), null, false)),
                [new GoodId.Voxel(new("fixture:ore"))] = new ContentLookup<GoodPresentation>.Missing([]),
                [Equipment] = new ContentLookup<GoodPresentation>.Missing([])
            },
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>
            {
                [icon] = new ContentLookup<ContentAsset>.Found(new(icon, bytes, new AssetMetadata.Png(1, 1), []))
            },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            {
                [new("fixture-vehicle")] = new ContentLookup<DesignEvaluation>.Missing([]),
                [new("fixture-service-building")] = new ContentLookup<DesignEvaluation>.Found(ServiceBuilding()),
                [new("fixture-plain-building")] = new ContentLookup<DesignEvaluation>.Found(PlainBuilding()),
                [new("fixture-food-building")] = new ContentLookup<DesignEvaluation>.Found(FoodBuilding())
            }
        };
    }

    private static DesignEvaluation.Complete ServiceBuilding()
    {
        var definition = new DesignDraft(new("施工建筑", ""),
            new DesignSpecification.Building(PlotSize.Small, BuildingKind.Construction, []), new([], [], []));
        var design = new DesignInput(HostCoreScenario.Input,
            new(new("fixture-service-building"), new("fixture-building-composition"),
                new DesignKind.Building(PlotSize.Small, BuildingKind.Construction, []),
                [new(new(1), new CoreDefinitionReference.Voxel(new("fixture:builder")), Food, 100, 1, new(1),
                    [new PartCapability.Construction(1, PlotSize.Small, SizeClass.Medium)], false)],
                100, 1, 1, new(1), [], [], 0, [], true, []), []);
        return new(design.Design.Design, design.Design.Composition, definition, design,
            new(null, null, null, true, null, null, [], [], [], [], []), true, []);
    }

    private static DesignEvaluation.Complete PlainBuilding()
    {
        var definition = new DesignDraft(new("普通建筑", ""),
            new DesignSpecification.Building(PlotSize.Small, BuildingKind.Production, []), new([], [], []));
        var design = new DesignInput(HostCoreScenario.Input,
            new(new("fixture-plain-building"), new("fixture-plain-composition"),
                new DesignKind.Building(PlotSize.Small, BuildingKind.Production, []),
                [new(new(1), new CoreDefinitionReference.Voxel(new("fixture:builder")), Food, 100, 1, new(1),
                    [new PartCapability.Production(1)], false)],
                100, 1, 1, new(1), [], [], 0, [], true, []), []);
        return new(design.Design.Design, design.Design.Composition, definition, design,
            new(null, null, null, true, null, null, [], [], [], [], []), true, []);
    }

    private static DesignEvaluation.Complete FoodBuilding()
    {
        var definition = new DesignDraft(new("食品建筑", ""),
            new DesignSpecification.Building(PlotSize.Small, BuildingKind.Production, [FacilityTag.FoodProcessing]), new([], [], []));
        var design = new DesignInput(HostCoreScenario.Input,
            new(new("fixture-food-building"), new("fixture-food-composition"),
                new DesignKind.Building(PlotSize.Small, BuildingKind.Production, [FacilityTag.FoodProcessing]),
                [new(new(1), new CoreDefinitionReference.Voxel(new("fixture:builder")), Food, 100, 1, new(1),
                    [new PartCapability.Production(1)], false)],
                100, 1, 1, new(1), [], [], 0, [], true, []), []);
        return new(design.Design.Design, design.Design.Composition, definition, design,
            new(null, null, null, true, null, null, [], [], [], [], []), true, []);
    }
}
