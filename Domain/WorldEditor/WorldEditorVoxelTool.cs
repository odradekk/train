using Train.Domain.Commands;
using Train.Domain.Voxels;
using Train.Content;

namespace Train.Domain.WorldEditor;

public enum WorldEditorVoxelMode { Cell, Brush, Box }

/// <summary>One horizontal-plane preview and draft shared by pointer and precise input.</summary>
public sealed class WorldEditorVoxelTool : IDisposable
{
    private readonly VoxelDevelopmentSession _session;
    private readonly List<DraftState> _undo = new();
    private readonly List<DraftState> _redo = new();
    private readonly HashSet<WorldVoxelCoord> _stroke = new();
    private VoxelRegionPreview? _cachedPreview;
    private VoxelCatalogSnapshot? _previewCatalog;

    public WorldEditorVoxelTool(VoxelDevelopmentSession session)
    {
        _session = session;
        session.VoxelTool = this;
        DefinitionId = Definitions.FirstOrDefault();
        session.WorldReplaced += Reset;
        session.Changed += InvalidatePreview;
    }

    public bool Active { get; private set; }
    public bool HasDraft { get; private set; }
    public WorldEditorVoxelMode Mode { get; private set; }
    public int BrushSize { get; private set; } = 1;
    public int BoxHeight { get; private set; } = 1;
    public WorldVoxelCoord BoxCorner { get; private set; }
    public bool BoxCornerPending { get; private set; }
    public bool StrokeInProgress { get; private set; }
    public bool CanUndoDraft => HasDraft && !StrokeInProgress && !_session.IsChangingWorld && _undo.Count > 0;
    public bool CanRedoDraft => HasDraft && !StrokeInProgress && !_session.IsChangingWorld && _redo.Count > 0;
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

    private VoxelCellContent? Content => Operation == VoxelCellOperation.Remove ? null : new(DefinitionId, Orientation);

    private VoxelCellSetEdit CellEdit => new(new[]
    {
        new VoxelCellTarget(Position, Operation, Content)
    });

    private VoxelCellRegion Region => Mode == WorldEditorVoxelMode.Box
        ? VoxelCellRegion.Box(Position, BoxCorner, BoxHeight)
        : _stroke.Count > 0 ? VoxelCellRegion.FromCells(_stroke) : BrushRegion(Position);

    public VoxelRegionPreview RegionPreview
    {
        get
        {
            if (_cachedPreview is not null && ReferenceEquals(_previewCatalog, _session.CurrentVoxels))
                return _cachedPreview;
            _previewCatalog = _session.CurrentVoxels;
            return _cachedPreview = _session.PreviewRegion(Region, Operation, Content);
        }
    }

    private void InvalidatePreview() => _cachedPreview = null;
    public VoxelCellSetEdit CurrentEdit => Mode == WorldEditorVoxelMode.Cell ? CellEdit : RegionPreview.Edit;
    public VoxelCellSetResult Preview => Mode == WorldEditorVoxelMode.Cell ? _session.ValidateCells(CellEdit) : RegionPreview.Validation;

    public bool SetMode(WorldEditorVoxelMode mode)
    {
        if (HasDraft || _session.IsChangingWorld)
            return false;
        Mode = mode;
        BoxCorner = Position;
        InvalidatePreview();
        return true;
    }

    public bool SetBrushSize(int size)
    {
        if (size < 1 || size > VoxelRegionLimits.MaxBrushSize || HasDraft || _session.IsChangingWorld)
            return false;
        BrushSize = size;
        InvalidatePreview();
        return true;
    }

    public bool SetBoxHeight(int height)
    {
        if (_session.IsChangingWorld || height < 1)
            return false;
        var next = CaptureDraft() with { BoxHeight = height };
        if (HasDraft) RecordDraftChange(next);
        RestoreDraft(next);
        return true;
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
        BoxCorner = BoxCorner with { Y = height };
        InvalidatePreview();
        return true;
    }

