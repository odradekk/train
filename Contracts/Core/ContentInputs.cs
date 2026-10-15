using System.Collections.Immutable;

namespace Train.Core.Contracts;

public readonly record struct ProductionProcessDefinitionId(string Value);
public readonly record struct RecoveryProcessDefinitionId(string Value);

public abstract record ProcessRef
{
    private ProcessRef() { }
    public sealed record Production(ProductionProcessDefinitionId Definition) : ProcessRef;
    public sealed record Recovery(RecoveryProcessDefinitionId Definition) : ProcessRef;
}

public readonly record struct TerrainTypeId(string Value);
public readonly record struct SettlementTemplateId(string Value);
public readonly record struct CreatureDefinitionId(string Value);

public sealed record MissingCoreReference(CoreDefinitionReference Reference, string FieldPath);
public abstract record CoreDefinitionReference
{
    private CoreDefinitionReference() { }
    public sealed record Voxel(VoxelDefinitionId Id) : CoreDefinitionReference;
    public sealed record Composite(CompositeDefinitionId Id) : CoreDefinitionReference;
    public sealed record Process(ProcessRef Id) : CoreDefinitionReference;
    public sealed record Rule(RuleDefinitionId Id) : CoreDefinitionReference;
    public sealed record SettlementType(SettlementTypeId Id) : CoreDefinitionReference;
    public sealed record Template(SettlementTemplateId Id) : CoreDefinitionReference;
    public sealed record Terrain(TerrainTypeId Id) : CoreDefinitionReference;
    public sealed record Creature(CreatureDefinitionId Id) : CoreDefinitionReference;
    public sealed record Lair(LairTypeId Id) : CoreDefinitionReference;
}

public sealed record CoreContentProjection(
    CoreInputId Identity,
    ImmutableArray<GoodDefinitionProjection> Goods,
    ImmutableArray<DesignProjection> Designs,
    ImmutableArray<GoodDefinitionProjection> DesignGoods,
    ImmutableArray<ProcessProjection> Processes,
    ImmutableArray<PopulationRuleProjection> PopulationRules,
    ImmutableArray<MarketRuleProjection> MarketRules,
    ImmutableArray<ConstructionRuleProjection> ConstructionRules,
    ImmutableArray<RailwayRuleProjection> RailwayRules,
    ImmutableArray<CombatRuleProjection> CombatRules,
    ImmutableArray<SettlementTypeProjection> SettlementTypes,
    ImmutableArray<SettlementTemplateProjection> SettlementTemplates,
    ImmutableArray<CreatureProjection> Creatures,
    ImmutableArray<LairTypeProjection> LairTypes,
    ImmutableArray<MissingCoreReference> MissingReferences);

public sealed record GoodDefinitionProjection(GoodId Good, SemanticRevision Revision,
    Money BaseValue, double MassTons, bool RawGood, double? FuelEnergyPerUnit);

public enum PlotSize { Small, Medium, Large, Giant, SmallStation, LargeStation }
public enum BuildingKind { Production, Warehouse, Power, Construction, Station, Defense }
public enum FacilityTag
{
    Agriculture, Extraction, Metallurgy, BuildingMaterials, LightIndustry,
    Chemical, FoodProcessing, Assembly, Electronics, Recovery
}

public abstract record DesignKind
{
    private DesignKind() { }
    public sealed record Vehicle(SizeClass Size, int LengthCells) : DesignKind;
    public sealed record Building(PlotSize PlotSize, BuildingKind Type,
        ImmutableArray<FacilityTag> FacilityTags) : DesignKind;
    public sealed record LargeEquipment(GoodId ManufacturedGood) : DesignKind;
}

public sealed record DesignProjection(DesignHash Design, CompositionHash Composition,
    DesignKind Kind, ImmutableArray<PartProjection> Parts,
    long MaxStructureDurability, long StructureHitWeight, double MassTons,
    Money StructuralBaseValue, ImmutableArray<GoodsQuantity> StructuralGoods,
    ImmutableArray<GoodsQuantity> BillOfMaterials, long BillOfMaterialsItemCount,
    ImmutableArray<DesignReturnUnit> ReturnUnits,
    bool CanConstruct, ImmutableArray<MissingCoreReference> MissingReferences);

public sealed record DesignInput(CoreInputId InputIdentity, DesignProjection Design,
    ImmutableArray<GoodDefinitionProjection> AssemblyGoods);

public sealed record DesignReturnUnit(GoodId Good, long Quantity,
    ImmutableArray<PartId> Parts, ImmutableArray<GoodsQuantity> StructuralGoods,
    ImmutableArray<MissingCoreReference> MissingReferences);

public sealed record PartProjection(PartId Id, CoreDefinitionReference Definition,
    GoodId RepairGood, long MaxDurability, long HitWeight, Money BaseValue,
    ImmutableArray<PartCapability> Capabilities, bool Missing);

