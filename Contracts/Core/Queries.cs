// 核心契约的不可变业务读视图和预览结果。
// 所有详情归属于取得视图的完整边界，不回写活动会话。
using System.Collections.Immutable;

namespace Train.Core.Contracts;

/// <summary>同一模拟位置的不可变快照；保留及会话释放均不使结果失效，查询无副作用。</summary>
public interface ICoreReadView
{
    SimulationPosition Position { get; }
    CoreRevision Revision { get; }
    CoreInputId InputIdentity { get; }
    /// <summary>内部故障冻结事实；正常运行时为空。</summary>
    CoreFault? Fault { get; }
    WorldSummary World { get; }
    EconomyView Economy { get; }
    Lookup<NodeView> FindNode(NodeId id);
    Lookup<EdgeView> FindEdge(EdgeId id);
    /// <summary>按节点或整条线路查询场景对象；Edge.DistanceMeters 不参与匹配，不按距离筛选。</summary>
    Lookup<LocationView> FindLocation(WorldLocation location);
    Lookup<TrainView> FindTrain(TrainId id);
    Lookup<InstanceView> FindInstance(InstanceId id);
    Lookup<SettlementView> FindSettlement(SettlementId id);
    Lookup<InventoryView> FindInventory(InventoryId id);
    Lookup<LeaseView> FindLease(LeaseId id);
    Lookup<FacilityView> FindFacility(FacilityKey key);
    Lookup<JobView> FindJob(JobId id);
    Lookup<CombatView> FindCombat(CombatId id);
    Lookup<CreatureGroupView> FindCreatureGroup(CreatureGroupId id);
    Lookup<CreatureView> FindCreature(CreatureId id);
    Lookup<LairView> FindLair(LairId id);
    CoreResult<TradeQuote> QuoteTrade(TradeQuoteRequest request);
    CoreResult<VehicleTradeQuote> QuoteVehicle(VehicleTradeQuoteRequest request);
    CoreResult<LeaseQuote> QuoteLease(LeaseRequest request);
    CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding);
    CoreResult<SettlementFoundingPreview> PreviewSettlement(NodeId node);
}

/// <summary>宏观地图摘要；距离为米，所有集合均属于本视图。</summary>
public sealed record WorldSummary(double WidthMeters, double HeightMeters,
    ImmutableArray<NodeMapItem> Nodes, ImmutableArray<EdgeMapItem> Edges,
    ImmutableArray<TrainMapItem> Trains, ImmutableArray<CreatureGroupMapItem> CreatureGroups,
    ImmutableArray<CombatMapItem> Combats);
/// <summary>内容定义的地貌标识及起伏和特征，不包含生成器或栅格。</summary>
public sealed record TerrainView(TerrainTypeId TypeId, double Relief, ImmutableArray<string> FeatureIds);
/// <summary>节点唯一占据者。</summary>
public abstract record NodeOccupant
{
    private NodeOccupant() { }
    public sealed record Empty : NodeOccupant;
    public sealed record Settlement(SettlementId Id, SettlementRelation Relation) : NodeOccupant;
    public sealed record Lair(LairId Id) : NodeOccupant;
}
/// <summary>节点地图信息。</summary>
public sealed record NodeMapItem(NodeId Id, MapPoint Position, TerrainView Terrain,
    NodeOccupant Occupant, ImmutableArray<NodeResourceView> Resources, int OccupiedPlatforms, int Platforms,
    JobId? FoundingJob);
/// <summary>线路地图信息；施工标记来自真实线路任务。</summary>
public sealed record EdgeMapItem(EdgeId Id, NodeId Start, NodeId End, double LengthMeters,
    TerrainView Terrain, int Tracks, int TrackLimit, ImmutableArray<TrackBreakView> Breaks,
    ImmutableArray<RailwayWorkView> Works);
