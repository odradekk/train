using System.Globalization;
using Train.Domain.Voxels;

namespace Train.Domain.Commands;

/// <summary>A point in the global total order: the completed simulation step, then the in-step input sequence.</summary>
public readonly record struct SimulationPosition(ulong Step, ulong Sequence) : IComparable<SimulationPosition>
{
    public int CompareTo(SimulationPosition other)
    {
        var step = Step.CompareTo(other.Step);
        return step != 0 ? step : Sequence.CompareTo(other.Sequence);
    }

    public override string ToString() =>
        $"{Step.ToString(CultureInfo.InvariantCulture)}:{Sequence.ToString(CultureInfo.InvariantCulture)}";
}

public enum WorldEditState
{
    Pending = 0,
    Executed = 1,
    Rejected = 2,
    Faulted = 3
}

public enum WorldEditRefusal
{
    None = 0,
    PastStep = 1,
    InputExhausted = 2,
    SimulationFaulted = 3,
    UnallocatedReference = 4,
    WorldChanging = 5,
    HistoryConfirmationRequired = 6,
    VoxelDraftExists = 7,
    NoHistory = 8,
    SimulationRunning = 9,
    LargeEditConfirmationRequired = 10,
    RegionRejected = 11,
    DraftTargetsChanged = 12
}

/// <summary>
/// One accepted world edit. The accepted position orders inputs; the executed position is where the transaction
/// actually ran (equal to the accepted position for immediate edits, a later boundary for scheduled ones).
/// </summary>
public sealed record WorldEditRecord(
    ulong Id,
    WorldEdit Edit,
    SimulationPosition AcceptedAt,
    ulong TargetStep,
    WorldEditState State = WorldEditState.Pending,
    SimulationPosition? ExecutedAt = null,
    string? Status = null)
{
    public string Canonical => WorldEditCodec.EncodeRecord(this);
}

public sealed record WorldEditSubmission(WorldEditRefusal Refusal, WorldEditRecord? Record, object? Result)
{
    public VoxelRegionPreview? RegionPreview { get; init; }
    public bool Accepted => Refusal == WorldEditRefusal.None;

    /// <summary>Refusal name, <c>Pending</c>, the business status, or the fault type name.</summary>
    public string Status =>
        Refusal != WorldEditRefusal.None ? Refusal.ToString()
        : Record!.State == WorldEditState.Pending ? nameof(WorldEditState.Pending)
        : Record.Status ?? Record.State.ToString();
}

/// <summary>
/// An internal invariant failure: authority was rolled back to the last complete boundary and the session halts.
/// Edit id and kind are absent when the fault came from simulation motion rather than an edit.
/// </summary>
public sealed record SimulationFault(
    ulong? EditId,
    WorldEditKind? Kind,
    SimulationPosition Position,
    string ExceptionType,
    string Message);

/// <summary>Command state that belongs to the authoritative world and survives save and restore.</summary>
public sealed record WorldEditQueueState(
    ulong NextEditId,
    ulong BoundarySequence,
    IReadOnlyList<WorldEditRecord> Pending)
{
    public static WorldEditQueueState Initial { get; } = new(1, 0, Array.Empty<WorldEditRecord>());
}
