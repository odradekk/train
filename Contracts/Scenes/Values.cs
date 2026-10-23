using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Scenes.Contracts;

public readonly record struct SceneBinding(Guid Value);
public readonly record struct SceneSessionId(Guid Value);
public readonly record struct SceneRequestId(ulong Value);
public readonly record struct SceneSpaceId(ulong Value);
public readonly record struct SceneObjectId(ulong Value);
public readonly record struct SceneEffectId(ulong Value);
public readonly record struct EditSessionId(Guid Value);
public readonly record struct EditElementId(ulong Value);

public readonly record struct ScenePoint(double X, double Y, double Z);
public sealed record ScenePose(Vector3 Position, Quaternion Rotation);
public sealed record SceneBounds(Vector3 Minimum, Vector3 Maximum);
public sealed record ScenePanBounds(Vector2 Minimum, Vector2 Maximum);
public sealed record SceneRay(Vector3 Origin, Vector3 Direction, double MaximumDistanceCells);
public sealed record SceneFrameStamp(SceneSessionId Session, SceneRequestId Request,
    SceneSpaceId Space, ulong Revision);

public enum SceneStage { Input, Build, Reconcile, Present, Pick, Edit, Close }
public sealed record SceneProblem(string Code, SceneStage Stage, WorldObjectId? Subject,
    ContentReference? Content, PackageId? Package, DesignElementPath? Element, string Detail);
public abstract record SceneResult<T>
{
    private SceneResult() { }
    public sealed record Success(T Value) : SceneResult<T>;
    public sealed record Failure(ImmutableArray<SceneProblem> Problems) : SceneResult<T>;
    public sealed record Faulted(SceneProblem Problem) : SceneResult<T>;
}

public enum GridFace { NegativeX, PositiveX, NegativeY, PositiveY, NegativeZ, PositiveZ }
public abstract record PickSubject
{
    private PickSubject() { }
    public sealed record Terrain(WorldLocation Location) : PickSubject;
    public sealed record WorldObject(WorldObjectId Object, DesignHash? Design, PartId? Part) : PickSubject;
    public sealed record Editing(EditSessionId Session, EditRevision Revision, EditElementId Element) : PickSubject;
}
public sealed record SceneHit(SceneFrameStamp Frame, SceneObjectId? Object, PickSubject Subject,
    GridPosition Cell, GridFace Face, Vector3 Point, Vector3 Normal,
    double DistanceCells, ImmutableArray<int> NestedObjectPath);
public abstract record ScenePick
{
    private ScenePick() { }
    public sealed record Hit(SceneHit Value) : ScenePick;
    public sealed record Miss : ScenePick;
    public sealed record NotReady : ScenePick;
}
