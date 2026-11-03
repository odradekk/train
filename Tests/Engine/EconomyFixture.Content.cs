using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Engine.Tests;

internal static partial class EconomyFixture
{
    internal static FixedContentView Content(ulong generation = 1, bool missingMarketRule = false, int maxLeaseMultiple = 3,
        ImmutableArray<ProcessProjection> processes = default,
        IReadOnlyDictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>? definitionDisplays = null,
        BlueprintRecord? vehicleBlueprint = null, BlueprintRecord? otherBlueprint = null,
        ImmutableArray<SavedContentDraft> drafts = default, bool mapPresentation = false, string foodName = "主食", bool armedBuilding = false,
        string? equipmentName = null, long? leaseDurationTicks = null, bool vehiclePresentation = false,
        IReadOnlyDictionary<DesignHash, ContentLookup<DesignEvaluation>>? additionalDesigns = null, string? lairName = null)
    {
        mapPresentation |= vehiclePresentation;
        var icon = new AssetRevision("fixture-food-png");
        var bytes = ImmutableArray.CreateRange(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNoOODwHwAFxAKAPuJ4AwAAAABJRU5ErkJggg=="));
        var market = new MarketRuleProjection(new("fixture:economy"), new("fixture-market-v1"), 1, 1, 1,
            new(new(10000), new(10000), new(10000), new(0)), [], new(10000), new(10000), [],
            60, 10, new(1), new(1), new(10000), leaseDurationTicks ?? (long)LeaseDuration, maxLeaseMultiple);
        var projection = HostContentScenario.CreateProjection(HostCoreScenario.Input) with
        {
            MarketRules = missingMarketRule ? [] : [market with { Id = new("fixture:another-market"), LeaseDurationTicks = 120 }, market],
            Processes = processes.IsDefault ? [] : processes,
            SettlementTemplates = mapPresentation ? [new(new("fixture:town"), new("fixture:town"), 1, [], [], [], [], 80)] : []
        };
        return new(projection, new(generation))
        {
            Voxels = [],
            Lairs = new Dictionary<LairTypeId, ContentLookup<LairAppearanceProjection>>
            { [new("fixture:lair")] = lairName is null ? new ContentLookup<LairAppearanceProjection>.Missing([])
                : new ContentLookup<LairAppearanceProjection>.Found(new(new("fixture:lair"),
                    new(new("fixture:model"), new(null, null, null, true, null, null, [], [], [], [], []), new(lairName, "")),
                    new(new(lairName, ""), null, null, false))) },
            LocalDrafts = drafts.IsDefault ? [] : drafts,
            Blueprints = [.. new[] { vehicleBlueprint, otherBlueprint }.OfType<BlueprintRecord>().Select(record =>
                new BlueprintSummary(record.Address, record.Definition.Display,
                    ((DesignEvaluation.Complete)record.Evaluation).Design, true, []))],
            BlueprintRecords = new[] { vehicleBlueprint, otherBlueprint }.OfType<BlueprintRecord>().ToDictionary(
                record => record.Address, record => (ContentLookup<BlueprintRecord>)new ContentLookup<BlueprintRecord>.Found(record)),
            DefinitionDisplays = definitionDisplays ?? (mapPresentation
                ? new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
                {
                    [new CoreDefinitionReference.Terrain(new("fixture:plain"))] = new ContentLookup<ContentDisplay>.NotFound(),
                    [new CoreDefinitionReference.Terrain(new("core:plains"))] = new ContentLookup<ContentDisplay>.NotFound(),
                    [new CoreDefinitionReference.SettlementType(new("fixture:town"))] =
                        new ContentLookup<ContentDisplay>.Found(new(new("聚居地", "有限外壳情景"), null, icon, false))
                }
                : new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
                {
                    [new CoreDefinitionReference.Terrain(new("fixture:plain"))] = new ContentLookup<ContentDisplay>.NotFound(),
                    [new CoreDefinitionReference.Terrain(new("core:plains"))] = new ContentLookup<ContentDisplay>.NotFound()
                }),
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
            {
                [new(1)] = mapPresentation
                    ? new ContentLookup<SettlementLayoutProjection>.Found(new(new(1), new("fixture:town"),
                        new(new("fixture-ground"), new(new("fixture-ground"), null, null, true, null, null, [], [], [], [], []),
                            new("地面", "")), [], new("青川", "有限外壳情景")))
                    : new ContentLookup<SettlementLayoutProjection>.NotFound(),
                [new(2)] = vehiclePresentation
                    ? new ContentLookup<SettlementLayoutProjection>.Found(new(new(2), new("fixture:town"),
                        new(new("fixture-ground"), new(new("fixture-ground"), null, null, true, null, null, [], [], [], [], []),
                            new("地面", "")), [], new("石港", "有限车辆情景")))
                    : new ContentLookup<SettlementLayoutProjection>.NotFound()
            },
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>>
            {
                [Food] = new ContentLookup<GoodPresentation>.Found(new(Food, new(new(foodName, "有限经济情景"), icon, null, false), null, false)),
                [new GoodId.Voxel(new("fixture:ore"))] = new ContentLookup<GoodPresentation>.Missing([]),
                [Equipment] = equipmentName is null ? new ContentLookup<GoodPresentation>.Missing([])
                    : new ContentLookup<GoodPresentation>.Found(new(Equipment, new(new(equipmentName, ""), icon, null, false), null, false))
            },
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>>
            {
                [icon] = new ContentLookup<ContentAsset>.Found(new(icon, bytes, new AssetMetadata.Png(1, 1), []))
            },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            {
                [new("fixture-vehicle")] = vehiclePresentation
                    ? new ContentLookup<DesignEvaluation>.Found(new DesignEvaluation.Complete(new("fixture-vehicle"), new("fixture-composition"),
                        new(new("货车", ""), new DesignSpecification.Vehicle(SizeClass.Medium), new([], [], [])), null,
                        new(null, null, null, true, null, null, [], [], [], [], []), true, []))
                    : new ContentLookup<DesignEvaluation>.Missing([]),
                [new("fixture-service-building")] = new ContentLookup<DesignEvaluation>.Found(ServiceBuilding()),
                [new("fixture-plain-building")] = new ContentLookup<DesignEvaluation>.Found(PlainBuilding(armedBuilding)),
                [new("fixture-food-building")] = new ContentLookup<DesignEvaluation>.Found(FoodBuilding())
            }.Concat(additionalDesigns ?? new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>()).ToDictionary(item => item.Key, item => item.Value)
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

    private static DesignEvaluation.Complete PlainBuilding(bool armed)
    {
        var definition = new DesignDraft(new("普通建筑", ""),
            new DesignSpecification.Building(PlotSize.Small, BuildingKind.Production, []), new([], [], []));
        var design = new DesignInput(HostCoreScenario.Input,
            new(new("fixture-plain-building"), new("fixture-plain-composition"),
                new DesignKind.Building(PlotSize.Small, BuildingKind.Production, []),
                [new(new(1), new CoreDefinitionReference.Voxel(new("fixture:builder")), Food, 100, 1, new(1),
                    [new PartCapability.Production(1), .. (armed ? new PartCapability[] { new PartCapability.Weapon(12, 2) } : [])], false)],
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
