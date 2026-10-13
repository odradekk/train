using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.Tests.World;

internal static partial class WorldFixture
{
    public static CoreSessionOptions Options => new(new("fixture", "1", "Windows", "x64", "10"), false, 1);

    public static T Success<T>(CoreResult<T> result) where T : notnull => result is CoreResult<T>.Success success
        ? success.Value : throw new InvalidOperationException($"核心调用失败：{result}");

    public static ICoreSessionCandidate Prepare(WorldGenerationInput? input = null, CoreContentProjection? content = null) =>
        Success(CoreFactory.Create().PrepareNew(input ?? Input, content ?? Content, Options));

    public static DesignHash Vehicle { get; } = new(new string('A', 64));
    public static DesignHash Station { get; } = new(new string('B', 64));
    public static TerrainTypeId Plain { get; } = new("fixture:plain");
    public static VoxelDefinitionId Material { get; } = new("fixture:material");
    public static SemanticRevision Revision { get; } = new(new string('C', 64));
    public static RuleSelection Rules { get; } = new(new("fixture:population"), new("fixture:market"), new("fixture:construction"), new("fixture:railway"), new("fixture:combat"));
    public static TerrainTypeId Hills { get; } = new("fixture:hills");
    public static VoxelDefinitionId Ore { get; } = new("fixture:ore");
    public static DesignHash SecondVehicle { get; } = new(new string('F', 64));
    public static DesignHash FactoryBuilding { get; } = new(new string('9', 64));

    public static WorldGenerationInput MultiInput => Input with
    {
        Configuration = Input.Configuration with
        {
            WidthMeters = 60, HeightMeters = 60,
            ElevationNoise = [new(16, .25), new(32, .75)],
            MoistureNoise = [new(16, .4), new(48, .6)],
            RiverNoise = [new(16, .3), new(32, .7)],
            TerrainBands = [new(Plain, 0, 1, 0, 1, .15, 1, 2), new(Hills, 0, 1, 0, 1, 1, 1.5, 2)],
            TerrainSampleSpacingMeters = 3, LakeElevationThreshold = .05, RiverBandHalfWidth = .025,
            SettlementRatio = .4, LairRatio = .1, HostileSettlementRatio = .2,
            LairWeights = [new(new("fixture:lair-a"), 1), new(new("fixture:lair-b"), 2)],
            Resources = [new(Material, [Plain, Hills], 1, 10, 30, false, 0, 10, 0),
                new(Ore, [Plain, Hills], 1, 40, 80, true, 2, 10, .5)],
            InitialRailway = new(2, 1, 2, 1), Budget = new(5, 3, 200_000, 10_000_000),
            NpcInitialization = new(false, 0, 0, 0, [Vehicle, SecondVehicle]),
            StartConstraints = new(1, 1, true, false, 1, Vehicle)
        },
        Start = new(new(1000), [new([Vehicle, SecondVehicle], InitialGoods(2, 3)),
            new([SecondVehicle, Vehicle], InitialGoods(4, 5))])
    };

