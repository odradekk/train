// 纯数据渲染交接；所有数组和元素均为不可变值，不借用可被生产方复用的缓冲。
using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Scenes.Contracts;

[Flags]
public enum ExposedFaceEdges { None = 0, MinimumU = 1, MaximumU = 2, MinimumV = 4, MaximumV = 8 }
/// <summary>位置为对象局部格；FaceUv 为所属格面 [0,1] 坐标，供外露边磨损；损伤和相对高度为 [0,1]。</summary>
public sealed record SceneVertex(Vector3 Position, Vector3 Normal, Vector2 TextureUv,
    Vector2 FaceUv, ExposedFaceEdges ExposedEdges, float RelativeHeight, float Damage);
public enum BuiltInMaterial { RailSteel, Sleeper, Water, Construction }
public abstract record SceneMaterial
{
    private SceneMaterial() { }
    public sealed record Voxel(AssetRevision Texture, MaterialCategory Category, PaintColor? Paint) : SceneMaterial;
    public sealed record BuiltIn(BuiltInMaterial Kind) : SceneMaterial;
}
/// <summary>三角形索引为从外侧看逆时针，叉积方向与法线一致；不含像素或导入模型的几何。</summary>
public sealed record SceneMesh(SceneMaterial Material, ImmutableArray<SceneVertex> Vertices,
    ImmutableArray<int> TriangleIndices);
/// <summary>纯表现状态；随机种子由体素按对象身份派生，WearEnabled=false 不叠加磨损。</summary>
public sealed record SurfaceState(bool WearEnabled, ulong WearSeed, float Damage, bool Scorched);
public abstract record SceneVisual
{
    private SceneVisual() { }
    public sealed record Geometry(SceneMesh Mesh, SurfaceState Surface) : SceneVisual;
    /// <summary>
    /// 引用原始 GLB，材质次序沿用内容元数据；模型解码与资源由 Godot 管理。
    /// RelativeHeightPlane 点乘模型局部格坐标 (x,y,z,1) 得到所在对象中的相对高度，供下部积灰。
    /// </summary>
    public sealed record Model(AssetRevision Asset, ImmutableArray<MaterialCategory> Materials,
        PaintColor? Paint, SurfaceState Surface, Vector4 RelativeHeightPlane) : SceneVisual;
    /// <summary>轮对模型由 Godot 提供，位置、轴向、尺寸和转角由体素提供；尺寸单位格。</summary>
    public sealed record Wheelset(double GaugeCells, double RadiusCells,
        double WidthCells, double RotationRadians) : SceneVisual;
    /// <summary>KnownCell=true 为缺失单格；false 为未知占格的放置点标记，不进入逐格拾取。</summary>
    public sealed record Missing(ContentReference Reference, bool KnownCell) : SceneVisual;
}
/// <summary>颜色和范围来自内容；损伤和随机键供 Godot 按表现时间生成受限的闪烁。</summary>
public sealed record SceneLight(PaintColor Color, double RangeCells, float Damage, ulong Seed);
public enum PersistentEffectKind { Smoke, Fire, Sparks, Leak, FacilitySmoke }
public sealed record PersistentSceneEffect(PersistentEffectKind Kind, Vector3 LocalPosition,
    float Strength, ulong Seed);
/// <summary>对象身份在本次会话中不复用；子对象变换相对 Parent，移动不更换 Geometry 的不可变缓冲。</summary>
public sealed record SceneObject(SceneObjectId Id, SceneObjectId? Parent, ScenePose Pose,
    WorldObjectId? Subject, ImmutableArray<SceneVisual> Visuals,
    ImmutableArray<SceneLight> Lights, ImmutableArray<PersistentSceneEffect> Effects);

public enum SceneEffectKind { Shot, Impact, Fragments, Destruction, RailwayDamage }
/// <summary>只播放实际收到且发生于已观察场景的事件；稳定 Id 防止资源准备重试重复播放。</summary>
public sealed record SceneEffectCue(SceneEffectId Id, SceneEffectKind Kind,
    Vector3 From, Vector3? To, PaintColor? Color, VoxelCollision? FragmentCollision,
    WorldObjectId? Subject, double StartedAtSimulationSeconds);
/// <summary>供碎片近似碰撞的高度场，行优先 X 最快；高度单位格，不是一格一个碰撞体。</summary>
public sealed record TerrainCollisionPatch(SceneObjectId Id, Vector3 Origin,
    int Width, int Depth, double SpacingCells, ImmutableArray<float> Heights);
public sealed record SceneEnvironment(ImmutableArray<SceneBounds> FoggedRegions,
    ImmutableArray<TerrainCollisionPatch> TerrainCollision);
/// <summary>一次完整显示更新；Base 不匹配时不得逐项尝试应用，须请求完整替换。</summary>
public sealed record SceneUpdate(SceneFrameStamp? Base, SceneFrameStamp Frame,
    bool ReplaceAll, Vector3 ShiftExistingRoots, ImmutableArray<SceneObject> Upserts,
    ImmutableArray<SceneObjectId> Removed, SceneEnvironment Environment,
    ImmutableArray<SceneEffectCue> Effects);
public enum SceneReadiness { Loading, Partial, Ready, Failed }
[Flags]
public enum SceneDebugLayer { None = 0, Chunks = 1, RailwaySegments = 2, Origin = 4, PickRay = 8, Centerline = 16 }
public sealed record SceneDebugLine(Vector3 From, Vector3 To, Vector4 Color);

/// <summary>不可变显示帧；会话关闭后仍可保留作为历史值，不使其重新获得当前交互资格。</summary>
public interface ISceneFrame
{
    SceneFrameStamp Stamp { get; }
    ScenePoint Origin { get; }
    SceneReadiness Readiness { get; }
    double SimulationSeconds { get; }
    SceneUpdate Update { get; }
    ImmutableArray<SceneProblem> Diagnostics { get; }
    ImmutableArray<SceneDebugLine> DebugLines { get; }
    /// <summary>射线、命中点和法线均使用本帧的浮动原点；在对象格网中按完整格拾取。</summary>
    ScenePick Pick(SceneRay ray);
    /// <summary>相机与渲染共享本帧合成后的根空间姿态（已减浮动原点）；未显示对象时为 Missing。</summary>
    Lookup<ScenePose> FindPose(WorldObjectId subject);
    /// <summary>返回当前点对应的业务地点；编辑场景或尚无几何映射时为 Missing。</summary>
    Lookup<WorldLocation> Locate(Vector3 point);
    /// <summary>相机防穿地形使用同一帧；尚未生成的列不伪造高度。</summary>
    Lookup<double> TerrainHeight(double x, double z);
}
