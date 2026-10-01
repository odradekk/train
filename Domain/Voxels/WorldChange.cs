namespace Train.Domain.Voxels;

public enum WorldChangeKind
{
    Reset,
    SwitchConfiguration,
    Load,
    NewBlank,
    ReturnToWorkspace,
    ReturnToMainMenu,
    Quit,
    ReloadSaved
}

public enum WorldChangeChoice
{
    SaveThenSwitch,
    Discard,
    Cancel,
    SaveAsThenReload
}

public enum WorldChangeStatus
{
    Completed,
    Cancelled,
    ChoiceRequired,
    SaveFailed,
    CandidateFailed,
    Switching,
    Busy
}

public sealed record WorldChangeRequest(
    WorldChangeKind Kind,
    WorldChangeChoice? Choice,
    string? ConfigurationId = null,
    Guid? SaveId = null,
    string? FixtureId = null,
    VoxelWorldIdentity? BlankIdentity = null,
    string? SaveDisplayName = null);

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
