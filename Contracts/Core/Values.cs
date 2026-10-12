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

public static class SimulationTime
{
    public const int StepsPerSecond = 60;
    public const double StepSeconds = 1.0 / StepsPerSecond;
}

public readonly record struct Money(long Value);
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
    public sealed record Edge(EdgeId Id, int Track, TravelDirection Direction,
        double DistanceMeters) : TrainPosition;
}

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
