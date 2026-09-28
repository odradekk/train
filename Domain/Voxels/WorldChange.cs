namespace Train.Domain.Voxels;

public enum WorldChangeKind
{
    Reset,
    SwitchConfiguration,
    Load
}

public enum WorldChangeChoice
{
    SaveThenSwitch,
    Discard,
    Cancel
}

public enum WorldChangeStatus
{
    Completed,
    Cancelled,
    ChoiceRequired,
    SaveFailed,
    CandidateFailed
}

public sealed record WorldChangeRequest(
    WorldChangeKind Kind,
    WorldChangeChoice? Choice,
    string? ConfigurationId = null,
    Guid? SaveId = null,
    string? FixtureId = null);

public sealed record WorldChangeResult(
    WorldChangeStatus Status,
    WorldSaveStatus? SaveStatus = null,
    WorldSaveStatus? CandidateStatus = null,
    WorldSaveDiagnostic? Diagnostic = null,
    IReadOnlyList<WorldSaveAdaptation>? Adaptations = null,
    bool UnsavedChanges = false,
    string? FixtureStatus = null,
    ulong? FixtureVehicleId = null,
    int? FixturePartCount = null);
