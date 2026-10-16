using System.Collections.Immutable;

namespace Train.Core.Contracts;

public interface ICoreReadView
{
    SimulationPosition Position { get; }
    CoreRevision Revision { get; }
    CoreInputId InputIdentity { get; }
    CoreFault? Fault { get; }
    WorldSummary World { get; }
    EconomyView Economy { get; }
    Lookup<NodeView> FindNode(NodeId id);
    Lookup<EdgeView> FindEdge(EdgeId id);
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

public sealed record WorldSummary(ulong WorldSeed, RuleDefinitionId RailwayRule,
    double WidthMeters, double HeightMeters,
    ImmutableArray<NodeMapItem> Nodes, ImmutableArray<EdgeMapItem> Edges,
    ImmutableArray<TrainMapItem> Trains, ImmutableArray<CreatureGroupMapItem> CreatureGroups,
    ImmutableArray<CombatMapItem> Combats);
public sealed record TerrainView(TerrainTypeId TypeId, double Relief, ImmutableArray<string> FeatureIds);
public abstract record NodeOccupant
{
    private NodeOccupant() { }
    public sealed record Empty : NodeOccupant;
    public sealed record Settlement(SettlementId Id, SettlementRelation Relation) : NodeOccupant;
    public sealed record Lair(LairId Id) : NodeOccupant;
}
public sealed record NodeMapItem(NodeId Id, MapPoint Position, TerrainView Terrain,
    NodeOccupant Occupant, ImmutableArray<NodeResourceView> Resources, int OccupiedPlatforms, int Platforms,
    JobId? FoundingJob);
public sealed record EdgeMapItem(EdgeId Id, NodeId Start, NodeId End, double LengthMeters,
    TerrainView Terrain, int Tracks, int TrackLimit, ImmutableArray<TrackBreakView> Breaks,
    ImmutableArray<RailwayWorkView> Works);
public sealed record TrainMapItem(TrainId Id, TrainPosition Position, TrainActivity Activity);
public sealed record CreatureGroupMapItem(CreatureGroupId Id, WorldLocation Location, CreatureActivity Activity);
public sealed record CombatMapItem(CombatId Id, WorldLocation Location);
public sealed record NodeView(NodeMapItem Map, ImmutableArray<EdgeId> AdjacentEdges,
    double ApproachLengthMeters, double PlatformLengthMeters, double DepartureLengthMeters);
public sealed record EdgeView(EdgeMapItem Map, ImmutableArray<TrackView> Tracks, SettlementId? AutoRepairMaterialSource);
public sealed record TrackView(int Number, TravelDirection? DirectionLock, ImmutableArray<TrainId> Trains);
public sealed record TrackBreakView(BreakId Id, int Track, double DistanceMeters);
public sealed record RailwayWorkView(JobId Id, RailwayWorkKind Kind, int Track,
    BreakId? RepairingBreak, SettlementId MaterialSource, ImmutableArray<GoodsQuantity> InvestedGoods,
    double Progress, ImmutableArray<CoreProblem> Blockers);
public enum RailwayWorkKind { AddTrack, RepairBreak }
public sealed record LocationView(WorldLocation Location, ImmutableArray<TrainId> Trains,
    ImmutableArray<InstanceId> Instances, ImmutableArray<CreatureGroupId> CreatureGroups,
    ImmutableArray<CombatId> Combats, NodeOccupant? Occupant);

public enum TrainActivity { Stopped, Running, Servicing, Waiting, NoRoute, Combat, Retreating }
public sealed record RunPlan(bool Loop, ImmutableArray<PlanStop> Stops);
public abstract record PlanStop
{
    private PlanStop() { }
    public sealed record Waypoint(NodeId Node) : PlanStop;
    public sealed record Station(NodeId Node, ulong DwellSteps,
        ImmutableArray<AutomaticGoodsRule> GoodsRules) : PlanStop;
}
public enum TradeAction { Buy, Sell, Load, Unload }
public abstract record AutomaticGoodsAction
{
    private AutomaticGoodsAction() { }
    public sealed record Buy(Money? MaximumUnitPrice) : AutomaticGoodsAction;
    public sealed record Sell(Money? MinimumUnitPrice) : AutomaticGoodsAction;
    public sealed record Load : AutomaticGoodsAction;
    public sealed record Unload : AutomaticGoodsAction;
}
public sealed record AutomaticGoodsRule(AutomaticGoodsAction Action, GoodId Good, InventoryId LocalInventory,
    long TargetQuantity);
public enum TargetPriority { ArmedFirst, LowestDurability, HighestDurability }
public enum TargetScope { ArmedOnly, All }
public enum EncounterAction { Engage, Retreat }
public sealed record TrainEngagementRules(TargetPriority Priority, TargetScope Scope,
    EncounterAction Encounter, double RetreatStructureRatio);
public sealed record SettlementEngagementRules(TargetPriority Priority, TargetScope Scope);
public enum TrainOrderFacing { FirstEndLeading, LastEndLeading }
public sealed record TrainView(TrainId Id, TrainPosition Position, TrainActivity Activity,
    ImmutableArray<VehicleId> Vehicles, InventoryId Inventory, double SpeedMetersPerSecond,
    double LengthMeters, double MassTons, double TractionKilonewtons, double Acceleration,
    double MaximumSpeedMetersPerSecond, RunPlan? Plan, int? NextStopIndex, ulong RemainingDwellSteps,
    TrainEngagementRules Engagement,
    ImmutableArray<CoreProblem> Blockers, RunAccumulatorView? CurrentRun, ImmutableArray<RunReport> RecentReports,
    TrainOrderFacing OrderFacing, ulong? PlatformQueueEnteredStep);

[System.Flags]
public enum InstanceLock { None = 0, UnderRefit = 1, UnderRepair = 2, UnderDeconstruction = 4, Combat = 8 }
public sealed record DurabilityView(long EffectiveCurrent, long Maximum);
public sealed record PartDamageView(PartId Part, DurabilityView Durability, bool DefinitionMissing, bool Destroyed);
public abstract record InstanceDetails
{
    private InstanceDetails() { }
    public sealed record Vehicle(SizeClass Size, TrainId? Train) : InstanceDetails;
    public sealed record Building(PlotId Plot, PlotSize Size) : InstanceDetails;
}
public sealed record InstanceView(InstanceId Id, DesignHash Design, CompositionHash Composition,
    WorldLocation Location, SettlementRelation Ownership, InstanceDetails Details,
    InstanceLock Lock, DurabilityView Structure, ImmutableArray<PartDamageView> Parts,
    ImmutableArray<CoreProblem> MissingContent);
public sealed record SettlementView(SettlementId Id, NodeId Node, SettlementTypeId Type,
    SettlementTemplateId? SourceTemplateId,
    SettlementRelation Relation, InventoryId Inventory, ImmutableArray<PlotView> Plots,
    ImmutableArray<VehicleId> ParkedVehicles, int ParkingCapacity, int Platforms, int OccupiedPlatforms,
    ImmutableArray<VehicleId> VehiclesForSale, ImmutableArray<VehiclePurchaseCapacity> VehiclePurchaseCapacities,
    ImmutableArray<FacilityView> Facilities, PowerPoolView Power, PopulationView Population,
    SettlementEngagementRules Engagement, double MoraleRatio, bool Surrendered,
    ImmutableArray<JobId> CommissionJobs, ImmutableArray<LeaseId> Leases, MarketView Market);
public abstract record PlotOccupant
{
    private PlotOccupant() { }
    public sealed record Empty : PlotOccupant;
    public sealed record Building(BuildingId Id) : PlotOccupant;
    public sealed record Construction(JobId Id) : PlotOccupant;
}
public sealed record PlotView(PlotId Id, int TemplatePlotNumber, PlotSize Size,
    ImmutableArray<BuildingKind> AllowedBuildingKinds, PlotOccupant Occupant);
public sealed record VehiclePurchaseCapacity(SizeClass Size, int ForSale, int Limit, int Available);
public abstract record InventoryOwner
{
    private InventoryOwner() { }
    public sealed record Train(TrainId Id) : InventoryOwner;
    public sealed record Settlement(SettlementId Id) : InventoryOwner;
    public sealed record Lease(LeaseId Id) : InventoryOwner;
}
public enum InventoryAccess { ReadWrite, WithdrawOnly, Frozen }
public sealed record InventoryStackView(GoodId Good, long Quantity, bool DefinitionMissing);
public sealed record StorageReservationView(JobId Job, long Capacity, ImmutableArray<GoodsQuantity> ExpectedReturns);
public sealed record InventoryView(InventoryId Id, InventoryOwner Owner, WorldLocation Location,
    InventoryAccess Access, long Capacity, long Used, long Reserved, long Available,
    double GoodsMassTons, ImmutableArray<InventoryStackView> Stacks, ImmutableArray<StorageReservationView> Reservations);
public sealed record SettlementStorageView(long PhysicalCapacity, long TotalUsed, long TotalReserved,
    long ActiveLeaseCapacity, long ExpiredLeaseUsed, long NpcCapacity, long RentableCapacity);
public sealed record LeaseView(LeaseId Id, SettlementId Settlement, InventoryId Inventory,
    long Capacity, ulong ExpiresAtStep, Money Paid, bool Expired);

public enum ProductionState { Disabled, Running, WaitingForInputs, OutputBlocked, ZeroCapability, ConditionsNotMet, Locked, ProcessMissing }
public enum WorkLimit { Capability, Power, Labor }
public sealed record FacilityView(FacilityKey Key, ProcessRef? Process, bool Enabled, int Priority,
    ProductionState State, double CapabilityWorkPerSecond, double ActualWorkPerSecond,
    ImmutableArray<WorkLimit> Limits, ActiveBatchView? Batch, ImmutableArray<GoodsQuantity> OutputBuffer,
    ImmutableArray<GoodsRateView> PotentialConsumption, ImmutableArray<CoreProblem> Blockers);
public sealed record GoodsRateView(GoodId Good, double UnitsPerSecond);
public sealed record ActiveBatchView(ProcessRef Process, SemanticRevision StartedFromRevision,
    long FrozenWorkRequired, double ProgressWork, double Progress,
    ImmutableArray<GoodsQuantity> FrozenOutputs, bool CurrentSpecificationDiffers);
public sealed record PowerPoolView(double GenerationCapacity, double Demand, double ActualSupply, double FuelEnergyRemaining);
public abstract record ResourceRenewal
{
    private ResourceRenewal() { }
    public sealed record NonRenewable : ResourceRenewal;
    public sealed record Renewable(long Maximum, double UnitsPerSecond) : ResourceRenewal;
}
public sealed record NodeResourceView(GoodId Good, long Quantity, ResourceRenewal Renewal);

public sealed record RuleStatusView(RuleDefinitionId Rule, SemanticRevision? PublishedRevision, ImmutableArray<CoreProblem> Problems);
public sealed record PopulationNeedView(PopulationNeed Kind, double RequiredPerSecond,
    double AccumulatedRequired, double AccumulatedSatisfied, double? SatisfactionRatio);
public sealed record PopulationPeriodView(RuleDefinitionId ExecutionRule, SemanticRevision ExecutionRevision,
    ulong PeriodSteps, ulong ElapsedEffectiveSteps,
    ImmutableArray<PopulationNeedView> Needs, double? BasicSatisfaction, double? ImprovementSatisfaction);
public enum PopulationTrend { Growing, Stable, Declining, AtMinimum }
public abstract record PopulationExecution
{
    private PopulationExecution() { }
    public sealed record NotStarted(ImmutableArray<PopulationNeedView> CurrentNeeds) : PopulationExecution;
    public sealed record InProgress(PopulationPeriodView Period, PopulationTrend Trend) : PopulationExecution;
    public sealed record Disabled(PopulationPeriodView? RetainedPeriod, ImmutableArray<CoreProblem> Reasons) : PopulationExecution;
}
public enum PopulationSettlementReason { Growth, ShortageLoss, Stable, MinimumReached }
public sealed record PopulationSettlementReport(SimulationPosition Position, SemanticRevision ExecutionRevision,
    long Before, long After, long Change, double BasicSatisfaction, double ImprovementSatisfaction,
    PopulationSettlementReason Reason);
public sealed record PopulationView(RuleStatusView CurrentRule, long Residents,
    double AvailableLabor, double AllocatedLabor, double IdleLabor, PopulationExecution Execution,
    PopulationSettlementReport? LastSettlement, ImmutableArray<PopulationSettlementReport> RecentSettlements);
public sealed record SettlementFoundingPreview(NodeId Node, long InitialResidents,
    ImmutableArray<GoodsRateView> ExpectedConsumption, ImmutableArray<GoodsQuantity> RequiredMaterials,
    ulong DurationTicks, ImmutableArray<CoreProblem> Blockers);

public abstract record JobView
{
    private JobView() { }
    public sealed record Construction(ConstructionJobView Value) : JobView;
    public sealed record Railway(EdgeId Edge, RailwayWorkView Value) : JobView;
    public sealed record Founding(FoundingJobView Value) : JobView;
}

public sealed record FoundingJobView(JobId Id, NodeId Node, ulong FrozenDurationTicks,
    ulong ElapsedTicks, SettlementId ReservedSettlementId, SettlementTemplateId SourceTemplateId,
    ImmutableArray<GoodsQuantity> InvestedGoods);

public enum ConstructionKind { Construction, Refit, Repair, Deconstruction }
public enum ConstructionState { Queued, Working, ResourceBlocked, Paused, Failed, Completed }
public abstract record ConstructionTarget
{
    private ConstructionTarget() { }
    public sealed record NewVehicle(DesignHash Design) : ConstructionTarget;
    public sealed record NewBuilding(PlotId Plot, DesignHash Design) : ConstructionTarget;
    public sealed record NewAssembly(DesignHash Design, CompositionHash Composition, InventoryId Destination) : ConstructionTarget;
    public sealed record Existing(InstanceId Instance, DesignHash TargetDesign) : ConstructionTarget;
}
public sealed record ConstructionJobView(JobId Id, ConstructionKind Kind, SettlementId Settlement,
    FacilityKey Facility, ConstructionTarget Target, ConstructionState State, int Priority,
    long FrozenTotalWork, double ProgressWork, double Progress,
    double ActualWorkPerSecond, ImmutableArray<WorkLimit> Limits,
    ImmutableArray<GoodsQuantity> InvestedGoods, ImmutableArray<CoreProblem> Blockers);

public abstract record CombatUnitId
{
    private CombatUnitId() { }
    public sealed record Instance(InstanceId Id) : CombatUnitId;
    public sealed record CreatureGroup(CreatureGroupId Id) : CombatUnitId;
}
public sealed record CombatParticipantView(CombatUnitId Unit, bool Retreating,
    ulong RemainingDisengagementSteps, CombatUnitId? FocusTarget);
public sealed record CombatView(CombatId Id, WorldLocation Location, ulong StartedAtStep,
    ImmutableArray<CombatParticipantView> Participants);
public enum CreatureActivity { Guarding, Wandering, RaidingSettlement, RaidingRailway, Combat, Retreating }
public sealed record CreatureView(CreatureId Id, CreatureDefinitionId Definition, long Health, long MaximumHealth);
public sealed record CreatureGroupView(CreatureGroupId Id, LairId? Home, WorldLocation Location,
    TravelDirection? Direction, CreatureActivity Activity, double SpeedMetersPerSecond, ImmutableArray<CreatureView> Creatures);
public sealed record LairView(LairId Id, NodeId Node, LairTypeId Type, ImmutableArray<CreatureGroupId> Groups);

public sealed record MarketGoodView(GoodId Good, long Stock, long RestockTarget, long ReservedForUse,
    long SellableQuantity, long ReceivableQuantity, ImmutableArray<CoreProblem> Blockers);
public sealed record MarketView(RuleStatusView Rule, SettlementStorageView Storage, ImmutableArray<MarketGoodView> Goods);
public sealed record TradeQuoteRequest(TrainId Train, SettlementId Settlement, InventoryId LocalInventory,
    TradeAction Action, GoodId Good, long Quantity);
public sealed record TradeQuote(TradeQuoteRequest Request, CoreRevision Revision, long CurrentlyExecutableQuantity,
    Money? Total, ImmutableArray<CoreProblem> Blockers, ImmutableArray<CoreProblem> ShortageWarnings);
public enum VehicleTradeAction { Buy, Sell }
public sealed record VehicleTradeQuoteRequest(SettlementId Settlement, VehicleId Vehicle, VehicleTradeAction Action);
public sealed record VehicleTradeQuote(VehicleTradeQuoteRequest Request, CoreRevision Revision,
    Money? Total, ImmutableArray<CoreProblem> Blockers);
public sealed record LeaseQuote(LeaseRequest Request, CoreRevision Revision, Money? Total,
    long CurrentlyRentableCapacity, ImmutableArray<CoreProblem> Blockers);
public sealed record JobQuote(JobRequest Request, JobFunding Funding, CoreRevision Revision,
    long TotalWork, ImmutableArray<GoodsQuantity> RequiredGoods,
    ImmutableArray<JobMaterialSourceView> SourceConsumption, ImmutableArray<GoodsQuantity> NpcPurchases,
    Money? MaterialPayment, Money? ServicePayment, Money? TotalPayment,
    ImmutableArray<CoreProblem> Blockers);
public sealed record JobMaterialSourceView(InventoryId Inventory, ImmutableArray<GoodsQuantity> Goods);
public sealed record AutomaticRuleResult(int StopIndex, int RuleIndex, TradeAction Action, GoodId Good,
    long RequestedQuantity, long CompletedQuantity, Money ActualAmount, ImmutableArray<CoreProblem> IncompleteReasons);
public sealed record RunAccumulatorView(SimulationPosition StartedAt, Money SalesReceipts, Money PurchasePayments,
    Money ServicePayments, long NetCashFlow, long TransportedUnits, double LoadedCapacityRatio,
    ImmutableArray<AutomaticRuleResult> IncompleteRules);
public enum RunEndReason { PlanCompleted, LoopCompleted, PlanReplaced, TrainDisbanded, AllVehiclesDestroyed }
public sealed record RunReport(TrainId Train, SimulationPosition EndedAt, RunEndReason Reason, RunAccumulatorView Totals);
public sealed record EconomyView(Money Wallet, RuleStatusView MarketRule, RuleStatusView PopulationRule,
    RuleStatusView ConstructionRule, Money UnassignedServicePayments, ImmutableArray<RunReport> RecentRunReports);
