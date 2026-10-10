// 完整布局和编辑快照属于内容；核心只接收 ContentInputs.cs 中的业务投影。
using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Content.Contracts;

/// <summary>整数格坐标；一格 0.5 米，+X 向右，+Y 向上，+Z 向前。</summary>
public readonly record struct GridPosition(int X, int Y, int Z);
public readonly record struct GridColumn(int X, int Z);
/// <summary>包含两端的格范围；不以宿主自行推导的尺寸替代内容的包络结果。</summary>
public sealed record GridBounds(GridPosition Minimum, GridPosition Maximum);
/// <summary>局部 +X 和 +Y 的宿主轴；局部 +Z 为二者叉积。PxPy 是默认朝向。</summary>
public enum VoxelOrientation
{
    PxPy, PxNy, PxPz, PxNz, NxPy, NxNy, NxPz, NxNz,
    PyPx, PyNx, PyPz, PyNz, NyPx, NyNx, NyPz, NyNz,
    PzPx, PzNx, PzPy, PzNy, NzPx, NzNx, NzPy, NzNy
}
/// <summary>形状目录中的名字；草稿允许尚未被目录接受的名字。</summary>
public readonly record struct VoxelShape(string Name);
/// <summary>颜色的编码文本；有效设计使用小写 #rrggbb，草稿保留未通过校验的文本。</summary>
public readonly record struct PaintColor(string Value);
public sealed record DisplayText(string Name, string Description);

/// <summary>缺失必填参数用 null 表达；不以零或默认枚举值补成有效设计。</summary>
public abstract record DesignSpecification
{
    private DesignSpecification() { }
    public sealed record Vehicle(SizeClass? Size) : DesignSpecification;
    public sealed record Building(PlotSize? PlotSize, BuildingKind? Kind,
        ImmutableArray<FacilityTag> FacilityTags) : DesignSpecification;
    public sealed record Assembly(RotationAppearance? Rotation) : DesignSpecification;
    /// <summary>仅包创作可声明；其布局不能含功能体素或任何嵌套对象。</summary>
    public sealed record FunctionalComposite(ImmutableArray<PartCapability> Behaviors,
        RotationAppearance? Rotation) : DesignSpecification;
    public sealed record SceneModel(FlattenRegion? Flatten) : DesignSpecification;
    public sealed record Creature(CreatureBehavior? Behavior,
        PartCapability.Weapon? Weapon) : DesignSpecification;
}

/// <summary>可编码但不必合法的不可变快照；集合及递归元素不可变，集合不能为 default。</summary>
public sealed record DesignDraft(DisplayText Display, DesignSpecification Specification,
    VoxelLayout Layout);
public sealed record VoxelPaletteEntry(VoxelDefinitionId Voxel,
    VoxelOrientation Orientation, VoxelShape? Shape, PaintColor? Paint);
public sealed record LayoutCell(GridPosition At, int PaletteIndex);
public sealed record VoxelLayout(ImmutableArray<VoxelPaletteEntry> Palette,
    ImmutableArray<LayoutCell> Cells, ImmutableArray<NestedDesignItem> NestedItems);

public abstract record NestedDesignItem
{
    private NestedDesignItem() { }
    /// <summary>复制品具有自己的递归布局；没有来源蓝图身份或实例运行状态。</summary>
    public sealed record AssemblyCopy(GridPosition At, VoxelOrientation Orientation,
        DesignDraft Design) : NestedDesignItem;
    /// <summary>封闭的整件引用；内部布局不属于宿主草稿，只能整件操作和整件涂装。</summary>
    public sealed record FunctionalCompositeReference(GridPosition At,
        VoxelOrientation Orientation, CompositeDefinitionId Definition,
        PaintColor? Paint) : NestedDesignItem;
    public sealed record SceneModelCopy(GridPosition At, VoxelOrientation Orientation,
        DesignDraft Design) : NestedDesignItem;
}

