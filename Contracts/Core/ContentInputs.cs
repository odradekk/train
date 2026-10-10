// 核心契约接收内容层已经解析和计算的业务投影。
// 不包含布局、显示资源或算法；当前规则与冻结执行资料由核心分别保留。
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

/// <summary>保留缺失引用的原身份；字段路径用于定位，不是显示文案。</summary>
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

/// <summary>固定外部定义与恢复资料；Identity 不包含 Designs/DesignGoods 或派生 MissingReferences；设计目录不是玩家蓝图库。</summary>
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

/// <summary>
/// 质量为有限正吨数，价值为正；燃料电量为空表示非燃料，否则为有限正数。
/// 定义型复合体 Revision 覆盖全部核心语义及依赖，包括能力、耐久和返还规格，而不只此记录中的属性。
/// </summary>
public sealed record GoodDefinitionProjection(GoodId Good, SemanticRevision Revision,
    Money BaseValue, double MassTons, bool RawGood, double? FuelEnergyPerUnit);

public enum PlotSize { Small, Medium, Large, Station }
public enum BuildingKind { Production, Warehouse, Power, Construction, Station, Defense }
public enum FacilityTag
{
    Agriculture, Extraction, Metallurgy, BuildingMaterials, LightIndustry,
    Chemical, FoodProcessing, Assembly, Electronics, Recovery
}

/// <summary>类别专属资料；公共质量、部件与 BOM 不随类别重复声明。</summary>
public abstract record DesignKind
{
    private DesignKind() { }
    public sealed record Vehicle(SizeClass Size, int LengthCells) : DesignKind;
    public sealed record Building(PlotSize PlotSize, BuildingKind Type,
        ImmutableArray<FacilityTag> FacilityTags) : DesignKind;
    public sealed record LargeEquipment(GoodId ManufacturedGood) : DesignKind;
}

/// <summary>由内容计算；缺失格不计质量、价值、能力、耐久上限或命中权重。BOM 数量为正整数件。</summary>
public sealed record DesignProjection(DesignHash Design, CompositionHash Composition,
    DesignKind Kind, ImmutableArray<PartProjection> Parts,
    long MaxStructureDurability, long StructureHitWeight, double MassTons,
    Money StructuralBaseValue, ImmutableArray<GoodsQuantity> StructuralGoods,
    ImmutableArray<GoodsQuantity> BillOfMaterials, long BillOfMaterialsItemCount,
    ImmutableArray<DesignReturnUnit> ReturnUnits,
    bool CanConstruct, ImmutableArray<MissingCoreReference> MissingReferences);

/// <summary>
/// 设计命令的固定输入；InputIdentity 必须匹配当前外部定义。
/// AssemblyGoods 包含本设计及嵌套设备的全部衍生组装体货物，不能覆盖同身份的不同规格。
/// 完整布局不进入核心；命令录制与核心状态保留实际使用的业务资料。
/// </summary>
public sealed record DesignInput(CoreInputId InputIdentity, DesignProjection Design,
    ImmutableArray<GoodDefinitionProjection> AssemblyGoods);

/// <summary>
/// 拆解返还的非重叠单元：直接结构体素可按货物合并；功能部件与嵌套设备各自一件。
/// 设备只列整件 Good，其 Parts 与 StructuralGoods 仅表达维修归属，不额外返还内部货物。
/// Parts 使用宿主部件编号；StructuralGoods 数量为正整数件，且不含部件。
/// 缺失引用按原身份保留，缺失部分不能提供返还；组装体不能据此伪装成完整可返还设备。
/// </summary>
public sealed record DesignReturnUnit(GoodId Good, long Quantity,
    ImmutableArray<PartId> Parts, ImmutableArray<GoodsQuantity> StructuralGoods,
    ImmutableArray<MissingCoreReference> MissingReferences);

/// <summary>命中权重为已解析体素数；耐久为正整数。Missing 部件的能力为空、权重和上限为零。</summary>
public sealed record PartProjection(PartId Id, CoreDefinitionReference Definition,
    GoodId RepairGood, long MaxDurability, long HitWeight, Money BaseValue,
    ImmutableArray<PartCapability> Capabilities, bool Missing);

public abstract record PartCapability
{
    private PartCapability() { }
    /// <summary>有限正数：千牛、米/秒。</summary>
    public sealed record Propulsion(double TractionKilonewtons, double RatedSpeedMetersPerSecond) : PartCapability;
    public sealed record Control : PartCapability;
    /// <summary>货物单位的正整数容量。</summary>
    public sealed record Storage(long Capacity) : PartCapability;
    /// <summary>有限正数，工作量/模拟秒。</summary>
    public sealed record Production(double WorkPerSecond) : PartCapability;
    public sealed record Extraction(double WorkPerSecond) : PartCapability;
    public sealed record Construction(double WorkPerSecond, SizeClass MaximumVehicleSize) : PartCapability;
    /// <summary>有限正数，电量/模拟秒。</summary>
    public sealed record Power(double EnergyPerSecond) : PartCapability;
    /// <summary>正整数伤害；有限正数间隔，模拟秒。</summary>
    public sealed record Weapon(long Damage, double IntervalSeconds) : PartCapability;
    public sealed record Platform : PartCapability;
}

