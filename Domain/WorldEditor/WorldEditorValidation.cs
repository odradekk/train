using Train.Core.Saves;
using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Train.Core.Content;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Domain.WorldEditor;

public enum WorldEditorOperationKind
{
    Pause,
    Resume,
    Advance,
    StartTrain,
    StopTrain,
    DamageVoxel,
    Edit,
    RefitLatestVehicle,
    DamageLatestVehicle
}

public enum WorldEditorRefitKind
{
    Add,
    Remove,
    Move,
    Rotate,
    Replace
}

/// <summary>
/// One refit step addressed by local cell on the most recently created vehicle, the way the panel picks it. It is
/// resolved into concrete part ids before submission, so the executed payload is an ordinary refit edit.
/// </summary>
public sealed record WorldEditorRefit(
    WorldEditorRefitKind Kind,
    LocalVoxelCoord Cell,
    LocalVoxelCoord Destination = default,
    VoxelDefinitionId DefinitionId = default,
    VoxelOrientation? Orientation = null)
{
    public static WorldEditorRefit Add(LocalVoxelCoord cell, VoxelDefinitionId definitionId, VoxelOrientation? orientation = null) =>
        new(WorldEditorRefitKind.Add, cell, DefinitionId: definitionId, Orientation: orientation);
    public static WorldEditorRefit Remove(LocalVoxelCoord cell) => new(WorldEditorRefitKind.Remove, cell);
    public static WorldEditorRefit Move(LocalVoxelCoord cell, LocalVoxelCoord destination) =>
        new(WorldEditorRefitKind.Move, cell, destination);
    public static WorldEditorRefit Rotate(LocalVoxelCoord cell, VoxelOrientation orientation) =>
        new(WorldEditorRefitKind.Rotate, cell, Orientation: orientation);
    public static WorldEditorRefit Replace(LocalVoxelCoord cell, VoxelDefinitionId definitionId, VoxelOrientation? orientation = null) =>
        new(WorldEditorRefitKind.Replace, cell, DefinitionId: definitionId, Orientation: orientation);

    public CompositeRefitOperation? Resolve(CompositeQuery vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        var orientation = Orientation ?? VoxelOrientation.Identity;
        if (Kind == WorldEditorRefitKind.Add)
            return CompositeRefitOperation.Add(Cell, DefinitionId, orientation);
        if (Kind == WorldEditorRefitKind.Replace)
            return CompositeRefitOperation.Replace(Cell, DefinitionId, orientation);
        var part = vehicle.Parts.FirstOrDefault(item => item.Cell == Cell);
        if (part is null)
            return null;
        return Kind switch
        {
            WorldEditorRefitKind.Remove => CompositeRefitOperation.Remove(part.Id),
            WorldEditorRefitKind.Move => CompositeRefitOperation.Move(part.Id, Destination),
            WorldEditorRefitKind.Rotate => CompositeRefitOperation.Rotate(part.Id, orientation),
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null)
        };
    }

    public string Describe() => Kind switch
    {
        WorldEditorRefitKind.Move => $"move {Cell.X},{Cell.Y},{Cell.Z}->{Destination.X},{Destination.Y},{Destination.Z}",
        WorldEditorRefitKind.Add or WorldEditorRefitKind.Replace => $"{Kind.ToString().ToLowerInvariant()} {Cell.X},{Cell.Y},{Cell.Z} {DefinitionId.Value}",
        _ => $"{Kind.ToString().ToLowerInvariant()} {Cell.X},{Cell.Y},{Cell.Z}"
    };
}

