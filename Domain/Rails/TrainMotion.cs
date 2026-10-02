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
    SimulationFaulted = 17,
    Pending = 18,
    InputExhausted = 19,
    UnallocatedReference = 20,
    WorldChanging = 21,
    HistoryConfirmationRequired = 22
}

/// <summary>A pending <c>train.start</c> or <c>train.stop</c> edit; <see cref="Sequence"/> is its edit id.</summary>
public sealed record ScheduledTrainCommandQuery(
    ulong AtStep,
    bool Start,
    ulong Sequence,
    EntityId? AssemblyId = null);

/// <summary>
/// <see cref="EditId"/> names the accepted <c>train.start</c>/<c>train.stop</c> edit; <see cref="TrainMotionStatus.Pending"/>
/// means it was queued for <see cref="AtStep"/> and its conditions are checked when it executes there.
/// </summary>
public sealed record TrainMotionResult(
    TrainMotionStatus Status,
    ulong SimulationStep = 0,
    ulong AdvancedSteps = 0,
    ulong AtStep = 0,
    ulong? EditId = null)
{
    public bool Succeeded => Status is
        TrainMotionStatus.Started or
        TrainMotionStatus.Stopped or
        TrainMotionStatus.Advanced or
        TrainMotionStatus.Paused or
        TrainMotionStatus.Resumed or
        TrainMotionStatus.Pending;
}
