using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Rails;

namespace Train.Domain.Voxels;

/// <summary>
/// The single validation and transaction entry for voxel and vehicle modifications. Every accepted edit takes the
/// next in-step sequence at the current boundary; immediate edits run there, scheduled edits run when the clock
/// reaches their target boundary, in acceptance order.
/// </summary>
public sealed partial class VoxelDevelopmentSession
{
    public const int EditLogCapacity = 256;

    private readonly List<WorldEditRecord> _pendingEdits = new();
    private readonly LinkedList<WorldEditRecord> _editLog = new();

    public ulong NextEditId { get; private set; } = 1;
    public ulong EditBoundarySequence { get; private set; }
    public SimulationPosition EditBoundary => new(SimulationStep, EditBoundarySequence);
    public SimulationFault? Fault { get; private set; }
    public ulong EditLogCount { get; private set; }
    public string EditLogDigest { get; private set; } = WorldEditCodec.EmptyLogDigest;

    /// <summary>Test seam for internal invariant faults: runs inside the edit transaction after the business change.</summary>
    internal Action<WorldEditRecord>? EditFaultProbe { get; set; }

    // The typed entries below keep their business statuses for payloads a WorldEdit cannot encode (missing ids,
    // non-orthogonal orientations, non-finite poses); such calls are never accepted, so they take no id or sequence.

    public WorldCommandResult Add(WorldVoxelCoord position, VoxelDefinitionId definitionId, VoxelOrientation orientation)
    {
        if (definitionId.Namespace is null)
            return new WorldCommandResult(WorldCommandStatus.UnknownDefinition, position);
        if (!orientation.IsOrthogonal)
            return new WorldCommandResult(WorldCommandStatus.DisallowedOrientation, position);
        var submission = SubmitEdit(new VoxelAddEdit(position, definitionId, orientation));
        return submission.Result as WorldCommandResult ?? new WorldCommandResult(RefusedVoxelStatus(submission), position);
    }

    public WorldCommandResult Remove(WorldVoxelCoord position)
    {
        var submission = SubmitEdit(new VoxelRemoveEdit(position));
        return submission.Result as WorldCommandResult ?? new WorldCommandResult(RefusedVoxelStatus(submission), position);
    }

    public WorldDamageResult Damage(WorldVoxelCoord position, int amount) =>
        Damage(new[] { new VoxelDamageTarget(position, amount) });

    public WorldDamageResult Damage(IReadOnlyList<VoxelDamageTarget> targets)
    {
        var edit = new VoxelDamageEdit(targets);
        var submission = SubmitEdit(edit);
        return submission.Result is WorldDamageResult result ? edit.ToCallerOrder(result) : new WorldDamageResult(
            RefusedVoxelStatus(submission),
            targets.Count == 0 ? default : targets[0].Position,
            Array.Empty<WorldDamageTargetResult>());
    }

    public CompositeDamageResult Damage(EntityId vehicleId, LocalVoxelCoord cell, int amount) =>
        Damage(new[] { new CompositeDamageTarget(vehicleId, cell, amount) });

