using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Content.Contracts;

public enum MaterialCategory { PaintedMetal, BareMetal, Concrete, StoneSoil, Glass, PolymerRubber, Organic }
public enum AllowedVoxelOrientations { None, FourWay, All24 }
public readonly record struct LocalPoint(double X, double Y, double Z);

public abstract record VoxelCollision
{
    private VoxelCollision() { }
    public sealed record None : VoxelCollision;
    public sealed record FullBox : VoxelCollision;
    public sealed record LocalBox(LocalPoint Offset, LocalPoint Size) : VoxelCollision;
}
public abstract record VoxelPresentation
{
    private VoxelPresentation() { }
    public sealed record Cube(ImmutableArray<VoxelShape> Shapes, MaterialCategory Material,
        int TileSpan, AssetRevision Top, AssetRevision Side,
        AssetRevision Bottom, AssetRevision Front) : VoxelPresentation;
    public sealed record Mesh(AssetRevision Model,
        ImmutableArray<MaterialCategory> SurfaceMaterials) : VoxelPresentation;
}
public sealed record LightAppearance(PaintColor Color, double RangeCells);
public sealed record VoxelAppearanceBehaviors(bool Wheelset, LightAppearance? Light);
public sealed record VoxelAppearanceProjection(VoxelDefinitionId Definition,
    AllowedVoxelOrientations AllowedOrientations, VoxelCollision Collision,
    VoxelPresentation Presentation, VoxelAppearanceBehaviors Behaviors);
public sealed record VoxelDisplayProjection(VoxelDefinitionId Definition,
    DisplayText Display, AssetRevision Icon);
public sealed record VoxelCatalogEntry(VoxelDefinitionId Definition, GoodDefinitionProjection? Good);

public enum RotationAxis { X, Y, Z }
public sealed record RotationAppearance(RotationAxis Axis, GridPosition Pivot,
    double MinimumDegrees, double MaximumDegrees, double SpeedDegreesPerSecond);
public sealed record FlattenRegion(GridColumn From, GridColumn To, int Height);
public sealed record CreatureBehavior(long HitPoints, double SpeedMetersPerSecond,
    double RetreatThreshold, double RaidDurationSeconds);

public sealed record SceneModelProjection(DesignHash Design, DesignSceneProjection Scene,
    DisplayText Display);
public sealed record CreatureAppearanceProjection(CreatureDefinitionId Definition,
    DesignSceneProjection Scene, DisplayText Display);
public sealed record SettlementPlotLayout(int Number, PlotSize Size,
    GridPosition At, VoxelOrientation Orientation);
public sealed record SettlementLayoutProjection(SettlementId Owner,
    SettlementTemplateId SourceTemplate,
    SceneModelProjection SceneModel, ImmutableArray<SettlementPlotLayout> Plots,
    DisplayText Display);
