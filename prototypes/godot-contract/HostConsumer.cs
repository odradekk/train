using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Godot.ContractSamples;

public sealed record HostComposition(ICoreFactory Core, IContentFactory Content,
    IScenesFactory Scenes, IContentFiles Files, IAssetInspector Assets);

public static class HostConsumer
{
    public static ContentResult<IContentCandidate> PrepareContent(
        HostComposition composition, ContentLoadRequest request)
        => composition.Content.PrepareLoad(request, composition.Files, composition.Assets);

    public static CoreResult<ICoreSessionCandidate> PrepareGame(HostComposition composition,
        WorldGenerationInput world, IContentView content, CoreSessionOptions options)
        => composition.Core.PrepareNew(world, content.Core, options);

    public static SceneResult<IWorldSceneSession> OpenScene(HostComposition composition,
        SceneBinding binding, ICoreReadView view, IContentView content)
        => composition.Scenes.OpenWorld(binding, new SceneWorldSnapshot(view, content));

    public static (WorldSummary Map, Lookup<TrainView> Panel, CoreResult<TradeQuote> Quote)
        ReadUi(ICoreSession session, TrainId train, TradeQuoteRequest quote)
    {
        var view = session.Read();
        return (view.World, view.FindTrain(train), view.QuoteTrade(quote));
    }

    public static SubmissionResult StopTrain(ICoreSession session, TrainId train,
        Action<ImmutableArray<EventEnvelope>> dispatch)
    {
        var result = session.Submit(new CoreCommand.StopTrain(train), CommandSource.UserInterface);
        if (result is SubmissionResult.Completed completed)
            dispatch(completed.Events);
        return result;
    }

    // 宿主每步接入通知暂停/降速，再决定是否继续；不预先批量推进跨过通知边界。
    public static AdvanceResult AdvanceOne(ICoreSession session,
        Action<ImmutableArray<EventEnvelope>> dispatch)
    {
        var result = session.Advance(1);
        dispatch(result.Events);
        return result;
    }
}

public interface IFrameDisplay
{
    bool TryInstall(ISceneFrame frame, IContentView content);
}

public sealed class WorldDisplayConsumer(IWorldSceneSession scene, IFrameDisplay display)
{
    private ISceneFrame? _displayed;
    private ScenePickRaySample? _pickRay;

    public SceneResult<SceneRequestId> RequestView(WorldSceneTarget target)
    {
        var result = scene.RequestView(target);
        if (result is SceneResult<SceneRequestId>.Success) _pickRay = null;
        return result;
    }

    public SceneResult<ISceneFrame> Present(IContentView content, double interpolation,
        Vector3 focus, SceneDebugLayer debug)
    {
        var camera = _displayed is null ? null : new SceneCameraSample(_displayed, focus);
        var result = scene.Present(new SceneFrameRequest(interpolation, camera, debug, _pickRay), _displayed);
        if (result is not SceneResult<ISceneFrame>.Success ready || ready.Value.Readiness == SceneReadiness.Failed)
        {
            _displayed = null;
            return result;
        }
        try
        {
            if (display.TryInstall(ready.Value, content))
            {
                if (_pickRay is { } sample && sample.Frame.Update.Frame.Space != ready.Value.Update.Frame.Space)
                    _pickRay = null;
                _displayed = ready.Value;
            }
        }
        catch
        {
            _displayed = null;
            throw;
        }
        return result;
    }

    public Lookup<ScenePose>? CameraPose(WorldObjectId subject) => _displayed?.FindPose(subject);

    public SceneResult<PickSubject>? Pick(SceneRay ray)
    {
        if (_displayed is null) return null;
        var picked = _displayed.Pick(ray);
        _pickRay = new ScenePickRaySample(_displayed, ray);
        return picked is ScenePick.Hit hit ? scene.Resolve(hit.Value) : null;
    }
}
