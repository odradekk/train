using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Domain.WorldEditor;

public enum WorldEditorVoxelInputStatus
{
    Applied,
    WorldChanging,
    BatchTargetsBound,
    BatchOperationBound
}

/// <summary>Single-cell preview and identity-bound cell batches, sharing one draft and submission path.</summary>
public sealed class WorldEditorVoxelTool : IDisposable
{
    private readonly VoxelDevelopmentSession _session;
    private readonly List<DraftState> _undo = new();
    private readonly List<DraftState> _redo = new();
    private BatchTarget[]? _batch;
    private WorldVoxelCoord? _changedTarget;

    public WorldEditorVoxelTool(VoxelDevelopmentSession session)
    {
        _session = session;
        session.VoxelTool = this;
        DefinitionId = Definitions.FirstOrDefault();
        session.WorldReplaced += Reset;
        session.EditExecuted += CheckBatchTargets;
    }

    public bool Active { get; private set; }
    public bool HasDraft { get; private set; }
    public bool HasBatchDraft => _batch is not null;
    public IReadOnlyList<WorldVoxelCoord> BatchTargets => _batch?.Select(target => target.Position).ToArray() ?? Array.Empty<WorldVoxelCoord>();
    public bool CanUndoDraft => HasDraft && !_session.IsChangingWorld && _undo.Count > 0;
    public bool CanRedoDraft => HasDraft && !_session.IsChangingWorld && _redo.Count > 0;
    public ulong HistoryEvictionCount { get; private set; }
    public int Height { get; private set; }
    public WorldVoxelCoord Position { get; private set; }
    public VoxelDefinitionId DefinitionId { get; private set; }
    public VoxelOrientation Orientation { get; private set; } = VoxelOrientation.Identity;
    public VoxelCellOperation Operation { get; private set; }
    public IReadOnlyList<VoxelDefinitionId> Definitions => _session.CurrentVoxels?.Entries.Keys.OrderBy(id => id.Value, StringComparer.Ordinal).ToArray()
        ?? Array.Empty<VoxelDefinitionId>();
    public IReadOnlyList<VoxelOrientation> Orientations => _session.CurrentVoxels?.TryGet(DefinitionId, out var entry) == true
        ? VoxelOrientation.All.Where(value => value.IsAllowedBy(entry.Definition.AllowedOrientations)).ToArray()
        : Array.Empty<VoxelOrientation>();

    public VoxelCellSetEdit CurrentEdit => new((_batch?.Select(target => target.Position) ?? new[] { Position })
        .Select(position => new VoxelCellTarget(position, Operation,
            Operation == VoxelCellOperation.Remove ? null : new VoxelCellContent(DefinitionId, Orientation))).ToArray());

    public VoxelCellSetResult Preview => BatchIdentityFailure() ?? _session.ValidateCells(CurrentEdit);

    /// <summary>Captures identity once. Selection changes and precise coordinates cannot redirect these targets.</summary>
    public VoxelCellSetResult StartBatch(IEnumerable<ObservedObject> selection, VoxelCellOperation operation)
    {
        var items = selection.Distinct().OrderBy(item => item.Cell.X).ThenBy(item => item.Cell.Y).ThenBy(item => item.Cell.Z).ToArray();
        if (_session.IsChangingWorld)
            return new(VoxelCellSetStatus.WorldChanging);
        if (HasDraft)
            return new(VoxelCellSetStatus.VoxelDraftExists);
        if (operation is not (VoxelCellOperation.Remove or VoxelCellOperation.Replace))
            return new(VoxelCellSetStatus.UnsupportedOperation);
        if (items.Any(item => item.Kind != ObservedObjectKind.WorldCell))
            return new(VoxelCellSetStatus.NotWorldCells);
        if (items.Length == 0)
            return new(VoxelCellSetStatus.NoTargets);
        var targets = new List<BatchTarget>();
        foreach (var item in items)
        {
            if (_session.Query(item.Cell).Voxel is not { } voxel || voxel.RuntimeId != item.RuntimeId ||
                (item.Definition is { } definition && voxel.DefinitionId != definition))
                return new(VoxelCellSetStatus.ExpectedContentMismatch, item.Cell);
            targets.Add(new(item.Cell, voxel.DefinitionId, voxel.Orientation, voxel.RuntimeId));
        }
        _batch = targets.ToArray();
        _changedTarget = null;
        Position = _batch[0].Position;
        Operation = operation;
        Active = false;
        HasDraft = true;
        return Preview;
    }

    public bool SetActive(bool active)
    {
        if (active == Active)
            return true;
        if (HasDraft || _session.IsChangingWorld)
            return false;
        Active = active;
        return true;
    }

    public bool SetHeight(int height)
    {
        if (height == Height)
            return true;
        if (HasDraft || _session.IsChangingWorld)
            return false;
        Height = height;
        Position = Position with { Y = height };
        return true;
    }

