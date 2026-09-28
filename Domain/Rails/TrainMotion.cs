using Train.Domain.Composites;

namespace Train.Domain.Rails;

public enum TrainMotionStatus
{
    Started = 0,
    Stopped = 1,
    Advanced = 2,
    Paused = 3,
    Resumed = 4,
    NoAssembly = 5,
    NoRailway = 6,
    ReverseUnsupported = 7,
    MissingPropulsion = 8,
    MissingControl = 9,
    InsufficientSupport = 10,
    DisconnectedPath = 11,
    VehicleConstraint = 12,
    StepOverflow = 13,
    TransactionExhausted = 14,
    PastStep = 15,
    UnknownAssembly = 16,
    SimulationFaulted = 17
}

public sealed record ScheduledTrainCommandQuery(
    ulong AtStep,
    bool Start,
    ulong Sequence,
    EntityId? AssemblyId = null);

public sealed record TrainMotionResult(
    TrainMotionStatus Status,
    ulong SimulationStep = 0,
    ulong AdvancedSteps = 0,
    ulong AtStep = 0)
{
    public bool Succeeded => Status is
        TrainMotionStatus.Started or
        TrainMotionStatus.Stopped or
        TrainMotionStatus.Advanced or
        TrainMotionStatus.Paused or
        TrainMotionStatus.Resumed;
}