/// <summary>A formal session operation shared by the manual WorldEditor controls and the automatic validation runner.</summary>
public sealed record WorldEditorOperation(
    WorldEditorOperationKind Kind,
    ulong Steps = 0,
    RailHeading Heading = RailHeading.AlongAlignment,
    WorldVoxelCoord Cell = default,
    int Amount = 0,
    WorldEdit? Edit = null,
    ulong? AtStep = null,
    IReadOnlyList<WorldEditorRefit>? Refits = null,
    LocalVoxelCoord VehicleCell = default)
{
    public const string NoVehicleStatus = "NoVehicle";
    public const string NoPartStatus = "NoPart";

    /// <summary>Submits a parsed edit; <paramref name="atStep"/> queues it for that step instead of the current boundary.</summary>
    public static WorldEditorOperation Submit(WorldEdit edit, ulong? atStep = null) =>
        new(WorldEditorOperationKind.Edit, Edit: edit ?? throw new ArgumentNullException(nameof(edit)), AtStep: atStep);
    public static WorldEditorOperation RefitLatest(IReadOnlyList<WorldEditorRefit> refits, ulong? atStep = null) =>
        new(WorldEditorOperationKind.RefitLatestVehicle, Refits: refits, AtStep: atStep);
    public static WorldEditorOperation RefitLatest(WorldEditorRefit refit, ulong? atStep = null) =>
        RefitLatest(new[] { refit }, atStep);
    public static WorldEditorOperation DamageLatest(LocalVoxelCoord cell, int amount, ulong? atStep = null) =>
        new(WorldEditorOperationKind.DamageLatestVehicle, Amount: amount, AtStep: atStep, VehicleCell: cell);

    public static WorldEditorOperation Pause() => new(WorldEditorOperationKind.Pause);
    public static WorldEditorOperation Resume() => new(WorldEditorOperationKind.Resume);
    public static WorldEditorOperation Advance(ulong steps) => new(WorldEditorOperationKind.Advance, steps);
    public static WorldEditorOperation Start(RailHeading heading = RailHeading.AlongAlignment, ulong? atStep = null) =>
        new(WorldEditorOperationKind.StartTrain, Heading: heading, AtStep: atStep);
    public static WorldEditorOperation Stop(ulong? atStep = null) => new(WorldEditorOperationKind.StopTrain, AtStep: atStep);
    public static WorldEditorOperation Damage(WorldVoxelCoord cell, int amount) =>
        new(WorldEditorOperationKind.DamageVoxel, Cell: cell, Amount: amount);

    public string Apply(VoxelDevelopmentSession session, bool confirmEndHistory = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Kind switch
        {
            WorldEditorOperationKind.Pause => session.PauseSimulation().Status.ToString(),
            WorldEditorOperationKind.Resume => session.ResumeSimulation().Status.ToString(),
            WorldEditorOperationKind.Advance => session.AdvanceSimulation(Steps).Status.ToString(),
            WorldEditorOperationKind.StartTrain => session.StartTrain(Heading, AtStep, confirmEndHistory: confirmEndHistory).Status.ToString(),
            WorldEditorOperationKind.StopTrain => session.StopTrain(AtStep, confirmEndHistory: confirmEndHistory).Status.ToString(),
            WorldEditorOperationKind.DamageVoxel => session.Damage(Cell, Amount, confirmEndHistory).Status.ToString(),
            WorldEditorOperationKind.Edit or WorldEditorOperationKind.RefitLatestVehicle or WorldEditorOperationKind.DamageLatestVehicle =>
                ResolveEdit(session, out var edit) is { } refused ? refused : session.SubmitEdit(edit!, AtStep, confirmEndHistory).Status,
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null)
        };
    }

    /// <summary>
    /// Turns an edit operation into the parsed payload it submits. Returns a refusal status, without touching the
    /// session, when the latest vehicle or an addressed part does not exist.
    /// </summary>
    public string? ResolveEdit(VoxelDevelopmentSession session, out WorldEdit? edit)
    {
        ArgumentNullException.ThrowIfNull(session);
        edit = Edit;
        if (Kind == WorldEditorOperationKind.Edit)
            return edit is null ? throw new InvalidOperationException("Edit operation has no payload.") : null;
        if (Kind is not (WorldEditorOperationKind.RefitLatestVehicle or WorldEditorOperationKind.DamageLatestVehicle))
            throw new InvalidOperationException($"{Kind} is not an edit operation.");

        var vehicle = session.QueryComposites().OrderByDescending(item => item.Id.Value).FirstOrDefault();
        if (vehicle is null)
            return NoVehicleStatus;
        if (Kind == WorldEditorOperationKind.DamageLatestVehicle)
        {
            edit = new VehicleDamageEdit(new[] { new CompositeDamageTarget(vehicle.Id, VehicleCell, Amount) });
            return null;
        }

        var operations = new List<CompositeRefitOperation>();
        foreach (var refit in Refits ?? Array.Empty<WorldEditorRefit>())
        {
            if (refit.Resolve(vehicle) is not { } operation)
                return NoPartStatus;
            operations.Add(operation);
        }

        edit = new VehicleRefitEdit(vehicle.Id, operations);
        return null;
    }

    /// <summary>
    /// Canonical identity of this command: the full payload, its scheduled step, and, for latest/refit, the target
    /// resolved before execution. <see cref="Describe"/> is display text and is not this identity.
    /// </summary>
    public string InputIdentity(VoxelDevelopmentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            switch (Kind)
            {
                case WorldEditorOperationKind.Pause:
                    writer.WriteString("kind", "pause");
                    WriteAtStep(writer);
                    break;
                case WorldEditorOperationKind.Resume:
                    writer.WriteString("kind", "resume");
                    WriteAtStep(writer);
                    break;
                case WorldEditorOperationKind.Advance:
                    writer.WriteString("kind", "advance");
                    WriteAtStep(writer);
                    writer.WriteNumber("steps", Steps);
                    break;
                case WorldEditorOperationKind.StartTrain:
                    writer.WriteString("kind", "start");
                    WriteAtStep(writer);
                    writer.WriteString("heading", Heading.ToString());
                    break;
                case WorldEditorOperationKind.StopTrain:
                    writer.WriteString("kind", "stop");
                    WriteAtStep(writer);
                    break;
                case WorldEditorOperationKind.DamageVoxel:
                    writer.WriteString("kind", "damage_voxel");
                    WriteAtStep(writer);
                    WriteCoord(writer, "position", Cell.X, Cell.Y, Cell.Z);
                    writer.WriteNumber("amount", Amount);
                    break;
                case WorldEditorOperationKind.Edit:
                    writer.WriteString("kind", "edit");
                    WriteAtStep(writer);
                    writer.WritePropertyName("edit");
                    writer.WriteRawValue(Edit?.Canonical ?? throw new InvalidOperationException("Edit operation has no payload."));
                    break;
                case WorldEditorOperationKind.RefitLatestVehicle:
                    writer.WriteString("kind", "refit_latest");
                    WriteAtStep(writer);
                    writer.WriteStartArray("request");
                    foreach (var refit in Refits ?? Array.Empty<WorldEditorRefit>())
                        WriteRefitRequest(writer, refit);
                    writer.WriteEndArray();
                    WriteResolved(writer, session);
                    break;
                case WorldEditorOperationKind.DamageLatestVehicle:
                    writer.WriteString("kind", "damage_latest");
                    WriteAtStep(writer);
                    WriteCoord(writer, "cell", VehicleCell.X, VehicleCell.Y, VehicleCell.Z);
                    writer.WriteNumber("amount", Amount);
                    WriteResolved(writer, session);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string Describe() => Kind switch
    {
        WorldEditorOperationKind.Advance => $"advance {Steps.ToString(CultureInfo.InvariantCulture)}",
        WorldEditorOperationKind.StartTrain => $"start {Heading}{DescribeAt()}",
        WorldEditorOperationKind.StopTrain => $"stop{DescribeAt()}",
        WorldEditorOperationKind.DamageVoxel => $"damage {Cell.X},{Cell.Y},{Cell.Z} {Amount.ToString(CultureInfo.InvariantCulture)}",
        WorldEditorOperationKind.Edit => $"edit {WorldEditCodec.WireKind(Edit!.Kind)}{DescribeAt()}",
        WorldEditorOperationKind.RefitLatestVehicle =>
            $"refit latest {string.Join("; ", (Refits ?? Array.Empty<WorldEditorRefit>()).Select(refit => refit.Describe()))}{DescribeAt()}",
        WorldEditorOperationKind.DamageLatestVehicle =>
            $"damage latest {VehicleCell.X},{VehicleCell.Y},{VehicleCell.Z} {Amount.ToString(CultureInfo.InvariantCulture)}{DescribeAt()}",
        _ => Kind.ToString().ToLowerInvariant()
    };

    private void WriteAtStep(Utf8JsonWriter writer)
    {
        if (AtStep is { } step)
            writer.WriteNumber("at_step", step);
        else
            writer.WriteNull("at_step");
    }

    private static void WriteCoord(Utf8JsonWriter writer, string name, int x, int y, int z)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("x", x);
        writer.WriteNumber("y", y);
        writer.WriteNumber("z", z);
        writer.WriteEndObject();
    }

    private static void WriteRefitRequest(Utf8JsonWriter writer, WorldEditorRefit refit)
    {
        writer.WriteStartObject();
        writer.WriteString("op", refit.Kind.ToString().ToLowerInvariant());
        WriteCoord(writer, "cell", refit.Cell.X, refit.Cell.Y, refit.Cell.Z);
        switch (refit.Kind)
        {
            case WorldEditorRefitKind.Add:
            case WorldEditorRefitKind.Replace:
                writer.WriteString("definition_id", refit.DefinitionId.Value);
                writer.WriteString("orientation", (refit.Orientation ?? VoxelOrientation.Identity).ToString());
                break;
            case WorldEditorRefitKind.Remove:
                break;
            case WorldEditorRefitKind.Move:
                WriteCoord(writer, "destination", refit.Destination.X, refit.Destination.Y, refit.Destination.Z);
                break;
            case WorldEditorRefitKind.Rotate:
                if (refit.Orientation is { } orientation)
                    writer.WriteString("orientation", orientation.ToString());
                else
                    writer.WriteNull("orientation");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(refit), refit.Kind, "Unknown refit kind.");
        }

        writer.WriteEndObject();
    }

    private void WriteResolved(Utf8JsonWriter writer, VoxelDevelopmentSession session)
    {
        writer.WritePropertyName("resolved");
        if (ResolveEdit(session, out var edit) is null && edit is not null)
            writer.WriteRawValue(edit.Canonical);
        else
            writer.WriteNullValue();
    }

    private string DescribeAt() => AtStep is { } step ? $" at {step.ToString(CultureInfo.InvariantCulture)}" : "";
}