/// <summary>索引针对固定草稿快照；不是部件编号，编辑后必须重新取得内容计算的对应。</summary>
public abstract record DesignElementPath
{
    private DesignElementPath() { }
    public sealed record Cell(ImmutableArray<int> NestedItemIndices, int CellIndex)
        : DesignElementPath;
    public sealed record FunctionalComposite(ImmutableArray<int> NestedItemIndices,
        int NestedItemIndex) : DesignElementPath;
}
public enum LockedDesignOperation { Move, Rotate, Copy, Remove, Replace, Paint, ChangeShape, Enter }
/// <summary>缺失引用及其祖先操作的锁定边界；Scenes 必须检查间接影响，不能只禁用按钮。</summary>
public sealed record DesignEditLock(DesignElementPath Element, ContentReference Reference,
    PackageId Package, ImmutableArray<LockedDesignOperation> BlockedOperations,
    ImmutableArray<ContentDiagnostic> Diagnostics);

/// <summary>Unknown 不得显示或用于提交为有效的零；Known 可包含真实的零。</summary>
public abstract record ComputedValue<T> where T : notnull
{
    private ComputedValue() { }
    public sealed record Known(T Value) : ComputedValue<T>;
    public sealed record Unknown(ImmutableArray<ContentDiagnostic> Reasons) : ComputedValue<T>;
}
public sealed record PartialDesignMetrics(ComputedValue<double> MassTons,
    ComputedValue<Money> StructuralBaseValue, ComputedValue<long> MaxStructureDurability,
    ComputedValue<ImmutableArray<PartProjection>> Parts,
    ComputedValue<ImmutableArray<GoodsQuantity>> BillOfMaterials,
    ComputedValue<int> VehicleLengthCells);
/// <summary>已解析条目的小计，明确不是完整设计总量；缺失条目不以零贡献宣称计算完整。</summary>
public sealed record KnownDesignSubtotal(double MassTons, Money StructuralBaseValue,
    long MaxStructureDurability, ImmutableArray<PartProjection> Parts,
    ImmutableArray<GoodsQuantity> BillOfMaterials);

/// <summary>内容只生成一次编号与占格对应；所有体素均有号，封闭复合体整件共用一个号。</summary>
public sealed record DesignElementProjection(PartId Number, DesignElementPath Element,
    ComputedValue<ImmutableArray<GridPosition>> OccupiedCells, bool FunctionalPart,
    bool Missing);
/// <summary>
/// At/Orientation 是设计零姿态下的最外层格坐标及朝向，供占格与拾取使用；
/// LocalAt/LocalOrientation 相对 ObjectPath 指定的对象，供父子动画构建。
/// 缺失体素仍提供原定义、位置和编号，由场景显示占位；不伪造其外观定义。
/// </summary>
public sealed record SceneVoxelProjection(GridPosition At, VoxelDefinitionId Voxel,
    VoxelOrientation Orientation, VoxelShape? Shape, PaintColor? Paint,
    PartId Number, DesignElementPath Element,
    ImmutableArray<int> ObjectPath, GridPosition LocalAt,
    VoxelOrientation LocalOrientation, bool Missing);
/// <summary>对象种类与专属资料一同表达；缺失复合体只保留原引用和整件编号，不伪造转动参数。</summary>
public abstract record NestedObjectDetails
{
    private NestedObjectDetails() { }
    public sealed record Assembly(RotationAppearance? Rotation) : NestedObjectDetails;
    public sealed record FunctionalComposite(CompositeDefinitionId Definition, PartId Number,
        RotationAppearance? Rotation) : NestedObjectDetails;
    public sealed record MissingFunctionalComposite(CompositeDefinitionId Definition, PartId Number)
        : NestedObjectDetails;
    public sealed record SceneModel(FlattenRegion? Flatten) : NestedObjectDetails;
}
/// <summary>
/// 路径去掉最后一项即父对象路径，空路径表示最外层对象；At/Orientation 相对父对象。
/// Details 中的 Rotation/Pivot 与 Flatten 均使用对象自身坐标，不能把子对象烘焙成不随父对象转动的根对象。
/// 功能复合体提供整件编号和原引用；缺失时仍有放置变换，但不宣称其占格已知。
/// </summary>
public sealed record NestedObjectProjection(ImmutableArray<int> NestedItemIndices,
    GridPosition At, VoxelOrientation Orientation, NestedObjectDetails Details);