/// <summary>列车地图位置与状态。</summary>
public sealed record TrainMapItem(TrainId Id, TrainPosition Position, TrainActivity Activity);
/// <summary>生物群地图位置。</summary>
public sealed record CreatureGroupMapItem(CreatureGroupId Id, WorldLocation Location, CreatureActivity Activity);
/// <summary>进行中的战斗地图标记。</summary>
public sealed record CombatMapItem(CombatId Id, WorldLocation Location);
/// <summary>节点详细信息及相邻线路。</summary>
public sealed record NodeView(NodeMapItem Map, ImmutableArray<EdgeId> AdjacentEdges);
/// <summary>线路详情，不暴露等待仲裁队列。</summary>
public sealed record EdgeView(EdgeMapItem Map, ImmutableArray<TrackView> Tracks, SettlementId? AutoRepairMaterialSource);
/// <summary>铁轨当前方向锁；空值表示没有方向锁。</summary>
public sealed record TrackView(int Number, TravelDirection? DirectionLock, ImmutableArray<TrainId> Trains);
/// <summary>断点沿线路正方向的距离，单位米。</summary>
public sealed record TrackBreakView(BreakId Id, int Track, double DistanceMeters);
/// <summary>线路施工当前进度；材料已经投入，不列内部仲裁数据。</summary>
public sealed record RailwayWorkView(JobId Id, RailwayWorkKind Kind, int Track,
    BreakId? RepairingBreak, SettlementId MaterialSource, ImmutableArray<GoodsQuantity> InvestedGoods,
    double Progress, ImmutableArray<CoreProblem> Blockers);
/// <summary>线路任务种类。</summary>
public enum RailwayWorkKind { AddTrack, RepairBreak }
/// <summary>整个节点或线路场景中的业务对象；线路 Location.DistanceMeters 规范化为零，不提供局部几何。</summary>
public sealed record LocationView(WorldLocation Location, ImmutableArray<TrainId> Trains,
    ImmutableArray<InstanceId> Instances, ImmutableArray<CreatureGroupId> CreatureGroups,
    ImmutableArray<CombatId> Combats, NodeOccupant? Occupant);

/// <summary>地图和车队面板共用的列车活动状态。</summary>
public enum TrainActivity { Stopped, Running, Servicing, Waiting, NoRoute, Combat, Retreating }
/// <summary>有序运行计划；单次或循环。</summary>
public sealed record RunPlan(bool Loop, ImmutableArray<PlanStop> Stops);
/// <summary>停靠或途经点；仅停靠点使用停留步数及按数组顺序执行的货物规则。</summary>
public sealed record PlanStop(NodeId Node, bool Stop, ulong DwellSteps, ImmutableArray<AutomaticGoodsRule> GoodsRules);
/// <summary>玩家视角的买卖或免费装卸方向。</summary>
public enum TradeAction { Buy, Sell, Load, Unload }
/// <summary>达到列车库存目标；买卖限价为逐件限价，装卸不使用限价。</summary>
public sealed record AutomaticGoodsRule(TradeAction Action, GoodId Good, InventoryId LocalInventory,
    long TargetQuantity, Money? UnitPriceLimit);
/// <summary>交战目标优先级。</summary>
public enum TargetPriority { ArmedFirst, LowestDurability, HighestDurability }
/// <summary>允许攻击的目标范围。</summary>
public enum TargetScope { ArmedOnly, All }
/// <summary>列车遇敌行为。</summary>
public enum EncounterAction { Engage, Retreat }
/// <summary>列车交战规则；撤退比例在 0 至 1 之间。</summary>
public sealed record TrainEngagementRules(TargetPriority Priority, TargetScope Scope,
    EncounterAction Encounter, double RetreatStructureRatio);
/// <summary>聚居地防御规则。</summary>
public sealed record SettlementEngagementRules(TargetPriority Priority, TargetScope Scope);
/// <summary>列车详情；速度米/秒，长度米，质量吨，牵引力千牛，加速度米/秒²。</summary>
public sealed record TrainView(TrainId Id, TrainPosition Position, TrainActivity Activity,
    ImmutableArray<VehicleId> Vehicles, InventoryId Inventory, double SpeedMetersPerSecond,
    double LengthMeters, double MassTons, double TractionKilonewtons, double Acceleration,
    double MaximumSpeedMetersPerSecond, RunPlan? Plan, int? NextStopIndex, ulong RemainingDwellSteps,
    ulong RemainingTradeEvaluationSteps, bool TradeEvaluationFrozen, TrainEngagementRules Engagement,
    ImmutableArray<CoreProblem> Blockers, RunAccumulatorView? CurrentRun, ImmutableArray<RunReport> RecentReports);

