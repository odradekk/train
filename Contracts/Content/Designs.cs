using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Content.Contracts;

public readonly record struct GridPosition(int X, int Y, int Z);
public readonly record struct GridColumn(int X, int Z);
public sealed record GridBounds(GridPosition Minimum, GridPosition Maximum);
public enum VoxelOrientation
{
    PxPy, PxNy, PxPz, PxNz, NxPy, NxNy, NxPz, NxNz,
    PyPx, PyNx, PyPz, PyNz, NyPx, NyNx, NyPz, NyNz,
    PzPx, PzNx, PzPy, PzNy, NzPx, NzNx, NzPy, NzNy
}
public readonly record struct VoxelShape(string Name);
public readonly record struct PaintColor(string Value);
public sealed record DisplayText(string Name, string Description);

public abstract record DesignSpecification
{
    private DesignSpecification() { }
    public sealed record Vehicle(SizeClass? Size) : DesignSpecification;
    public sealed record Building(PlotSize? PlotSize, BuildingKind? Kind,
        ImmutableArray<FacilityTag> FacilityTags) : DesignSpecification;
    public sealed record Assembly(RotationAppearance? Rotation) : DesignSpecification;
    public sealed record FunctionalComposite(ImmutableArray<PartCapability> Behaviors,
        RotationAppearance? Rotation) : DesignSpecification;
    public sealed record SceneModel(FlattenRegion? Flatten) : DesignSpecification;
    public sealed record Creature(CreatureBehavior? Behavior,
        PartCapability.Weapon? Weapon) : DesignSpecification;
}

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
    public sealed record AssemblyCopy(GridPosition At, VoxelOrientation Orientation,
        DesignDraft Design) : NestedDesignItem;
    public sealed record FunctionalCompositeReference(GridPosition At,
        VoxelOrientation Orientation, CompositeDefinitionId Definition,
        PaintColor? Paint) : NestedDesignItem;
    public sealed record SceneModelCopy(GridPosition At, VoxelOrientation Orientation,
        DesignDraft Design) : NestedDesignItem;
}

public abstract record DesignElementPath
{
    private DesignElementPath() { }
    public sealed record Cell(ImmutableArray<int> NestedItemIndices, int CellIndex)
        : DesignElementPath;
    public sealed record FunctionalComposite(ImmutableArray<int> NestedItemIndices,
        int NestedItemIndex) : DesignElementPath;
}
public enum LockedDesignOperation { Move, Rotate, Copy, Remove, Replace, Paint, ChangeShape, Enter }
public sealed record DesignEditLock(DesignElementPath Element, ContentReference Reference,
    PackageId Package, ImmutableArray<LockedDesignOperation> BlockedOperations,
    ImmutableArray<ContentDiagnostic> Diagnostics);

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
public sealed record KnownDesignSubtotal(double MassTons, Money StructuralBaseValue,
    long MaxStructureDurability, ImmutableArray<PartProjection> Parts,
    ImmutableArray<GoodsQuantity> BillOfMaterials);

public sealed record DesignElementProjection(PartId Number, DesignElementPath Element,
    ComputedValue<ImmutableArray<GridPosition>> OccupiedCells, bool FunctionalPart,
    bool Missing);
public sealed record SceneVoxelProjection(GridPosition At, VoxelDefinitionId Voxel,
    VoxelOrientation Orientation, VoxelShape? Shape, PaintColor? Paint,
    PartId Number, DesignElementPath Element,
    ImmutableArray<int> ObjectPath, GridPosition LocalAt,
    VoxelOrientation LocalOrientation, bool Missing);
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
public sealed record NestedObjectProjection(ImmutableArray<int> NestedItemIndices,
    GridPosition At, VoxelOrientation Orientation, NestedObjectDetails Details);
public sealed record RotationSweepProjection(ImmutableArray<int> NestedItemIndices,
    ComputedValue<ImmutableArray<GridPosition>> SweptCells,
    ComputedValue<ImmutableArray<GridPosition>> ConflictCells);
public sealed record DesignSceneProjection(DesignHash? Design,
    GridBounds? OccupiedBounds, GridBounds? AllowedEnvelope, bool OccupancyComplete,
    RotationAppearance? RootRotation, FlattenRegion? RootFlatten,
    ImmutableArray<SceneVoxelProjection> Voxels,
    ImmutableArray<DesignElementProjection> Elements,
    ImmutableArray<NestedObjectProjection> NestedObjects,
    ImmutableArray<RotationSweepProjection> RotationSweeps,
    ImmutableArray<DesignEditLock> EditLocks);

public abstract record DesignEvaluation
{
    private DesignEvaluation() { }
    public sealed record Complete(DesignHash Design, CompositionHash Composition,
        DesignDraft Definition, DesignInput? Core, DesignSceneProjection Scene,
        bool IsValid, ImmutableArray<ContentDiagnostic> Diagnostics) : DesignEvaluation;
    public sealed record Partial(DesignDraft Definition, PartialDesignMetrics Metrics,
        KnownDesignSubtotal KnownSubtotal, DesignProjection? CorePlaceholder,
        DesignSceneProjection Scene,
        ImmutableArray<ContentDiagnostic> Diagnostics) : DesignEvaluation;
    public sealed record Failed(ImmutableArray<ContentDiagnostic> Diagnostics) : DesignEvaluation;
}
public sealed record DesignChangeEvaluation(DesignChangeProjection Core,
    ImmutableArray<DesignElementCorrespondence> Elements, bool CanApplyImmediately,
    ImmutableArray<ContentDiagnostic> Diagnostics);
public sealed record DesignElementCorrespondence(PartCorrespondence Parts,
    DesignElementPath Before, DesignElementPath After);
