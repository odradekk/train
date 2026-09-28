using System.Globalization;
using System.Text;
using System.Text.Json;
using Train.Content;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Domain.Playground;

public enum PlaygroundOperationKind
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

public enum PlaygroundRefitKind
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
public sealed record PlaygroundRefit(
    PlaygroundRefitKind Kind,
    LocalVoxelCoord Cell,
    LocalVoxelCoord Destination = default,
    VoxelDefinitionId DefinitionId = default,
    VoxelOrientation? Orientation = null)
{
    public static PlaygroundRefit Add(LocalVoxelCoord cell, VoxelDefinitionId definitionId, VoxelOrientation? orientation = null) =>
        new(PlaygroundRefitKind.Add, cell, DefinitionId: definitionId, Orientation: orientation);
    public static PlaygroundRefit Remove(LocalVoxelCoord cell) => new(PlaygroundRefitKind.Remove, cell);
    public static PlaygroundRefit Move(LocalVoxelCoord cell, LocalVoxelCoord destination) =>
        new(PlaygroundRefitKind.Move, cell, destination);
    public static PlaygroundRefit Rotate(LocalVoxelCoord cell, VoxelOrientation orientation) =>
        new(PlaygroundRefitKind.Rotate, cell, Orientation: orientation);
    public static PlaygroundRefit Replace(LocalVoxelCoord cell, VoxelDefinitionId definitionId, VoxelOrientation? orientation = null) =>
        new(PlaygroundRefitKind.Replace, cell, DefinitionId: definitionId, Orientation: orientation);

    public CompositeRefitOperation? Resolve(CompositeQuery vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        var orientation = Orientation ?? VoxelOrientation.Identity;
        if (Kind == PlaygroundRefitKind.Add)
            return CompositeRefitOperation.Add(Cell, DefinitionId, orientation);
        if (Kind == PlaygroundRefitKind.Replace)
            return CompositeRefitOperation.Replace(Cell, DefinitionId, orientation);
        var part = vehicle.Parts.FirstOrDefault(item => item.Cell == Cell);
        if (part is null)
            return null;
        return Kind switch
        {
            PlaygroundRefitKind.Remove => CompositeRefitOperation.Remove(part.Id),
            PlaygroundRefitKind.Move => CompositeRefitOperation.Move(part.Id, Destination),
            PlaygroundRefitKind.Rotate => CompositeRefitOperation.Rotate(part.Id, orientation),
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null)
        };
    }

    public string Describe() => Kind switch
    {
        PlaygroundRefitKind.Move => $"move {Cell.X},{Cell.Y},{Cell.Z}->{Destination.X},{Destination.Y},{Destination.Z}",
        PlaygroundRefitKind.Add or PlaygroundRefitKind.Replace => $"{Kind.ToString().ToLowerInvariant()} {Cell.X},{Cell.Y},{Cell.Z} {DefinitionId.Value}",
        _ => $"{Kind.ToString().ToLowerInvariant()} {Cell.X},{Cell.Y},{Cell.Z}"
    };
}