/// <summary>实例锁定种类，可组合施工锁和战斗锁；施工期间仍可受攻击。</summary>
[System.Flags]
public enum InstanceLock { None = 0, UnderRefit = 1, UnderRepair = 2, UnderDeconstruction = 4, Combat = 8 }
/// <summary>历史绝对耐久与当前有效上限；显示完好比例须使用 EffectiveCurrent / Maximum。</summary>
public sealed record DurabilityView(long StoredCurrent, long EffectiveCurrent, long Maximum);
/// <summary>部件持续损伤；缺定义状态与被毁状态分开。</summary>
public sealed record PartDamageView(PartId Part, DurabilityView Durability, bool DefinitionMissing, bool Destroyed);
/// <summary>实例设计引用及持续损伤，不含布局；车辆 Train 为空表示停放。</summary>
public sealed record InstanceView(InstanceId Id, DesignHash Design, CompositionHash Composition,
    WorldLocation Location, SettlementRelation Ownership, TrainId? Train, PlotId? Plot,
    SizeClass? VehicleSize, PlotSize? BuildingPlotSize, InstanceLock Lock, DurabilityView Structure, ImmutableArray<PartDamageView> Parts,
    ImmutableArray<CoreProblem> MissingContent);
/// <summary>聚居地详情；统一库存与各设施均使用同一快照。</summary>
public sealed record SettlementView(SettlementId Id, NodeId Node, SettlementTypeId Type,
    SettlementRelation Relation, InventoryId Inventory, ImmutableArray<PlotView> Plots,
    ImmutableArray<VehicleId> ParkedVehicles, int ParkingCapacity, int Platforms, int OccupiedPlatforms,
    ImmutableArray<VehicleId> VehiclesForSale, ImmutableArray<VehiclePurchaseCapacity> VehiclePurchaseCapacities,
    ImmutableArray<FacilityView> Facilities, PowerPoolView Power, PopulationView Population,
    SettlementEngagementRules Engagement, double MoraleRatio, bool Surrendered,
    ImmutableArray<JobId> CommissionJobs, ImmutableArray<LeaseId> Leases, MarketView Market);
/// <summary>地块占用类型；空地、建筑和工地互斥。</summary>
public abstract record PlotOccupant
{
    private PlotOccupant() { }
    public sealed record Empty : PlotOccupant;
    public sealed record Building(BuildingId Id) : PlotOccupant;
    public sealed record Construction(JobId Id) : PlotOccupant;
}
/// <summary>地块身份、尺寸及内容声明的建筑种类限制。</summary>
public sealed record PlotView(PlotId Id, PlotSize Size, ImmutableArray<BuildingKind> AllowedBuildingKinds, PlotOccupant Occupant);
/// <summary>当前分档现车收购容量；待售数可超过新上限，剩余名额最低为零。</summary>
public sealed record VehiclePurchaseCapacity(SizeClass Size, int ForSale, int Limit, int Available);
/// <summary>库存归属，租用库存不与 NPC 库存合并。</summary>
public abstract record InventoryOwner
{
    private InventoryOwner() { }
    public sealed record Train(TrainId Id) : InventoryOwner;
    public sealed record Settlement(SettlementId Id) : InventoryOwner;
    public sealed record Lease(LeaseId Id) : InventoryOwner;
}
/// <summary>库存访问权，不从关系或租期由调用方推算。</summary>
public enum InventoryAccess { ReadWrite, WithdrawOnly, Frozen }
/// <summary>库存项目；定义缺失时仍保留数量但不能使用。</summary>
public sealed record InventoryStackView(GoodId Good, long Quantity, bool DefinitionMissing);
/// <summary>返还容量预留对应的任务及预计货物。</summary>
public sealed record StorageReservationView(JobId Job, long Capacity, ImmutableArray<GoodsQuantity> ExpectedReturns);
/// <summary>容量点等于货物件数；Available 可为负表示超容，不能据此删货。</summary>
public sealed record InventoryView(InventoryId Id, InventoryOwner Owner, WorldLocation Location,
    InventoryAccess Access, long Capacity, long Used, long Reserved, long Available,
    double GoodsMassTons, ImmutableArray<InventoryStackView> Stacks, ImmutableArray<StorageReservationView> Reservations);
/// <summary>共有物理容量及租约占位，只计算一次租出完整额度。</summary>
public sealed record SettlementStorageView(long PhysicalCapacity, long TotalUsed, long TotalReserved,
    long ActiveLeaseCapacity, long ExpiredLeaseUsed, long NpcCapacity, long RentableCapacity);
/// <summary>租约原合同不随规则更新改变，到期后的访问由库存查询给出。</summary>
public sealed record LeaseView(LeaseId Id, SettlementId Settlement, InventoryId Inventory,
    long Capacity, ulong ExpiresAtStep, Money Paid, bool Expired);