public abstract record PartCapability
{
    private PartCapability() { }
    public sealed record Propulsion(double TractionKilonewtons, double RatedSpeedMetersPerSecond) : PartCapability;
    public sealed record Control : PartCapability;
    public sealed record Storage(long Capacity) : PartCapability;
    public sealed record Production(double WorkPerSecond) : PartCapability;
    public sealed record Extraction(double WorkPerSecond) : PartCapability;
    public sealed record Construction(double WorkPerSecond, PlotSize MaximumBuildingPlotSize, SizeClass? MaximumVehicleSize) : PartCapability;
    public sealed record Power(double EnergyPerSecond) : PartCapability;
    public sealed record Weapon(long Damage, double IntervalSeconds) : PartCapability;
    public sealed record Platform : PartCapability;
}

public sealed record PartCorrespondence(PartId Before, PartId After);
public sealed record DesignChangeProjection(DesignHash Before, CompositionHash BeforeComposition,
    DesignInput Target,
    ImmutableArray<PartCorrespondence> RetainedParts,
    ImmutableArray<GoodsQuantity> AddedGoods,
    ImmutableArray<DesignReturnUnit> RemovedItems, long ChangedItemCount);

public abstract record ProcessDetails
{
    private ProcessDetails() { }
    public sealed record Production(ProductionProcessDefinitionId Id,
        ImmutableArray<GoodsQuantity> Inputs, ImmutableArray<GoodsQuantity> Outputs) : ProcessDetails;
    public sealed record Extraction(ProductionProcessDefinitionId Id, GoodsQuantity Extracts) : ProcessDetails;
    public sealed record Recovery(RecoveryProcessDefinitionId Id,
        ImmutableArray<GoodsQuantity> Inputs, ImmutableArray<GoodsQuantity> Outputs) : ProcessDetails;
}
public sealed record ProcessProjection(ProcessDetails Details, SemanticRevision Revision,
    long WorkRequired, double PowerPerWork, double LaborPerWork,
    FacilityTag RequiredFacilityTag, ImmutableArray<TerrainTypeId> AllowedTerrainTypes);

public enum PopulationNeed { Food, Clothing, ImprovedDiet, ConsumerGoods, ConsumerElectronics, MedicalSupplies }
public enum PopulationNeedClass { Basic, Improvement }
public sealed record PopulationNeedProjection(PopulationNeed Need, PopulationNeedClass Class,
    double AmountPerPersonSecond, double Weight);
public sealed record ConsumptionGoodProjection(GoodId Good,
    ImmutableArray<NeedConversion> Satisfies, int SelectionOrder);
public sealed record NeedConversion(PopulationNeed Need, double AmountPerGoodUnit);
public sealed record PopulationRuleProjection(RuleDefinitionId Id, SemanticRevision Revision,
    ImmutableArray<PopulationNeedProjection> Needs,
    ImmutableArray<ConsumptionGoodProjection> ConsumptionGoods,
    double LaborRatio, int ConsumptionIntervalTicks, int StatisticsPeriodTicks,
    double BaseGrowthPerSecond, double ImprovementGrowthPerSecond, double MaximumLossPerSecond);

public sealed record GoodsPriceParameters(BasisPoints Sensitivity, BasisPoints MinimumMultiplier,
    BasisPoints MaximumMultiplier, BasisPoints Spread);
public sealed record GoodPriceOverride(GoodId Good, GoodsPriceParameters Parameters);
public sealed record VehiclePurchaseLimit(SizeClass Size, int MaximumForSale);
public sealed record MarketRuleProjection(RuleDefinitionId Id, SemanticRevision Revision,
    double BasicReserveHours, double ProductionReserveHours, double ImprovementReserveHours,
    GoodsPriceParameters DefaultGoodsPrices, ImmutableArray<GoodPriceOverride> GoodPrices,
    BasisPoints VehicleBuyMultiplier, BasisPoints VehicleSellMultiplier,
    ImmutableArray<VehiclePurchaseLimit> VehiclePurchaseLimits,
    int AutomaticEvaluationIntervalTicks, int RecentReportLimit,
    Money CommissionMoneyPerWork, Money RentMoneyPerCapacityHour,
    BasisPoints RentableCapacityRatio, long LeaseDurationTicks);

public sealed record ConstructionRuleProjection(RuleDefinitionId Id, SemanticRevision Revision,
    ulong FoundingDurationTicks,
    long ConstructionWorkPerItem, long RefitWorkPerChangedItem,
    long DeconstructionWorkPerItem, long RepairWorkPerDamagedPart,
    double PowerPerWork, double LaborPerWork);
public sealed record RailwayRuleProjection(RuleDefinitionId Id, SemanticRevision Revision,
    double BrakingDeceleration, double MinimumAcceleration, double MinimumGapMeters,
    VoxelDefinitionId RailGood, double RailUnitsPerMeter, double BuildSecondsPerMeter,
    long RepairRailUnitsPerBreak, double RepairSecondsPerBreak,
    double ApproachLengthMeters, double DepartureLengthMeters, double NodeSpeedLimitMetersPerSecond);
public sealed record CombatRuleProjection(RuleDefinitionId Id, SemanticRevision Revision,
    double RetreatSeconds, double MoraleDamageCoefficient, double WeaponDestroyedMoraleLoss,
    double MoraleRecoveryPerSecond, double SurrenderThreshold, double NewLairIntervalSeconds);

