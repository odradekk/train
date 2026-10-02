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

    public VoxelCellSetResult ValidateCells(VoxelCellSetEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.Targets.Count == 0)
            return new(VoxelCellSetStatus.NoTargets);

        var seen = new HashSet<WorldVoxelCoord>();
        foreach (var target in edit.Targets)
        {
            if (!seen.Add(target.Position))
                return new(VoxelCellSetStatus.DuplicateTarget, target.Position);
            if (!ValidCellTarget(target))
                return new(VoxelCellSetStatus.InvalidTarget, target.Position);
            if (!World.Bounds.Contains(target.Position))
                return new(VoxelCellSetStatus.OutOfBounds, target.Position);
            if (HasRailVoxel(target.Position))
                return TryRailOwner(target.Position, out var segmentId)
                    ? new(VoxelCellSetStatus.RailOccupied, target.Position, VoxelCellOwnerKind.RailSegment, segmentId)
                    : new(VoxelCellSetStatus.RailOccupied, target.Position);
            if (TryVehicleOwner(target.Position, out var vehicleId, out var partId))
                return new(VoxelCellSetStatus.VehicleOccupied, target.Position, VoxelCellOwnerKind.VehiclePart, vehicleId, partId);
            var current = World.Query(target.Position).Voxel;
            if (target.Operation == VoxelCellOperation.Add && current is not null)
                return new(VoxelCellSetStatus.Occupied, target.Position);
            if (target.Operation != VoxelCellOperation.Add && current is null)
                return new(VoxelCellSetStatus.Empty, target.Position);
            if (target.Content is not { } content)
                continue;
            if (CurrentVoxels?.TryGet(content.DefinitionId, out var entry) != true)
                return new(VoxelCellSetStatus.UnknownDefinition, target.Position);
            if (!content.Orientation.IsAllowedBy(entry.Definition.AllowedOrientations))
                return new(VoxelCellSetStatus.DisallowedOrientation, target.Position);
            if (target.Operation == VoxelCellOperation.Replace && current!.DefinitionId == content.DefinitionId &&
                current.Orientation == content.Orientation)
                return new(VoxelCellSetStatus.NoChange, target.Position);
        }

        return new(VoxelCellSetStatus.Valid);
    }

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

    public WorldDamageResult Damage(WorldVoxelCoord position, int amount, bool confirmEndHistory = false) =>
        Damage(new[] { new VoxelDamageTarget(position, amount) }, confirmEndHistory);

    public WorldDamageResult Damage(IReadOnlyList<VoxelDamageTarget> targets, bool confirmEndHistory = false)
    {
        var edit = new VoxelDamageEdit(targets);
        var submission = SubmitEdit(edit, confirmEndHistory: confirmEndHistory);
        return submission.Result is WorldDamageResult result ? edit.ToCallerOrder(result) : new WorldDamageResult(
            RefusedVoxelStatus(submission),
            targets.Count == 0 ? default : targets[0].Position,
            Array.Empty<WorldDamageTargetResult>());
    }

    public CompositeDamageResult Damage(EntityId vehicleId, LocalVoxelCoord cell, int amount, bool confirmEndHistory = false) =>
        Damage(new[] { new CompositeDamageTarget(vehicleId, cell, amount) }, confirmEndHistory);

    public CompositeDamageResult Damage(IReadOnlyList<CompositeDamageTarget> targets, bool confirmEndHistory = false)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Any(target => target.VehicleId.Value == 0))
            return new CompositeDamageResult(CompositeDamageStatus.UnknownVehicle, targets.First(target => target.VehicleId.Value == 0).VehicleId);
        var edit = new VehicleDamageEdit(targets);
        var submission = SubmitEdit(edit, confirmEndHistory: confirmEndHistory);
        return submission.Result is CompositeDamageResult result ? edit.ToCallerOrder(result) : new CompositeDamageResult(
            submission.Refusal == WorldEditRefusal.HistoryConfirmationRequired ? CompositeDamageStatus.HistoryConfirmationRequired :
            submission.Refusal == WorldEditRefusal.WorldChanging ? CompositeDamageStatus.WorldChanging :
            submission.Refusal == WorldEditRefusal.InputExhausted ? CompositeDamageStatus.InputExhausted : CompositeDamageStatus.SimulationFaulted,
            targets.Count == 0 ? null : targets[0].VehicleId);
    }

    public CompositeManufactureResult Manufacture(CompositeDefinitionId definitionId, CompositeTransform? transform = null, bool confirmEndHistory = false)
    {
        var pose = transform ?? HollowShellFixture.DefaultTransform;
        if (definitionId.Namespace is null)
            return new CompositeManufactureResult(ConstructionStatus.UnknownDefinition);
        if (!pose.IsFinite)
            return new CompositeManufactureResult(ConstructionStatus.NonFiniteTransform);
        var submission = SubmitEdit(new VehicleManufactureEdit(definitionId, pose), confirmEndHistory: confirmEndHistory);
        return submission.Result as CompositeManufactureResult ?? new CompositeManufactureResult(RefusedConstructionStatus(submission));
    }

    public CompositeRefitResult Refit(EntityId vehicleId, CompositeRefitOperation operation, bool confirmEndHistory = false) =>
        Refit(vehicleId, new[] { operation }, confirmEndHistory);

    public CompositeRefitResult Refit(EntityId vehicleId, IReadOnlyList<CompositeRefitOperation> operations, bool confirmEndHistory = false)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (UnencodableRefit(vehicleId, operations) is { } status)
            return new CompositeRefitResult(status, vehicleId);
        var submission = SubmitEdit(new VehicleRefitEdit(vehicleId, operations), confirmEndHistory: confirmEndHistory);
        return submission.Result as CompositeRefitResult ?? new CompositeRefitResult(RefusedConstructionStatus(submission), vehicleId);
    }

    public RailEditResult ConstructRails(RailConstructionPlan? plan = null, bool confirmEndHistory = false)
    {
        plan ??= StraightRailFixture.CreateFixturePlan();
        if (!RailConstructEdit.IsEncodable(plan))
            return new RailEditResult(RailEditStatus.InvalidPlan);
        var submission = SubmitEdit(new RailConstructEdit(plan), confirmEndHistory: confirmEndHistory);
        return submission.Result as RailEditResult ?? new RailEditResult(
            submission.Refusal == WorldEditRefusal.HistoryConfirmationRequired ? RailEditStatus.HistoryConfirmationRequired :
            submission.Refusal == WorldEditRefusal.WorldChanging ? RailEditStatus.WorldChanging :
            submission.Refusal == WorldEditRefusal.InputExhausted ? RailEditStatus.InputExhausted : RailEditStatus.SimulationFaulted);
    }

    public RailRepairResult RepairRails(IReadOnlyList<WorldVoxelCoord> cells, bool confirmEndHistory = false)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var submission = SubmitEdit(new RailRepairEdit(cells), confirmEndHistory: confirmEndHistory);
        return submission.Result as RailRepairResult ?? new RailRepairResult(
            submission.Refusal == WorldEditRefusal.HistoryConfirmationRequired ? RailRepairStatus.HistoryConfirmationRequired :
            submission.Refusal == WorldEditRefusal.WorldChanging ? RailRepairStatus.WorldChanging :
            submission.Refusal == WorldEditRefusal.InputExhausted ? RailRepairStatus.InputExhausted : RailRepairStatus.SimulationFaulted,
            cells.Count == 0 ? default : cells[0]);
    }

    public TrainAssemblyResult AssembleStaticTrain(bool confirmEndHistory = false)
    {
        var submission = SubmitEdit(TrainAssembleEdit.StaticFixture, confirmEndHistory: confirmEndHistory);
        return submission.Result as TrainAssemblyResult ?? new TrainAssemblyResult(
            submission.Refusal == WorldEditRefusal.HistoryConfirmationRequired ? TrainAssemblyStatus.HistoryConfirmationRequired :
            submission.Refusal == WorldEditRefusal.WorldChanging ? TrainAssemblyStatus.WorldChanging :
            submission.Refusal == WorldEditRefusal.InputExhausted ? TrainAssemblyStatus.InputExhausted : TrainAssemblyStatus.SimulationFaulted);
    }

    /// <summary>
    /// Submits <c>train.start</c> for the resolved assembly. Reverse heading, a session without railway, and an
    /// unresolvable target (none, or several without an explicit id) cannot be encoded and take no id.
    /// </summary>
    public TrainMotionResult StartTrain(RailHeading heading, ulong? atStep = null, EntityId? assemblyId = null, bool confirmEndHistory = false)
    {
        if (heading == RailHeading.AgainstAlignment)
            return RejectMotion(TrainMotionStatus.ReverseUnsupported, atStep);
        if (!HasRailway)
            return RejectMotion(TrainMotionStatus.NoRailway, atStep);
        if (!TryResolveCommandTarget(assemblyId, out var target, out var status))
            return RejectMotion(status, atStep);
        return SubmitTrainCommand(new TrainStartEdit(target), atStep, confirmEndHistory);
    }

    public TrainMotionResult StopTrain(ulong? atStep = null, EntityId? assemblyId = null, bool confirmEndHistory = false)
    {
        if (!TryResolveCommandTarget(assemblyId, out var target, out var status))
            return RejectMotion(status, atStep);
        return SubmitTrainCommand(new TrainStopEdit(target), atStep, confirmEndHistory);
    }

    private TrainMotionResult SubmitTrainCommand(WorldEdit edit, ulong? atStep, bool confirmEndHistory)
    {
        var target = atStep ?? SimulationStep;
        var submission = SubmitEdit(edit, atStep, confirmEndHistory);
        if (!submission.Accepted)
        {
            return RejectMotion(submission.Refusal switch
            {
                WorldEditRefusal.WorldChanging => TrainMotionStatus.WorldChanging,
                WorldEditRefusal.PastStep => TrainMotionStatus.PastStep,
                WorldEditRefusal.InputExhausted => TrainMotionStatus.InputExhausted,
                WorldEditRefusal.UnallocatedReference => TrainMotionStatus.UnallocatedReference,
                WorldEditRefusal.HistoryConfirmationRequired => TrainMotionStatus.HistoryConfirmationRequired,
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
        !IsChangingWorld && Fault is null && NextEditId != ulong.MaxValue && EditBoundarySequence != ulong.MaxValue;

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
    public WorldEditSubmission SubmitEdit(WorldEdit edit, ulong? atStep = null, bool confirmEndHistory = false)
        => SubmitEditCore(edit, atStep, confirmEndHistory, trackHistory: true);

    private WorldEditSubmission SubmitEditCore(WorldEdit edit, ulong? atStep, bool confirmEndHistory, bool trackHistory)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit is ContentPublishEdit)
            throw new ArgumentException("Content publications are recorded by the content reload boundary.", nameof(edit));
        if (IsChangingWorld)
            return new WorldEditSubmission(WorldEditRefusal.WorldChanging, null, null);
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

        if (!confirmEndHistory && RequiresHistoryConfirmation(edit, atStep))
            return new WorldEditSubmission(WorldEditRefusal.HistoryConfirmationRequired, null, null);

        var accepted = new SimulationPosition(SimulationStep, EditBoundarySequence);
        var record = new WorldEditRecord(NextEditId, edit, accepted, target);
        NextEditId++;
        EditBoundarySequence++;
        _pendingEdits.Add(record);
        if (target > SimulationStep)
        {
            ClearHistory();
            Changed?.Invoke();
            return new WorldEditSubmission(WorldEditRefusal.None, record, null);
        }

        return Execute(record, accepted, () => Dispatch(record.Edit), trackHistory);
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
            if (Execute(due, position, () => Dispatch(due.Edit), trackHistory: false).Record!.State == WorldEditState.Faulted)
                return false;
        }

        return true;
    }

    private bool HasDueEdits() => _pendingEdits.Any(item => item.TargetStep == SimulationStep);

    private WorldEditSubmission Execute(
        WorldEditRecord pending,
        SimulationPosition position,
        Func<(bool Succeeded, string Status, object? Result)> dispatch,
        bool trackHistory = true)
    {
        var historyBefore = trackHistory ? CaptureHistoryBefore(pending.Edit) : null;
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
        {
            if (trackHistory)
                RecordHistory(historyBefore);
            EditExecuted?.Invoke(done);
        }
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
            case VoxelCellSetEdit cells:
                var cellSet = ApplyCellsCore(cells);
                return (cellSet.Succeeded, cellSet.Status.ToString(), cellSet);
            case VoxelCellRestoreEdit restore:
                var restored = RestoreCellsCore(restore);
                return (restored.Succeeded, restored.Status.ToString(), restored);
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

    private VoxelCellSetResult ApplyCellsCore(VoxelCellSetEdit edit)
    {
        var validation = ValidateCells(edit);
        if (!validation.Succeeded)
            return validation;
        foreach (var target in edit.Targets)
        {
            if (target.Content is { } content)
            {
                var definition = CurrentVoxels!.Entries[content.DefinitionId].Definition;
                var voxel = new WorldVoxel(content.DefinitionId, definition.MaxDurability, content.Orientation);
                if (target.Operation == VoxelCellOperation.Add)
                    World.TryAdd(target.Position, voxel);
                else
                    World.TryReplace(target.Position, voxel);
            }
            else
            {
                World.TryRemove(target.Position);
            }
            NoteChange(target.Position, occupancyChanged: target.Operation != VoxelCellOperation.Replace, publish: false);
            // Replacement can change the collision shape without changing occupancy or destroying railway support.
            if (target.Operation == VoxelCellOperation.Replace)
                _pendingCollisionChanges.Add(target.Position);
        }
        FreezeUnsupportedAssemblies();
        Changed?.Invoke();
        return new(VoxelCellSetStatus.Applied);
    }

    private static bool ValidCellTarget(VoxelCellTarget target) => target.Operation switch
    {
        VoxelCellOperation.Add or VoxelCellOperation.Replace => target.Content is not null,
        VoxelCellOperation.Remove => target.Content is null,
        _ => false
    };

    internal bool HasRailVoxel(WorldVoxelCoord cell) =>
        !_rails.IsDestroyed(cell) && World.Query(cell).Voxel is { } voxel &&
        voxel.DefinitionId == StraightRailFixture.RailDefinitionId;

    internal bool TryRailOwner(WorldVoxelCoord cell, out EntityId segmentId)
    {
        segmentId = default;
        if (!HasRailVoxel(cell) ||
            !_rails.TryGetOccupancy(cell, out var occupancy) ||
            occupancy.Supports.Count == 0)
            return false;
        segmentId = occupancy.Supports.MinBy(support => support.SegmentId.Value)!.SegmentId;
        return true;
    }

    private bool TryVehicleOwner(WorldVoxelCoord cell, out EntityId vehicleId, out EntityId partId)
    {
        foreach (var vehicle in _vehicles.OrderBy(item => item.Id.Value))
        foreach (var part in vehicle.Parts.OrderBy(item => item.Id.Value))
        {
            if (!vehicle.Transform.OverlapsCell(part.Cell, cell))
                continue;
            vehicleId = vehicle.Id;
            partId = part.Id;
            return true;
        }
        vehicleId = default;
        partId = default;
        return false;
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
        ClearHistory();
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
        submission.Refusal == WorldEditRefusal.HistoryConfirmationRequired ? WorldCommandStatus.HistoryConfirmationRequired :
        submission.Refusal == WorldEditRefusal.WorldChanging ? WorldCommandStatus.WorldChanging :
            submission.Refusal == WorldEditRefusal.InputExhausted ? WorldCommandStatus.InputExhausted : WorldCommandStatus.SimulationFaulted;

    private static ConstructionStatus RefusedConstructionStatus(WorldEditSubmission submission) =>
        submission.Refusal == WorldEditRefusal.HistoryConfirmationRequired ? ConstructionStatus.HistoryConfirmationRequired :
        submission.Refusal == WorldEditRefusal.WorldChanging ? ConstructionStatus.WorldChanging :
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