/// <summary>八种互斥的生产计划状态。</summary>
public enum ProductionState { Disabled, Running, WaitingForInputs, OutputBlocked, ZeroCapability, ConditionsNotMet, Locked, ProcessMissing }
/// <summary>联合分配的限制因素，可同时出现多项。</summary>
public enum WorkLimit { Capability, Power, Labor }
/// <summary>当前计划及执行信息；无计划时 Process 为空且 State 为 Disabled。</summary>
public sealed record FacilityView(FacilityKey Key, ProcessRef? Process, bool Enabled, int Priority,
    ProductionState State, double CapabilityWorkPerSecond, double ActualWorkPerSecond,
    ImmutableArray<WorkLimit> Limits, ActiveBatchView? Batch, ImmutableArray<GoodsQuantity> OutputBuffer,
    ImmutableArray<GoodsRateView> PotentialConsumption, ImmutableArray<CoreProblem> Blockers);
/// <summary>货物每秒潜在消耗；不是固定收购单。</summary>
public sealed record GoodsRateView(GoodId Good, double UnitsPerSecond);
/// <summary>批次冻结规格；进度工作量可有小数，总工作量为正整数。</summary>
public sealed record ActiveBatchView(ProcessRef Process, SemanticRevision StartedFromRevision,
    long FrozenWorkRequired, double ProgressWork, double Progress, double FrozenPowerPerWork,
    double FrozenLaborPerWork, ImmutableArray<GoodsQuantity> FrozenOutputs, bool CurrentSpecificationDiffers);
/// <summary>聚居地电力池，功率为电量/秒，燃料余量为电量。</summary>
public sealed record PowerPoolView(double GenerationCapacity, double Demand, double ActualSupply, double FuelEnergyRemaining);
/// <summary>资源恢复种类，不可再生资源没有伪造的零恢复参数。</summary>
public abstract record ResourceRenewal
{
    private ResourceRenewal() { }
    public sealed record NonRenewable : ResourceRenewal;
    public sealed record Renewable(long Maximum, double UnitsPerSecond) : ResourceRenewal;
}
/// <summary>节点资源储量是整数；可再生恢复的小数累计由核心管理。</summary>
public sealed record NodeResourceView(GoodId Good, long Quantity, ResourceRenewal Renewal);

/// <summary>当前发布规则的有效性，版本为空只表示规则缺失。</summary>
public sealed record RuleStatusView(RuleDefinitionId Rule, SemanticRevision? PublishedRevision, ImmutableArray<CoreProblem> Problems);
/// <summary>各需求累计与当前速率；满足率在没有应需时为空，不伪装成零。</summary>
public sealed record PopulationNeedView(PopulationNeed Kind, double RequiredPerSecond,
    double AccumulatedRequired, double AccumulatedSatisfied, double? SatisfactionRatio);
/// <summary>按货物保留的消费剩余量，非可交易库存。</summary>
public sealed record ConsumptionRemainderView(GoodId Good, double Quantity);
/// <summary>已开始统计期采用的版本和冻结参数，独立于当前发布规则。</summary>
public sealed record PopulationPeriodView(PopulationRuleProjection FrozenRule, ulong PeriodSteps,
    ulong ElapsedEffectiveSteps, ulong ConsumptionIntervalSteps, ulong RemainingConsumptionSteps,
    ImmutableArray<PopulationNeedView> Needs, double? BasicSatisfaction, double? ImprovementSatisfaction);
/// <summary>人口趋势是核心计算结果，不由界面根据人数猜测。</summary>
public enum PopulationTrend { Growing, Stable, Declining, AtMinimum }
/// <summary>人口统计进度显式区分尚未开始、未完成与因缺失停用。</summary>
public abstract record PopulationExecution
{
    private PopulationExecution() { }
    public sealed record NotStarted(ImmutableArray<PopulationNeedView> CurrentNeeds) : PopulationExecution;
    public sealed record InProgress(PopulationPeriodView Period, PopulationTrend Trend) : PopulationExecution;
    public sealed record Disabled(PopulationPeriodView? RetainedPeriod, ImmutableArray<CoreProblem> Reasons) : PopulationExecution;
}
/// <summary>人口结算原因。</summary>
public enum PopulationSettlementReason { Growth, ShortageLoss, Stable, MinimumReached }
/// <summary>已结算统计期摘要；没有历史结算时不创建一条全零摘要。</summary>
public sealed record PopulationSettlementReport(SimulationPosition Position, SemanticRevision ExecutionRevision,
    long Before, long After, long Change, double BasicSatisfaction, double ImprovementSatisfaction,
    PopulationSettlementReason Reason);