public enum WorldEditorObservation
{
    SimulationStep,
    Paused,
    AssemblyCount,
    LeadFrontContactX,
    Speed,
    Running,
    HaltReason,
    EditBoundary,
    NextEditId,
    PendingEdits,
    LastEdit,
    VehicleCount,
    VoxelCount
}

public sealed record WorldEditorCheck(WorldEditorObservation Observation, string Expected)
{
    public static string Observe(VoxelDevelopmentSession session, WorldEditorObservation observation)
    {
        ArgumentNullException.ThrowIfNull(session);
        var assembly = session.QueryTrainAssemblies().FirstOrDefault();
        return observation switch
        {
            WorldEditorObservation.SimulationStep => session.SimulationStep.ToString(CultureInfo.InvariantCulture),
            WorldEditorObservation.Paused => session.SimulationPaused ? "true" : "false",
            WorldEditorObservation.AssemblyCount => session.QueryTrainAssemblies().Count.ToString(CultureInfo.InvariantCulture),
            WorldEditorObservation.LeadFrontContactX => assembly is null ? "none" : Number(assembly.Vehicles[0].FrontContact.X),
            WorldEditorObservation.Speed => assembly is null ? "none" : Number(assembly.Speed),
            WorldEditorObservation.Running => assembly is null ? "none" : assembly.Running ? "true" : "false",
            WorldEditorObservation.HaltReason => assembly is null ? "none" : assembly.HaltReason.ToString(),
            WorldEditorObservation.EditBoundary => session.EditBoundary.ToString(),
            WorldEditorObservation.NextEditId => session.NextEditId.ToString(CultureInfo.InvariantCulture),
            WorldEditorObservation.PendingEdits => session.QueryPendingEdits().Count.ToString(CultureInfo.InvariantCulture),
            WorldEditorObservation.LastEdit => session.QueryEditLog() is { Count: > 0 } log ? DescribeRecord(log[^1]) : "none",
            WorldEditorObservation.VehicleCount => session.QueryComposites().Count.ToString(CultureInfo.InvariantCulture),
            WorldEditorObservation.VoxelCount => session.World.VoxelCount.ToString(CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(observation), observation, null)
        };
    }

