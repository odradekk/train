using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Scenes.Contracts;

public interface IScenesFactory
{
    SceneResult<IWorldSceneSession> OpenWorld(SceneBinding binding, SceneWorldSnapshot initial);
    SceneResult<IDesignEditSession> OpenEditor(IContentView content, DesignEvaluation initial);
    SceneResult<IDesignPreview> PreviewDesign(IContentView content, DesignEvaluation initial);
    SceneResult<ITemplatePreview> PreviewTemplate(IContentView content,
        SettlementTemplateDraft draft, EditRevision revision);
}
public sealed record SceneWorldSnapshot(ICoreReadView Core, IContentView Content);
public abstract record WorldSceneTarget
{
    private WorldSceneTarget() { }
    public sealed record Location(WorldLocation Value) : WorldSceneTarget;
    public sealed record Follow(TrainId Train, VehicleId Vehicle) : WorldSceneTarget;
    public sealed record Combat(CombatId Id) : WorldSceneTarget;
}
public sealed record SceneCameraSample(ISceneFrame Frame, Vector3 Focus);
public sealed record ScenePickRaySample(ISceneFrame Frame, SceneRay Ray);
public sealed record SceneFrameRequest(double Interpolation, SceneCameraSample? Camera,
    SceneDebugLayer DebugLayers, ScenePickRaySample? PickRay = null);
public interface IWorldSceneSession : IAsyncDisposable
{
    SceneSessionId Id { get; }
    SceneBinding Binding { get; }
    SceneResult<SceneRequestId> RequestView(WorldSceneTarget target);
    SceneResult<SimulationPosition> Observe(SceneWorldSnapshot snapshot, ImmutableArray<EventEnvelope> events);
    SceneResult<ISceneFrame> Present(SceneFrameRequest request, ISceneFrame? displayedFrame);
    SceneResult<SceneUpdate?> PresentCandidate(SceneFrameStamp? prepared);
    SceneResult<PickSubject> Resolve(SceneHit hit);
    new ValueTask DisposeAsync();
}
