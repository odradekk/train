using System.Collections.Immutable;

namespace Train.Core.Contracts;

public abstract record CoreCommand
{
    private CoreCommand() { }
    public sealed record FormTrain(ImmutableArray<VehicleId> OrderedVehicles) : CoreCommand;
    public sealed record DisbandTrain(TrainId Train) : CoreCommand;
    public sealed record ReplaceRunPlan(TrainId Train, RunPlan Plan) : CoreCommand;
    public sealed record StartTrain(TrainId Train) : CoreCommand;
    public sealed record StopTrain(TrainId Train) : CoreCommand;
    public sealed record ExpandRail(EdgeId Edge, SettlementId MaterialSource) : CoreCommand;
    public sealed record RepairRail(EdgeId Edge, BreakId Break, SettlementId MaterialSource) : CoreCommand;
    public sealed record SetAutomaticRailRepair(EdgeId Edge, SettlementId? MaterialSource) : CoreCommand;

    public sealed record BuyGoods(TrainId Train, SettlementId Settlement, InventoryId LocalInventory,
        GoodId Good, long Quantity, Money MaximumPayment) : CoreCommand;
    public sealed record SellGoods(TrainId Train, SettlementId Settlement, InventoryId LocalInventory,
        GoodId Good, long Quantity, Money MinimumReceipt) : CoreCommand;
    public sealed record LoadGoods(TrainId Train, SettlementId Settlement, InventoryId LocalInventory,
        GoodId Good, long Quantity) : CoreCommand;
    public sealed record UnloadGoods(TrainId Train, SettlementId Settlement, InventoryId LocalInventory,
        GoodId Good, long Quantity) : CoreCommand;
    public sealed record BuyVehicle(SettlementId Seller, VehicleId Vehicle, Money MaximumPayment) : CoreCommand;
    public sealed record SellVehicle(SettlementId Buyer, VehicleId Vehicle, Money MinimumReceipt) : CoreCommand;
    public sealed record AcquireLease(LeaseRequest Request, Money MaximumPayment) : CoreCommand;

    public sealed record SetProductionPlan(FacilityKey Facility, ProcessRef Process,
        bool Enabled, int Priority) : CoreCommand;
    public sealed record StartOwnedJob(JobRequest Request) : CoreCommand;
    public sealed record CommissionVehicleWork(JobRequest Request, Money MaximumPayment) : CoreCommand;
    public sealed record SetJobPriority(JobId Job, int Priority) : CoreCommand;
    public sealed record SetDeconstructionPaused(JobId Job, bool Paused) : CoreCommand;
    public sealed record ApplyInstanceDesign(InstanceId Instance, DesignChangeProjection Change) : CoreCommand;

    public sealed record FoundSettlement(NodeId Node, TrainId MaterialSource) : CoreCommand;
    public sealed record DeclareWar(SettlementId Settlement) : CoreCommand;
    public sealed record OccupySettlement(SettlementId Settlement, TrainId PresentTrain) : CoreCommand;
    public sealed record SetTrainEngagement(TrainId Train, TrainEngagementRules Rules) : CoreCommand;
    public sealed record SetSettlementEngagement(SettlementId Settlement, SettlementEngagementRules Rules) : CoreCommand;
    public sealed record Retreat(TrainId Train) : CoreCommand;
    public sealed record FocusFire(CombatId Combat, CombatController Controller, WorldObjectId Target) : CoreCommand;

    public sealed record PublishContent(CoreContentProjection Content) : CoreCommand;
    public sealed record DebugAddRailBreak(EdgeId Edge, int Track, double DistanceMeters) : CoreCommand;
}

public abstract record CombatController
{
    private CombatController() { }
    public sealed record Train(TrainId Id) : CombatController;
    public sealed record Settlement(SettlementId Id) : CombatController;
}

public abstract record LeaseRequest
{
    private LeaseRequest() { }
    public sealed record Rent(SettlementId Settlement, long Capacity, ulong DurationSteps) : LeaseRequest;
    public sealed record Renew(LeaseId Lease, ulong DurationSteps) : LeaseRequest;
}

public abstract record JobRequest
{
    private JobRequest() { }
    public sealed record BuildVehicle(FacilityKey Facility, DesignInput Design,
        ImmutableArray<InventoryId> Sources, int Priority) : JobRequest;
    public sealed record BuildBuilding(FacilityKey Facility, PlotId Plot, DesignInput Design,
        ImmutableArray<InventoryId> Sources, int Priority) : JobRequest;
    public sealed record BuildEquipment(FacilityKey Facility, DesignInput Design,
        ImmutableArray<InventoryId> Sources, int Priority) : JobRequest;
    public sealed record Refit(FacilityKey Facility, InstanceId Instance, DesignChangeProjection Change,
        ImmutableArray<InventoryId> Sources, int Priority) : JobRequest;
    public sealed record Repair(FacilityKey Facility, InstanceId Instance,
        ImmutableArray<InventoryId> Sources, int Priority) : JobRequest;
    public sealed record Dismantle(FacilityKey Facility, InstanceId Instance, int Priority) : JobRequest;
}

public enum JobFunding { OwnInventory, NpcCommission }
public enum CommandSource { UserInterface, Console, AutomationTest }
public abstract record CommandDecision
{
    private CommandDecision() { }
    public sealed record Accepted(ImmutableArray<WorldObjectId> Created,
        ImmutableArray<OwnedSettlementTemplateReference> SettlementCopies) : CommandDecision;
    public sealed record Rejected(ImmutableArray<CoreProblem> Problems) : CommandDecision;
    public sealed record Faulted(CoreFault Fault) : CommandDecision;
}

public sealed record CommandReceipt(ulong Number, SimulationPosition Before, SimulationPosition After,
    CommandDecision Decision);

public abstract record SubmissionResult
{
    private SubmissionResult() { }
    public sealed record NotAccepted(CoreProblem Reason) : SubmissionResult;
    public sealed record Completed(CommandReceipt Receipt, ImmutableArray<EventEnvelope> Events,
        CoreDiagnostic? Diagnostic) : SubmissionResult;
}