    /// <summary>Compact record text: <c>id:state:status@step:sequence</c>.</summary>
    public static string DescribeRecord(WorldEditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return $"{record.Id.ToString(CultureInfo.InvariantCulture)}:{WorldEditCodec.WireState(record.State)}:{record.Status}@{record.ExecutedAt}";
    }

    private static string Number(double value) =>
        Math.Round(value, 6, MidpointRounding.AwayFromZero).ToString("0.######", CultureInfo.InvariantCulture);
}

public sealed record WorldEditorValidationStep(
    WorldEditorOperation Operation,
    string ExpectedStatus,
    IReadOnlyList<WorldEditorCheck> Checkpoint,
    bool ConfirmEndHistory = false);

/// <summary>Validation steps live outside the fixed configuration and only reference it by id.</summary>
public sealed record WorldEditorSequence(
    string Id,
    string ConfigurationId,
    IReadOnlyList<WorldEditorValidationStep> Steps);

public sealed record WorldEditorCheckResult(WorldEditorObservation Observation, string Expected, string Observed)
{
    public bool Passed => string.Equals(Expected, Observed, StringComparison.Ordinal);
}

public sealed record WorldEditorStepResult(
    int Index,
    string Operation,
    string ExpectedStatus,
    string ActualStatus,
    IReadOnlyList<WorldEditorCheckResult> Checks)
{
    public bool Passed =>
        string.Equals(ExpectedStatus, ActualStatus, StringComparison.Ordinal) &&
        Checks.All(check => check.Passed);
}

