using Train.Domain.Commands;
using Train.Domain.Voxels;

namespace Train.Domain.WorldEditor;

/// <summary>A single horizontal-plane preview. Only precise input or a failed commit creates a draft.</summary>
public sealed class WorldEditorVoxelTool : IDisposable
{
    private readonly VoxelDevelopmentSession _session;
    private bool _committing;

    public WorldEditorVoxelTool(VoxelDevelopmentSession session)
    {
        _session = session;
        session.VoxelTool = this;
        DefinitionId = Definitions.FirstOrDefault();
        session.WorldReplaced += Reset;
    }

    public bool Active { get; private set; }
    public bool HasDraft { get; private set; }
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
            Operation == VoxelCellOperation.Add ? VoxelCellOccupancy.Empty : VoxelCellOccupancy.Occupied,
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
        Position = position;
        DefinitionId = definition;
        Orientation = orientation;
        Operation = operation;
        HasDraft = true;
    }

    public WorldEditSubmission Commit()
    {
        if (_committing)
            return new(WorldEditRefusal.DraftCommitInProgress, null, null);
        if (_session.IsChangingWorld && !HasDraft)
            return new(WorldEditRefusal.WorldChanging, null, null);
        _committing = true;
        try
        {
            HasDraft = true;
            var submission = _session.SubmitVoxelDraft(CurrentEdit);
            if (submission.Accepted && submission.Record?.State is WorldEditState.Executed or WorldEditState.Pending)
                Discard();
            return submission;
        }
        finally { _committing = false; }
    }

    public void Discard()
    {
        HasDraft = false;
        _session.ResolveVoxelDraft();
    }

    private void Reset(WorldReplacement replacement)
    {
        HasDraft = false;
        Height = 0;
        Position = default;
    }

    public void Dispose()
    {
        _session.WorldReplaced -= Reset;
        if (ReferenceEquals(_session.VoxelTool, this))
            _session.VoxelTool = null;
    }
}