    public CompositeDamageResult Damage(IReadOnlyList<CompositeDamageTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Any(target => target.VehicleId.Value == 0))
            return new CompositeDamageResult(CompositeDamageStatus.UnknownVehicle, targets.First(target => target.VehicleId.Value == 0).VehicleId);
        var edit = new VehicleDamageEdit(targets);
        var submission = SubmitEdit(edit);
        return submission.Result is CompositeDamageResult result ? edit.ToCallerOrder(result) : new CompositeDamageResult(
            submission.Refusal == WorldEditRefusal.InputExhausted ? CompositeDamageStatus.InputExhausted : CompositeDamageStatus.SimulationFaulted,
            targets.Count == 0 ? null : targets[0].VehicleId);
    }

    public CompositeManufactureResult Manufacture(CompositeDefinitionId definitionId, CompositeTransform? transform = null)
    {
        var pose = transform ?? HollowShellFixture.DefaultTransform;
        if (definitionId.Namespace is null)
            return new CompositeManufactureResult(ConstructionStatus.UnknownDefinition);
        if (!pose.IsFinite)
            return new CompositeManufactureResult(ConstructionStatus.NonFiniteTransform);
        var submission = SubmitEdit(new VehicleManufactureEdit(definitionId, pose));
        return submission.Result as CompositeManufactureResult ?? new CompositeManufactureResult(RefusedConstructionStatus(submission));
    }

    public CompositeRefitResult Refit(EntityId vehicleId, CompositeRefitOperation operation) =>
        Refit(vehicleId, new[] { operation });

    public CompositeRefitResult Refit(EntityId vehicleId, IReadOnlyList<CompositeRefitOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (UnencodableRefit(vehicleId, operations) is { } status)
            return new CompositeRefitResult(status, vehicleId);
        var submission = SubmitEdit(new VehicleRefitEdit(vehicleId, operations));
        return submission.Result as CompositeRefitResult ?? new CompositeRefitResult(RefusedConstructionStatus(submission), vehicleId);
    }

    /// <summary>
    /// Accepts one parsed edit at the current boundary. <paramref name="atStep"/> defaults to the current step
    /// (execute now); a later step queues the edit for that boundary. Past steps, exhausted counters and a faulted
    /// session are refused without consuming an id or a sequence.
    /// </summary>
    public WorldEditSubmission SubmitEdit(WorldEdit edit, ulong? atStep = null)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (Fault is not null)
            return new WorldEditSubmission(WorldEditRefusal.SimulationFaulted, null, null);
        var target = atStep ?? SimulationStep;
        if (target < SimulationStep)
            return new WorldEditSubmission(WorldEditRefusal.PastStep, null, null);
        if (NextEditId == ulong.MaxValue || EditBoundarySequence == ulong.MaxValue)
            return new WorldEditSubmission(WorldEditRefusal.InputExhausted, null, null);

        var accepted = new SimulationPosition(SimulationStep, EditBoundarySequence);
        var record = new WorldEditRecord(NextEditId, edit, accepted, target);
        NextEditId++;
        EditBoundarySequence++;
        _pendingEdits.Add(record);
        if (target > SimulationStep)
        {
            Changed?.Invoke();
            return new WorldEditSubmission(WorldEditRefusal.None, record, null);
        }

        return Execute(record, accepted);
    }

    public IReadOnlyList<WorldEditRecord> QueryPendingEdits() => _pendingEdits.ToArray();

    /// <summary>The most recent executed, rejected, or faulted records (oldest first), bounded by <see cref="EditLogCapacity"/>.</summary>
    public IReadOnlyList<WorldEditRecord> QueryEditLog() => _editLog.ToArray();

    public WorldEditQueueState CaptureEditQueue() => new(NextEditId, EditBoundarySequence, QueryPendingEdits());

    /// <summary>Runs every pending edit due at the current boundary in acceptance order; false when one faults.</summary>
    private bool ExecuteDueEdits()
    {
        while (_pendingEdits.FirstOrDefault(item => item.TargetStep == SimulationStep) is { } due)
        {
            if (EditBoundarySequence == ulong.MaxValue)
            {
                Fault = new SimulationFault(due.Id, due.Edit.Kind, EditBoundary, "BoundarySequenceExhausted", "No in-step sequence is left at this boundary.");
                return false;
            }

            var position = new SimulationPosition(SimulationStep, EditBoundarySequence++);
            if (Execute(due, position).Record!.State == WorldEditState.Faulted)
                return false;
        }

        return true;
    }

    private bool HasDueEdits() => _pendingEdits.Any(item => item.TargetStep == SimulationStep);

    private WorldEditSubmission Execute(WorldEditRecord pending, SimulationPosition position)
    {
        var rollback = CaptureEditRollback();
        try
        {
            var (succeeded, status, result) = Dispatch(pending.Edit);
            EditFaultProbe?.Invoke(pending);
            var done = pending with
            {
                State = succeeded ? WorldEditState.Executed : WorldEditState.Rejected,
                ExecutedAt = position,
                Status = status
            };
            _pendingEdits.Remove(pending);
            AppendLog(done);
            return new WorldEditSubmission(WorldEditRefusal.None, done, result);
        }
        catch (Exception exception)
        {
            RestoreEditRollback(rollback);
            var faulted = pending with
            {
                State = WorldEditState.Faulted,
                ExecutedAt = position,
                Status = exception.GetType().Name
            };
            Fault = new SimulationFault(pending.Id, pending.Edit.Kind, position, exception.GetType().Name, exception.Message);
            AppendLog(faulted);
            Changed?.Invoke();
            return new WorldEditSubmission(WorldEditRefusal.None, faulted, null);
        }
    }

    private (bool Succeeded, string Status, object Result) Dispatch(WorldEdit edit)
    {
        switch (edit)
        {
            case VoxelAddEdit add:
                var added = AddCore(add.Position, add.DefinitionId, add.Orientation);
                return (added.Succeeded, added.Status.ToString(), added);
            case VoxelRemoveEdit remove:
                var removed = RemoveCore(remove.Position);
                return (removed.Succeeded, removed.Status.ToString(), removed);
            case VoxelDamageEdit damage:
                var damaged = DamageVoxelsCore(damage.Targets);
                return (damaged.Succeeded, damaged.Status.ToString(), damaged);
            case VehicleManufactureEdit manufacture:
                var manufactured = ManufactureCore(manufacture.DefinitionId, manufacture.Transform);
                return (manufactured.Succeeded, manufactured.Status.ToString(), manufactured);
            case VehicleDamageEdit vehicleDamage:
                var vehicleDamaged = DamageVehiclesCore(vehicleDamage.Targets);
                return (vehicleDamaged.Succeeded, vehicleDamaged.Status.ToString(), vehicleDamaged);
            case VehicleRefitEdit refit:
                var refitted = RefitCore(refit.VehicleId, refit.Operations);
                return (refitted.Succeeded, refitted.Status.ToString(), refitted);
            default:
                throw new InvalidOperationException($"Unknown world edit kind {edit.Kind}.");
        }
    }

    private void AppendLog(WorldEditRecord record)
    {
        _editLog.AddLast(record);
        while (_editLog.Count > EditLogCapacity)
            _editLog.RemoveFirst();
        EditLogDigest = WorldEditCodec.ChainLogDigest(EditLogDigest, record);
        if (EditLogCount < ulong.MaxValue)
            EditLogCount++;
    }

    private void ResetEditState(WorldEditQueueState state)
    {
        NextEditId = state.NextEditId;
        EditBoundarySequence = state.BoundarySequence;
        _pendingEdits.Clear();
        _pendingEdits.AddRange(state.Pending.OrderBy(item => item.Id));
        _editLog.Clear();
        EditLogCount = 0;
        EditLogDigest = WorldEditCodec.EmptyLogDigest;
        Fault = null;
    }

    private static ConstructionStatus? UnencodableRefit(EntityId vehicleId, IReadOnlyList<CompositeRefitOperation> operations)
    {
        if (vehicleId.Value == 0)
            return ConstructionStatus.UnknownVehicle;
        foreach (var operation in operations)
        {
            ArgumentNullException.ThrowIfNull(operation, nameof(operations));
            if (operation.PartId is { Value: 0 })
                return ConstructionStatus.UnknownPart;
            if (operation.DefinitionId is { Namespace: null })
                return ConstructionStatus.UnknownDefinition;
            if (operation.Orientation is { IsOrthogonal: false })
                return ConstructionStatus.DisallowedOrientation;
        }

        return null;
    }

    private static WorldCommandStatus RefusedVoxelStatus(WorldEditSubmission submission) =>
        submission.Refusal == WorldEditRefusal.InputExhausted ? WorldCommandStatus.InputExhausted : WorldCommandStatus.SimulationFaulted;

    private static ConstructionStatus RefusedConstructionStatus(WorldEditSubmission submission) =>
        submission.Refusal == WorldEditRefusal.InputExhausted ? ConstructionStatus.InputExhausted : ConstructionStatus.SimulationFaulted;

    private EditRollback CaptureEditRollback() =>
        new(
            World.CaptureVoxels(),
            _vehicles.ToArray(),
            _assemblies.ToArray(),
            _scheduled.ToArray(),
            _scheduledSequence,
            NextEntityId,
            TransactionCount,
            FollowedVehicleId,
            _rails.CaptureWorldEffects());

    private void RestoreEditRollback(EditRollback rollback)
    {
        World.RestoreVoxels(rollback.Voxels);
        _vehicles.Clear();
        _vehicles.AddRange(rollback.Vehicles);
        _assemblies.Clear();
        _assemblies.AddRange(rollback.Assemblies);
        _scheduled.Clear();
        _scheduled.AddRange(rollback.Scheduled);
        _scheduledSequence = rollback.ScheduledSequence;
        NextEntityId = rollback.NextEntityId;
        TransactionCount = rollback.TransactionCount;
        FollowedVehicleId = rollback.FollowedVehicleId;
        _rails.RestoreWorldEffects(rollback.RailEffects);
        NoteAssemblyGraphCommit();
        SnapDisplayInterval();
        _pendingChanges.Clear();
        _pendingCollisionChanges.Clear();
        _pendingTrackChunks.Clear();
        _rebuildAll = true;
        _rebuildAllCollision = true;
        _rebuildComposites = true;
        _rebuildTracks = true;
    }

    private sealed record EditRollback(
        IReadOnlyList<KeyValuePair<WorldVoxelCoord, WorldVoxel>> Voxels,
        IReadOnlyList<CompositeInstance> Vehicles,
        IReadOnlyList<TrainAssembly> Assemblies,
        IReadOnlyList<ScheduledTrainCommand> Scheduled,
        ulong ScheduledSequence,
        ulong NextEntityId,
        ulong TransactionCount,
        EntityId? FollowedVehicleId,
        (WorldVoxelCoord[] Destroyed, DerivedRailGraph Derived, bool DerivedCurrent) RailEffects);
}