public sealed record WorldEditorSequenceReport(
    string SequenceId,
    string ConfigurationId,
    WorldEditorEstablishStatus EstablishStatus,
    IReadOnlyList<WorldEditorStepResult> Steps,
    string? FinalFingerprint)
{
    public bool Passed =>
        EstablishStatus == WorldEditorEstablishStatus.Established &&
        Steps.All(step => step.Passed);

    public IEnumerable<string> Lines()
    {
        yield return $"sequence {SequenceId} config {ConfigurationId} establish {EstablishStatus}";
        foreach (var step in Steps)
        {
            var checks = string.Join(' ', step.Checks.Select(check =>
                $"{check.Observation}={check.Observed}{(check.Passed ? "" : $"(expected {check.Expected})")}"));
            yield return $"step {step.Index} {step.Operation} -> {step.ActualStatus}" +
                $"{(step.ActualStatus == step.ExpectedStatus ? "" : $" (expected {step.ExpectedStatus})")}" +
                $"{(checks.Length == 0 ? "" : " | " + checks)} {(step.Passed ? "ok" : "FAIL")}";
        }

        yield return $"result {(Passed ? "passed" : "failed")} fingerprint {FinalFingerprint ?? "none"}";
    }
}

public static class WorldEditorValidation
{
    public static WorldEditorSequenceReport Run(
        WorldEditorSequence sequence,
        VoxelCatalog catalog,
        CompositeCatalog composites,
        ISaveStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var status = WorldEditorSession.TryEstablish(sequence.ConfigurationId, catalog, composites, store, out var session);
        if (status != WorldEditorEstablishStatus.Established)
            return new WorldEditorSequenceReport(sequence.Id, sequence.ConfigurationId, status, Array.Empty<WorldEditorStepResult>(), null);

        try
        {
            return Execute(sequence, session!);
        }
        finally
        {
            session!.Detach();
        }
    }

    public static WorldEditorSequenceReport Execute(WorldEditorSequence sequence, VoxelDevelopmentSession session)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(session);
        if (!string.Equals(session.WorldSource?.ConfigurationId, sequence.ConfigurationId, StringComparison.Ordinal))
            return new WorldEditorSequenceReport(
                sequence.Id,
                sequence.ConfigurationId,
                WorldEditorEstablishStatus.ConfigurationMismatch,
                Array.Empty<WorldEditorStepResult>(),
                null);

        var results = new List<WorldEditorStepResult>();
        for (var index = 0; index < sequence.Steps.Count; index++)
        {
            var step = sequence.Steps[index];
            var actual = step.Operation.Apply(session, step.ConfirmEndHistory);
            var checks = step.Checkpoint
                .Select(check => new WorldEditorCheckResult(check.Observation, check.Expected, WorldEditorCheck.Observe(session, check.Observation)))
                .ToArray();
            results.Add(new WorldEditorStepResult(index, step.Operation.Describe(), step.ExpectedStatus, actual, checks));
        }

        return new WorldEditorSequenceReport(
            sequence.Id,
            sequence.ConfigurationId,
            WorldEditorEstablishStatus.Established,
            results,
            WorldSaveAuthority.Fingerprint(session));
    }
}

public static class WorldEditorSequences
{
    public const string StraightRailRunAndHaltId = "straight-rail-run-and-halt";
    public const string StraightRailRejectionsId = "straight-rail-rejections";
    public const string StraightRailOrderedEditsId = "straight-rail-ordered-edits";
    public const string StraightRailRepairRestartId = "straight-rail-repair-restart";
    public const string AuthorityManufactureFractureId = "authority-manufacture-fracture";
    public const string CrossProcessComparisonId = "cross-process-comparison";

    public static readonly WorldVoxelCoord EditProbe = new(60, 3, 20);
    public static readonly VoxelDefinitionId EditVoxelId = new("core", "ferrous-ore");
    public static readonly CompositeTransform EditShellPose = CompositeTransform.Translation(60, 6, 24);
    public static readonly CompositeTransform AuthorityShellPose = CompositeTransform.Translation(40, 8, 8);
    public static readonly CompositeTransform AuthorityBridgePose = CompositeTransform.Translation(200, 8, 8);