/// <summary>
/// 对象路径沿用 NestedObjectProjection.NestedItemIndices，空路径为根。
/// 两组格均在最外层设计坐标中；内容按 rotation 全角度范围及嵌套转动计算，包含自身占格的扫过范围不把自身判为冲突。
/// 缺失占格或转动资料时，相应结果为 Unknown，不以已知格子或空集合冒充完整结果。
/// </summary>
public sealed record RotationSweepProjection(ImmutableArray<int> NestedItemIndices,
    ComputedValue<ImmutableArray<GridPosition>> SweptCells,
    ComputedValue<ImmutableArray<GridPosition>> ConflictCells);
/// <summary>
/// Voxels 包含已知格及缺失单格占位；缺失功能复合体见 NestedObjects/Elements，不能按一格补齐。
/// OccupancyComplete=false 时 OccupiedBounds 只涵盖已知占格，不能用于宣布冲突检查完成。
/// 根对象自身的转动或平整声明见 RootRotation/RootFlatten。
/// </summary>
public sealed record DesignSceneProjection(DesignHash? Design,
    GridBounds? OccupiedBounds, GridBounds? AllowedEnvelope, bool OccupancyComplete,
    RotationAppearance? RootRotation, FlattenRegion? RootFlatten,
    ImmutableArray<SceneVoxelProjection> Voxels,
    ImmutableArray<DesignElementProjection> Elements,
    ImmutableArray<NestedObjectProjection> NestedObjects,
    ImmutableArray<RotationSweepProjection> RotationSweeps,
    ImmutableArray<DesignEditLock> EditLocks);

/// <summary>完整、部分和失败明确区分；计算结果只适用于请求固定的草稿及内容世代。</summary>
public abstract record DesignEvaluation
{
    private DesignEvaluation() { }
    /// <summary>场景模型与生物没有核心 DesignInput；其他类别必须提供。完整结果仍可不合法，Scene.Design 必须等于 Design。</summary>
    public sealed record Complete(DesignHash Design, CompositionHash Composition,
        DesignDraft Definition, DesignInput? Core, DesignSceneProjection Scene,
        bool IsValid, ImmutableArray<ContentDiagnostic> Diagnostics) : DesignEvaluation;
    /// <summary>
    /// CorePlaceholder 仅供恢复既有缺失设计，不能成为建造或编辑提交目标。
    /// Scene 始终提供已知布局、元素与锁；无法取得规范设计身份时仅 Scene.Design 为空。
    /// </summary>
    public sealed record Partial(DesignDraft Definition, PartialDesignMetrics Metrics,
        KnownDesignSubtotal KnownSubtotal, DesignProjection? CorePlaceholder,
        DesignSceneProjection Scene,
        ImmutableArray<ContentDiagnostic> Diagnostics) : DesignEvaluation;
    public sealed record Failed(ImmutableArray<ContentDiagnostic> Diagnostics) : DesignEvaluation;
}
/// <summary>差额及耐久保留对应由内容计算；调用方不重做布局遍历或对应算法。</summary>
public sealed record DesignChangeEvaluation(DesignChangeProjection Core,
    ImmutableArray<DesignElementCorrespondence> Elements, bool CanApplyImmediately,
    ImmutableArray<ContentDiagnostic> Diagnostics);
public sealed record DesignElementCorrespondence(PartCorrespondence Parts,
    DesignElementPath Before, DesignElementPath After);
