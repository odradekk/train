using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.ContractSupport;

/// <summary>只支持固定命中的解析及一次指定目标请求；不执行场景构建。</summary>
public sealed class FixedWorldRequestSession(WorldSceneTarget nextTarget) : IWorldSceneSession
{
    private SceneRequestId request = WorldRequestScenario.Hit.Frame.Request;
    private bool disposed;

    public SceneSessionId Id => WorldRequestScenario.Hit.Frame.Session;
    public SceneBinding Binding { get; } = new(Guid.Parse("00000000-0000-0000-0000-000000000303"));

    public SceneResult<SceneRequestId> RequestView(WorldSceneTarget target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (target != nextTarget || request != WorldRequestScenario.Hit.Frame.Request)
            throw new InvalidOperationException("UNCONFIGURED: 观察目标或请求顺序与情景不符。");
        request = new SceneRequestId(request.Value + 1);
        return new SceneResult<SceneRequestId>.Success(request);
    }

    public SceneResult<PickSubject> Resolve(SceneHit hit)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ReferenceEquals(hit, WorldRequestScenario.Hit))
            throw new InvalidOperationException("UNCONFIGURED: 场景命中未配置。");
        return hit.Frame.Request == request
            ? new SceneResult<PickSubject>.Success(hit.Subject)
            : new SceneResult<PickSubject>.Failure([
                new SceneProblem("stale_request", SceneStage.Pick, null, null, null, null, "旧观察请求")]);
    }

    public SceneResult<SimulationPosition> Observe(SceneWorldSnapshot snapshot,
        ImmutableArray<EventEnvelope> events) =>
        throw new InvalidOperationException("UNCONFIGURED: 场景观察未配置。");

    public SceneResult<ISceneFrame> Present(SceneFrameRequest frame, ISceneFrame? displayedFrame) =>
        throw new InvalidOperationException("UNCONFIGURED: 帧接入未配置。");

    public ValueTask DisposeAsync()
    {
        disposed = true;
        return ValueTask.CompletedTask;
    }
}