/// <summary>A formal session operation shared by the manual Playground controls and the automatic validation runner.</summary>
public sealed record PlaygroundOperation(
    PlaygroundOperationKind Kind,
    ulong Steps = 0,
    RailHeading Heading = RailHeading.AlongAlignment,
    WorldVoxelCoord Cell = default,
    int Amount = 0,
    WorldEdit? Edit = null,
    ulong? AtStep = null,
    IReadOnlyList<PlaygroundRefit>? Refits = null,
    LocalVoxelCoord VehicleCell = default)
{
    public const string NoVehicleStatus = "NoVehicle";
    public const string NoPartStatus = "NoPart";

    /// <summary>Submits a parsed edit; <paramref name="atStep"/> queues it for that step instead of the current boundary.</summary>
    public static PlaygroundOperation Submit(WorldEdit edit, ulong? atStep = null) =>
        new(PlaygroundOperationKind.Edit, Edit: edit ?? throw new ArgumentNullException(nameof(edit)), AtStep: atStep);
    public static PlaygroundOperation RefitLatest(IReadOnlyList<PlaygroundRefit> refits, ulong? atStep = null) =>
        new(PlaygroundOperationKind.RefitLatestVehicle, Refits: refits, AtStep: atStep);
    public static PlaygroundOperation RefitLatest(PlaygroundRefit refit, ulong? atStep = null) =>
        RefitLatest(new[] { refit }, atStep);
    public static PlaygroundOperation DamageLatest(LocalVoxelCoord cell, int amount, ulong? atStep = null) =>
        new(PlaygroundOperationKind.DamageLatestVehicle, Amount: amount, AtStep: atStep, VehicleCell: cell);

    public static PlaygroundOperation Pause() => new(PlaygroundOperationKind.Pause);
    public static PlaygroundOperation Resume() => new(PlaygroundOperationKind.Resume);
    public static PlaygroundOperation Advance(ulong steps) => new(PlaygroundOperationKind.Advance, steps);
    public static PlaygroundOperation Start(RailHeading heading = RailHeading.AlongAlignment, ulong? atStep = null) =>
        new(PlaygroundOperationKind.StartTrain, Heading: heading, AtStep: atStep);
    public static PlaygroundOperation Stop(ulong? atStep = null) => new(PlaygroundOperationKind.StopTrain, AtStep: atStep);
    public static PlaygroundOperation Damage(WorldVoxelCoord cell, int amount) =>
        new(PlaygroundOperationKind.DamageVoxel, Cell: cell, Amount: amount);

    public string Apply(VoxelDevelopmentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Kind switch
        {
            PlaygroundOperationKind.Pause => session.PauseSimulation().Status.ToString(),
            PlaygroundOperationKind.Resume => session.ResumeSimulation().Status.ToString(),
            PlaygroundOperationKind.Advance => session.AdvanceSimulation(Steps).Status.ToString(),
            PlaygroundOperationKind.StartTrain => session.StartTrain(Heading, AtStep).Status.ToString(),
            PlaygroundOperationKind.StopTrain => session.StopTrain(AtStep).Status.ToString(),
            PlaygroundOperationKind.DamageVoxel => session.Damage(Cell, Amount).Status.ToString(),
            PlaygroundOperationKind.Edit or PlaygroundOperationKind.RefitLatestVehicle or PlaygroundOperationKind.DamageLatestVehicle =>
                ResolveEdit(session, out var edit) is { } refused ? refused : session.SubmitEdit(edit!, AtStep).Status,
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
        if (Kind == PlaygroundOperationKind.Edit)
            return edit is null ? throw new InvalidOperationException("Edit operation has no payload.") : null;
        if (Kind is not (PlaygroundOperationKind.RefitLatestVehicle or PlaygroundOperationKind.DamageLatestVehicle))
            throw new InvalidOperationException($"{Kind} is not an edit operation.");

        var vehicle = session.QueryComposites().OrderByDescending(item => item.Id.Value).FirstOrDefault();
        if (vehicle is null)
            return NoVehicleStatus;
        if (Kind == PlaygroundOperationKind.DamageLatestVehicle)
        {
            edit = new VehicleDamageEdit(new[] { new CompositeDamageTarget(vehicle.Id, VehicleCell, Amount) });
            return null;
        }

        var operations = new List<CompositeRefitOperation>();
        foreach (var refit in Refits ?? Array.Empty<PlaygroundRefit>())
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
                case PlaygroundOperationKind.Pause:
                    writer.WriteString("kind", "pause");
                    WriteAtStep(writer);
                    break;
                case PlaygroundOperationKind.Resume:
                    writer.WriteString("kind", "resume");
                    WriteAtStep(writer);
                    break;
                case PlaygroundOperationKind.Advance:
                    writer.WriteString("kind", "advance");
                    WriteAtStep(writer);
                    writer.WriteNumber("steps", Steps);
                    break;
                case PlaygroundOperationKind.StartTrain:
                    writer.WriteString("kind", "start");
                    WriteAtStep(writer);
                    writer.WriteString("heading", Heading.ToString());
                    break;
                case PlaygroundOperationKind.StopTrain:
                    writer.WriteString("kind", "stop");
                    WriteAtStep(writer);
                    break;
                case PlaygroundOperationKind.DamageVoxel:
                    writer.WriteString("kind", "damage_voxel");
                    WriteAtStep(writer);
                    WriteCoord(writer, "position", Cell.X, Cell.Y, Cell.Z);
                    writer.WriteNumber("amount", Amount);
                    break;
                case PlaygroundOperationKind.Edit:
                    writer.WriteString("kind", "edit");
                    WriteAtStep(writer);
                    writer.WritePropertyName("edit");
                    writer.WriteRawValue(Edit?.Canonical ?? throw new InvalidOperationException("Edit operation has no payload."));
                    break;
                case PlaygroundOperationKind.RefitLatestVehicle:
                    writer.WriteString("kind", "refit_latest");
                    WriteAtStep(writer);
                    writer.WriteStartArray("request");
                    foreach (var refit in Refits ?? Array.Empty<PlaygroundRefit>())
                        WriteRefitRequest(writer, refit);
                    writer.WriteEndArray();
                    WriteResolved(writer, session);
                    break;
                case PlaygroundOperationKind.DamageLatestVehicle:
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
        PlaygroundOperationKind.Advance => $"advance {Steps.ToString(CultureInfo.InvariantCulture)}",
        PlaygroundOperationKind.StartTrain => $"start {Heading}{DescribeAt()}",
        PlaygroundOperationKind.StopTrain => $"stop{DescribeAt()}",
        PlaygroundOperationKind.DamageVoxel => $"damage {Cell.X},{Cell.Y},{Cell.Z} {Amount.ToString(CultureInfo.InvariantCulture)}",
        PlaygroundOperationKind.Edit => $"edit {WorldEditCodec.WireKind(Edit!.Kind)}{DescribeAt()}",
        PlaygroundOperationKind.RefitLatestVehicle =>
            $"refit latest {string.Join("; ", (Refits ?? Array.Empty<PlaygroundRefit>()).Select(refit => refit.Describe()))}{DescribeAt()}",
        PlaygroundOperationKind.DamageLatestVehicle =>
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

    private static void WriteRefitRequest(Utf8JsonWriter writer, PlaygroundRefit refit)
    {
        writer.WriteStartObject();
        writer.WriteString("op", refit.Kind.ToString().ToLowerInvariant());
        WriteCoord(writer, "cell", refit.Cell.X, refit.Cell.Y, refit.Cell.Z);
        switch (refit.Kind)
        {
            case PlaygroundRefitKind.Add:
            case PlaygroundRefitKind.Replace:
                writer.WriteString("definition_id", refit.DefinitionId.Value);
                writer.WriteString("orientation", (refit.Orientation ?? VoxelOrientation.Identity).ToString());
                break;
            case PlaygroundRefitKind.Remove:
                break;
            case PlaygroundRefitKind.Move:
                WriteCoord(writer, "destination", refit.Destination.X, refit.Destination.Y, refit.Destination.Z);
                break;
            case PlaygroundRefitKind.Rotate:
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

public enum PlaygroundObservation
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

public sealed record PlaygroundCheck(PlaygroundObservation Observation, string Expected)
{
    public static string Observe(VoxelDevelopmentSession session, PlaygroundObservation observation)
    {
        ArgumentNullException.ThrowIfNull(session);
        var assembly = session.QueryTrainAssemblies().FirstOrDefault();
        return observation switch
        {
            PlaygroundObservation.SimulationStep => session.SimulationStep.ToString(CultureInfo.InvariantCulture),
            PlaygroundObservation.Paused => session.SimulationPaused ? "true" : "false",
            PlaygroundObservation.AssemblyCount => session.QueryTrainAssemblies().Count.ToString(CultureInfo.InvariantCulture),
            PlaygroundObservation.LeadFrontContactX => assembly is null ? "none" : Number(assembly.Vehicles[0].FrontContact.X),
            PlaygroundObservation.Speed => assembly is null ? "none" : Number(assembly.Speed),
            PlaygroundObservation.Running => assembly is null ? "none" : assembly.Running ? "true" : "false",
            PlaygroundObservation.HaltReason => assembly is null ? "none" : assembly.HaltReason.ToString(),
            PlaygroundObservation.EditBoundary => session.EditBoundary.ToString(),
            PlaygroundObservation.NextEditId => session.NextEditId.ToString(CultureInfo.InvariantCulture),
            PlaygroundObservation.PendingEdits => session.QueryPendingEdits().Count.ToString(CultureInfo.InvariantCulture),
            PlaygroundObservation.LastEdit => session.QueryEditLog() is { Count: > 0 } log ? DescribeRecord(log[^1]) : "none",
            PlaygroundObservation.VehicleCount => session.QueryComposites().Count.ToString(CultureInfo.InvariantCulture),
            PlaygroundObservation.VoxelCount => session.World.VoxelCount.ToString(CultureInfo.InvariantCulture),
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

public sealed record PlaygroundValidationStep(
    PlaygroundOperation Operation,
    string ExpectedStatus,
    IReadOnlyList<PlaygroundCheck> Checkpoint);

/// <summary>Validation steps live outside the fixed configuration and only reference it by id.</summary>
public sealed record PlaygroundSequence(
    string Id,
    string ConfigurationId,
    IReadOnlyList<PlaygroundValidationStep> Steps);

public sealed record PlaygroundCheckResult(PlaygroundObservation Observation, string Expected, string Observed)
{
    public bool Passed => string.Equals(Expected, Observed, StringComparison.Ordinal);
}

public sealed record PlaygroundStepResult(
    int Index,
    string Operation,
    string ExpectedStatus,
    string ActualStatus,
    IReadOnlyList<PlaygroundCheckResult> Checks)
{
    public bool Passed =>
        string.Equals(ExpectedStatus, ActualStatus, StringComparison.Ordinal) &&
        Checks.All(check => check.Passed);
}

public sealed record PlaygroundSequenceReport(
    string SequenceId,
    string ConfigurationId,
    PlaygroundEstablishStatus EstablishStatus,
    IReadOnlyList<PlaygroundStepResult> Steps,
    string? FinalFingerprint)
{
    public bool Passed =>
        EstablishStatus == PlaygroundEstablishStatus.Established &&
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

public static class PlaygroundValidation
{
    public static PlaygroundSequenceReport Run(
        PlaygroundSequence sequence,
        VoxelCatalog catalog,
        CompositeCatalog composites,
        IWorldSaveStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var status = PlaygroundSession.TryEstablish(sequence.ConfigurationId, catalog, composites, store, out var session);
        if (status != PlaygroundEstablishStatus.Established)
            return new PlaygroundSequenceReport(sequence.Id, sequence.ConfigurationId, status, Array.Empty<PlaygroundStepResult>(), null);

        try
        {
            return Execute(sequence, session!);
        }
        finally
        {
            session!.Detach();
        }
    }

    public static PlaygroundSequenceReport Execute(PlaygroundSequence sequence, VoxelDevelopmentSession session)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(session);
        if (!string.Equals(session.WorldSource?.ConfigurationId, sequence.ConfigurationId, StringComparison.Ordinal))
            return new PlaygroundSequenceReport(
                sequence.Id,
                sequence.ConfigurationId,
                PlaygroundEstablishStatus.ConfigurationMismatch,
                Array.Empty<PlaygroundStepResult>(),
                null);

        var results = new List<PlaygroundStepResult>();
        for (var index = 0; index < sequence.Steps.Count; index++)
        {
            var step = sequence.Steps[index];
            var actual = step.Operation.Apply(session);
            var checks = step.Checkpoint
                .Select(check => new PlaygroundCheckResult(check.Observation, check.Expected, PlaygroundCheck.Observe(session, check.Observation)))
                .ToArray();
            results.Add(new PlaygroundStepResult(index, step.Operation.Describe(), step.ExpectedStatus, actual, checks));
        }

        return new PlaygroundSequenceReport(
            sequence.Id,
            sequence.ConfigurationId,
            PlaygroundEstablishStatus.Established,
            results,
            WorldSaveAuthority.Fingerprint(session));
    }
}

public static class PlaygroundSequences
{
    public const string StraightRailRunAndHaltId = "straight-rail-run-and-halt";
    public const string StraightRailRejectionsId = "straight-rail-rejections";
    public const string StraightRailOrderedEditsId = "straight-rail-ordered-edits";
    public const string StraightRailRepairRestartId = "straight-rail-repair-restart";
    public const string AuthorityManufactureFractureId = "authority-manufacture-fracture";

    public static readonly WorldVoxelCoord EditProbe = new(60, 3, 20);
    public static readonly VoxelDefinitionId EditVoxelId = new("core", "ferrous-ore");
    public static readonly CompositeTransform EditShellPose = CompositeTransform.Translation(60, 6, 24);
    public static readonly CompositeTransform AuthorityShellPose = CompositeTransform.Translation(40, 8, 8);
    public static readonly CompositeTransform AuthorityBridgePose = CompositeTransform.Translation(200, 8, 8);

    public static IReadOnlyList<PlaygroundSequence> All { get; } = new[]
    {
        new PlaygroundSequence(
            StraightRailOrderedEditsId,
            PlaygroundConfigurations.StraightRailTrainId,
            new[]
            {
                Step(PlaygroundOperation.Start(), "Started",
                    Check(PlaygroundObservation.Paused, "true"),
                    Check(PlaygroundObservation.EditBoundary, "0:1"),
                    Check(PlaygroundObservation.LastEdit, "1:executed:Started@0:0")),
                Step(PlaygroundOperation.Submit(new VoxelAddEdit(EditProbe, EditVoxelId, VoxelOrientation.Identity)), "Added",
                    Check(PlaygroundObservation.SimulationStep, "0"),
                    Check(PlaygroundObservation.EditBoundary, "0:2"),
                    Check(PlaygroundObservation.NextEditId, "3")),
                Step(PlaygroundOperation.Submit(new VoxelDamageEdit(new[] { new VoxelDamageTarget(EditProbe, 25) })), "Damaged",
                    Check(PlaygroundObservation.EditBoundary, "0:3")),
                Step(PlaygroundOperation.Submit(new VehicleManufactureEdit(HollowShellFixture.DefinitionId, EditShellPose)), "Valid",
                    Check(PlaygroundObservation.VehicleCount, "3")),
                Step(PlaygroundOperation.RefitLatest(PlaygroundRefit.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId)), "Valid"),
                Step(PlaygroundOperation.DamageLatest(HollowShellRefit.FirstAdd, 25), "Damaged"),
                Step(PlaygroundOperation.RefitLatest(PlaygroundRefit.Move(HollowShellRefit.FirstAdd, HollowShellRefit.Moved)), "Valid"),
                Step(PlaygroundOperation.RefitLatest(PlaygroundRefit.Rotate(HollowShellRefit.Moved, HollowShellRefit.MarkerOrientation)), "Valid"),
                Step(PlaygroundOperation.RefitLatest(PlaygroundRefit.Replace(HollowShellRefit.Moved, HollowShellRefit.ReplacementId)), "Valid"),
                Step(PlaygroundOperation.RefitLatest(PlaygroundRefit.Remove(HollowShellRefit.Moved)), "Valid"),
                Step(PlaygroundOperation.RefitLatest(new[]
                    {
                        PlaygroundRefit.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId),
                        PlaygroundRefit.Add(HollowShellRefit.SecondAdd, HollowShellRefit.MarkerId)
                    }), "Valid",
                    Check(PlaygroundObservation.SimulationStep, "0"),
                    Check(PlaygroundObservation.LeadFrontContactX, "80"),
                    Check(PlaygroundObservation.EditBoundary, "0:11"),
                    Check(PlaygroundObservation.LastEdit, "11:executed:Valid@0:10")),
                Step(PlaygroundOperation.RefitLatest(PlaygroundRefit.Remove(HollowShellRefit.Moved)), PlaygroundOperation.NoPartStatus,
                    Check(PlaygroundObservation.EditBoundary, "0:11"),
                    Check(PlaygroundObservation.NextEditId, "12")),
                Step(PlaygroundOperation.Submit(new VoxelRemoveEdit(EditProbe), 3), "Pending",
                    Check(PlaygroundObservation.PendingEdits, "1")),
                Step(PlaygroundOperation.Submit(new VoxelAddEdit(EditProbe, EditVoxelId, VoxelOrientation.Identity), 3), "Pending"),
                Step(PlaygroundOperation.RefitLatest(PlaygroundRefit.Add(HollowShellRefit.OutOfEnvelope, HollowShellRefit.MarkerId), 3), "Pending",
                    Check(PlaygroundObservation.PendingEdits, "3"),
                    Check(PlaygroundObservation.EditBoundary, "0:14")),
                Step(PlaygroundOperation.Advance(3), "Advanced",
                    Check(PlaygroundObservation.SimulationStep, "3"),
                    Check(PlaygroundObservation.PendingEdits, "0"),
                    Check(PlaygroundObservation.EditBoundary, "3:3"),
                    Check(PlaygroundObservation.LastEdit, "14:rejected:EnvelopeExceeded@3:2")),
                Step(PlaygroundOperation.Submit(new VoxelAddEdit(EditProbe, EditVoxelId, VoxelOrientation.Identity)), "Occupied",
                    Check(PlaygroundObservation.EditBoundary, "3:4"),
                    Check(PlaygroundObservation.LastEdit, "15:rejected:Occupied@3:3")),
                Step(PlaygroundOperation.Advance(1), "Advanced",
                    Check(PlaygroundObservation.EditBoundary, "4:0"),
                    Check(PlaygroundObservation.NextEditId, "16"))
            }),
        new PlaygroundSequence(
            StraightRailRunAndHaltId,
            PlaygroundConfigurations.StraightRailTrainId,
            new[]
            {
                Step(PlaygroundOperation.Start(), "Started",
                    Check(PlaygroundObservation.SimulationStep, "0"),
                    Check(PlaygroundObservation.Paused, "true")),
                Step(PlaygroundOperation.Advance(1), "Advanced",
                    Check(PlaygroundObservation.SimulationStep, "1")),
                Step(PlaygroundOperation.Advance(299), "Advanced",
                    Check(PlaygroundObservation.SimulationStep, "300"),
                    Check(PlaygroundObservation.LeadFrontContactX, "96"),
                    Check(PlaygroundObservation.Speed, "4"),
                    Check(PlaygroundObservation.Running, "true")),
                Step(PlaygroundOperation.Damage(StraightRailFixture.FirstSegmentDamageCell, 100), "Removed"),
                Step(PlaygroundOperation.Advance(2000), "Advanced",
                    Check(PlaygroundObservation.SimulationStep, "2300"),
                    Check(PlaygroundObservation.Speed, "0"),
                    Check(PlaygroundObservation.Running, "false"),
                    Check(PlaygroundObservation.HaltReason, "ForwardGap"))
            }),
        new PlaygroundSequence(
            StraightRailRejectionsId,
            PlaygroundConfigurations.StraightRailTrainId,
            new[]
            {
                Step(PlaygroundOperation.Start(RailHeading.AgainstAlignment), "ReverseUnsupported",
                    Check(PlaygroundObservation.Running, "false"),
                    Check(PlaygroundObservation.LeadFrontContactX, "80")),
                Step(PlaygroundOperation.Start(), "Started"),
                Step(PlaygroundOperation.Damage(StraightRailFixture.FirstSegmentDamageCell, 100), "Removed"),
                Step(PlaygroundOperation.Advance(2000), "Advanced",
                    Check(PlaygroundObservation.HaltReason, "ForwardGap")),
                Step(PlaygroundOperation.Start(), "DisconnectedPath",
                    Check(PlaygroundObservation.Speed, "0"),
                    Check(PlaygroundObservation.Running, "false"),
                    Check(PlaygroundObservation.HaltReason, "ForwardGap")),
                Step(PlaygroundOperation.Advance(ulong.MaxValue), "StepOverflow",
                    Check(PlaygroundObservation.SimulationStep, "2000"))
            }),
        new PlaygroundSequence(
            StraightRailRepairRestartId,
            PlaygroundConfigurations.StraightRailTrainId,
            new[]
            {
                Step(PlaygroundOperation.Start(), "Started"),
                Step(PlaygroundOperation.Advance(300), "Advanced",
                    Check(PlaygroundObservation.LeadFrontContactX, "96")),
                Step(PlaygroundOperation.Damage(StraightRailFixture.FirstSegmentDamageCell, 100), "Removed"),
                Step(PlaygroundOperation.Advance(2000), "Advanced",
                    Check(PlaygroundObservation.SimulationStep, "2300"),
                    Check(PlaygroundObservation.Running, "false"),
                    Check(PlaygroundObservation.HaltReason, "ForwardGap")),
                Step(PlaygroundOperation.Start(), "DisconnectedPath",
                    Check(PlaygroundObservation.LastEdit, "3:rejected:DisconnectedPath@2300:0")),
                Step(PlaygroundOperation.Submit(new RailRepairEdit(new[] { StraightRailFixture.FirstSegmentDamageCell })), "Repaired",
                    Check(PlaygroundObservation.HaltReason, "None"),
                    Check(PlaygroundObservation.Running, "false"),
                    Check(PlaygroundObservation.LastEdit, "4:executed:Repaired@2300:1")),
                Step(PlaygroundOperation.Start(), "Started",
                    Check(PlaygroundObservation.Speed, "0"),
                    Check(PlaygroundObservation.Running, "true"),
                    Check(PlaygroundObservation.LeadFrontContactX, "110"),
                    Check(PlaygroundObservation.EditBoundary, "2300:3")),
                Step(PlaygroundOperation.Stop(2400), "Pending"),
                Step(PlaygroundOperation.Start(atStep: 2400), "Pending"),
                Step(PlaygroundOperation.Stop(2450), "Pending",
                    Check(PlaygroundObservation.PendingEdits, "3")),
                Step(PlaygroundOperation.Advance(101), "Advanced",
                    Check(PlaygroundObservation.SimulationStep, "2401"),
                    Check(PlaygroundObservation.Running, "true"),
                    Check(PlaygroundObservation.PendingEdits, "1"),
                    Check(PlaygroundObservation.LastEdit, "7:executed:Started@2400:1")),
                Step(PlaygroundOperation.Pause(), "Paused",
                    Check(PlaygroundObservation.Running, "true"),
                    Check(PlaygroundObservation.PendingEdits, "1")),
                Step(PlaygroundOperation.Advance(100), "Advanced",
                    Check(PlaygroundObservation.SimulationStep, "2501"),
                    Check(PlaygroundObservation.Running, "false"),
                    Check(PlaygroundObservation.Speed, "0"),
                    Check(PlaygroundObservation.PendingEdits, "0"),
                    Check(PlaygroundObservation.LastEdit, "8:executed:Stopped@2450:0"))
            }),
        new PlaygroundSequence(
            AuthorityManufactureFractureId,
            PlaygroundConfigurations.StraightRailTrainId,
            new[]
            {
                Step(PlaygroundOperation.Submit(new VehicleManufactureEdit(HollowShellFixture.DefinitionId, AuthorityShellPose)), "Valid",
                    Check(PlaygroundObservation.VehicleCount, "3"),
                    Check(PlaygroundObservation.Paused, "true")),
                Step(PlaygroundOperation.RefitLatest(PlaygroundRefit.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId)), "Valid"),
                Step(PlaygroundOperation.RefitLatest(PlaygroundRefit.Add(HollowShellRefit.OutOfEnvelope, HollowShellRefit.MarkerId)), "EnvelopeExceeded"),
                Step(PlaygroundOperation.RefitLatest(PlaygroundRefit.Remove(HollowShellRefit.FirstAdd)), "Valid"),
                Step(PlaygroundOperation.RefitLatest(PlaygroundRefit.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId)), "Valid"),
                Step(PlaygroundOperation.Submit(new VehicleManufactureEdit(BridgeFixture.DefinitionId, AuthorityBridgePose)), "Valid",
                    Check(PlaygroundObservation.VehicleCount, "4"),
                    Check(PlaygroundObservation.Paused, "true")),
                Step(PlaygroundOperation.DamageLatest(BridgeFixture.BridgeCell, 0), "InvalidDamage"),
                Step(PlaygroundOperation.DamageLatest(BridgeFixture.BridgeCell, 100), "Damaged",
                    Check(PlaygroundObservation.VehicleCount, "5"),
                    Check(PlaygroundObservation.Paused, "true"))
            })
    };

    public static PlaygroundSequence? Find(string id) =>
        All.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));

    private static PlaygroundValidationStep Step(PlaygroundOperation operation, string expectedStatus, params PlaygroundCheck[] checks) =>
        new(operation, expectedStatus, checks);

    private static PlaygroundCheck Check(PlaygroundObservation observation, string expected) => new(observation, expected);
}
