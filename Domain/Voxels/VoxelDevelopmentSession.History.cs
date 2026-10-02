using Train.Domain.Commands;

namespace Train.Domain.Voxels;

public sealed partial class VoxelDevelopmentSession
{
    // Provisional limit for the single-cell editing slice; tune after measuring real editing sessions.
    public const int VoxelHistoryCapacity = 64;

    private readonly List<VoxelCellRestoreEdit> _undo = new();
    private readonly List<VoxelCellRestoreEdit> _redo = new();

    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;
    public ulong HistoryEvictionCount { get; private set; }
    public bool HasEditHistory => UndoCount != 0 || RedoCount != 0;
    public bool CanUndo => HistoryRefusal(_undo) == WorldEditRefusal.None;
    public bool CanRedo => HistoryRefusal(_redo) == WorldEditRefusal.None;

    public WorldEditSubmission Undo() => ApplyHistory(_undo, _redo);
    public WorldEditSubmission Redo() => ApplyHistory(_redo, _undo);

    private WorldEditRefusal HistoryRefusal(IReadOnlyCollection<VoxelCellRestoreEdit> source)
    {
        if (IsChangingWorld)
            return WorldEditRefusal.WorldChanging;
        if (Fault is not null)
            return WorldEditRefusal.SimulationFaulted;
        if (HasEditorDraft)
            return WorldEditRefusal.VoxelDraftExists;
        if (!SimulationPaused)
            return WorldEditRefusal.SimulationRunning;
        if (source.Count == 0)
            return WorldEditRefusal.NoHistory;
        return NextEditId == ulong.MaxValue || EditBoundarySequence == ulong.MaxValue
            ? WorldEditRefusal.InputExhausted : WorldEditRefusal.None;
    }

    private WorldEditSubmission ApplyHistory(List<VoxelCellRestoreEdit> source, List<VoxelCellRestoreEdit> destination)
    {
        var refusal = HistoryRefusal(source);
        if (refusal != WorldEditRefusal.None)
            return new(refusal, null, null);
        var edit = source[^1];
        var result = SubmitEditCore(edit, null, confirmEndHistory: true, trackHistory: false);
        if (result.Record?.State == WorldEditState.Executed)
        {
            source.RemoveAt(source.Count - 1);
            destination.Add(new VoxelCellRestoreEdit(edit.Targets.Select(target =>
                new VoxelCellRestoreTarget(target.Position, target.Content, target.Expected)).ToArray()));
            Changed?.Invoke();
        }
        return result;
    }

    private bool RequiresHistoryConfirmation(WorldEdit edit, ulong? atStep) =>
        HasEditHistory && ((atStep ?? SimulationStep) != SimulationStep || !IsUndoable(edit));

    private bool IsUndoable(WorldEdit edit) =>
        SimulationPaused && edit is VoxelAddEdit or VoxelRemoveEdit or VoxelCellSetEdit;

    private VoxelCellState? CellState(WorldVoxelCoord position) => World.Query(position).Voxel is { } voxel
        ? new VoxelCellState(voxel.DefinitionId, voxel.Orientation, voxel.CurrentDurability)
        : null;

    private VoxelCellRestoreTarget[]? CaptureHistoryBefore(WorldEdit edit)
    {
        if (!IsUndoable(edit))
            return null;
        IEnumerable<WorldVoxelCoord> positions = edit switch
        {
            VoxelAddEdit add => new[] { add.Position },
            VoxelRemoveEdit remove => new[] { remove.Position },
            VoxelCellSetEdit cells => cells.Targets.Select(target => target.Position),
            _ => Array.Empty<WorldVoxelCoord>()
        };
        return positions.Select(position => new VoxelCellRestoreTarget(position, null, CellState(position))).ToArray();
    }

    private void RecordHistory(VoxelCellRestoreTarget[]? before)
    {
        if (before is null)
        {
            ClearHistory();
            return;
        }
        _redo.Clear();
        _undo.Add(new VoxelCellRestoreEdit(before.Select(target =>
            target with { Expected = CellState(target.Position) }).ToArray()));
        if (_undo.Count > VoxelHistoryCapacity)
        {
            _undo.RemoveAt(0);
            HistoryEvictionCount++;
        }
        Changed?.Invoke();
    }

    private void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
        HistoryEvictionCount = 0;
    }

    private VoxelCellSetResult RestoreCellsCore(VoxelCellRestoreEdit edit)
    {
        if (edit.Targets.Count == 0)
            return new(VoxelCellSetStatus.NoTargets);
        var seen = new HashSet<WorldVoxelCoord>();
        foreach (var target in edit.Targets)
        {
            if (!seen.Add(target.Position))
                return new(VoxelCellSetStatus.DuplicateTarget, target.Position);
            if (!World.Bounds.Contains(target.Position))
                return new(VoxelCellSetStatus.OutOfBounds, target.Position);
            if (CellState(target.Position) != target.Expected)
                return new(VoxelCellSetStatus.ExpectedContentMismatch, target.Position);
            if (HasRailVoxel(target.Position))
                return new(VoxelCellSetStatus.RailOccupied, target.Position);
            if (TryVehicleOwner(target.Position, out var vehicleId, out var partId))
                return new(VoxelCellSetStatus.VehicleOccupied, target.Position, VoxelCellOwnerKind.VehiclePart, vehicleId, partId);
            if (target.Content is not { } content)
                continue;
            if (CurrentVoxels?.TryGet(content.DefinitionId, out var entry) != true)
                return new(VoxelCellSetStatus.UnknownDefinition, target.Position);
            if (!content.Orientation.IsAllowedBy(entry.Definition.AllowedOrientations))
                return new(VoxelCellSetStatus.DisallowedOrientation, target.Position);
            if (content.CurrentDurability <= 0 || content.CurrentDurability > entry.Definition.MaxDurability)
                return new(VoxelCellSetStatus.InvalidDurability, target.Position);
        }
        foreach (var target in edit.Targets)
        {
            // Deleted debris is rebuilt as an ordinary voxel: no old part identity and no entity allocation.
            if (target.Content is { } content)
            {
                var voxel = new WorldVoxel(content.DefinitionId, content.CurrentDurability, content.Orientation);
                if (target.Expected is null)
                    World.TryAdd(target.Position, voxel);
                else
                    World.TryReplace(target.Position, voxel);
            }
            else
                World.TryRemove(target.Position);
            NoteChange(target.Position, occupancyChanged: (target.Expected is null) != (target.Content is null), publish: false);
            _pendingCollisionChanges.Add(target.Position);
        }
        FreezeUnsupportedAssemblies();
        Changed?.Invoke();
        return new(VoxelCellSetStatus.Applied);
    }
}
