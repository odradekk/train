using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Rails;

namespace Train.Domain.Voxels;

/// <summary>
/// The single validation and transaction entry for voxel, vehicle, rail, and train modifications. Every accepted edit takes the
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

    /// <summary>
    /// Raised once per executed edit after its transaction is logged, so each edit of a same-step batch is observable
    /// on its own; never for a rejected edit or a faulted one, whose changes were rolled back. It runs mid-boundary,
    /// outside the transaction: subscribers only read the session and must not throw.
    /// </summary>
    internal event Action<WorldEditRecord>? EditExecuted;

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

    public RailEditResult ConstructRails(RailConstructionPlan? plan = null)
    {
        plan ??= StraightRailFixture.CreateFixturePlan();
        if (!RailConstructEdit.IsEncodable(plan))
            return new RailEditResult(RailEditStatus.InvalidPlan);
        var submission = SubmitEdit(new RailConstructEdit(plan));
        return submission.Result as RailEditResult ?? new RailEditResult(
            submission.Refusal == WorldEditRefusal.InputExhausted ? RailEditStatus.InputExhausted : RailEditStatus.SimulationFaulted);
    }

    public RailRepairResult RepairRails(IReadOnlyList<WorldVoxelCoord> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var submission = SubmitEdit(new RailRepairEdit(cells));
        return submission.Result as RailRepairResult ?? new RailRepairResult(
            submission.Refusal == WorldEditRefusal.InputExhausted ? RailRepairStatus.InputExhausted : RailRepairStatus.SimulationFaulted,
            cells.Count == 0 ? default : cells[0]);
    }

    public TrainAssemblyResult AssembleStaticTrain()
    {
        var submission = SubmitEdit(TrainAssembleEdit.StaticFixture);
        return submission.Result as TrainAssemblyResult ?? new TrainAssemblyResult(
            submission.Refusal == WorldEditRefusal.InputExhausted ? TrainAssemblyStatus.InputExhausted : TrainAssemblyStatus.SimulationFaulted);
    }

    /// <summary>
    /// Submits <c>train.start</c> for the resolved assembly. Reverse heading, a session without railway, and an
    /// unresolvable target (none, or several without an explicit id) cannot be encoded and take no id.
    /// </summary>
    public TrainMotionResult StartTrain(RailHeading heading, ulong? atStep = null, EntityId? assemblyId = null)
    {
        if (heading == RailHeading.AgainstAlignment)
            return RejectMotion(TrainMotionStatus.ReverseUnsupported, atStep);
        if (!HasRailway)
            return RejectMotion(TrainMotionStatus.NoRailway, atStep);
        if (!TryResolveCommandTarget(assemblyId, out var target, out var status))
            return RejectMotion(status, atStep);
        return SubmitTrainCommand(new TrainStartEdit(target), atStep);
    }

    public TrainMotionResult StopTrain(ulong? atStep = null, EntityId? assemblyId = null)
    {
        if (!TryResolveCommandTarget(assemblyId, out var target, out var status))
            return RejectMotion(status, atStep);
        return SubmitTrainCommand(new TrainStopEdit(target), atStep);
    }

    private TrainMotionResult SubmitTrainCommand(WorldEdit edit, ulong? atStep)
    {
        var target = atStep ?? SimulationStep;
        var submission = SubmitEdit(edit, atStep);
        if (!submission.Accepted)
        {
            return RejectMotion(submission.Refusal switch
            {
                WorldEditRefusal.PastStep => TrainMotionStatus.PastStep,
                WorldEditRefusal.InputExhausted => TrainMotionStatus.InputExhausted,
                WorldEditRefusal.UnallocatedReference => TrainMotionStatus.UnallocatedReference,
                _ => TrainMotionStatus.SimulationFaulted
            }, target);
        }

        var id = submission.Record!.Id;
        if (submission.Result is TrainMotionResult result)
            return result with { EditId = id };
        return new TrainMotionResult(
            submission.Record.State == WorldEditState.Pending ? TrainMotionStatus.Pending : TrainMotionStatus.SimulationFaulted,
            SimulationStep,
            AtStep: target,
            EditId: id);
    }

    /// <summary>False when the session cannot take another input at its current boundary.</summary>
    internal bool CanRecordContentPublication =>
        Fault is null && NextEditId != ulong.MaxValue && EditBoundarySequence != ulong.MaxValue;

    /// <summary>
    /// Records one world-adapting content publication at the current boundary and applies it inside the edit
    /// transaction. An internal fault restores the boundary, halts the session, and throws so the catalog is not
    /// published over a world that was never adapted to it.
    /// </summary>
    internal void PublishContent(ContentPublishEdit edit, Action apply)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(apply);
        if (!CanRecordContentPublication)
            throw new InvalidOperationException("Content publication was prepared for a session that can no longer take input.");
        var accepted = EditBoundary;
        var record = new WorldEditRecord(NextEditId, edit, accepted, SimulationStep);
        NextEditId++;
        EditBoundarySequence++;
        var done = Execute(record, accepted, () =>
        {
            apply();
            return (true, "Published", null);
        });
        if (done.Record!.State == WorldEditState.Faulted)
            throw new InvalidOperationException($"Content publication faulted: {Fault!.ExceptionType}: {Fault.Message}");
    }

    /// <summary>
    /// Accepts one parsed edit at the current boundary. <paramref name="atStep"/> defaults to the current step
    /// (execute now); a later step queues the edit for that boundary. Past steps, queued edits naming never-allocated
    /// runtime ids, exhausted counters and a faulted session are refused without consuming an id or a sequence.
    /// </summary>
    public WorldEditSubmission SubmitEdit(WorldEdit edit, ulong? atStep = null)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit is ContentPublishEdit)
            throw new ArgumentException("Content publications are recorded by the content reload boundary.", nameof(edit));
        if (Fault is not null)
            return new WorldEditSubmission(WorldEditRefusal.SimulationFaulted, null, null);
        var target = atStep ?? SimulationStep;
        if (target < SimulationStep)
            return new WorldEditSubmission(WorldEditRefusal.PastStep, null, null);
        // Save restore rejects pending ids that were never allocated, so a queued edit may only name ids issued so far.
        if (target > SimulationStep && edit.EntityReferences.Any(id => id.Value >= NextEntityId))
            return new WorldEditSubmission(WorldEditRefusal.UnallocatedReference, null, null);
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

        return Execute(record, accepted, () => Dispatch(record.Edit));
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
            if (Execute(due, position, () => Dispatch(due.Edit)).Record!.State == WorldEditState.Faulted)
                return false;
        }

        return true;
    }

    private bool HasDueEdits() => _pendingEdits.Any(item => item.TargetStep == SimulationStep);

    private WorldEditSubmission Execute(
        WorldEditRecord pending,
        SimulationPosition position,
        Func<(bool Succeeded, string Status, object? Result)> dispatch)
    {
        var rollback = CaptureEditRollback();
        WorldEditRecord done;
        bool succeeded;
        string status;
        object? result;
        try
        {
            (succeeded, status, result) = dispatch();
            EditFaultProbe?.Invoke(pending);
            done = pending with
            {
                State = succeeded ? WorldEditState.Executed : WorldEditState.Rejected,
                ExecutedAt = position,
                Status = status
            };
            _pendingEdits.Remove(pending);
            AppendLog(done);
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

        if (done.State == WorldEditState.Executed)
            EditExecuted?.Invoke(done);
        return new WorldEditSubmission(WorldEditRefusal.None, done, result);
    }

    private (bool Succeeded, string Status, object? Result) Dispatch(WorldEdit edit)
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
            case RailConstructEdit construct:
                var built = ConstructRailsCore(construct.Plan);
                return (built.Succeeded, built.Status.ToString(), built);
            case RailRepairEdit repair:
                var repaired = RepairRailsCore(repair.Cells);
                return (repaired.Succeeded, repaired.Status.ToString(), repaired);
            case TrainAssembleEdit assemble:
                var formed = AssembleCore(assemble);
                return (formed.Succeeded, formed.Status.ToString(), formed);
            case TrainStartEdit start:
                var started = StartCore(start.AssemblyId);
                return (started.Succeeded, started.Status.ToString(), started);
            case TrainStopEdit stop:
                var stopped = StopCore(stop.AssemblyId);
                return (stopped.Succeeded, stopped.Status.ToString(), stopped);
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
            NextEntityId,
            TransactionCount,
            FollowedVehicleId,
            _rails.CaptureState());

    private void RestoreEditRollback(EditRollback rollback)
    {
        World.RestoreVoxels(rollback.Voxels);
        _vehicles.Clear();
        _vehicles.AddRange(rollback.Vehicles);
        _assemblies.Clear();
        _assemblies.AddRange(rollback.Assemblies);
        NextEntityId = rollback.NextEntityId;
        TransactionCount = rollback.TransactionCount;
        FollowedVehicleId = rollback.FollowedVehicleId;
        _rails.RestoreState(rollback.Rails);
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
        ulong NextEntityId,
        ulong TransactionCount,
        EntityId? FollowedVehicleId,
        PhysicalRailState.Snapshot Rails);
}
