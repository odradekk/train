using System.Collections.Immutable;

namespace Train.Core.Contracts;

public sealed record EventEnvelope(SimulationPosition Position, int Ordinal, CoreEvent Event);

public abstract record CoreEvent
{
    private CoreEvent() { }

    public sealed record TrainArrived(TrainId Train, NodeId Node) : CoreEvent;
    public sealed record TrainActivityChanged(TrainId Train, TrainActivity Before,
        TrainActivity After, TrainPosition Position, ImmutableArray<CoreProblem> Reasons) : CoreEvent;
    public sealed record RunReported(RunReport Report) : CoreEvent;
    public sealed record RetreatCompleted(TrainId Train, NodeId Node) : CoreEvent;
    public sealed record TrainCompositionChanged(TrainId Train, ImmutableArray<VehicleId> Vehicles) : CoreEvent;
    public sealed record RailBroken(EdgeId Edge, TrackBreakView Break) : CoreEvent;
    public sealed record RailwayWorkStarted(EdgeId Edge, RailwayWorkView Work) : CoreEvent;
    public sealed record RailRepaired(EdgeId Edge, BreakId Break, int Track) : CoreEvent;
    public sealed record RailExpanded(EdgeId Edge, int Tracks) : CoreEvent;
    public sealed record RailwayWorkChanged(EdgeId Edge, JobId Job, ImmutableArray<CoreProblem> Reasons) : CoreEvent;

    public sealed record ProductionCompleted(FacilityKey Facility, ProcessRef Process,
        SemanticRevision StartedFromRevision, ImmutableArray<GoodsQuantity> Outputs, bool Buffered) : CoreEvent;
    public sealed record ProductionStateChanged(FacilityKey Facility, ProductionState Before,
        ProductionState After, ImmutableArray<CoreProblem> Reasons) : CoreEvent;
    public sealed record ResourceExhausted(NodeId Node, GoodId Good, bool Renewable) : CoreEvent;
    public sealed record ResourceRegenerated(NodeId Node, GoodId Good, long Quantity) : CoreEvent;
    public sealed record ResourceRecovered(NodeId Node, GoodId Good, long Quantity) : CoreEvent;
    public sealed record PopulationSettled(SettlementId Settlement, PopulationSettlementReport Report) : CoreEvent;
    public sealed record PopulationShortageChanged(SettlementId Settlement,
        PopulationNeed Need, bool Shortage) : CoreEvent;
    public sealed record PopulationMinimumReached(SettlementId Settlement, long Residents) : CoreEvent;
    public sealed record PopulationAvailabilityChanged(SettlementId Settlement,
        bool Available, ImmutableArray<CoreProblem> Reasons) : CoreEvent;

    public sealed record GoodsTransferred(TradeAction Action, GoodId Good, long Quantity,
        InventoryId Source, InventoryId Destination, Money ActualAmount, TrainId Train,
        SettlementId Settlement) : CoreEvent;
    public sealed record AutomaticTradeEvaluated(TrainId Train, NodeId Node,
        ImmutableArray<AutomaticRuleResult> Results) : CoreEvent;
    public sealed record VehicleTraded(VehicleId Vehicle, SettlementId Settlement,
        VehicleTradeAction Action, Money ActualAmount) : CoreEvent;
    public sealed record LeaseAcquired(LeaseView Lease, Money ActualPayment, bool Renewal) : CoreEvent;
    public sealed record LeaseExpired(LeaseId Lease, SettlementId Settlement, InventoryId Inventory) : CoreEvent;
    public sealed record MarketAvailabilityChanged(bool Available, ImmutableArray<CoreProblem> Reasons) : CoreEvent;

    public sealed record ConstructionStarted(ConstructionJobView Job, JobFunding Funding,
        Money MaterialPayment, Money ServicePayment) : CoreEvent;
    public sealed record ConstructionStateChanged(JobId Job, ConstructionState Before,
        ConstructionState After, ImmutableArray<CoreProblem> Reasons) : CoreEvent;
    public sealed record ConstructionCompleted(JobId Job, ConstructionKind Kind,
        ConstructionTarget Target, InstanceId? CreatedInstance, ImmutableArray<GoodsQuantity> ReturnedGoods) : CoreEvent;
    public sealed record InstanceDesignChanged(InstanceId Instance, DesignHash Before, DesignHash After) : CoreEvent;
    public sealed record SettlementFounded(SettlementId Settlement, NodeId Node, long Residents) : CoreEvent;
    public sealed record SettlementRelationChanged(SettlementId Settlement,
        SettlementRelation Before, SettlementRelation After) : CoreEvent;

    public sealed record CombatStarted(CombatId Combat, WorldLocation Location,
        ImmutableArray<CombatUnitId> Participants) : CoreEvent;
    public sealed record CombatJoined(CombatId Combat, CombatUnitId Participant) : CoreEvent;
    public sealed record CombatHits(CombatId Combat, WorldLocation Location,
        ImmutableArray<CombatHit> Hits) : CoreEvent;
    public sealed record CombatDestruction(CombatId Combat, WorldLocation Location,
        ImmutableArray<DestroyedPart> Parts, ImmutableArray<DestroyedUnit> Units) : CoreEvent;
    public sealed record CombatRetreatStarted(CombatId Combat, CombatUnitId Unit, ulong DisengagementSteps) : CoreEvent;
    public sealed record SettlementSurrendered(CombatId Combat, SettlementId Settlement) : CoreEvent;
    public sealed record CombatEnded(CombatId Combat, WorldLocation Location, CombatSummary Summary) : CoreEvent;
    public sealed record LairCreated(LairId Lair, NodeId Node, LairTypeId Type) : CoreEvent;
    public sealed record LairCleared(LairId Lair, NodeId Node, Money Reward) : CoreEvent;
}

public sealed record InstancePresentationRef(InstanceId Instance, DesignHash Design, WorldLocation Location);
public sealed record CreaturePresentationRef(CreatureGroupId Group, CreatureId Creature,
    CreatureDefinitionId Definition, WorldLocation Location);
public abstract record AttackSource
{
    private AttackSource() { }
    public sealed record Weapon(InstancePresentationRef Instance, PartId WeaponPart) : AttackSource;
    public sealed record Creature(CreaturePresentationRef Individual) : AttackSource;
}
public abstract record HitTarget
{
    private HitTarget() { }
    public sealed record Structure(InstancePresentationRef Instance) : HitTarget;
    public sealed record Part(InstancePresentationRef Instance, PartId HitPart) : HitTarget;
    public sealed record Creature(CreaturePresentationRef Individual) : HitTarget;
}
public sealed record CombatHit(AttackSource Source, HitTarget Target, long Damage);
public sealed record DestroyedPart(InstancePresentationRef Instance, PartId Part);
public abstract record DestroyedUnit
{
    private DestroyedUnit() { }
    public sealed record Instance(InstancePresentationRef Value) : DestroyedUnit;
    public sealed record Creature(CreaturePresentationRef Value) : DestroyedUnit;
}

public abstract record CombatOwner
{
    private CombatOwner() { }
    public sealed record Player : CombatOwner;
    public sealed record Settlement(SettlementId Id) : CombatOwner;
    public sealed record Creatures : CombatOwner;
}

public sealed record CombatSideSummary(CombatOwner Owner, ImmutableArray<DestroyedUnit> UnitsLost,
    ImmutableArray<DestroyedPart> PartsLost, ImmutableArray<GoodsQuantity> CargoLost, Money Reward);

public sealed record CombatSummary(ulong StartedAtStep, ulong EndedAtStep,
    ImmutableArray<CombatSideSummary> Sides);
