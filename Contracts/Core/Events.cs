// 核心契约的已提交领域事件，随一次命令或推进调用返回。
// 事件保存瞬时表现所需的身份和资料，持续状态仍由读视图提供。
using System.Collections.Immutable;

namespace Train.Core.Contracts;

/// <summary>本次提交或推进产生的事件；Ordinal 为同一 Position 内从零起的顺序，多步批次按 Position、Ordinal 排序，宿主负责分发。</summary>
public sealed record EventEnvelope(SimulationPosition Position, int Ordinal, CoreEvent Event);

/// <summary>已提交领域事实，不含订阅、通知设置或消费者游标；持续状态从读视图恢复。</summary>
public abstract record CoreEvent
{
    private CoreEvent() { }

    /// <summary>列车到达节点；可用于跟随场景转换。</summary>
    public sealed record TrainArrived(TrainId Train, NodeId Node) : CoreEvent;
    /// <summary>列车活动改变，避免每步重复发送位置全量。</summary>
    public sealed record TrainActivityChanged(TrainId Train, TrainActivity Before,
        TrainActivity After, TrainPosition Position, ImmutableArray<CoreProblem> Reasons) : CoreEvent;
    /// <summary>单次完成或循环一圈已经归集，不由 UI 再次结算。</summary>
    public sealed record RunReported(RunReport Report) : CoreEvent;
    /// <summary>撤退到达安全地点并停车，须重新启动。</summary>
    public sealed record RetreatCompleted(TrainId Train, NodeId Node) : CoreEvent;
    /// <summary>编组有序成员变化，包括战斗中车辆被摧毁。</summary>
    public sealed record TrainCompositionChanged(TrainId Train, ImmutableArray<VehicleId> Vehicles) : CoreEvent;
    /// <summary>真实断点新增，普通战斗本身不产生断点。</summary>
    public sealed record RailBroken(EdgeId Edge, TrackBreakView Break) : CoreEvent;
    /// <summary>铁路施工已经接受并投入材料。</summary>
    public sealed record RailwayWorkStarted(EdgeId Edge, RailwayWorkView Work) : CoreEvent;
    /// <summary>断点修复完成。</summary>
    public sealed record RailRepaired(EdgeId Edge, BreakId Break, int Track) : CoreEvent;
    /// <summary>线路建成或扩建完成后的铁轨数。</summary>
    public sealed record RailExpanded(EdgeId Edge, int Tracks) : CoreEvent;
    /// <summary>线路施工的阻塞或失败原因发生变化。</summary>
    public sealed record RailwayWorkChanged(EdgeId Edge, JobId Job, ImmutableArray<CoreProblem> Reasons) : CoreEvent;

    /// <summary>批次完成的冻结产物已经产生，是否成功入库由 Buffered 表示。</summary>
    public sealed record ProductionCompleted(FacilityKey Facility, ProcessRef Process,
        SemanticRevision StartedFromRevision, ImmutableArray<GoodsQuantity> Outputs, bool Buffered) : CoreEvent;
    /// <summary>生产状态改变，包括出料阻塞与恢复；无逐步进度事件。</summary>
    public sealed record ProductionStateChanged(FacilityKey Facility, ProductionState Before,
        ProductionState After, ImmutableArray<CoreProblem> Reasons) : CoreEvent;
    /// <summary>资源降到零；Renewable 为真时资源项保留。</summary>
    public sealed record ResourceExhausted(NodeId Node, GoodId Good, bool Renewable) : CoreEvent;
    /// <summary>全局再生事件新增真实资源，含新增数量。</summary>
    public sealed record ResourceRegenerated(NodeId Node, GoodId Good, long Quantity) : CoreEvent;
    /// <summary>可再生资源从零恢复为可用，不按每单位恢复发送事件。</summary>
    public sealed record ResourceRecovered(NodeId Node, GoodId Good, long Quantity) : CoreEvent;
    /// <summary>每个已结算人口统计期一个摘要；常规摘要不自动成为弹窗。</summary>
    public sealed record PopulationSettled(SettlementId Settlement, PopulationSettlementReport Report) : CoreEvent;
    /// <summary>生活需求短缺开始或恢复，使用核心认定的需求种类。</summary>
    public sealed record PopulationShortageChanged(SettlementId Settlement,
        PopulationNeed Need, bool Shortage) : CoreEvent;
    /// <summary>首次降至最低人口，不重复发送每期下限事件。</summary>
    public sealed record PopulationMinimumReached(SettlementId Settlement, long Residents) : CoreEvent;
    /// <summary>缺失导致停用或内容恢复后接续，区别于生活物资短缺。</summary>
    public sealed record PopulationAvailabilityChanged(SettlementId Settlement,
        bool Available, ImmutableArray<CoreProblem> Reasons) : CoreEvent;