public sealed record PartCorrespondence(PartId Before, PartId After);
/// <summary>构成差异由内容计算；新增、移除、替换各算一个改动单位，不由核心读取布局。</summary>
public sealed record DesignChangeProjection(DesignHash Before, CompositionHash BeforeComposition,
    DesignInput Target,
    ImmutableArray<PartCorrespondence> RetainedParts,
    ImmutableArray<GoodsQuantity> AddedGoods,
    ImmutableArray<DesignReturnUnit> RemovedItems, long ChangedItemCount);

/// <summary>流程类别、强类型身份与每批货物；开采产出隐含为 Extracts 的同种货物及数量。</summary>
public abstract record ProcessDetails
{
    private ProcessDetails() { }
    public sealed record Production(ProductionProcessDefinitionId Id,
        ImmutableArray<GoodsQuantity> Inputs, ImmutableArray<GoodsQuantity> Outputs) : ProcessDetails;
    public sealed record Extraction(ProductionProcessDefinitionId Id, GoodsQuantity Extracts) : ProcessDetails;
    public sealed record Recovery(RecoveryProcessDefinitionId Id,
        ImmutableArray<GoodsQuantity> Inputs, ImmutableArray<GoodsQuantity> Outputs) : ProcessDetails;
}
/// <summary>工作总量为正整数；电量/工作量与劳动力/(工作量/秒)均有限非负；所有类别都可要求地貌，空数组表示无要求。</summary>
public sealed record ProcessProjection(ProcessDetails Details, SemanticRevision Revision,
    long WorkRequired, double PowerPerWork, double LaborPerWork,
    FacilityTag RequiredFacilityTag, ImmutableArray<TerrainTypeId> AllowedTerrainTypes);

public enum PopulationNeed { Food, Clothing, ImprovedDiet, ConsumerGoods, ConsumerElectronics, MedicalSupplies }
public enum PopulationNeedClass { Basic, Improvement }
/// <summary>六项人均率均为有限正数，单位为需求量/人/模拟秒；权重为有限正数。</summary>
public sealed record PopulationNeedProjection(PopulationNeed Need, PopulationNeedClass Class,
    double AmountPerPersonSecond, double Weight);
/// <summary>同一货物可同时满足不同需求；换算为需求量/货物单位，有限正数。顺序显式表达食品优先选择。</summary>
public sealed record ConsumptionGoodProjection(GoodId Good,
    ImmutableArray<NeedConversion> Satisfies, int SelectionOrder);
public sealed record NeedConversion(PopulationNeed Need, double AmountPerGoodUnit);
/// <summary>周期为正整数 ticks（60 ticks/秒）；统计期是消费间隔大于一的整数倍。劳动比例在 [0,1]，增长率为每模拟秒的有限非负比例。</summary>
public sealed record PopulationRuleProjection(RuleDefinitionId Id, SemanticRevision Revision,
    ImmutableArray<PopulationNeedProjection> Needs,
    ImmutableArray<ConsumptionGoodProjection> ConsumptionGoods,
    double LaborRatio, int ConsumptionIntervalTicks, int StatisticsPeriodTicks,
    double BaseGrowthPerSecond, double ImprovementGrowthPerSecond, double MaximumLossPerSecond);

/// <summary>10000 为一倍；敏感度非负；0&lt;最低倍率&lt;=最高倍率；价差在 [0,10000)。</summary>
public sealed record GoodsPriceParameters(BasisPoints Sensitivity, BasisPoints MinimumMultiplier,
    BasisPoints MaximumMultiplier, BasisPoints Spread);
public sealed record GoodPriceOverride(GoodId Good, GoodsPriceParameters Parameters);
public sealed record VehiclePurchaseLimit(SizeClass Size, int MaximumForSale);
/// <summary>备货时长为有限非负模拟小时；重评间隔和报告上限为正整数。车辆倍率为正；可租比例在 [0,10000]。</summary>
public sealed record MarketRuleProjection(RuleDefinitionId Id, SemanticRevision Revision,
    double BasicReserveHours, double ProductionReserveHours, double ImprovementReserveHours,
    GoodsPriceParameters DefaultGoodsPrices, ImmutableArray<GoodPriceOverride> GoodPrices,
    BasisPoints VehicleBuyMultiplier, BasisPoints VehicleSellMultiplier,
    ImmutableArray<VehiclePurchaseLimit> VehiclePurchaseLimits,
    int AutomaticEvaluationIntervalTicks, int RecentReportLimit,
    Money CommissionMoneyPerWork, Money RentMoneyPerCapacityHour,
    BasisPoints RentableCapacityRatio, long LeaseDurationTicks);

