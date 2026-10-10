// 核心契约共有的身份和值；有效范围和规范编码见 docs/contracts/core.md。
using System.Collections.Immutable;

namespace Train.Core.Contracts;

public readonly record struct NodeId(ulong Value);
public readonly record struct EdgeId(ulong Value);
public readonly record struct TrainId(ulong Value);
public readonly record struct VehicleId(ulong Value);
public readonly record struct BuildingId(ulong Value);
public readonly record struct SettlementId(ulong Value);
public readonly record struct PlotId(ulong Value);
public readonly record struct InventoryId(ulong Value);
public readonly record struct JobId(ulong Value);
public readonly record struct LeaseId(ulong Value);
public readonly record struct CombatId(ulong Value);
public readonly record struct CreatureId(ulong Value);
public readonly record struct CreatureGroupId(ulong Value);
public readonly record struct LairId(ulong Value);
public readonly record struct BreakId(ulong Value);
public readonly record struct PartId(int Value);
public readonly record struct VoxelDefinitionId(string Value);
public readonly record struct CompositeDefinitionId(string Value);
public readonly record struct RuleDefinitionId(string Value);
public readonly record struct SettlementTypeId(string Value);
public readonly record struct LairTypeId(string Value);
public readonly record struct DesignHash(string Value);
public readonly record struct CompositionHash(string Value);
public readonly record struct SemanticRevision(string Value);
public readonly record struct CoreInputId(string Value);
public readonly record struct SimulationPosition(ulong CompletedSteps, ulong Sequence);
public readonly record struct CoreRevision(ulong Value);
public readonly record struct RecordingPointId(ulong Value);

/// <summary>游戏构建固定的模拟时间单位；宿主倍速只改变实际推进步数，不改变本常量。</summary>
public static class SimulationTime
{
    public const int StepsPerSecond = 60;
    public const double StepSeconds = 1.0 / StepsPerSecond;
}

/// <summary>最小货币单位的非负整数；不表示带有小数的中间估值。</summary>
public readonly record struct Money(long Value);
/// <summary>整数万分比；10000 表示 1 倍。具体字段另有限定范围。</summary>
public readonly record struct BasisPoints(int Value);

public abstract record GoodId
{
    private GoodId() { }
    public sealed record Voxel(VoxelDefinitionId Definition) : GoodId;
    public sealed record Composite(CompositeDefinitionId Definition) : GoodId;
    public sealed record Assembly(CompositionHash Composition) : GoodId;
}

public abstract record InstanceId
{
    private InstanceId() { }
    public sealed record Vehicle(VehicleId Id) : InstanceId;
    public sealed record Building(BuildingId Id) : InstanceId;
}

/// <summary>跨领域诊断和选择所需的带种类身份；业务入口仍使用具体身份。</summary>
public abstract record WorldObjectId
{
    private WorldObjectId() { }
    public sealed record Node(NodeId Id) : WorldObjectId;
    public sealed record Edge(EdgeId Id) : WorldObjectId;
    public sealed record Train(TrainId Id) : WorldObjectId;
    public sealed record Instance(InstanceId Id) : WorldObjectId;
    public sealed record Settlement(SettlementId Id) : WorldObjectId;
    public sealed record Inventory(InventoryId Id) : WorldObjectId;
    public sealed record Job(JobId Id) : WorldObjectId;
    public sealed record Combat(CombatId Id) : WorldObjectId;
    public sealed record Creature(CreatureId Id) : WorldObjectId;
    public sealed record CreatureGroup(CreatureGroupId Id) : WorldObjectId;
    public sealed record Lair(LairId Id) : WorldObjectId;
}

public readonly record struct MapPoint(double X, double Y);
public enum TravelDirection { Forward, Reverse }
public enum SettlementRelation { PlayerOwned, NonHostile, Hostile }
public enum CapabilityKind { Production, Extraction, Construction }
public enum SizeClass { Small, Medium, Large, ExtraLarge, Titan }
public sealed record GoodsQuantity(GoodId Good, long Quantity);
public sealed record FacilityKey(BuildingId Building, CapabilityKind Capability);

/// <summary>节点或线路上的业务地点；不包含局部场景几何。</summary>
public abstract record WorldLocation
{
    private WorldLocation() { }
    public sealed record Node(NodeId Id) : WorldLocation;
    public sealed record Edge(EdgeId Id, double DistanceMeters) : WorldLocation;
}

public abstract record TrainPosition
{
    private TrainPosition() { }
    public sealed record Node(NodeId Id) : TrainPosition;
    /// <summary>DistanceMeters 为整列长度中心到线路起点的正向距离，范围为 [0, 线路长度]；倒车不改变中心。</summary>
    public sealed record Edge(EdgeId Id, int Track, TravelDirection Direction,
        double DistanceMeters) : TrainPosition;
}

/// <summary>稳定代码供调用方分支；详情只用于显示，不参与规则判断。</summary>
public sealed record CoreProblem(string Code, string Field, WorldObjectId? Subject,
    ImmutableArray<string> RelatedIdentities);

public abstract record Lookup<T> where T : notnull
{
    private Lookup() { }
    public sealed record Found(T Value) : Lookup<T>;
    public sealed record Missing : Lookup<T>;
}

public abstract record CoreResult<T> where T : notnull
{
    private CoreResult() { }
    public sealed record Success(T Value) : CoreResult<T>;
    public sealed record Failure(ImmutableArray<CoreProblem> Problems) : CoreResult<T>;
    public sealed record Faulted(CoreFault Fault, CoreDiagnostic? Diagnostic) : CoreResult<T>;
}