    /// <summary>实际原子货物成交或免费转移；金额为零不代表销售收入。</summary>
    public sealed record GoodsTransferred(TradeAction Action, GoodId Good, long Quantity,
        InventoryId Source, InventoryId Destination, Money ActualAmount, TrainId Train,
        SettlementId Settlement) : CoreEvent;
    /// <summary>一次自动评估的实际结果与未完成原因；不修改规则动作。</summary>
    public sealed record AutomaticTradeEvaluated(TrainId Train, NodeId Node,
        ImmutableArray<AutomaticRuleResult> Results) : CoreEvent;
    /// <summary>现车交易保留同一实例、设计和损伤。</summary>
    public sealed record VehicleTraded(VehicleId Vehicle, SettlementId Settlement,
        VehicleTradeAction Action, Money ActualAmount) : CoreEvent;
    /// <summary>租约新签或续签后的合同及本次费用。</summary>
    public sealed record LeaseAcquired(LeaseView Lease, Money ActualPayment, bool Renewal) : CoreEvent;
    /// <summary>租约到期只取不存，实际占位由库存查询给出。</summary>
    public sealed record LeaseExpired(LeaseId Lease, SettlementId Settlement, InventoryId Inventory) : CoreEvent;
    /// <summary>市场缺规则停用或恢复；不延长租期或追加旧停站交易。</summary>
    public sealed record MarketAvailabilityChanged(bool Available, ImmutableArray<CoreProblem> Reasons) : CoreEvent;

    /// <summary>施工已原子受理；已消耗材料和已付费由报价及任务查询核对。</summary>
    public sealed record ConstructionStarted(ConstructionJobView Job, JobFunding Funding,
        Money MaterialPayment, Money ServicePayment) : CoreEvent;
    /// <summary>施工进度暂停、阻塞、恢复或失败等状态变化。</summary>
    public sealed record ConstructionStateChanged(JobId Job, ConstructionState Before,
        ConstructionState After, ImmutableArray<CoreProblem> Reasons) : CoreEvent;
    /// <summary>施工提交完成；CreatedInstance 为空时产出为货物或修改已有实例。</summary>
    public sealed record ConstructionCompleted(JobId Job, ConstructionKind Kind,
        ConstructionTarget Target, InstanceId? CreatedInstance, ImmutableArray<GoodsQuantity> ReturnedGoods) : CoreEvent;
    /// <summary>不改变构成的设计更新或改装完成后设计切换。</summary>
    public sealed record InstanceDesignChanged(InstanceId Instance, DesignHash Before, DesignHash After) : CoreEvent;
    /// <summary>聚居地创建完成，人口从成立时开始消费。</summary>
    public sealed record SettlementFounded(SettlementId Settlement, NodeId Node, long Residents) : CoreEvent;
    /// <summary>宣战、征服等关系变化，不隐式改写交易规则。</summary>
    public sealed record SettlementRelationChanged(SettlementId Settlement,
        SettlementRelation Before, SettlementRelation After) : CoreEvent;

