using Train.Content;
using Train.Domain.Composites;
using Train.Domain.WorldEditor;

namespace Train.Domain.Voxels;

public sealed partial class VoxelDevelopmentSession
{
    private string _cleanFingerprint = "";
    private bool _worldEditorHost;
    private WorldChangeRequest? _worldChange;
    private bool _changeNeedsChoice;
    private bool _changeNeedsSave;
    private WorldSaveStatus? _changeSaveStatus;
    private bool _discardHostDelta;
    private Task<(VoxelDevelopmentSession Candidate, WorldChangeResult Result)>? _candidateTask;
    private Task? _abandonedCandidateTask;
    private (VoxelDevelopmentSession Candidate, WorldChangeResult Result)? _preparedCandidate;
    private bool _advancingWorldChange;

    public bool IsChangingWorld => _worldChange is not null;
    public bool CanReloadSaved => SaveId is not null;
    public event Action<WorldChangeRequest, WorldChangeResult>? WorldChangeChanged;

    public bool HasUnsavedChanges =>
        Fault is not null ||
        !string.Equals(_cleanFingerprint, WorldSaveAuthority.Fingerprint(this), StringComparison.Ordinal);

    public void MarkPersistenceClean() => _cleanFingerprint = WorldSaveAuthority.Fingerprint(this);

    internal void MarkNewWorldUnsaved() => _cleanFingerprint = "";

    internal void MarkUnsavedCopy()
    {
        SaveId = null;
        MarkNewWorldUnsaved();
    }

    /// <summary>
    /// Marks this session as the world editor host. The duty stays with the session object and is not cleared when a
    /// save replaces <see cref="WorldSource"/>.
    /// </summary>
    internal void MarkWorldEditorHost() => _worldEditorHost = true;

    /// <summary>Completes an explicit choice synchronously. ChoiceRequired leaves no pending transition.</summary>
    public WorldChangeResult TryChangeWorld(WorldChangeRequest request)
    {
        var result = BeginWorldChange(request, retainPendingChoice: false);
        while (result.Status == WorldChangeStatus.Switching)
        {
            try { (_candidateTask ?? _abandonedCandidateTask)?.GetAwaiter().GetResult(); }
            catch { /* AdvanceWorldChange reports a failed preparation without replacing the world. */ }
            result = AdvanceWorldChange();
        }
        return result;
    }

    /// <summary>Captures the destination and suspends input until cancellation, failure or replacement.</summary>
    public WorldChangeResult BeginWorldChange(WorldChangeRequest request) =>
        BeginWorldChange(request, retainPendingChoice: true);