    public static IReadOnlyList<WorldEditorSequence> All { get; } = new[]
    {
        new WorldEditorSequence(
            StraightRailOrderedEditsId,
            WorldEditorConfigurations.StraightRailTrainId,
            new[]
            {
                Step(WorldEditorOperation.Start(), "Started",
                    Check(WorldEditorObservation.Paused, "true"),
                    Check(WorldEditorObservation.EditBoundary, "0:1"),
                    Check(WorldEditorObservation.LastEdit, "1:executed:Started@0:0")),
                Step(WorldEditorOperation.Submit(new VoxelAddEdit(EditProbe, EditVoxelId, VoxelOrientation.Identity)), "Added",
                    Check(WorldEditorObservation.SimulationStep, "0"),
                    Check(WorldEditorObservation.EditBoundary, "0:2"),
                    Check(WorldEditorObservation.NextEditId, "3")),
                Step(WorldEditorOperation.Submit(new VoxelDamageEdit(new[] { new VoxelDamageTarget(EditProbe, 25) })), "Damaged",
                    Check(WorldEditorObservation.EditBoundary, "0:3")) with { ConfirmEndHistory = true },
                Step(WorldEditorOperation.Submit(new VehicleManufactureEdit(HollowShellFixture.DefinitionId, EditShellPose)), "Valid",
                    Check(WorldEditorObservation.VehicleCount, "3")),
                Step(WorldEditorOperation.RefitLatest(WorldEditorRefit.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId)), "Valid"),
                Step(WorldEditorOperation.DamageLatest(HollowShellRefit.FirstAdd, 25), "Damaged"),
                Step(WorldEditorOperation.RefitLatest(WorldEditorRefit.Move(HollowShellRefit.FirstAdd, HollowShellRefit.Moved)), "Valid"),
                Step(WorldEditorOperation.RefitLatest(WorldEditorRefit.Rotate(HollowShellRefit.Moved, HollowShellRefit.MarkerOrientation)), "Valid"),
                Step(WorldEditorOperation.RefitLatest(WorldEditorRefit.Replace(HollowShellRefit.Moved, HollowShellRefit.ReplacementId)), "Valid"),
                Step(WorldEditorOperation.RefitLatest(WorldEditorRefit.Remove(HollowShellRefit.Moved)), "Valid"),
                Step(WorldEditorOperation.RefitLatest(new[]
                    {
                        WorldEditorRefit.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId),
                        WorldEditorRefit.Add(HollowShellRefit.SecondAdd, HollowShellRefit.MarkerId)
                    }), "Valid",
                    Check(WorldEditorObservation.SimulationStep, "0"),
                    Check(WorldEditorObservation.LeadFrontContactX, "80"),
                    Check(WorldEditorObservation.EditBoundary, "0:11"),
                    Check(WorldEditorObservation.LastEdit, "11:executed:Valid@0:10")),
                Step(WorldEditorOperation.RefitLatest(WorldEditorRefit.Remove(HollowShellRefit.Moved)), WorldEditorOperation.NoPartStatus,
                    Check(WorldEditorObservation.EditBoundary, "0:11"),
                    Check(WorldEditorObservation.NextEditId, "12")),
                Step(WorldEditorOperation.Submit(new VoxelRemoveEdit(EditProbe), 3), "Pending",
                    Check(WorldEditorObservation.PendingEdits, "1")),
                Step(WorldEditorOperation.Submit(new VoxelAddEdit(EditProbe, EditVoxelId, VoxelOrientation.Identity), 3), "Pending"),
                Step(WorldEditorOperation.RefitLatest(WorldEditorRefit.Add(HollowShellRefit.OutOfEnvelope, HollowShellRefit.MarkerId), 3), "Pending",
                    Check(WorldEditorObservation.PendingEdits, "3"),
                    Check(WorldEditorObservation.EditBoundary, "0:14")),
                Step(WorldEditorOperation.Advance(3), "Advanced",
                    Check(WorldEditorObservation.SimulationStep, "3"),
                    Check(WorldEditorObservation.PendingEdits, "0"),
                    Check(WorldEditorObservation.EditBoundary, "3:3"),
                    Check(WorldEditorObservation.LastEdit, "14:rejected:EnvelopeExceeded@3:2")),
                Step(WorldEditorOperation.Submit(new VoxelAddEdit(EditProbe, EditVoxelId, VoxelOrientation.Identity)), "Occupied",
                    Check(WorldEditorObservation.EditBoundary, "3:4"),
                    Check(WorldEditorObservation.LastEdit, "15:rejected:Occupied@3:3")),
                Step(WorldEditorOperation.Advance(1), "Advanced",
                    Check(WorldEditorObservation.EditBoundary, "4:0"),
                    Check(WorldEditorObservation.NextEditId, "16"))
            }),
        new WorldEditorSequence(
            StraightRailRunAndHaltId,
            WorldEditorConfigurations.StraightRailTrainId,
            new[]
            {
                Step(WorldEditorOperation.Start(), "Started",
                    Check(WorldEditorObservation.SimulationStep, "0"),
                    Check(WorldEditorObservation.Paused, "true")),
                Step(WorldEditorOperation.Advance(1), "Advanced",
                    Check(WorldEditorObservation.SimulationStep, "1")),
                Step(WorldEditorOperation.Advance(299), "Advanced",
                    Check(WorldEditorObservation.SimulationStep, "300"),
                    Check(WorldEditorObservation.LeadFrontContactX, "96"),
                    Check(WorldEditorObservation.Speed, "4"),
                    Check(WorldEditorObservation.Running, "true")),
                Step(WorldEditorOperation.Damage(StraightRailFixture.FirstSegmentDamageCell, 100), "Removed"),
                Step(WorldEditorOperation.Advance(2000), "Advanced",
                    Check(WorldEditorObservation.SimulationStep, "2300"),
                    Check(WorldEditorObservation.Speed, "0"),
                    Check(WorldEditorObservation.Running, "false"),
                    Check(WorldEditorObservation.HaltReason, "ForwardGap"))
            }),
        new WorldEditorSequence(
            StraightRailRejectionsId,
            WorldEditorConfigurations.StraightRailTrainId,
            new[]
            {
                Step(WorldEditorOperation.Start(RailHeading.AgainstAlignment), "ReverseUnsupported",
                    Check(WorldEditorObservation.Running, "false"),
                    Check(WorldEditorObservation.LeadFrontContactX, "80")),
                Step(WorldEditorOperation.Start(), "Started"),
                Step(WorldEditorOperation.Damage(StraightRailFixture.FirstSegmentDamageCell, 100), "Removed"),
                Step(WorldEditorOperation.Advance(2000), "Advanced",
                    Check(WorldEditorObservation.HaltReason, "ForwardGap")),
                Step(WorldEditorOperation.Start(), "DisconnectedPath",
                    Check(WorldEditorObservation.Speed, "0"),
                    Check(WorldEditorObservation.Running, "false"),
                    Check(WorldEditorObservation.HaltReason, "ForwardGap")),
                Step(WorldEditorOperation.Advance(ulong.MaxValue), "StepOverflow",
                    Check(WorldEditorObservation.SimulationStep, "2000"))
            }),
        new WorldEditorSequence(
            StraightRailRepairRestartId,
            WorldEditorConfigurations.StraightRailTrainId,
            new[]
            {
                Step(WorldEditorOperation.Start(), "Started"),
                Step(WorldEditorOperation.Advance(300), "Advanced",
                    Check(WorldEditorObservation.LeadFrontContactX, "96")),
                Step(WorldEditorOperation.Damage(StraightRailFixture.FirstSegmentDamageCell, 100), "Removed"),
                Step(WorldEditorOperation.Advance(2000), "Advanced",
                    Check(WorldEditorObservation.SimulationStep, "2300"),
                    Check(WorldEditorObservation.Running, "false"),
                    Check(WorldEditorObservation.HaltReason, "ForwardGap")),
                Step(WorldEditorOperation.Start(), "DisconnectedPath",
                    Check(WorldEditorObservation.LastEdit, "3:rejected:DisconnectedPath@2300:0")),
                Step(WorldEditorOperation.Submit(new RailRepairEdit(new[] { StraightRailFixture.FirstSegmentDamageCell })), "Repaired",
                    Check(WorldEditorObservation.HaltReason, "None"),
                    Check(WorldEditorObservation.Running, "false"),
                    Check(WorldEditorObservation.LastEdit, "4:executed:Repaired@2300:1")),
                Step(WorldEditorOperation.Start(), "Started",
                    Check(WorldEditorObservation.Speed, "0"),
                    Check(WorldEditorObservation.Running, "true"),
                    Check(WorldEditorObservation.LeadFrontContactX, "110"),
                    Check(WorldEditorObservation.EditBoundary, "2300:3")),
                Step(WorldEditorOperation.Stop(2400), "Pending"),
                Step(WorldEditorOperation.Start(atStep: 2400), "Pending"),
                Step(WorldEditorOperation.Stop(2450), "Pending",
                    Check(WorldEditorObservation.PendingEdits, "3")),
                Step(WorldEditorOperation.Advance(101), "Advanced",
                    Check(WorldEditorObservation.SimulationStep, "2401"),
                    Check(WorldEditorObservation.Running, "true"),
                    Check(WorldEditorObservation.PendingEdits, "1"),
                    Check(WorldEditorObservation.LastEdit, "7:executed:Started@2400:1")),
                Step(WorldEditorOperation.Pause(), "Paused",
                    Check(WorldEditorObservation.Running, "true"),
                    Check(WorldEditorObservation.PendingEdits, "1")),
                Step(WorldEditorOperation.Advance(100), "Advanced",
                    Check(WorldEditorObservation.SimulationStep, "2501"),
                    Check(WorldEditorObservation.Running, "false"),
                    Check(WorldEditorObservation.Speed, "0"),
                    Check(WorldEditorObservation.PendingEdits, "0"),
                    Check(WorldEditorObservation.LastEdit, "8:executed:Stopped@2450:0"))
            }),
        new WorldEditorSequence(
            CrossProcessComparisonId,
            WorldEditorConfigurations.StraightRailTrainId,
            new[]
            {
                Step(WorldEditorOperation.Submit(new VehicleManufactureEdit(BridgeFixture.DefinitionId, AuthorityBridgePose)), "Valid"),
                Step(WorldEditorOperation.DamageLatest(BridgeFixture.BridgeCell, 100), "Damaged",
                    Check(WorldEditorObservation.VehicleCount, "4")),
                Step(WorldEditorOperation.Start(), "Started", Check(WorldEditorObservation.Paused, "true")),
                Step(WorldEditorOperation.Submit(new VoxelAddEdit(EditProbe, EditVoxelId, VoxelOrientation.Identity)), "Added"),
                Step(WorldEditorOperation.Submit(new VoxelRemoveEdit(EditProbe), 3), "Pending") with { ConfirmEndHistory = true },
                Step(WorldEditorOperation.Submit(new VoxelRemoveEdit(EditProbe), 3), "Pending"),
                Step(WorldEditorOperation.Submit(new VoxelAddEdit(EditProbe, EditVoxelId, VoxelOrientation.Identity), 3), "Pending"),
                Step(WorldEditorOperation.Submit(new VoxelDamageEdit(new[] { new VoxelDamageTarget(EditProbe, 25) }), 3), "Pending",
                    Check(WorldEditorObservation.PendingEdits, "4")),
                Step(WorldEditorOperation.Advance(3), "Advanced",
                    Check(WorldEditorObservation.SimulationStep, "3"),
                    Check(WorldEditorObservation.PendingEdits, "0")),
                Step(WorldEditorOperation.Damage(EditProbe, 100), "Removed"),
                Step(WorldEditorOperation.Advance(297), "Advanced",
                    Check(WorldEditorObservation.SimulationStep, "300"),
                    Check(WorldEditorObservation.Speed, "4"),
                    Check(WorldEditorObservation.Running, "true")),
                Step(WorldEditorOperation.Damage(StraightRailFixture.FirstSegmentDamageCell, 100), "Removed"),
                Step(WorldEditorOperation.Advance(2000), "Advanced",
                    Check(WorldEditorObservation.SimulationStep, "2300"),
                    Check(WorldEditorObservation.HaltReason, "ForwardGap"),
                    Check(WorldEditorObservation.Running, "false"))
            }),
        new WorldEditorSequence(
            AuthorityManufactureFractureId,
            WorldEditorConfigurations.StraightRailTrainId,
            new[]
            {
                Step(WorldEditorOperation.Submit(new VehicleManufactureEdit(HollowShellFixture.DefinitionId, AuthorityShellPose)), "Valid",
                    Check(WorldEditorObservation.VehicleCount, "3"),
                    Check(WorldEditorObservation.Paused, "true")),
                Step(WorldEditorOperation.RefitLatest(WorldEditorRefit.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId)), "Valid"),
                Step(WorldEditorOperation.RefitLatest(WorldEditorRefit.Add(HollowShellRefit.OutOfEnvelope, HollowShellRefit.MarkerId)), "EnvelopeExceeded"),
                Step(WorldEditorOperation.RefitLatest(WorldEditorRefit.Remove(HollowShellRefit.FirstAdd)), "Valid"),
                Step(WorldEditorOperation.RefitLatest(WorldEditorRefit.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId)), "Valid"),
                Step(WorldEditorOperation.Submit(new VehicleManufactureEdit(BridgeFixture.DefinitionId, AuthorityBridgePose)), "Valid",
                    Check(WorldEditorObservation.VehicleCount, "4"),
                    Check(WorldEditorObservation.Paused, "true")),
                Step(WorldEditorOperation.DamageLatest(BridgeFixture.BridgeCell, 0), "InvalidDamage"),
                Step(WorldEditorOperation.DamageLatest(BridgeFixture.BridgeCell, 100), "Damaged",
                    Check(WorldEditorObservation.VehicleCount, "5"),
                    Check(WorldEditorObservation.Paused, "true"))
            })
    };

    public static WorldEditorSequence? Find(string id) =>
        All.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));

    private static WorldEditorValidationStep Step(WorldEditorOperation operation, string expectedStatus, params WorldEditorCheck[] checks) =>
        new(operation, expectedStatus, checks);

    private static WorldEditorCheck Check(WorldEditorObservation observation, string expected) => new(observation, expected);
}
