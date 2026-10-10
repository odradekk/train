using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Godot.ContractSamples;

// 装配方传入真实工厂或替身；样例不引用实现项目，不创建第四个契约程序集。
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

    // 地图、详情和报价由同一不可变视图取得；提交仍由核心重新裁决。
    public static (WorldSummary Map, Lookup<TrainView> Panel, CoreResult<TradeQuote> Quote)
        ReadUi(ICoreSession session, TrainId train, TradeQuoteRequest quote)
    {
        var view = session.Read();
        return (view.World, view.FindTrain(train), view.QuoteTrade(quote));
    }

    public static SubmissionResult Submit(ICoreSession session, CoreCommand command,
        Action<ImmutableArray<EventEnvelope>> dispatch)
    {
        var result = session.Submit(command, CommandSource.UserInterface);
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

// 仅样例内宿主边界，无实现。成功必须表示资源已准备且整帧已在帧间接入；
// false 保持上一帧完整可用，不能在部分应用后返回 false。
public interface IFrameDisplay
{
    bool TryInstall(ISceneFrame frame, IContentView content);
}

public sealed class WorldDisplayConsumer(IWorldSceneSession scene, IFrameDisplay display)
{
    private ISceneFrame? _displayed;

    public SceneResult<ISceneFrame> Present(IContentView content, double interpolation,
        Vector3 focus, SceneDebugLayer debug)
    {
        var camera = _displayed is null ? null : new SceneCameraSample(_displayed, focus);
        var result = scene.Present(new SceneFrameRequest(interpolation, camera, debug), _displayed);
        if (result is not SceneResult<ISceneFrame>.Success ready || ready.Value.Readiness == SceneReadiness.Failed)
        {
            _displayed = null;
            return result;
        }
        try
        {
            if (display.TryInstall(ready.Value, content))
                _displayed = ready.Value;
        }
        catch
        {
            _displayed = null;
            throw;
        }
        // Failure/Faulted、Success 中的 Failed 帧或接入异常交回宿主：世界返回地图。
        return result;
    }

    public Lookup<ScenePose>? CameraPose(WorldObjectId subject) => _displayed?.FindPose(subject);

    // ray 属于实际显示帧空间；历史命中不能直接用于当前核心命令。
    public SceneResult<PickSubject>? Pick(SceneRay ray)
        => _displayed?.Pick(ray) is ScenePick.Hit hit ? scene.Resolve(hit.Value) : null;
}