    private WorldChangeResult BeginWorldChange(WorldChangeRequest request, bool retainPendingChoice)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_advancingWorldChange)
            return Change(WorldChangeStatus.Busy);
        if (_worldChange is { } pending)
        {
            if (request.Kind == WorldChangeKind.ReloadSaved)
                request = request with { SaveId = pending.SaveId };
            if (request.Choice == WorldChangeChoice.Cancel)
                return ChooseWorldChange(WorldChangeChoice.Cancel);
            if (request.Choice is { } choice &&
                (pending with { Choice = null }) == (request with { Choice = null }))
                return ChooseWorldChange(choice);
            return Change(WorldChangeStatus.Busy);
        }
        if (!_worldEditorHost && request.Kind is WorldChangeKind.NewBlank or WorldChangeKind.SwitchConfiguration or WorldChangeKind.ReloadSaved)
            return Change(WorldChangeStatus.CandidateFailed, candidateStatus: WorldSaveStatus.IncompatibleBase,
                diagnostic: new WorldSaveDiagnostic("base", "session", "world_editor", "new worlds and configuration switches require the world editor host"));
        if (request.Choice == WorldChangeChoice.Cancel)
            return Change(WorldChangeStatus.Cancelled);
        if (request.Choice == WorldChangeChoice.SaveAsThenReload && request.Kind != WorldChangeKind.ReloadSaved)
            return Change(WorldChangeStatus.CandidateFailed);
        if (request.Kind == WorldChangeKind.ReloadSaved)
        {
            if (SaveId is null)
                return Change(WorldChangeStatus.CandidateFailed, candidateStatus: WorldSaveStatus.NotFound);
            request = request with { SaveId = SaveId };
            if (request.Choice == WorldChangeChoice.SaveThenSwitch)
                request = request with { Choice = WorldChangeChoice.SaveAsThenReload };
        }
        if (request.Kind == WorldChangeKind.NewBlank &&
            (request.BlankIdentity is not { } blank ||
             !BlankWorldSource.TryIdentity(blank.Bounds.SizeX, blank.Bounds.SizeY, blank.Bounds.SizeZ, blank.Seed, out var supported) ||
             blank != supported))
            return Change(WorldChangeStatus.CandidateFailed, candidateStatus: WorldSaveStatus.IncompatibleBase,
                diagnostic: new WorldSaveDiagnostic("base", "blank", "parameters", $"blank extent requires {BlankWorldSource.ExtentDescription} and the supported source version"));
        var needsChoice = HasUnsavedChanges && request.Choice is null;
        if (needsChoice && !retainPendingChoice)
            return Change(WorldChangeStatus.ChoiceRequired);
        _worldChange = request;
        _preparedCandidate = null;
        _changeSaveStatus = null;
        _hostScheduler.Reset();
        _changeNeedsChoice = needsChoice;
        _changeNeedsSave = request.Choice is WorldChangeChoice.SaveThenSwitch or WorldChangeChoice.SaveAsThenReload;
        return ReportChange(_changeNeedsChoice ? WorldChangeStatus.ChoiceRequired : WorldChangeStatus.Switching);
    }

    public WorldChangeResult ChooseWorldChange(WorldChangeChoice choice)
    {
        if (_advancingWorldChange)
            return Change(WorldChangeStatus.Busy);
        if (_worldChange is null)
            return Change(WorldChangeStatus.Cancelled);
        if (choice == WorldChangeChoice.Cancel)
            return FinishChange(Change(WorldChangeStatus.Cancelled));
        if (!_changeNeedsChoice)
            return Change(WorldChangeStatus.Busy);
        if (_worldChange.Kind == WorldChangeKind.ReloadSaved && choice == WorldChangeChoice.SaveThenSwitch)
            choice = WorldChangeChoice.SaveAsThenReload;
        if (choice == WorldChangeChoice.SaveAsThenReload && _worldChange.Kind != WorldChangeKind.ReloadSaved)
            return Change(WorldChangeStatus.Busy);
        _worldChange = _worldChange with { Choice = choice };
        _changeNeedsChoice = false;
        _changeNeedsSave = choice is WorldChangeChoice.SaveThenSwitch or WorldChangeChoice.SaveAsThenReload;
        return ReportChange(WorldChangeStatus.Switching);
    }

    /// <summary>Advances save, background preparation or adoption while the host keeps processing cancellation.</summary>
    public WorldChangeResult AdvanceWorldChange()
    {
        if (_advancingWorldChange)
            return Change(WorldChangeStatus.Busy);
        _advancingWorldChange = true;
        try { return AdvanceWorldChangeCore(); }
        finally { _advancingWorldChange = false; }
    }

    private WorldChangeResult AdvanceWorldChangeCore()
    {
        if (_worldChange is not { } request)
            return Change(WorldChangeStatus.Cancelled);
        if (_changeNeedsChoice)
            return Change(WorldChangeStatus.ChoiceRequired);
        if (_changeNeedsSave)
        {
            var saved = WriteSave(request.Choice == WorldChangeChoice.SaveAsThenReload ? Guid.NewGuid() : SaveId ?? Guid.NewGuid(), request.SaveDisplayName);
            if (!saved.Succeeded)
            {
                return FinishChange(Change(
                    WorldChangeStatus.SaveFailed,
                    saveStatus: saved.Status,
                    diagnostic: saved.Diagnostic ?? WorldSaveDiagnostic.For(saved.Status)));
            }
            _changeSaveStatus = saved.Status;
            _changeNeedsSave = false;
            return ReportChange(WorldChangeStatus.Switching);
        }

        if (request.LeavesScene)
            return FinishChange(Change(WorldChangeStatus.Completed));

        if (_preparedCandidate is { } prepared)
        {
            _preparedCandidate = null;
            if (prepared.Result.Status == WorldChangeStatus.Completed)
                AdoptPreparedCandidate(prepared.Candidate, request.Kind);
            else
                prepared.Candidate.Detach();
            return FinishChange(prepared.Result with { UnsavedChanges = HasUnsavedChanges });
        }
        if (_candidateTask is null)
        {
            // A cancelled read may still be inside the synchronous store. Wait before starting another preparation.
            if (_abandonedCandidateTask is { IsCompleted: false })
                return Change(WorldChangeStatus.Switching);
            _abandonedCandidateTask = null;
            // Isolated immutable publications prevent content reload after cancellation from reaching a worker.
            var voxels = new VoxelCatalog();
            if (_catalog.Current is { } voxelSnapshot)
                voxels.Publish(voxelSnapshot);
            var composites = new CompositeCatalog();
            composites.Publish(_composites.Current);
            var identity = Identity;
            var source = WorldSource;
            var saveId = SaveId;
            var name = DisplayName;
            var editorHost = _worldEditorHost;
            _candidateTask = Task.Run(() =>
            {
                var candidate = new VoxelDevelopmentSession(voxels, identity, _store, composites)
                {
                    WorldSource = source, SaveId = saveId, DisplayName = name, _worldEditorHost = editorHost
                };
                try { return (candidate, candidate.ApplyCandidateChange(request)); }
                catch
                {
                    candidate.Detach();
                    throw;
                }
            });
            return ReportChange(WorldChangeStatus.Switching);
        }
        if (!_candidateTask.IsCompleted)
            return Change(WorldChangeStatus.Switching);
        try
        {
            _preparedCandidate = _candidateTask.GetAwaiter().GetResult();
            _candidateTask = null;
            return ReportChange(WorldChangeStatus.Switching);
        }
        catch (Exception exception)
        {
            _candidateTask = null;
            return FinishChange(Change(WorldChangeStatus.CandidateFailed,
                diagnostic: new WorldSaveDiagnostic("candidate", "world", "prepare", exception.Message)));
        }
    }

    private WorldChangeResult ApplyCandidateChange(WorldChangeRequest request) => request.Kind switch
        {
            WorldChangeKind.Reset => ResetThroughChange(request.FixtureId),
            WorldChangeKind.SwitchConfiguration => SwitchThroughChange(request.ConfigurationId),
            WorldChangeKind.Load => LoadThroughChange(request.SaveId),
            WorldChangeKind.ReloadSaved => LoadThroughChange(request.SaveId),
            WorldChangeKind.NewBlank => AdoptBlank(request.BlankIdentity!.Value, keepSaveIdentity: false),
            _ => Change(
                WorldChangeStatus.CandidateFailed,
                diagnostic: new WorldSaveDiagnostic("base", "session", "change", "the requested world change is not supported"))
        };

    private void AdoptPreparedCandidate(VoxelDevelopmentSession candidate, WorldChangeKind kind)
    {
        ReplaceFrom(candidate);
        WorldSource = candidate.WorldSource;
        SaveId = candidate.SaveId;
        DisplayName = candidate.DisplayName;
        _cleanFingerprint = candidate._cleanFingerprint;
        SimulationPaused = candidate.SimulationPaused;
        Changed?.Invoke();
        WorldReplaced?.Invoke(kind is WorldChangeKind.Load or WorldChangeKind.ReloadSaved ? WorldReplacement.Load : WorldReplacement.Reset);
        candidate.Detach();
    }

    private WorldChangeResult ReportChange(WorldChangeStatus status)
    {
        var request = _worldChange!;
        var result = Change(status, saveStatus: _changeSaveStatus);
        Changed?.Invoke();
        if (!ReferenceEquals(_worldChange, request))
            return Change(_worldChange is null ? WorldChangeStatus.Cancelled : WorldChangeStatus.Busy);
        WorldChangeChanged?.Invoke(request, result);
        return result;
    }

    private WorldChangeResult FinishChange(WorldChangeResult result)
    {
        var request = _worldChange!;
        var abandoned = _candidateTask;
        _candidateTask = null;
        _preparedCandidate?.Candidate.Detach();
        _preparedCandidate = null;
        if (abandoned is not null)
        {
            _abandonedCandidateTask = abandoned;
            _ = abandoned.ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully) task.Result.Candidate.Detach();
                else if (task.IsFaulted) _ = task.Exception;
            }, TaskScheduler.Default);
        }
        result = result with { SaveStatus = result.SaveStatus ?? _changeSaveStatus };
        _worldChange = null;
        _hostScheduler.Reset();
        _discardHostDelta = true;
        WorldChangeChanged?.Invoke(request, result);
        Changed?.Invoke();
        return result;
    }

    private WorldChangeResult ResetThroughChange(string? fixtureId)
    {
        if (!string.IsNullOrWhiteSpace(fixtureId))
            return ResetWithFixture(fixtureId);
        if (_worldEditorHost)
        {
            if (WorldSource is null)
                return WorldEditorResetNeedsConfiguration();

            if (WorldSource.Kind == WorldSource.BlankKind)
                return AdoptBlank(Identity, keepSaveIdentity: true);

            return AdoptConfiguration(WorldSource.ConfigurationId, keepSaveIdentity: true);
        }

        Reset();
        return Change(WorldChangeStatus.Completed);
    }

    private WorldChangeResult ResetWithFixture(string fixtureId)
    {
        CompositeDefinitionId definition;
        try
        {
            definition = CompositeDefinitionId.Parse(fixtureId);
        }
        catch (ArgumentException)
        {
            return Change(
                WorldChangeStatus.CandidateFailed,
                candidateStatus: WorldSaveStatus.IncompatibleBase,
                diagnostic: new WorldSaveDiagnostic("candidate", fixtureId, "fixture", "the reset fixture id is not a definition id"));
        }

        VoxelDevelopmentSession? candidate = null;
        var adopted = false;
        try
        {
            if (_worldEditorHost && WorldSource is null)
                return WorldEditorResetNeedsConfiguration();

            if (WorldSource is { } source)
            {
                if (source.Kind == WorldSource.BlankKind)
                    candidate = BlankWorldSource.Establish(Identity, _catalog, _composites, _store);
                else
                {
                    var status = WorldEditorSession.TryEstablish(source.ConfigurationId, _catalog, _composites, _store, out candidate);
                    if (status != WorldEditorEstablishStatus.Established || candidate is null)
                        return Change(WorldChangeStatus.CandidateFailed, candidateStatus: MapEstablish(status), diagnostic: DiagnoseEstablish(source.ConfigurationId, status));
                }
            }
            else
            {
                candidate = new VoxelDevelopmentSession(_catalog, Identity, _store, _composites);
            }

            var manufactured = candidate.Manufacture(definition);
            if (!manufactured.Succeeded || manufactured.VehicleId is not { } vehicleId)
            {
                var missing = manufactured.Status == ConstructionStatus.UnknownDefinition;
                return Change(
                    WorldChangeStatus.CandidateFailed,
                    candidateStatus: missing ? WorldSaveStatus.MissingDefinition : WorldSaveStatus.IncompatibleBase,
                    diagnostic: new WorldSaveDiagnostic(
                        missing ? "dependency" : "candidate",
                        definition.Value,
                        "fixture",
                        "the reset fixture was not manufactured"));
            }

            Adopt(candidate, keepSaveIdentity: true, pause: _worldEditorHost);
            adopted = true;
            return Change(
                WorldChangeStatus.Completed,
                fixtureStatus: manufactured.Status.ToString(),
                fixtureVehicleId: vehicleId.Value,
                fixturePartCount: manufactured.Parts?.Count ?? 0);
        }
        finally
        {
            if (!adopted)
                candidate?.Detach();
        }
    }

    private WorldChangeResult SwitchThroughChange(string? configurationId)
    {
        if (string.IsNullOrWhiteSpace(configurationId))
        {
            return Change(
                WorldChangeStatus.CandidateFailed,
                candidateStatus: WorldSaveStatus.IncompatibleBase,
                diagnostic: new WorldSaveDiagnostic("base", "session", "configuration_id", "a configuration id is required"));
        }

        return AdoptConfiguration(configurationId, keepSaveIdentity: false);
    }

    private WorldChangeResult LoadThroughChange(Guid? saveId)
    {
        if (saveId is not Guid id)
        {
            return Change(
                WorldChangeStatus.CandidateFailed,
                candidateStatus: WorldSaveStatus.NotFound,
                diagnostic: new WorldSaveDiagnostic("format", "save", "id", "a save id is required"));
        }

        var loaded = LoadCore(id);
        if (!loaded.Succeeded)
        {
            return Change(
                WorldChangeStatus.CandidateFailed,
                candidateStatus: loaded.Status,
                diagnostic: loaded.Diagnostic ?? WorldSaveDiagnostic.For(loaded.Status),
                adaptations: loaded.Adaptations);
        }

        if (_worldEditorHost)
            SimulationPaused = true;
        return Change(WorldChangeStatus.Completed, adaptations: loaded.Adaptations);
    }

    private WorldChangeResult AdoptConfiguration(string configurationId, bool keepSaveIdentity)
    {
        VoxelDevelopmentSession? candidate = null;
        var adopted = false;
        try
        {
            var status = WorldEditorSession.TryEstablish(configurationId, _catalog, _composites, _store, out candidate);
            if (status != WorldEditorEstablishStatus.Established || candidate is null)
                return Change(WorldChangeStatus.CandidateFailed, candidateStatus: MapEstablish(status), diagnostic: DiagnoseEstablish(configurationId, status));

            Adopt(candidate, keepSaveIdentity, pause: true);
            adopted = true;
            return Change(WorldChangeStatus.Completed);
        }
        finally
        {
            if (!adopted)
                candidate?.Detach();
        }
    }

    private WorldChangeResult AdoptBlank(VoxelWorldIdentity identity, bool keepSaveIdentity)
    {
        var candidate = BlankWorldSource.Establish(identity, _catalog, _composites, _store);
        Adopt(candidate, keepSaveIdentity, pause: true);
        return Change(WorldChangeStatus.Completed);
    }

    private void Adopt(VoxelDevelopmentSession candidate, bool keepSaveIdentity, bool pause)
    {
        var saveId = SaveId;
        var displayName = DisplayName;
        ReplaceFrom(candidate);
        WorldSource = candidate.WorldSource;
        _worldEditorHost |= candidate._worldEditorHost;
        if (keepSaveIdentity)
        {
            SaveId = saveId;
            DisplayName = displayName;
        }
        else
        {
            SaveId = null;
            DisplayName = WorldSaveDocument.DefaultDisplayName;
        }

        if (pause)
            SimulationPaused = true;
        MarkPersistenceClean();
        if (candidate.WorldSource?.Kind == WorldSource.BlankKind)
            MarkNewWorldUnsaved();
        Changed?.Invoke();
        WorldReplaced?.Invoke(WorldReplacement.Reset);
        candidate.Detach();
    }

    private void ReplaceFrom(VoxelDevelopmentSession candidate) =>
        ReplaceSession(new WorldSaveRestore(
            candidate.World,
            candidate.Vehicles.ToArray(),
            candidate.NextEntityId,
            candidate.TransactionCount,
            candidate.Rails,
            candidate._assemblies.ToArray(),
            candidate.SimulationStep,
            candidate.CaptureEditQueue()));

    private WorldChangeResult Change(
        WorldChangeStatus status,
        WorldSaveStatus? saveStatus = null,
        WorldSaveStatus? candidateStatus = null,
        WorldSaveDiagnostic? diagnostic = null,
        IReadOnlyList<WorldSaveAdaptation>? adaptations = null,
        string? fixtureStatus = null,
        ulong? fixtureVehicleId = null,
        int? fixturePartCount = null) =>
        new(status, saveStatus, candidateStatus, diagnostic, adaptations, HasUnsavedChanges, fixtureStatus, fixtureVehicleId, fixturePartCount);

    private static WorldSaveStatus MapEstablish(WorldEditorEstablishStatus status) => status switch
    {
        WorldEditorEstablishStatus.MissingContent or WorldEditorEstablishStatus.ContentUnavailable
            => WorldSaveStatus.MissingDefinition,
        _ => WorldSaveStatus.IncompatibleBase
    };

    private static WorldSaveDiagnostic DiagnoseEstablish(string configurationId, WorldEditorEstablishStatus status)
    {
        var relationship = status switch
        {
            WorldEditorEstablishStatus.UnknownConfiguration => "unknown_configuration",
            WorldEditorEstablishStatus.UnsupportedDeclaration => "declaration",
            WorldEditorEstablishStatus.InitialStateRejected => "initial_state",
            WorldEditorEstablishStatus.ConfigurationMismatch => "configuration",
            WorldEditorEstablishStatus.MissingContent => "content_dependency",
            WorldEditorEstablishStatus.ContentUnavailable => "content_unavailable",
            _ => "configuration"
        };
        var stage = status is WorldEditorEstablishStatus.MissingContent or WorldEditorEstablishStatus.ContentUnavailable
            ? "dependency"
            : "base";
        return new WorldSaveDiagnostic(stage, configurationId, relationship, "the configuration could not be established");
    }

    private WorldChangeResult WorldEditorResetNeedsConfiguration() =>
        Change(
            WorldChangeStatus.CandidateFailed,
            candidateStatus: WorldSaveStatus.IncompatibleBase,
            diagnostic: new WorldSaveDiagnostic("base", "session", "configuration", "world editor reset needs the current configuration"));
}
