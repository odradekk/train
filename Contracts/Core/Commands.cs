// 核心契约中的完整业务请求；UI、控制台和测试提交同一种输入。
// 参数不含运行状态或布局；业务条件由核心在当前完整边界裁决。
using System.Collections.Immutable;

namespace Train.Core.Contracts;

/// <summary>原子提交的核心命令目录；调用方不能注册新的命令种类。</summary>
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

    /// <summary>手动足量交易或免费转移；买入限总付款，卖出限总收款，转移不带价格条件。</summary>
    public sealed record ExecuteGoodsTrade(TradeQuoteRequest Request,
        Money? MaximumPayment, Money? MinimumReceipt) : CoreCommand;
    public sealed record BuyVehicle(SettlementId Seller, VehicleId Vehicle, Money MaximumPayment) : CoreCommand;
    public sealed record SellVehicle(SettlementId Buyer, VehicleId Vehicle, Money MinimumReceipt) : CoreCommand;
    public sealed record AcquireLease(LeaseRequest Request, Money MaximumPayment) : CoreCommand;

    public sealed record SetProductionPlan(FacilityKey Facility, ProcessRef Process,
        bool Enabled, int Priority) : CoreCommand;
    public sealed record StartOwnedJob(JobRequest Request) : CoreCommand;
    /// <summary>只允许车辆建造、改装、修理；材料补购、工钱和排队一次受理。</summary>
    public sealed record CommissionVehicleWork(JobRequest Request, Money MaximumPayment) : CoreCommand;
    public sealed record SetJobPriority(JobId Job, int Priority) : CoreCommand;
    public sealed record SetDeconstructionPaused(JobId Job, bool Paused) : CoreCommand;
    /// <summary>内容已完整校验、构成未变；只换设计身份，不携带可覆盖实例的损伤数据。</summary>
    public sealed record ApplyInstanceDesign(InstanceId Instance, DesignChangeProjection Change) : CoreCommand;

    public sealed record FoundSettlement(NodeId Node, TrainId MaterialSource) : CoreCommand;
    public sealed record DeclareWar(SettlementId Settlement) : CoreCommand;
    public sealed record OccupySettlement(SettlementId Settlement, TrainId PresentTrain) : CoreCommand;
    public sealed record SetTrainEngagement(TrainId Train, TrainEngagementRules Rules) : CoreCommand;
    public sealed record SetSettlementEngagement(SettlementId Settlement, SettlementEngagementRules Rules) : CoreCommand;
    public sealed record Retreat(TrainId Train) : CoreCommand;
    public sealed record FocusFire(CombatId Combat, CombatController Controller, WorldObjectId Target) : CoreCommand;

    /// <summary>只可与固定的候选投影一起准备；不是让核心重新扫描文件。</summary>
    public sealed record PublishContent(CoreInputId Target) : CoreCommand;
    /// <summary>开发入口制造真实断点，仍经过普通命令记录和原子结算。</summary>
    public sealed record DebugAddRailBreak(EdgeId Edge, int Track, double DistanceMeters) : CoreCommand;
}

/// <summary>控制集中攻击的玩家列车或自有聚居地。</summary>
public abstract record CombatController
{
    private CombatController() { }
    public sealed record Train(TrainId Id) : CombatController;
    public sealed record Settlement(SettlementId Id) : CombatController;
}

/// <summary>新租约选择容量；续约沿用原额度。期限为正整数模拟步。</summary>
public abstract record LeaseRequest
{
    private LeaseRequest() { }
    public sealed record Rent(SettlementId Settlement, long Capacity, ulong DurationSteps) : LeaseRequest;
    public sealed record Renew(LeaseId Lease, ulong DurationSteps) : LeaseRequest;
}

/// <summary>任务所需的稳定目标与显式来源顺序；构成和 BOM 由内容投影提供。</summary>
public abstract record JobRequest
{
    private JobRequest() { }
    public sealed record BuildVehicle(FacilityKey Facility, DesignHash Design,
        ImmutableArray<InventoryId> Sources, int Priority) : JobRequest;
    public sealed record BuildBuilding(FacilityKey Facility, PlotId Plot, DesignHash Design,
        ImmutableArray<InventoryId> Sources, int Priority) : JobRequest;
    public sealed record BuildEquipment(FacilityKey Facility, DesignHash Design,
        ImmutableArray<InventoryId> Sources, int Priority) : JobRequest;
    public sealed record Refit(FacilityKey Facility, InstanceId Instance, DesignChangeProjection Change,
        ImmutableArray<InventoryId> Sources, int Priority) : JobRequest;
    public sealed record Repair(FacilityKey Facility, InstanceId Instance,
        ImmutableArray<InventoryId> Sources, int Priority) : JobRequest;
    public sealed record Dismantle(FacilityKey Facility, InstanceId Instance, int Priority) : JobRequest;
}

public enum JobFunding { OwnInventory, NpcCommission }
public enum CommandSource { UserInterface, Console, AutomationTest }
public enum CommandOutcome { Accepted, Rejected, Faulted }

/// <summary>命令前后位置及稳定结果；新建身份通过 Created 返回，无需搜索世界猜测。</summary>
public sealed record CommandReceipt(ulong Number, SimulationPosition Before, SimulationPosition After,
    CommandOutcome Outcome, ImmutableArray<CoreProblem> Problems,
    ImmutableArray<WorldObjectId> Created);

/// <summary>尚未接入的请求不取得命令编号；正常拒绝属于 Completed。</summary>
public abstract record SubmissionResult
{
    private SubmissionResult() { }
    public sealed record NotAccepted(CoreProblem Reason) : SubmissionResult;
    public sealed record Completed(CommandReceipt Receipt, ImmutableArray<EventEnvelope> Events,
        CoreFault? Fault, CoreDiagnostic? Diagnostic) : SubmissionResult;
}
