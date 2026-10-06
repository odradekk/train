using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;
using Train.Domain.Voxels;

namespace Train.Domain.Composites;

public enum CompositeDamageStatus
{
    Damaged = 0,
    Removed = 1,
    InvalidDamage = 2,
    Empty = 3,
    UnknownVehicle = 4,
    TransactionExhausted = 5,
    EntityIdExhausted = 6,
    InputExhausted = 7,
    SimulationFaulted = 8,
    WorldChanging = 9,
    HistoryConfirmationRequired = 10
}

public readonly record struct CompositeDamageTarget(EntityId VehicleId, LocalVoxelCoord Cell, int Amount);

public sealed record CompositeDamageTargetResult(
    CompositeDamageStatus Status,
    EntityId? VehicleId,
    LocalVoxelCoord Cell,
    int Amount,
    int RemainingDurability,
    EntityId? PartId = null,
    WorldVoxelCoord? WorldCell = null);

public sealed record CompositeDamageResult(
    CompositeDamageStatus Status,
    EntityId? VehicleId = null,
    IReadOnlyList<CompositeDamageTargetResult>? Targets = null)
{
    public bool Succeeded => Status is CompositeDamageStatus.Damaged or CompositeDamageStatus.Removed;
}

public sealed record CompositeDamagedPartQuery(
    EntityId VehicleId,
    EntityId PartId,
    LocalVoxelCoord Cell,
    VoxelDefinitionId DefinitionId,
    VoxelOrientation Orientation,
    int CurrentDurability,
    int MaxDurability);