    public void Hover(WorldVoxelCoord position)
    {
        if (!Active || _session.IsChangingWorld || HasDraft && !BoxCornerPending)
            return;
        if (BoxCornerPending)
            position = position with { Y = Position.Y };
        if (position == (BoxCornerPending ? BoxCorner : Position))
            return;
        if (BoxCornerPending)
            BoxCorner = position;
        else if (!HasDraft)
        {
            Position = position;
            BoxCorner = position;
        }
        InvalidatePreview();
    }

    public void SetExact(WorldVoxelCoord position, VoxelDefinitionId definition, VoxelOrientation orientation, VoxelCellOperation operation)
    {
        if (_session.IsChangingWorld)
            return;
        var next = CaptureDraft() with { Position = position, DefinitionId = definition, Orientation = orientation, Operation = operation };
        if (Mode == WorldEditorVoxelMode.Brush)
        {
            next = next with { Stroke = _stroke.Count == 0 ? BrushRegion(position).Cells.ToArray()
                : _stroke.Select(cell => new WorldVoxelCoord(
                    position.X + (cell.X - Position.X), position.Y + (cell.Y - Position.Y),
                    position.Z + (cell.Z - Position.Z))).ToArray() };
        }
        if (HasDraft)
            RecordDraftChange(next);
        RestoreDraft(next);
        HasDraft = true;
    }

    public void SetBoxExact(WorldVoxelCoord first, WorldVoxelCoord second, int height,
        VoxelDefinitionId definition, VoxelOrientation orientation, VoxelCellOperation operation)
    {
        if (_session.IsChangingWorld || StrokeInProgress)
            return;
        var next = CaptureDraft() with { Position = first, BoxCorner = second with { Y = first.Y }, BoxHeight = height,
            DefinitionId = definition, Orientation = orientation, Operation = operation };
        if (HasDraft) RecordDraftChange(next);
        RestoreDraft(next);
        BoxCornerPending = false;
        HasDraft = true;
    }

    public bool BoxPoint(WorldVoxelCoord cell)
    {
        if (!Active || Mode != WorldEditorVoxelMode.Box || _session.IsChangingWorld || HasDraft && !BoxCornerPending)
            return false;
        if (BoxCornerPending)
        {
            BoxCorner = cell with { Y = Position.Y };
            BoxCornerPending = false;
        }
        else
        {
            Position = BoxCorner = cell;
            BoxCornerPending = HasDraft = true;
        }
        InvalidatePreview();
        return true;
    }

    public bool BeginStroke(WorldVoxelCoord cell)
    {
        if (!Active || Mode != WorldEditorVoxelMode.Brush || HasDraft || _session.IsChangingWorld)
            return false;
        Position = cell;
        HasDraft = StrokeInProgress = true;
        AddBrush(cell);
        return true;
    }

    public void ExtendStroke(WorldVoxelCoord cell)
    {
        if (!StrokeInProgress || _session.IsChangingWorld || _stroke.Count > VoxelRegionLimits.MaxCells)
            return;
        cell = cell with { Y = Position.Y };
        var previous = Position;
        var steps = Math.Max(Math.Abs((long)cell.X - previous.X), Math.Abs((long)cell.Z - previous.Z));
        if (steps == 0) return;
        AddDraftUndo(CaptureDraft());
        // Interpolate cursor cells so a quick motion cannot leave holes between delivered events.
        for (var step = 1L; step <= steps && _stroke.Count <= VoxelRegionLimits.MaxCells; step++)
            AddBrush(new((int)(previous.X + Math.Round((cell.X - (double)previous.X) * step / steps)), previous.Y,
                (int)(previous.Z + Math.Round((cell.Z - (double)previous.Z) * step / steps))));
        Position = cell;
        InvalidatePreview();
    }

    public WorldEditSubmission? EndStroke()
    {
        if (!StrokeInProgress) return null;
        return Commit();
    }

