using System.Collections.Immutable;
using Train.Content.Contracts;

namespace Train.Scenes.Contracts;

public sealed record EditStamp(EditSessionId Session, EditRevision Revision, ContentGeneration Content);
public sealed record EditSnapshot(EditStamp Stamp, DesignDraft Draft);
public enum EditElementKind { Cell, Assembly, FunctionalComposite, SceneModel }
public sealed record EditElement(EditElementId Id, EditElementId? Parent,
    EditElementKind Kind, GridPosition At, DesignElementPath? Path,
    ImmutableArray<int> ObjectPath, bool Locked);
public sealed record EditState(EditSnapshot Snapshot, bool IsDirty, bool CanUndo, bool CanRedo,
    EditElementId? EnteredObject, ImmutableArray<EditElementId> Selection,
    ImmutableArray<EditElement> Elements, DesignEvaluation? CurrentEvaluation,
    ImmutableArray<ContentDiagnostic> EvaluationDiagnostics);
public enum GridAxis { X, Y, Z }
public sealed record EditWorkPlane(GridAxis Axis, int Coordinate);
public sealed record EditViewOptions(EditWorkPlane WorkPlane, int? HighestVisibleY,
    SceneDebugLayer DebugLayers);
public abstract record EditSelection
{
    private EditSelection() { }
    public sealed record Elements(ImmutableArray<EditElementId> Ids) : EditSelection;
    public sealed record Box(GridBounds Bounds) : EditSelection;
}
public abstract record EditFootprint
{
    private EditFootprint() { }
    public sealed record Cell(GridPosition At) : EditFootprint;
    public sealed record Box(GridBounds Bounds) : EditFootprint;
    public sealed record Stroke(ImmutableArray<GridPosition> Samples, int BrushWidth) : EditFootprint;
}
public abstract record CellEdit
{
    private CellEdit() { }
    public sealed record Add(VoxelPaletteEntry Value) : CellEdit;
    public sealed record Remove : CellEdit;
    public sealed record Replace(VoxelPaletteEntry Value) : CellEdit;
    public sealed record Paint(PaintColor? Color) : CellEdit;
    public sealed record Shape(VoxelShape Value) : CellEdit;
}
public abstract record EditOperation
{
    private EditOperation() { }
    public sealed record Cells(EditFootprint Footprint, CellEdit Action) : EditOperation;
    public sealed record MoveSelection(GridPosition Offset) : EditOperation;
    public sealed record RotateSelection(GridAxis Axis, GridPosition Pivot, int QuarterTurns) : EditOperation;
    public sealed record CopySelection(GridPosition Offset) : EditOperation;
    public sealed record RemoveSelection : EditOperation;
    public sealed record PaintSelection(PaintColor? Color) : EditOperation;
    public sealed record ShapeSelection(VoxelShape Shape) : EditOperation;
    public sealed record PlaceNested(NestedDesignItem Value) : EditOperation;
    public sealed record ReplaceNested(EditElementId Element, NestedDesignItem Value) : EditOperation;
    public sealed record Properties(DisplayText Display, DesignSpecification Specification) : EditOperation;
}
public sealed record EditConflict(GridPosition? Cell, EditElementId? Element, SceneProblem Problem);
public interface IEditPreview
{
    EditStamp Basis { get; }
    int CoveredCells { get; }
    int AffectedCells { get; }
    int SkippedCells { get; }
    bool OccupancyComplete { get; }
    bool CanApply { get; }
    ImmutableArray<EditConflict> Conflicts { get; }
    ImmutableArray<GridPosition> HighlightCells { get; }
}
public interface IDesignEditSession : IDisposable
{
    EditSessionId Id { get; }
    ContentGeneration Content { get; }
    EditState Read();
    EditSnapshot Capture();
    SceneResult<DesignDraft> CaptureObject(EditElementId element);
    SceneResult<EditState> Select(EditSelection selection);
    SceneResult<EditState> Enter(EditElementId? element);
    SceneResult<IEditPreview> Preview(EditOperation operation);
    SceneResult<EditState> Apply(EditOperation operation);
    SceneResult<EditState> Undo();
    SceneResult<EditState> Redo();
    bool AcceptEvaluation(EditStamp requested, DesignEvaluation result);
    bool MarkSaved(EditSnapshot saved);
    SceneResult<ISceneFrame> Present(EditViewOptions options, ISceneFrame? displayedFrame);
    SceneResult<EditElementId> Locate(EditStamp stamp, DesignElementPath path);
}