    public void Hover(WorldVoxelCoord position)
    {
        if (Active && !HasDraft && !_session.IsChangingWorld)
            Position = position;
    }

    public WorldEditorVoxelInputStatus SetExact(WorldVoxelCoord position, VoxelDefinitionId definition, VoxelOrientation orientation, VoxelCellOperation operation)
    {
        if (_session.IsChangingWorld)
            return WorldEditorVoxelInputStatus.WorldChanging;
        if (HasBatchDraft)
            return WorldEditorVoxelInputStatus.BatchTargetsBound;
        var next = new DraftState(position, definition, orientation, operation);
        if (HasDraft)
            RecordDraftChange(next);
        RestoreDraft(next);
        HasDraft = true;
        return WorldEditorVoxelInputStatus.Applied;
    }

    public WorldEditorVoxelInputStatus SetSettings(VoxelDefinitionId definition, VoxelOrientation orientation, VoxelCellOperation operation)
    {
        if (_session.IsChangingWorld)
            return WorldEditorVoxelInputStatus.WorldChanging;
        var operationBound = HasBatchDraft && Operation != operation;
        var next = new DraftState(Position, definition, orientation, HasBatchDraft ? Operation : operation);
        if (HasDraft)
            RecordDraftChange(next);
        RestoreDraft(next);
        return operationBound ? WorldEditorVoxelInputStatus.BatchOperationBound : WorldEditorVoxelInputStatus.Applied;
    }

    public WorldEditSubmission Commit()
    {
        if (_session.IsChangingWorld)
            return new(WorldEditRefusal.WorldChanging, null, null);
        HasDraft = true;
        if (BatchIdentityFailure() is { } failure)
            return new(WorldEditRefusal.DraftTargetsChanged, null, failure);
        var submission = _session.SubmitEdit(CurrentEdit);
        if (submission.Accepted && submission.Record?.State is WorldEditState.Executed or WorldEditState.Pending)
            Discard();
        return submission;
    }

    public void Discard()
    {
        HasDraft = false;
        _batch = null;
        _changedTarget = null;
        _undo.Clear();
        _redo.Clear();
        HistoryEvictionCount = 0;
        _session.ResolveVoxelDraft();
    }

    private void Reset(WorldReplacement replacement)
    {
        HasDraft = false;
        _batch = null;
        _changedTarget = null;
        _undo.Clear();
        _redo.Clear();
        HistoryEvictionCount = 0;
        Height = 0;
        Position = default;
    }

    public void Dispose()
    {
        _session.WorldReplaced -= Reset;
        _session.EditExecuted -= CheckBatchTargets;
        if (ReferenceEquals(_session.VoxelTool, this))
            _session.VoxelTool = null;
    }

    public bool UndoDraft() => ApplyDraftHistory(_undo, _redo);
    public bool RedoDraft() => ApplyDraftHistory(_redo, _undo);

    private DraftState CaptureDraft() => new(Position, DefinitionId, Orientation, Operation);

    private void RecordDraftChange(DraftState next)
    {
        var previous = CaptureDraft();
        if (previous == next)
            return;
        _undo.Add(previous);
        _redo.Clear();
        if (_undo.Count > VoxelDevelopmentSession.VoxelHistoryCapacity)
        {
            _undo.RemoveAt(0);
            HistoryEvictionCount++;
        }
    }

    private bool ApplyDraftHistory(List<DraftState> source, List<DraftState> destination)
    {
        if (!HasDraft || _session.IsChangingWorld || source.Count == 0)
            return false;
        destination.Add(CaptureDraft());
        RestoreDraft(source[^1]);
        source.RemoveAt(source.Count - 1);
        return true;
    }

    private void RestoreDraft(DraftState state)
    {
        Position = state.Position;
        DefinitionId = state.DefinitionId;
        Orientation = state.Orientation;
        Operation = state.Operation;
    }

    private sealed record DraftState(WorldVoxelCoord Position, VoxelDefinitionId DefinitionId,
        VoxelOrientation Orientation, VoxelCellOperation Operation);

    private sealed record BatchTarget(WorldVoxelCoord Position, VoxelDefinitionId Definition,
        VoxelOrientation Orientation, EntityId? RuntimeId);

    // Remember loss at each edit boundary, including removal followed by identical recreation before a frame.
    private void CheckBatchTargets(WorldEditRecord record) => _changedTarget ??= FindChangedBatchTarget();

    private WorldVoxelCoord? FindChangedBatchTarget()
    {
        if (_batch is not null)
            foreach (var target in _batch)
                if (_session.Query(target.Position).Voxel is not { } voxel || voxel.DefinitionId != target.Definition ||
                    voxel.Orientation != target.Orientation || voxel.RuntimeId != target.RuntimeId)
                    return target.Position;
        return null;
    }

    private VoxelCellSetResult? BatchIdentityFailure() => (_changedTarget ?? FindChangedBatchTarget()) is { } changed
        ? new(VoxelCellSetStatus.ExpectedContentMismatch, changed) : null;
}
