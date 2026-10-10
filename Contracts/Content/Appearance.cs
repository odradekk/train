// 只读表现投影；不携带 Godot 对象、像素、解码网格或渲染资源。
using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Content.Contracts;

public enum MaterialCategory { PaintedMetal, BareMetal, Concrete, StoneSoil, Glass, PolymerRubber, Organic }
public enum AllowedVoxelOrientations { None, FourWay, All24 }
public readonly record struct LocalPoint(double X, double Y, double Z);

/// <summary>局部碰撞以格边长为 1；只供表现，拾取与逻辑占格使用完整格。</summary>
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
    /// <summary>形状列表非空；跨度为 1、2、4 或 8；Front 已解析缺省为 Side。</summary>
    public sealed record Cube(ImmutableArray<VoxelShape> Shapes, MaterialCategory Material,
        int TileSpan, AssetRevision Top, AssetRevision Side,
        AssetRevision Bottom, AssetRevision Front) : VoxelPresentation;
    /// <summary>模型修订引用原始 GLB；表面材质按资产检查返回的表面次序排列。</summary>
    public sealed record Mesh(AssetRevision Model,
        ImmutableArray<MaterialCategory> SurfaceMaterials) : VoxelPresentation;
}
/// <summary>灯光范围为有限正数，单位格；受损闪烁与被毁熄灭由实例耐久驱动。</summary>
public sealed record LightAppearance(PaintColor Color, double RangeCells);
public sealed record VoxelAppearanceBehaviors(bool Wheelset, LightAppearance? Light);
public sealed record VoxelAppearanceProjection(VoxelDefinitionId Definition,
    AllowedVoxelOrientations AllowedOrientations, VoxelCollision Collision,
    VoxelPresentation Presentation, VoxelAppearanceBehaviors Behaviors);
public sealed record VoxelDisplayProjection(VoxelDefinitionId Definition,
    DisplayText Display, AssetRevision Icon);
/// <summary>Good 为 null 表示场景体素；否则为同 Definition 的 GoodId.Voxel 投影。</summary>
public sealed record VoxelCatalogEntry(VoxelDefinitionId Definition, GoodDefinitionProjection? Good);

public enum RotationAxis { X, Y, Z }
/// <summary>轴为设备自身轴，枢轴经过指定格中心；角度为度，速度为度/秒。</summary>
public sealed record RotationAppearance(RotationAxis Axis, GridPosition Pivot,
    double MinimumDegrees, double MaximumDegrees, double SpeedDegreesPerSecond);
/// <summary>X/Z 两端包含；Height 以下填地面，以上清空，高度为整数格。</summary>
public sealed record FlattenRegion(GridColumn From, GridColumn To, int Height);
/// <summary>生物行为仅在创作设计中声明；有效数值有限，生命为正整数，速度为米/模拟秒。</summary>
public sealed record CreatureBehavior(long HitPoints, double SpeedMetersPerSecond,
    double RetreatThreshold, double RaidDurationSeconds);

public sealed record SceneModelProjection(DesignHash Design, DesignSceneProjection Scene,
    FlattenRegion? Flatten, DisplayText Display);
/// <summary>个体使用当前种类投影；不在生物实例上复制设计。</summary>
public sealed record CreatureAppearanceProjection(CreatureDefinitionId Definition,
    DesignSceneProjection Scene, DisplayText Display);
public sealed record SettlementPlotLayout(int Number, PlotSize Size,
    GridPosition At, VoxelOrientation Orientation);
/// <summary>固定模板副本的场景部分；来源身份不用于重新读取当前模板，场景模型设计固定，地块朝向限水平四向。</summary>
public sealed record SettlementLayoutProjection(SettlementId Owner,
    SettlementTemplateId SourceTemplate,
    SceneModelProjection SceneModel, ImmutableArray<SettlementPlotLayout> Plots,
    DisplayText Display);