    /// <summary>战斗开始的位置与最初参战者，可跳转到该地点。</summary>
    public sealed record CombatStarted(CombatId Combat, WorldLocation Location,
        ImmutableArray<CombatUnitId> Participants) : CoreEvent;
    /// <summary>新单位加入已经存在的战斗。</summary>
    public sealed record CombatJoined(CombatId Combat, CombatUnitId Participant) : CoreEvent;
    /// <summary>同一步射击与命中，表现引用为值快照，目标在本步删除后仍可消费。</summary>
    public sealed record CombatHits(CombatId Combat, WorldLocation Location,
        ImmutableArray<CombatHit> Hits) : CoreEvent;
    /// <summary>部件或单位被摧毁后的表现信息，不要求重新查询已删除对象。</summary>
    public sealed record CombatDestruction(CombatId Combat, WorldLocation Location,
        ImmutableArray<DestroyedPart> Parts, ImmutableArray<DestroyedUnit> Units) : CoreEvent;
    /// <summary>参战者开始脱离期间不开火但仍可被攻击。</summary>
    public sealed record CombatRetreatStarted(CombatId Combat, CombatUnitId Unit, ulong DisengagementSteps) : CoreEvent;
    /// <summary>聚居地投降，战斗结束后是否可占领由查询与命令裁决。</summary>
    public sealed record SettlementSurrendered(CombatId Combat, SettlementId Settlement) : CoreEvent;
    /// <summary>战斗结束，不包含可持久化的逐次战报历史。</summary>
    public sealed record CombatEnded(CombatId Combat, WorldLocation Location, CombatSummary Summary) : CoreEvent;
    /// <summary>生物聚落生成及其地点。</summary>
    public sealed record LairCreated(LairId Lair, NodeId Node, LairTypeId Type) : CoreEvent;
    /// <summary>清剿移除占据者并支付奖励，在外生物群随后失去归属。</summary>
    public sealed record LairCleared(LairId Lair, NodeId Node, Money Reward) : CoreEvent;
}

/// <summary>瞬时表现所需实例设计和地点，删除后仍是有效值。</summary>
public sealed record InstancePresentationRef(InstanceId Instance, DesignHash Design, WorldLocation Location);
/// <summary>瞬时表现所需生物定义、设计、个体和群身份。</summary>
public sealed record CreaturePresentationRef(CreatureGroupId Group, CreatureId Creature,
    CreatureDefinitionId Definition, DesignHash Design, WorldLocation Location);
/// <summary>射击者精确到武器部件或生物个体。</summary>
public abstract record AttackSource
{
    private AttackSource() { }
    public sealed record Weapon(InstancePresentationRef Instance, PartId WeaponPart) : AttackSource;
    public sealed record Creature(CreaturePresentationRef Individual) : AttackSource;
}
/// <summary>命中落点精确到结构、部件或生物个体，不泄露布局。</summary>
public abstract record HitTarget
{
    private HitTarget() { }
    public sealed record Structure(InstancePresentationRef Instance) : HitTarget;
    public sealed record Part(InstancePresentationRef Instance, PartId HitPart) : HitTarget;
    public sealed record Creature(CreaturePresentationRef Individual) : HitTarget;
}
/// <summary>一次必中攻击的名义伤害；本步聚合结算后的持续耐久以读视图为准。</summary>
public sealed record CombatHit(AttackSource Source, HitTarget Target, long Damage);
/// <summary>被毁部件及设计引用。</summary>
public sealed record DestroyedPart(InstancePresentationRef Instance, PartId Part);
/// <summary>被删除单位的表现快照。</summary>
public abstract record DestroyedUnit
{
    private DestroyedUnit() { }
    public sealed record Instance(InstancePresentationRef Value) : DestroyedUnit;
    public sealed record Creature(CreaturePresentationRef Value) : DestroyedUnit;
}

/// <summary>摘要中的原所属方，单位删除后仍能确定损失归属。</summary>
public abstract record CombatOwner
{
    private CombatOwner() { }
    public sealed record Player : CombatOwner;
    public sealed record Settlement(SettlementId Id) : CombatOwner;
    public sealed record Creatures : CombatOwner;
}

/// <summary>一方的已结算损失与奖励，不要求宿主遍历事件重新算账。</summary>
public sealed record CombatSideSummary(CombatOwner Owner, ImmutableArray<DestroyedUnit> UnitsLost,
    ImmutableArray<DestroyedPart> PartsLost, ImmutableArray<GoodsQuantity> CargoLost, Money Reward);

/// <summary>战斗起止步与各方损失，供本次运行的战报保留。</summary>
public sealed record CombatSummary(ulong StartedAtStep, ulong EndedAtStep,
    ImmutableArray<CombatSideSummary> Sides);