    /// <summary>Stop sampling a stroke or an unfinished box corner, retaining its unsubmitted draft.</summary>
    public void InterruptDraftInput()
    {
        StrokeInProgress = false;
        BoxCornerPending = false;
    }

    private VoxelCellRegion BrushRegion(WorldVoxelCoord cell)
    {
        var lower = BrushSize / 2;
        var min = new WorldVoxelCoord(cell.X - lower, cell.Y, cell.Z - lower);
        return VoxelCellRegion.Box(min, new(min.X + BrushSize - 1, cell.Y, min.Z + BrushSize - 1), 1);
    }

    private void AddBrush(WorldVoxelCoord cell)
    {
        InvalidatePreview();
        foreach (var covered in BrushRegion(cell).Cells)
        {
            _stroke.Add(covered);
            if (_stroke.Count > VoxelRegionLimits.MaxCells)
            {
                break;
            }
        }
    }

    public void SetSettings(VoxelDefinitionId definition, VoxelOrientation orientation, VoxelCellOperation operation)
    {
        if (_session.IsChangingWorld || StrokeInProgress)
            return;
        var next = CaptureDraft() with { DefinitionId = definition, Orientation = orientation, Operation = operation };
        if (HasDraft)
            RecordDraftChange(next);
        RestoreDraft(next);
    }

    public WorldEditSubmission Commit(bool confirmLargeEdit = false)
    {
        if (_session.IsChangingWorld)
            return new(WorldEditRefusal.WorldChanging, null, null);
        HasDraft = true;
        InterruptDraftInput();
        var submission = Mode == WorldEditorVoxelMode.Cell ? _session.SubmitEdit(CellEdit)
            : _session.SubmitRegion(Region, Operation, Content, confirmLargeEdit);
        if (submission.RegionPreview is { } preview)
        {
            _cachedPreview = preview;
            _previewCatalog = _session.CurrentVoxels;
        }
        if (submission.Accepted && submission.Record?.State is WorldEditState.Executed or WorldEditState.Pending)
            Discard();
        return submission;
    }

    public void Discard()
    {
        HasDraft = false;
        InterruptDraftInput();
        _stroke.Clear();
        InvalidatePreview();
        _undo.Clear();
        _redo.Clear();
        HistoryEvictionCount = 0;
        _session.ResolveVoxelDraft();
    }

    private void Reset(WorldReplacement replacement)
    {
        Discard();
        Height = 0;
        Position = default;
        BoxCorner = default;
    }

    public void Dispose()
    {
        _session.WorldReplaced -= Reset;
        _session.Changed -= InvalidatePreview;
        if (ReferenceEquals(_session.VoxelTool, this))
            _session.VoxelTool = null;
    }

    public bool UndoDraft() => ApplyDraftHistory(_undo, _redo);
    public bool RedoDraft() => ApplyDraftHistory(_redo, _undo);

    private DraftState CaptureDraft() => new(Position, DefinitionId, Orientation, Operation,
        BoxCorner, BoxHeight, _stroke.ToArray());

    private void RecordDraftChange(DraftState next)
    {
        var previous = CaptureDraft();
        if (previous with { Stroke = next.Stroke } == next && previous.Stroke.SequenceEqual(next.Stroke))
            return;
        AddDraftUndo(previous);
    }

    private void AddDraftUndo(DraftState previous)
    {
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
        if (!HasDraft || StrokeInProgress || _session.IsChangingWorld || source.Count == 0)
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
        BoxCorner = state.BoxCorner;
        BoxHeight = state.BoxHeight;
        _stroke.Clear();
        _stroke.UnionWith(state.Stroke);
        InvalidatePreview();
    }

    private sealed record DraftState(WorldVoxelCoord Position, VoxelDefinitionId DefinitionId,
        VoxelOrientation Orientation, VoxelCellOperation Operation, WorldVoxelCoord BoxCorner, int BoxHeight,
        WorldVoxelCoord[] Stroke);
}