/// <summary>人口、余量与真实劳动力分配；LastSettlement 为空表示尚未完成任何统计期。</summary>
public sealed record PopulationView(RuleStatusView CurrentRule, long Residents, double PopulationChangeRemainder,
    double AvailableLabor, double AllocatedLabor, double IdleLabor, PopulationExecution Execution,
    ImmutableArray<ConsumptionRemainderView> ConsumptionRemainders,
    PopulationSettlementReport? LastSettlement, ImmutableArray<PopulationSettlementReport> RecentSettlements);
/// <summary>建城前人口及生活需求预览。</summary>
public sealed record SettlementFoundingPreview(NodeId Node, long InitialResidents,
    ImmutableArray<GoodsRateView> ExpectedConsumption, ImmutableArray<GoodsQuantity> RequiredMaterials,
    ulong DurationTicks, ImmutableArray<CoreProblem> Blockers);

/// <summary>任务身份对应的明确种类；线路和建城任务不伪造施工设施。</summary>
public abstract record JobView
{
    private JobView() { }
    public sealed record Construction(ConstructionJobView Value) : JobView;
    public sealed record Railway(EdgeId Edge, RailwayWorkView Value) : JobView;
    public sealed record Founding(FoundingJobView Value) : JobView;
}

/// <summary>建城使用冻结的固定工期，不消耗设施工作速率；材料在开始时已投入。</summary>
public sealed record FoundingJobView(JobId Id, NodeId Node, ulong FrozenDurationTicks,
    ulong ElapsedTicks, ImmutableArray<GoodsQuantity> InvestedGoods);

/// <summary>四类实例或货物施工任务。</summary>
public enum ConstructionKind { Construction, Refit, Repair, Deconstruction }
/// <summary>施工任务活动状态。</summary>
public enum ConstructionState { Queued, Working, ResourceBlocked, Paused, Failed, Completed }
/// <summary>施工产出或目标；建造过程中不存在半成品实例。</summary>
public abstract record ConstructionTarget
{
    private ConstructionTarget() { }
    public sealed record NewVehicle(DesignHash Design) : ConstructionTarget;
    public sealed record NewBuilding(PlotId Plot, DesignHash Design) : ConstructionTarget;
    public sealed record NewAssembly(DesignHash Design, CompositionHash Composition, InventoryId Destination) : ConstructionTarget;
    public sealed record Existing(InstanceId Instance, DesignHash TargetDesign) : ConstructionTarget;
}
/// <summary>任务冻结工作量与系数；Progress 为 0 至 1，工作速率为工作量/秒。</summary>
public sealed record ConstructionJobView(JobId Id, ConstructionKind Kind, SettlementId Settlement,
    FacilityKey Facility, ConstructionTarget Target, ConstructionState State, int Priority,
    long FrozenTotalWork, double ProgressWork, double Progress, double FrozenPowerPerWork,
    double FrozenLaborPerWork, double ActualWorkPerSecond, ImmutableArray<WorkLimit> Limits,
    ImmutableArray<GoodsQuantity> InvestedGoods, ImmutableArray<CoreProblem> Blockers);

/// <summary>战斗单位身份。</summary>
public abstract record CombatUnitId
{
    private CombatUnitId() { }
    public sealed record Instance(InstanceId Id) : CombatUnitId;
    public sealed record CreatureGroup(CreatureGroupId Id) : CombatUnitId;
}
/// <summary>参战者当前撤退与集中攻击状态。</summary>
public sealed record CombatParticipantView(CombatUnitId Unit, bool Retreating,
    ulong RemainingDisengagementSteps, CombatUnitId? FocusTarget);
/// <summary>进行中的战斗详情；损伤读取实例或生物群。</summary>
public sealed record CombatView(CombatId Id, WorldLocation Location, ulong StartedAtStep,
    ImmutableArray<CombatParticipantView> Participants);
/// <summary>生物群当前活动。</summary>
public enum CreatureActivity { Guarding, Wandering, RaidingSettlement, RaidingRailway, Combat, Retreating }
/// <summary>生物个体设计及生命值，无部件。</summary>
public sealed record CreatureView(CreatureId Id, CreatureDefinitionId Definition, DesignHash Design, long Health, long MaximumHealth);
/// <summary>生物群持续状态；Home 为空表示聚落已被清剿。</summary>
public sealed record CreatureGroupView(CreatureGroupId Id, LairId? Home, WorldLocation Location,
    TravelDirection? Direction, CreatureActivity Activity, double SpeedMetersPerSecond, ImmutableArray<CreatureView> Creatures);
