using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Scenes.Contracts;

public readonly record struct SceneStreamingSceneId(SceneSessionId Session, ulong Value);
public readonly record struct SceneStreamingPartId(SceneStreamingSceneId Scene, SceneStreamingPartKind Kind, string Key);
public readonly record struct SceneStreamingAttemptId(SceneStreamingPartId Part, ulong Value);

public enum SceneStreamingRole { Active, Candidate, Successor }
public enum SceneStreamingPartKind { RailwaySegment, NodePreparation, NodeTerrain, NodeRail, NodeBoundary }
public enum SceneStreamingStage { Needed, DemandEnded, Queued, Started, Built, CacheHit, Admitted, Cancelled, Failed }

public interface ISceneStreamingObserver
{
    void OnEvent(SceneStreamingEvent value);
}

public interface ISceneStreamingCoverage
{
    ImmutableArray<SceneStreamingPartId> FindTerrainParts(double x, double z);
}

public abstract record SceneStreamingEvent
{
    private SceneStreamingEvent() { }
    public sealed record Opened(SceneStreamingSceneId Scene, WorldLocation Location, SceneStreamingRole Role) : SceneStreamingEvent;
    public sealed record Activated(SceneStreamingSceneId Scene) : SceneStreamingEvent;
    public sealed record Changed(SceneStreamingAttemptId Attempt, SceneStreamingStage Stage, ImmutableArray<SceneObjectId> Objects) : SceneStreamingEvent;
    public sealed record Frame(SceneStreamingSceneId Scene, SceneFrameStamp Stamp, ImmutableArray<SceneStreamingAttemptId> Parts, ISceneStreamingCoverage? Coverage) : SceneStreamingEvent;
    public sealed record Closed(SceneStreamingSceneId Scene) : SceneStreamingEvent;
}
