using Train.Domain.Commands;
using Train.Domain.Voxels;

namespace Train.Domain.WorldEditor;

/// <summary>A single horizontal-plane preview. Only precise input or a failed commit creates a draft.</summary>
public sealed class WorldEditorVoxelTool : IDisposable
{
    private readonly VoxelDevelopmentSession _session;
    private readonly List<DraftState> _undo = new();
    private readonly List<DraftState> _redo = new();

    public WorldEditorVoxelTool(VoxelDevelopmentSession session)
    {
        _session = session;
        session.VoxelTool = this;
        DefinitionId = Definitions.FirstOrDefault();
        session.WorldReplaced += Reset;
    }

    public bool Active { get; private set; }
    public bool HasDraft { get; private set; }
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

    public VoxelCellSetEdit CurrentEdit => new(new[]
    {
        new VoxelCellTarget(Position, Operation,
            Operation == VoxelCellOperation.Remove ? null : new VoxelCellContent(DefinitionId, Orientation))
    });

    public VoxelCellSetResult Preview => _session.ValidateCells(CurrentEdit);

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

    public void SetExact(WorldVoxelCoord position, VoxelDefinitionId definition, VoxelOrientation orientation, VoxelCellOperation operation)
    {
        if (_session.IsChangingWorld)
            return;
        var next = new DraftState(position, definition, orientation, operation);
        if (HasDraft)
            RecordDraftChange(next);
        RestoreDraft(next);
        HasDraft = true;
    }

    public void SetSettings(VoxelDefinitionId definition, VoxelOrientation orientation, VoxelCellOperation operation)
    {
        if (_session.IsChangingWorld)
            return;
        var next = new DraftState(Position, definition, orientation, operation);
        if (HasDraft)
            RecordDraftChange(next);
        RestoreDraft(next);
    }

    public WorldEditSubmission Commit()
    {
        if (_session.IsChangingWorld)
            return new(WorldEditRefusal.WorldChanging, null, null);
        HasDraft = true;
        var submission = _session.SubmitEdit(CurrentEdit);
        if (submission.Accepted && submission.Record?.State is WorldEditState.Executed or WorldEditState.Pending)
            Discard();
        return submission;
    }

    public void Discard()
    {
        HasDraft = false;
        _undo.Clear();
        _redo.Clear();
        HistoryEvictionCount = 0;
        _session.ResolveVoxelDraft();
    }

    private void Reset(WorldReplacement replacement)
    {
        HasDraft = false;
        _undo.Clear();
        _redo.Clear();
        HistoryEvictionCount = 0;
        Height = 0;
        Position = default;
    }

    public void Dispose()
    {
        _session.WorldReplaced -= Reset;
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
}