    public static CoreContentProjection MultiContent
    {
        get
        {
            var content = Content;
            var material = new GoodId.Voxel(Material);
            var ore = new GoodId.Voxel(Ore);
            var goods = content.Goods.Add(content.Goods[0] with { Good = ore });
            var designs = content.Designs.Add(content.Designs[0] with { Design = SecondVehicle })
                .Add(Design(FactoryBuilding, new DesignKind.Building(PlotSize.Small, BuildingKind.Production,
                    [FacilityTag.Agriculture, FacilityTag.Extraction]),
                    [new PartCapability.Production(1), new PartCapability.Extraction(2)]))
                .Select(design => design with
                {
                    Parts = design.Parts.Add(new(new(1), new CoreDefinitionReference.Voxel(Ore), ore,
                        50, 1, new(2), [new PartCapability.Storage(20), new PartCapability.Power(2)], false)),
                    MaxStructureDurability = 150, StructureHitWeight = 2,
                    StructuralGoods = InitialGoods(1, 2), BillOfMaterials = InitialGoods(2, 3), BillOfMaterialsItemCount = 5,
                    ReturnUnits = [new(material, 1, [new(0), new(1)], InitialGoods(1, 2), []),
                        new(ore, 2, [new(1), new(0)], InitialGoods(2, 1), [])]
                }).ToImmutableArray();
            ProcessRef production = new ProcessRef.Production(new("fixture:production"));
            ProcessRef extraction = new ProcessRef.Production(new("fixture:extraction"));
            var typeA = new SettlementTypeProjection(new("fixture:town-a"), Revision, 1, [Plain, Hills], 10, 20);
            var typeB = typeA with { Id = new("fixture:town-b"), Weight = 2, MinimumInitialPopulation = 30, MaximumInitialPopulation = 40 };
            var templateA = new SettlementTemplateProjection(new("fixture:template-a1"), typeA.Id, 1,
                [new(1, PlotSize.Station, Station), new(2, PlotSize.Small, FactoryBuilding),
                    new(3, PlotSize.Small, null), new(4, PlotSize.Small, null)],
                [new(3, FactoryBuilding), new(4, FactoryBuilding)], InitialGoods(3, 4),
                [new(2, CapabilityKind.Production, production, true, 0), new(2, CapabilityKind.Extraction, extraction, false, 1)]);
            var creatureA = new CreatureProjection(new("fixture:creature-a"), Revision, 10, 1, .1, 2, new(1, 1));
            var creatureB = creatureA with { Id = new("fixture:creature-b"), HitPoints = 20 };
            var lairA = new LairTypeProjection(new("fixture:lair-a"), Revision, 1,
                [new(creatureA.Id, 1), new(creatureB.Id, 2)], 3, 10, 20, 1, 2, 5, new(10));
            var population = content.PopulationRules[0] with
            {
                Needs = [new(PopulationNeed.Food, PopulationNeedClass.Basic, 1, 1),
                    new(PopulationNeed.Clothing, PopulationNeedClass.Basic, 1, 2)],
                ConsumptionGoods = [new(material, [new(PopulationNeed.Food, 1), new(PopulationNeed.Clothing, 2)], 0),
                    new(ore, [new(PopulationNeed.Clothing, 1), new(PopulationNeed.Food, 2)], 1)]
            };
            var market = content.MarketRules[0] with
            {
                GoodPrices = [new(material, content.MarketRules[0].DefaultGoodsPrices), new(ore, content.MarketRules[0].DefaultGoodsPrices)],
                VehiclePurchaseLimits = [new(SizeClass.Small, 0), new(SizeClass.Medium, 0)]
            };
            return content with
            {
                Goods = goods, DesignGoods = goods, Designs = designs,
                Processes = [new(new ProcessDetails.Production(new("fixture:production"), InitialGoods(1, 2), InitialGoods(3, 4)),
                        Revision, 1, 0, 0, FacilityTag.Agriculture, [Plain, Hills]),
                    new(new ProcessDetails.Extraction(new("fixture:extraction"), new(ore, 2)),
                        Revision, 2, 0, 0, FacilityTag.Extraction, [Hills, Plain])],
                PopulationRules = [population, population with { Id = new("fixture:population-unused") }],
                MarketRules = [market, market with { Id = new("fixture:market-unused") }],
                ConstructionRules = [content.ConstructionRules[0], content.ConstructionRules[0] with { Id = new("fixture:construction-unused") }],
                RailwayRules = [content.RailwayRules[0], content.RailwayRules[0] with { Id = new("fixture:railway-unused") }],
                CombatRules = [content.CombatRules[0], content.CombatRules[0] with { Id = new("fixture:combat-unused") }],
                SettlementTypes = [typeA, typeB],
                SettlementTemplates = [templateA, templateA with { Id = new("fixture:template-a2"), Weight = 2 },
                    templateA with { Id = new("fixture:template-b1"), Type = typeB.Id }],
                Creatures = [creatureA, creatureB], LairTypes = [lairA, lairA with { Id = new("fixture:lair-b"), GenerationWeight = 2 }]
            };
        }
    }

    private static ImmutableArray<GoodsQuantity> InitialGoods(long material, long ore) =>
        [new(new GoodId.Voxel(Material), material), new(new GoodId.Voxel(Ore), ore)];

    public static WorldGenerationInput Input => new(42, "stage1-world-v1", Revision,
        new(10, 10, 10, 1, [], [], [], -1, 0,
            [new(Plain, 0, 1, 0, 1, 1, 1, 2)], 1, 1, 0, 0, [], [],
            new(2, 1, 1, 0), new(1, 1, 100_000, 1_000_000),
            new(false, 0, 0, 0, []), new(0, 0, true, true, 1, Vehicle), Rules),
        new(new(1000), [new([Vehicle], [])]));

    public static CoreContentProjection Content => new(new(new string('D', 64)),
        [new(new GoodId.Voxel(Material), Revision, new(1), 1, true, null)],
        [Design(Vehicle, new DesignKind.Vehicle(SizeClass.Small, 1),
            [new PartCapability.Control(), new PartCapability.Propulsion(100, 10), new PartCapability.Storage(100)]),
         Design(Station, new DesignKind.Building(PlotSize.Station, BuildingKind.Station, []),
            [new PartCapability.Platform()])],
        [], [],
        [new(Rules.Population, Revision, [], [], 0.5, 1, 10, 0, 0, 0)],
        [new(Rules.Market, Revision, 0, 0, 0, new(new(0), new(10000), new(10000), new(0)),
            [], new(10000), new(10000), [], 1, 10, new(0), new(0), new(0), 1)],
        [new(Rules.Construction, Revision, 1, 1, 1, 1, 1, 0, 0)],
        [new(Rules.Railway, Revision, 1, 1, 1, Material, 1, 1, 1, 1)],
        [new(Rules.Combat, Revision, 1, 0, 0, 0, 0, 1)],
        [new(new("fixture:town"), Revision, 1, [Plain], 10, 10)],
        [new(new("fixture:town-template"), new("fixture:town"), 1, [new(1, PlotSize.Station, Station)], [], [], [])],
        [], [], []);

    private static DesignProjection Design(DesignHash hash, DesignKind kind,
        System.Collections.Immutable.ImmutableArray<PartCapability> capabilities) =>
        new(hash, new(new string('E', 64)), kind,
            [new(new(0), new CoreDefinitionReference.Voxel(Material), new GoodId.Voxel(Material),
                100, 1, new(1), capabilities, false)],
            100, 1, 1, new(1), [], [], 0, [], true, []);
}