public sealed record SettlementTypeProjection(SettlementTypeId Id, SemanticRevision Revision,
    int Weight, ImmutableArray<TerrainTypeId> PreferredTerrain, long MinimumInitialPopulation,
    long MaximumInitialPopulation);
public sealed record TemplatePlotProjection(int Number, PlotSize Size, DesignHash? PrebuiltDesign);
public sealed record FoundingBuildingProjection(int PlotNumber, DesignHash Design);
public sealed record TemplateProductionPlan(int PlotNumber, CapabilityKind Capability,
    ProcessRef Process, bool Enabled, int Priority);
public sealed record SettlementTemplateProjection(SettlementTemplateId Id,
    SettlementTypeId Type, int Weight, ImmutableArray<TemplatePlotProjection> Plots,
    ImmutableArray<FoundingBuildingProjection> FoundingBuildings,
    ImmutableArray<GoodsQuantity> SpecialInitialInventory,
    ImmutableArray<TemplateProductionPlan> ProductionPlans, double PlatformLengthMeters);

public sealed record CreatureProjection(CreatureDefinitionId Id, SemanticRevision Revision,
    long HitPoints, double SpeedMetersPerSecond, double RetreatLossRatio,
    double RaidDurationSeconds, PartCapability.Weapon? Weapon);
public sealed record WeightedCreature(CreatureDefinitionId Creature, int Weight);
public sealed record LairTypeProjection(LairTypeId Id, SemanticRevision Revision,
    int GenerationWeight, ImmutableArray<WeightedCreature> Members, int MaximumMembers,
    double SpawnIntervalSeconds, double RaidIntervalSeconds, int RoamingRadiusHops,
    int RaidRadiusHops, double RailDamageWorkSeconds, Money ClearanceReward);

public sealed record WorldGenerationInput(ulong WorldSeed, string GeneratorVersion,
    SemanticRevision GenerationContentRevision, ResolvedGenerationConfiguration Configuration,
    NewGameStartInput Start);
public sealed record ResolvedGenerationConfiguration(double WidthMeters, double HeightMeters,
    double MinimumNodeSpacingMeters, double CandidateProbability,
    ImmutableArray<NoiseOctave> ElevationNoise, ImmutableArray<NoiseOctave> MoistureNoise,
    ImmutableArray<NoiseOctave> RiverNoise, double LakeElevationThreshold,
    double RiverBandHalfWidth, ImmutableArray<TerrainGenerationBand> TerrainBands,
    double TerrainSampleSpacingMeters, double SettlementRatio, double LairRatio,
    double HostileSettlementRatio, ImmutableArray<LairGenerationWeight> LairWeights,
    ImmutableArray<ResourceGenerationEntry> Resources, InitialRailwayConfiguration InitialRailway,
    GenerationBudget Budget, NpcInitializationConfiguration NpcInitialization,
    DefaultStartConstraints StartConstraints, RuleSelection Rules);
public sealed record NoiseOctave(double ScaleMeters, double Amplitude);
public sealed record TerrainGenerationBand(TerrainTypeId Terrain, double MinimumElevation,
    double MaximumElevation, double MinimumMoisture, double MaximumMoisture,
    double MaximumRelief, double DetourMultiplier, int MaximumTracks);
public sealed record LairGenerationWeight(LairTypeId Type, int Weight);
public sealed record ResourceGenerationEntry(VoxelDefinitionId Good,
    ImmutableArray<TerrainTypeId> AllowedTerrainTypes, double Probability,
    long MinimumReserve, long MaximumReserve, bool Renewable, double RecoveryUnitsPerSecond,
    double RegenerationIntervalSeconds, double GlobalRegenerationRemainingReserveRatio);
public sealed record InitialRailwayConfiguration(int MaximumTracks, int MinimumInitialTracks,
    int MaximumInitialTracks, int AdditionalCycleEdges);
public sealed record GenerationBudget(int MaximumStageRetries, int MaximumWorldRetries,
    long MaximumCandidates, long MaximumSearchWork);
public sealed record NpcInitializationConfiguration(bool FillInputsAndLivingTargets,
    double OutputReserveHours, int MinimumForSaleVehicles, int MaximumForSaleVehicles,
    ImmutableArray<DesignHash> ForSaleVehicleCandidates);
public sealed record DefaultStartConstraints(int MinimumReachableNonHostileSettlements,
    int MinimumReachableEmptyNodes, bool RequireStation, bool ExcludeAdjacentLairs,
    int FullyLoadedPurchaseRoundsPerTrain, DesignHash RequiredAffordableCommissionDesign);
public sealed record RuleSelection(RuleDefinitionId Population, RuleDefinitionId Market,
    RuleDefinitionId Construction, RuleDefinitionId Railway, RuleDefinitionId Combat);
public sealed record StartingTrainProjection(ImmutableArray<DesignHash> Vehicles,
    ImmutableArray<GoodsQuantity> Inventory);
public sealed record NewGameStartInput(Money InitialMoney,
    ImmutableArray<StartingTrainProjection> Trains);