/// <summary>每件/改动单位/受损部件的工作量为正整数；强度单位同 ProcessProjection。建城工期为正整数模拟步，开始时冻结。</summary>
public sealed record ConstructionRuleProjection(RuleDefinitionId Id, SemanticRevision Revision,
    ulong FoundingDurationTicks,
    long ConstructionWorkPerItem, long RefitWorkPerChangedItem,
    long DeconstructionWorkPerItem, long RepairWorkPerDamagedPart,
    double PowerPerWork, double LaborPerWork);
/// <summary>连续值有限且为正：减速度 m/s²、启动加速度下限 m/s²、间距 m、材料件/m、工期秒/m；修复材料为正整数件。</summary>
public sealed record RailwayRuleProjection(RuleDefinitionId Id, SemanticRevision Revision,
    double BrakingDeceleration, double MinimumAcceleration, double MinimumGapMeters,
    VoxelDefinitionId RailGood, double RailUnitsPerMeter, double BuildSecondsPerMeter,
    long RepairRailUnitsPerBreak, double RepairSecondsPerBreak);
/// <summary>时间以模拟秒计且为有限正数；士气比例、阈值在 [0,1]；损伤系数和恢复率为有限非负数。</summary>
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
/// <summary>当前源模板的业务投影；选定后核心在实例或建城任务内保存自己的业务副本。</summary>
public sealed record SettlementTemplateProjection(SettlementTemplateId Id,
    SettlementTypeId Type, int Weight, ImmutableArray<TemplatePlotProjection> Plots,
    ImmutableArray<FoundingBuildingProjection> FoundingBuildings,
    ImmutableArray<GoodsQuantity> SpecialInitialInventory,
    ImmutableArray<TemplateProductionPlan> ProductionPlans);

/// <summary>生命和伤害为正整数；速度 m/s、时间模拟秒均为有限正数，撤退损失比例在 [0,1]。</summary>
public sealed record CreatureProjection(CreatureDefinitionId Id, SemanticRevision Revision,
    long HitPoints, double SpeedMetersPerSecond, double RetreatLossRatio,
    double RaidDurationSeconds, PartCapability.Weapon? Weapon);
public sealed record WeightedCreature(CreatureDefinitionId Creature, int Weight);
public sealed record LairTypeProjection(LairTypeId Id, SemanticRevision Revision,
    int GenerationWeight, ImmutableArray<WeightedCreature> Members, int MaximumMembers,
    double SpawnIntervalSeconds, double RaidIntervalSeconds, int RoamingRadiusHops,
    int RaidRadiusHops, double RailDamageWorkSeconds, Money ClearanceReward);

/// <summary>所有默认值已解析；范围以米计，比例为 [0,1]，预算为可计数工作量，全部连续值有限。</summary>
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
/// <summary>尺度为米且为正；振幅为有限非负数。</summary>
public sealed record NoiseOctave(double ScaleMeters, double Amplitude);
public sealed record TerrainGenerationBand(TerrainTypeId Terrain, double MinimumElevation,
    double MaximumElevation, double MinimumMoisture, double MaximumMoisture,
    double MaximumRelief, double DetourMultiplier, int MaximumTracks);
public sealed record LairGenerationWeight(LairTypeId Type, int Weight);
/// <summary>储量为非负整数件；概率 [0,1]；可再生资源恢复速率为有限非负件/模拟秒。</summary>
public sealed record ResourceGenerationEntry(VoxelDefinitionId Good,
    ImmutableArray<TerrainTypeId> AllowedTerrainTypes, double Probability,
    long MinimumReserve, long MaximumReserve, bool Renewable, double RecoveryUnitsPerSecond,
    double RegenerationIntervalSeconds);
public sealed record InitialRailwayConfiguration(int MaximumTracks, int MinimumInitialTracks,
    int MaximumInitialTracks, int AdditionalCycleEdges);
public sealed record GenerationBudget(int MaximumStageRetries, int MaximumWorldRetries,
    long MaximumCandidates, long MaximumSearchWork);
/// <summary>产出预存时长为模拟小时；现车数量范围为非负整数，候选设计按设施支持档位筛选。</summary>
public sealed record NpcInitializationConfiguration(bool FillInputsAndLivingTargets,
    double OutputReserveHours, int MinimumForSaleVehicles, int MaximumForSaleVehicles,
    ImmutableArray<DesignHash> ForSaleVehicleCandidates);
public sealed record DefaultStartConstraints(int MinimumReachableNonHostileSettlements,
    int MinimumReachableEmptyNodes, bool RequireStation, bool ExcludeAdjacentLairs,
    int FullyLoadedPurchaseRoundsPerTrain, DesignHash RequiredAffordableCommissionDesign);
public sealed record RuleSelection(RuleDefinitionId Population, RuleDefinitionId Market,
    RuleDefinitionId Construction, RuleDefinitionId Railway, RuleDefinitionId Combat);
/// <summary>车辆数组顺序就是编组顺序；初始货物为整数件，资金非负，不提供无来源的能力。</summary>
public sealed record StartingTrainProjection(ImmutableArray<DesignHash> Vehicles,
    ImmutableArray<GoodsQuantity> Inventory);
public sealed record NewGameStartInput(Money InitialMoney,
    ImmutableArray<StartingTrainProjection> Trains);
