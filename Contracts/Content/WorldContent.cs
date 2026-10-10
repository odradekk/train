// 场景和显示读取的世界内容；运行中的世界状态仍由核心查询提供。
// 蓝图引用已经解析成固定设计，体素层不读取包文件或自行计算设计哈希。
using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Content.Contracts;

public sealed record LairAppearanceProjection(LairTypeId Id,
    SceneModelProjection SceneModel, ContentDisplay Display);
public sealed record TerrainDecoration(DesignHash SceneModel, int Weight);
public sealed record RailwaySceneModels(int Tracks, DesignHash Bridge, DesignHash TunnelPortal);
/// <summary>噪声尺度为米、振幅有限非负；装饰密度为件/平方米；采样算法由体素层实现。</summary>
public sealed record TerrainAppearanceProjection(TerrainTypeId Id,
    VoxelDefinitionId SurfaceVoxel, VoxelDefinitionId SubsurfaceVoxel, VoxelDefinitionId RockVoxel,
    ImmutableArray<NoiseOctave> LocalHeightNoise,
    ImmutableArray<TerrainDecoration> Decorations, double DecorationTargetPerSquareMeter,
    ImmutableArray<RailwaySceneModels> RailwayModels, DisplayText Display);
/// <summary>半径为有限正数米，坡度为有限非负高差/水平距离；不改变核心线路长度。</summary>
public sealed record RailwayAppearanceProjection(RuleDefinitionId Id,
    double MinimumCurveRadiusMeters, double MaximumGrade);
