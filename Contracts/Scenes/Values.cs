// 场景身份、坐标和结果；不公开区块存储、任务队列或 Godot 对象。
using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Scenes.Contracts;

/// <summary>宿主为运行会话与有效内容绑定分配的身份；不是存档 UUID 或确定性状态。</summary>
public readonly record struct SceneBinding(Guid Value);
public readonly record struct SceneSessionId(Guid Value);
public readonly record struct SceneRequestId(ulong Value);
public readonly record struct SceneSpaceId(ulong Value);
public readonly record struct SceneObjectId(ulong Value);
public readonly record struct SceneEffectId(ulong Value);
public readonly record struct EditSessionId(Guid Value);
public readonly record struct EditElementId(ulong Value);

/// <summary>节点或线路场景内未减浮动原点的坐标，单位格；不等于宏观地图坐标。</summary>
public readonly record struct ScenePoint(double X, double Y, double Z);
/// <summary>位置单位格；单位四元数；父对象存在时相对父对象，否则相对本帧浮动原点。</summary>
public sealed record ScenePose(Vector3 Position, Quaternion Rotation);
public sealed record SceneBounds(Vector3 Minimum, Vector3 Maximum);
/// <summary>同一帧空间内的有限射线；Direction 为单位向量，最大距离为正数格。</summary>
public sealed record SceneRay(Vector3 Origin, Vector3 Direction, double MaximumDistanceCells);
/// <summary>帧身份仅在本次运行有效；Space 在节点、线路或编辑场景更换时改变。</summary>
public sealed record SceneFrameStamp(SceneSessionId Session, SceneRequestId Request,
    SceneSpaceId Space, ulong Revision);

public enum SceneStage { Input, Build, Reconcile, Present, Pick, Edit, Close }
/// <summary>稳定 Code 供分支；文字和底层异常由宿主记录，不参与核心状态。</summary>
public sealed record SceneProblem(string Code, SceneStage Stage, WorldObjectId? Subject,
    ContentReference? Content, PackageId? Package, DesignElementPath? Element, string Detail);
public abstract record SceneResult<T> where T : notnull
{
    private SceneResult() { }
    public sealed record Success(T Value) : SceneResult<T>;
    public sealed record Failure(ImmutableArray<SceneProblem> Problems) : SceneResult<T>;
    public sealed record Faulted(SceneProblem Problem) : SceneResult<T>;
}

/// <summary>内容格的六个轴向面；命中面使用被命中对象自身的格坐标。</summary>
public enum GridFace { NegativeX, PositiveX, NegativeY, PositiveY, NegativeZ, PositiveZ }
public abstract record PickSubject
{
    private PickSubject() { }
    public sealed record Terrain(WorldLocation Location) : PickSubject;
    public sealed record WorldObject(WorldObjectId Object, DesignHash? Design, PartId? Part) : PickSubject;
    public sealed record Editing(EditSessionId Session, EditRevision Revision, EditElementId Element) : PickSubject;
}
/// <summary>来自固定帧的历史命中值；不能直接当作当前世界命令的授权或编辑操作的有效目标。</summary>
public sealed record SceneHit(SceneFrameStamp Frame, SceneObjectId? Object, PickSubject Subject,
    GridPosition Cell, GridFace Face, Vector3 Point, Vector3 Normal,
    double DistanceCells, ImmutableArray<int> NestedObjectPath);
public abstract record ScenePick
{
    private ScenePick() { }
    public sealed record Hit(SceneHit Value) : ScenePick;
    public sealed record Miss : ScenePick;
    /// <summary>射线先进入尚未生成的区域，不能宣称后面的命中可见。</summary>
    public sealed record NotReady : ScenePick;
}
