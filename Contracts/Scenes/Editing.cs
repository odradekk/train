// 编辑只修改本次工作草稿；完整合法性、设计哈希和部件编号仍由内容计算。
using System.Collections.Immutable;
using Train.Content.Contracts;

namespace Train.Scenes.Contracts;

public sealed record EditStamp(EditSessionId Session, EditRevision Revision, ContentGeneration Content);
/// <summary>可交给内容校验、编码或保存的固定值；后续编辑和关闭不改变它。</summary>
public sealed record EditSnapshot(EditStamp Stamp, DesignDraft Draft);
public enum EditElementKind { Cell, Assembly, FunctionalComposite, SceneModel }
/// <summary>会话内稳定身份，不是核心部件编号；Path 只适用于 State.Snapshot 的固定修订。</summary>
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
    /// <summary>当前进入对象的整数格范围；封闭复合体只作为整件选中，不能形成内部格选区。</summary>
    public sealed record Box(GridBounds Bounds) : EditSelection;
}
public abstract record EditFootprint
{
    private EditFootprint() { }
    public sealed record Cell(GridPosition At) : EditFootprint;
    public sealed record Box(GridBounds Bounds) : EditFootprint;
    /// <summary>一笔各次立方笔刷的最小角格和边长；体素合并覆盖并去重，不在采样间隐式连线。</summary>
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
    /// <summary>绕当前进入对象中的格中心、正轴按右手规则旋转；QuarterTurns 为 1、2 或 3。</summary>
    public sealed record RotateSelection(GridAxis Axis, GridPosition Pivot, int QuarterTurns) : EditOperation;
    public sealed record CopySelection(GridPosition Offset) : EditOperation;
    public sealed record RemoveSelection : EditOperation;
    public sealed record PaintSelection(PaintColor? Color) : EditOperation;
    public sealed record ShapeSelection(VoxelShape Shape) : EditOperation;
    /// <summary>只接受当前内容支持的类别及明确的副本或封闭引用，不由宿主解析蓝图文件。</summary>
    public sealed record PlaceNested(NestedDesignItem Value) : EditOperation;
    public sealed record ReplaceNested(EditElementId Element, NestedDesignItem Value) : EditOperation;
    /// <summary>修改当前进入对象的文字及类别参数，也构成一次可撤销编辑；不编辑独立定义表单。</summary>
    public sealed record Properties(DisplayText Display, DesignSpecification Specification) : EditOperation;
}
public sealed record EditConflict(GridPosition? Cell, EditElementId? Element, SceneProblem Problem);
/// <summary>某一固定修订的操作预览；确认后仍只能应用这份预览，不能替换范围或目标。</summary>
public interface IEditPreview
{
    EditStamp Basis { get; }
    int CoveredCells { get; }
    int AffectedCells { get; }
    int SkippedCells { get; }
    bool OccupancyComplete { get; }
    bool CanApply { get; }
    bool RequiresConfirmation { get; }
    ImmutableArray<EditConflict> Conflicts { get; }
    /// <summary>当前进入对象的格坐标；RotateSelection 为离散编辑动作扫过范围，放置、替换、移动和复制为目标覆盖，其余为操作范围；设备行为的全转动范围来自内容评估。</summary>
    ImmutableArray<GridPosition> HighlightCells { get; }
}
public interface IDesignEditSession : IDisposable
{
    EditSessionId Id { get; }
    ContentGeneration Content { get; }
    EditState Read();
    EditSnapshot Capture();
    /// <summary>导出可编辑嵌套副本自身坐标中的独立草稿，用于另存蓝图；封闭引用或缺失锁禁止复制时拒绝。</summary>
    SceneResult<DesignDraft> CaptureObject(EditElementId element);
    /// <summary>替换当前选区，不修改草稿、修订或撤销栈。</summary>
    SceneResult<EditState> Select(EditSelection selection);
    /// <summary>null 返回根对象；只能进入可编辑副本，切换层级清空选区和未应用预览。</summary>
    SceneResult<EditState> Enter(EditElementId? element);
    SceneResult<IEditPreview> Preview(EditOperation operation);
    /// <summary>只接受本会话当前修订的预览；拒绝不改草稿和历史，成功整笔入栈。</summary>
    SceneResult<EditState> Apply(IEditPreview preview, bool confirmed);
    SceneResult<EditState> Undo();
    SceneResult<EditState> Redo();
    /// <summary>
    /// 宿主成对保留请求与结果，保证 result 来自 requested.Content 的固定内容和草稿。
    /// 会话核对修订，并递归比较草稿全部字段和集合内容，不能以默认 record 相等替代完整内容比较。
    /// 迟到返回 false，不覆盖提示或变更编辑锁；requested 不是对错误来源结果的验证凭证。
    /// </summary>
    bool AcceptEvaluation(EditStamp requested, DesignEvaluation result);
    /// <summary>对应保存目标成功后调用：本局记录已提交或外部草稿已写盘；不清除捕获后新增修改。</summary>
    bool MarkSaved(EditSnapshot saved);
    /// <summary>工作平面和剖切仅影响观察，不改变草稿；帧与当前编辑修订绑定。</summary>
    SceneResult<ISceneFrame> Present(EditViewOptions options, ISceneFrame? displayedFrame);
    /// <summary>由当前修订的内容诊断路径定位会话元素；不把旧数组索引解释为新修订。</summary>
    SceneResult<EditElementId> Locate(EditStamp stamp, DesignElementPath path);
}