/// <summary>生物聚落地图与面板信息。</summary>
public sealed record LairView(LairId Id, NodeId Node, LairTypeId Type, ImmutableArray<CreatureGroupId> Groups);

/// <summary>当前市场派生目标与可成交数量，不是独立库存。</summary>
public sealed record MarketGoodView(GoodId Good, long Stock, long RestockTarget, long ReservedForUse,
    long SellableQuantity, long ReceivableQuantity, ImmutableArray<CoreProblem> Blockers);
/// <summary>规则、容量划分及真实库存导出的市场信息。</summary>
public sealed record MarketView(RuleStatusView Rule, SettlementStorageView Storage, ImmutableArray<MarketGoodView> Goods);
/// <summary>当前快照上的货物整单报价请求。</summary>
public sealed record TradeQuoteRequest(TrainId Train, SettlementId Settlement, InventoryId LocalInventory,
    TradeAction Action, GoodId Good, long Quantity);
/// <summary>报价无预留；Total 为请求数量完整可执行时的金额，不能成交时为空。</summary>
public sealed record TradeQuote(TradeQuoteRequest Request, CoreRevision Revision, long CurrentlyExecutableQuantity,
    Money? Total, ImmutableArray<CoreProblem> Blockers, ImmutableArray<CoreProblem> ShortageWarnings);
/// <summary>现车买卖方向。</summary>
public enum VehicleTradeAction { Buy, Sell }
/// <summary>买卖原实例的当前报价请求。</summary>
public sealed record VehicleTradeQuoteRequest(SettlementId Settlement, VehicleId Vehicle, VehicleTradeAction Action);
/// <summary>整车报价，没有创建或修复实例的含义。</summary>
public sealed record VehicleTradeQuote(VehicleTradeQuoteRequest Request, CoreRevision Revision,
    Money? Total, ImmutableArray<CoreProblem> Blockers);
/// <summary>租约报价无容量预留，执行仍检查真实共享空间。</summary>
public sealed record LeaseQuote(LeaseRequest Request, CoreRevision Revision, Money? Total,
    long CurrentlyRentableCapacity, ImmutableArray<CoreProblem> Blockers);
/// <summary>施工接受事务预览：完整 BOM、来源、NPC 补购和工钱；不消费或预留。</summary>
public sealed record JobQuote(JobRequest Request, JobFunding Funding, CoreRevision Revision,
    long TotalWork, ImmutableArray<GoodsQuantity> RequiredGoods,
    ImmutableArray<JobMaterialSourceView> SourceConsumption, ImmutableArray<GoodsQuantity> NpcPurchases,
    Money? MaterialPayment, Money? ServicePayment, Money? TotalPayment,
    ImmutableArray<CoreProblem> Blockers);
/// <summary>按照请求顺序选用的材料来源及本次预计消耗。</summary>
public sealed record JobMaterialSourceView(InventoryId Inventory, ImmutableArray<GoodsQuantity> Goods);
/// <summary>自动规则的实际成交与未完成原因，顺序对应计划的显式规则。</summary>
public sealed record AutomaticRuleResult(int StopIndex, int RuleIndex, TradeAction Action, GoodId Good,
    long RequestedQuantity, long CompletedQuantity, Money ActualAmount, ImmutableArray<CoreProblem> IncompleteReasons);
/// <summary>当前运行累计；现金流有符号，不表示包括成本及折旧的利润。</summary>
public sealed record RunAccumulatorView(SimulationPosition StartedAt, Money SalesReceipts, Money PurchasePayments,
    Money ServicePayments, long NetCashFlow, long TransportedUnits, double LoadedCapacityRatio,
    ImmutableArray<AutomaticRuleResult> IncompleteRules);
/// <summary>运行报告结束原因。</summary>
public enum RunEndReason { PlanCompleted, LoopCompleted, PlanReplaced, TrainDisbanded, AllVehiclesDestroyed }
/// <summary>已完成或中断的一次运行报告，免费转移不计入销售收入。</summary>
public sealed record RunReport(TrainId Train, SimulationPosition EndedAt, RunEndReason Reason, RunAccumulatorView Totals);
/// <summary>全局玩家钱包、有效规则及不可归属运行的已付账目。</summary>
public sealed record EconomyView(Money Wallet, RuleStatusView MarketRule, RuleStatusView PopulationRule,
    RuleStatusView ConstructionRule, Money UnassignedServicePayments, ImmutableArray<RunReport> RecentRunReports);
