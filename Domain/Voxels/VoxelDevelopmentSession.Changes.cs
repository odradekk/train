using Train.Domain.Composites;
using Train.Domain.Playground;

namespace Train.Domain.Voxels;

public sealed partial class VoxelDevelopmentSession
{
    private string _cleanFingerprint = "";

    public bool HasUnsavedChanges =>
        Fault is not null ||
        !string.Equals(_cleanFingerprint, WorldSaveAuthority.Fingerprint(this), StringComparison.Ordinal);

    public void MarkPersistenceClean() => _cleanFingerprint = WorldSaveAuthority.Fingerprint(this);

    public WorldChangeResult TryChangeWorld(WorldChangeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Choice == WorldChangeChoice.Cancel)
            return Change(WorldChangeStatus.Cancelled);
        if (HasUnsavedChanges && request.Choice is null)
            return Change(WorldChangeStatus.ChoiceRequired);

        WorldSaveStatus? savedStatus = null;
        if (request.Choice == WorldChangeChoice.SaveThenSwitch)
        {
            var saved = Save();
            if (!saved.Succeeded)
            {
                return Change(
                    WorldChangeStatus.SaveFailed,
                    saveStatus: saved.Status,
                    diagnostic: saved.Diagnostic ?? WorldSaveDiagnostic.For(saved.Status));
            }

            savedStatus = saved.Status;
        }

        var result = request.Kind switch
        {
            WorldChangeKind.Reset => ResetThroughChange(request.FixtureId),
            WorldChangeKind.SwitchConfiguration => SwitchThroughChange(request.ConfigurationId),
            WorldChangeKind.Load => LoadThroughChange(request.SaveId),
            _ => Change(
                WorldChangeStatus.CandidateFailed,
                diagnostic: new WorldSaveDiagnostic("base", "session", "change", "the requested world change is not supported"))
        };
        return savedStatus is null ? result : result with { SaveStatus = savedStatus };
    }

    private WorldChangeResult ResetThroughChange(string? fixtureId)
    {
        if (!string.IsNullOrWhiteSpace(fixtureId))
            return ResetWithFixture(fixtureId);
        if (WorldSource is null)
        {
            Reset();
            return Change(WorldChangeStatus.Completed);
        }

        return AdoptConfiguration(WorldSource.ConfigurationId, keepSaveIdentity: true);
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
            if (WorldSource is { } source)
            {
                var status = PlaygroundSession.TryEstablish(source.ConfigurationId, _catalog, _composites, _store, out candidate);
                if (status != PlaygroundEstablishStatus.Established || candidate is null)
                    return Change(WorldChangeStatus.CandidateFailed, candidateStatus: MapEstablish(status), diagnostic: DiagnoseEstablish(source.ConfigurationId, status));
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

            var pause = WorldSource is not null;
            Adopt(candidate, keepSaveIdentity: true, pause);
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
        if (WorldSource is null)
        {
            return Change(
                WorldChangeStatus.CandidateFailed,
                candidateStatus: WorldSaveStatus.IncompatibleBase,
                diagnostic: new WorldSaveDiagnostic("base", "session", "world_source", "the current session has no fixed configuration to switch"));
        }

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

        var playground = WorldSource is not null;
        var loaded = Load(id);
        if (!loaded.Succeeded)
        {
            return Change(
                WorldChangeStatus.CandidateFailed,
                candidateStatus: loaded.Status,
                diagnostic: loaded.Diagnostic ?? WorldSaveDiagnostic.For(loaded.Status),
                adaptations: loaded.Adaptations);
        }

        if (playground)
            PauseSimulation();
        return Change(WorldChangeStatus.Completed, adaptations: loaded.Adaptations);
    }

    private WorldChangeResult AdoptConfiguration(string configurationId, bool keepSaveIdentity)
    {
        VoxelDevelopmentSession? candidate = null;
        var adopted = false;
        try
        {
            var status = PlaygroundSession.TryEstablish(configurationId, _catalog, _composites, _store, out candidate);
            if (status != PlaygroundEstablishStatus.Established || candidate is null)
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

    private void Adopt(VoxelDevelopmentSession candidate, bool keepSaveIdentity, bool pause)
    {
        var saveId = SaveId;
        var displayName = DisplayName;
        ReplaceSession(new WorldSaveRestore(
            candidate.World,
            candidate.Vehicles.ToArray(),
            candidate.NextEntityId,
            candidate.TransactionCount,
            candidate.Rails,
            candidate._assemblies.ToArray(),
            candidate.SimulationStep,
            candidate.CaptureEditQueue()));
        WorldSource = candidate.WorldSource;
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
            PauseSimulation();
        MarkPersistenceClean();
        WorldReplaced?.Invoke(WorldReplacement.Reset);
        candidate.Detach();
    }

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

    private static WorldSaveStatus MapEstablish(PlaygroundEstablishStatus status) => status switch
    {
        PlaygroundEstablishStatus.MissingContent or PlaygroundEstablishStatus.ContentUnavailable
            => WorldSaveStatus.MissingDefinition,
        _ => WorldSaveStatus.IncompatibleBase
    };

    private static WorldSaveDiagnostic DiagnoseEstablish(string configurationId, PlaygroundEstablishStatus status)
    {
        var relationship = status switch
        {
            PlaygroundEstablishStatus.UnknownConfiguration => "unknown_configuration",
            PlaygroundEstablishStatus.UnsupportedDeclaration => "declaration",
            PlaygroundEstablishStatus.InitialStateRejected => "initial_state",
            PlaygroundEstablishStatus.ConfigurationMismatch => "configuration",
            PlaygroundEstablishStatus.MissingContent => "content_dependency",
            PlaygroundEstablishStatus.ContentUnavailable => "content_unavailable",
            _ => "configuration"
        };
        var stage = status is PlaygroundEstablishStatus.MissingContent or PlaygroundEstablishStatus.ContentUnavailable
            ? "dependency"
            : "base";
        return new WorldSaveDiagnostic(stage, configurationId, relationship, "the configuration could not be established");
    }
}
