using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Content.Contracts;

public sealed record LairAppearanceProjection(LairTypeId Id,
    SceneModelProjection SceneModel, ContentDisplay Display);
public sealed record TerrainDecoration(DesignHash SceneModel, int Weight);
public sealed record RailwaySceneModels(int Tracks, DesignHash Bridge, DesignHash TunnelPortal, DesignHash RepairSite);
public sealed record TerrainAppearanceProjection(TerrainTypeId Id,
    VoxelDefinitionId SurfaceVoxel, VoxelDefinitionId SubsurfaceVoxel, VoxelDefinitionId RockVoxel,
    ImmutableArray<NoiseOctave> LocalHeightNoise,
    ImmutableArray<TerrainDecoration> Decorations, double DecorationTargetPerSquareMeter,
    ImmutableArray<RailwaySceneModels> RailwayModels, DesignHash ConstructionSite, DisplayText Display);
public sealed record RailwayAppearanceProjection(RuleDefinitionId Id,
    VoxelDefinitionId BallastVoxel, double MinimumCurveRadiusMeters, double MaximumGrade);
