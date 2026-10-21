using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Scenes.Contracts;

[Flags]
public enum ExposedFaceEdges { None = 0, MinimumU = 1, MaximumU = 2, MinimumV = 4, MaximumV = 8 }
public readonly record struct SceneVertex(Vector3 Position, Vector3 Normal, Vector2 TextureUv,
    Vector2 FaceUv, ExposedFaceEdges ExposedEdges, float RelativeHeight, float Damage);
public enum BuiltInMaterial { RailSteel, Sleeper, Water, Construction }
public abstract record SceneMaterial
{
    private SceneMaterial() { }
    public sealed record Voxel(AssetRevision Texture, MaterialCategory Category, PaintColor? Paint) : SceneMaterial;
    public sealed record BuiltIn(BuiltInMaterial Kind) : SceneMaterial;
}
public sealed record SceneMesh(SceneMaterial Material, ImmutableArray<SceneVertex> Vertices,
    ImmutableArray<int> TriangleIndices);
public sealed record SurfaceState(bool WearEnabled, ulong WearSeed, float Damage, bool Scorched);
public abstract record SceneVisual
{
    private SceneVisual() { }
    public sealed record Geometry(SceneMesh Mesh, SurfaceState Surface) : SceneVisual;
    public sealed record Model(AssetRevision Asset, ImmutableArray<MaterialCategory> Materials,
        PaintColor? Paint, SurfaceState Surface, Vector4 RelativeHeightPlane) : SceneVisual;
    public sealed record Wheelset(double GaugeCells, double RadiusCells,
        double WidthCells, double RotationRadians) : SceneVisual;
    public sealed record Missing(ContentReference Reference, bool KnownCell) : SceneVisual;
}
public sealed record SceneLight(PaintColor Color, double RangeCells, float Damage, ulong Seed);
public enum PersistentEffectKind { Smoke, Fire, Sparks, Leak, FacilitySmoke }
public sealed record PersistentSceneEffect(PersistentEffectKind Kind, Vector3 LocalPosition,
    float Strength, ulong Seed);
public sealed record SceneObject(SceneObjectId Id, SceneObjectId? Parent, ScenePose Pose,
    WorldObjectId? Subject, ImmutableArray<SceneVisual> Visuals,
    ImmutableArray<SceneLight> Lights, ImmutableArray<PersistentSceneEffect> Effects);

public enum SceneEffectKind { Shot, Impact, Fragments, Destruction, RailwayDamage }
public sealed record SceneEffectCue(SceneEffectId Id, SceneEffectKind Kind,
    Vector3 From, Vector3? To, PaintColor? Color, VoxelCollision? FragmentCollision,
    WorldObjectId? Subject, double StartedAtSimulationSeconds);
public sealed record TerrainCollisionPatch(SceneObjectId Id, Vector3 Origin,
    int Width, int Depth, double SpacingCells, ImmutableArray<float> Heights);
public sealed record SceneEnvironment(ImmutableArray<SceneBounds> FoggedRegions,
    ImmutableArray<TerrainCollisionPatch> TerrainCollision);
public sealed record SceneUpdate(SceneFrameStamp? Base, SceneFrameStamp Frame,
    Vector3 ShiftExistingRoots, ImmutableArray<SceneObject> Upserts,
    ImmutableArray<SceneObjectId> Removed, SceneEnvironment Environment,
    ImmutableArray<SceneEffectCue> Effects);
public enum SceneReadiness { Loading, Partial, Ready, Failed }
[Flags]
public enum SceneDebugLayer { None = 0, Chunks = 1, RailwaySegments = 2, Origin = 4, PickRay = 8, Centerline = 16 }
public sealed record SceneDebugLine(Vector3 From, Vector3 To, Vector4 Color);
public sealed record SceneOverviewPlot(int TemplatePlotNumber, PlotSize Size, ImmutableArray<Vector2> Corners);
public sealed record SceneNodeOverview(ImmutableArray<SceneOverviewPlot> Plots,
    ImmutableArray<ImmutableArray<Vector2>> Tracks, ImmutableArray<Vector2> Lair);

public interface ISceneFrame
{
    ScenePoint Origin { get; }
    ScenePanBounds? NodePanBounds { get; }
    SceneNodeOverview? NodeOverview { get; }
    SceneReadiness Readiness { get; }
    double SimulationSeconds { get; }
    SceneUpdate Update { get; }
    ImmutableArray<SceneProblem> Diagnostics { get; }
    ImmutableArray<SceneDebugLine> DebugLines { get; }
    ScenePick Pick(SceneRay ray);
    Lookup<ScenePose> FindPose(WorldObjectId subject);
    Lookup<WorldLocation> Locate(Vector3 point);
    Lookup<double> TerrainHeight(double x, double z);
}
