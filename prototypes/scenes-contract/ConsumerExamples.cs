// 仅验证契约消费可以编译；没有体素实现、Godot 实现或行为替身。
using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.ContractSamples;

/// <summary>Godot 适配边界。只有完整更新已经显示才返回 true；失败保留上一完整显示帧。</summary>
public interface ISceneDisplay
{
    bool TryDisplay(ISceneFrame frame);
}

/// <summary>宿主复核基准、确认缺失并完成目标所需写入后激活候选；失败不得发布候选。</summary>
public interface IContentCommit
{
    bool TryCommit(IContentCandidate candidate);
}

public sealed class TemplateConsumer(IScenesFactory factory, ISceneDisplay display)
{
    private ITemplatePreview? displayedPreview;

    public SceneResult<ITemplatePreview> Present(IContentView content,
        SettlementTemplateDraft draft, EditRevision revision)
    {
        var result = factory.PreviewTemplate(content, draft, revision);
        if (result is SceneResult<ITemplatePreview>.Success success && display.TryDisplay(success.Value.Frame))
            displayedPreview = success.Value;
        return result;
    }

    /// <summary>只把实际显示且仍与表单修订相符的下标用于当前表单；不按地块编号猜索引。</summary>
    public Lookup<int>? PickCurrent(ContentGeneration content, EditRevision revision, SceneRay ray)
        => displayedPreview is { } preview && preview.Content == content && preview.Revision == revision
            ? preview.PickPlot(ray) : null;
}

public sealed class WorldConsumer(IWorldSceneSession session, ISceneDisplay display) : IAsyncDisposable
{
    private ISceneFrame? displayedFrame;

    /// <summary>工厂收到完整固定边界；后续输入也必须来自同一运行绑定。</summary>
    public static SceneResult<IWorldSceneSession> Open(IScenesFactory factory,
        SceneBinding binding, ICoreReadView core, IContentView content)
        => factory.OpenWorld(binding, new SceneWorldSnapshot(core, content));

    public SceneResult<SceneRequestId> RequestView(WorldSceneTarget target)
        => session.RequestView(target);

    /// <summary>新事件仅交一次；相机焦点来自当前实际显示帧的空间。</summary>
    public SceneResult<ISceneFrame> Update(SceneWorldSnapshot snapshot,
        ImmutableArray<EventEnvelope> newEvents, double interpolation,
        Vector3 cameraFocus, SceneDebugLayer debugLayers)
    {
        var observed = session.Observe(snapshot, newEvents);
        if (observed is SceneResult<SimulationPosition>.Failure failure)
            return new SceneResult<ISceneFrame>.Failure(failure.Problems);
        if (observed is SceneResult<SimulationPosition>.Faulted faulted)
            return new SceneResult<ISceneFrame>.Faulted(faulted.Problem);
        var camera = displayedFrame is null ? null : new SceneCameraSample(displayedFrame, cameraFocus);
        var presented = session.Present(new SceneFrameRequest(interpolation, camera, debugLayers), displayedFrame);
        if (presented is SceneResult<ISceneFrame>.Success success && display.TryDisplay(success.Value))
            displayedFrame = success.Value;
        return presented;
    }

    /// <summary>仅在实际显示帧拾取；Resolve 再核对当前观察请求、对象及设计部件。</summary>
    public SceneResult<PickSubject>? PickCurrent(SceneRay ray, Action<ScenePick> showPick)
    {
        if (displayedFrame is null)
            return null;
        var picked = displayedFrame.Pick(ray);
        showPick(picked);
        return picked is ScenePick.Hit hit ? session.Resolve(hit.Value) : null;
    }

    public ValueTask DisposeAsync() => session.DisposeAsync();
}

public sealed class EditorConsumer(IDesignEditSession session, ISceneDisplay display) : IDisposable
{
    private ISceneFrame? displayedFrame;

    public static SceneResult<IDesignEditSession> Open(IScenesFactory factory,
        IContentView content, DesignEvaluation initial) => factory.OpenEditor(content, initial);

    /// <summary>用户确认的是这份固定预览；期间草稿改变时由 Apply 拒绝过期预览。</summary>
    public SceneResult<EditState>? Edit(EditOperation operation,
        Func<IEditPreview, bool> confirm, Action<object> report)
    {
        var prepared = session.Preview(operation);
        if (prepared is not SceneResult<IEditPreview>.Success success)
        {
            report(prepared);
            return null;
        }
        var preview = success.Value;
        report(preview);
        if (!preview.CanApply)
            return null;
        var confirmed = preview.RequiresConfirmation && confirm(preview);
        if (preview.RequiresConfirmation && !confirmed)
            return null;
        return session.Apply(preview, confirmed);
    }

    /// <summary>计算固定快照；若期间发生修改，迟到结果不能覆盖当前诊断。</summary>
    public bool Evaluate(IContentFactory factory, IContentView content, Action<object> report)
    {
        var snapshot = session.Capture();
        if (content.Generation != snapshot.Stamp.Content)
            throw new ArgumentException("校验必须使用编辑会话固定的内容世代。", nameof(content));
        var evaluated = factory.EvaluateDesign(content, snapshot.Draft);
        report(evaluated);
        return evaluated is ContentResult<DesignEvaluation>.Success success
            && session.AcceptEvaluation(snapshot.Stamp, success.Value);
    }

    /// <summary>保存草稿允许保持会话打开；提交成功才标记捕获修订，后续修改仍保留脏状态。</summary>
    public bool SaveDraft(IContentFactory factory, IContentView content, DraftId id,
        DraftTarget target, IContentCommit commit, Action<object> report)
    {
        var snapshot = session.Capture();
        var draft = new SavedContentDraft(id, snapshot.Stamp.Revision, target,
            new DraftPayload.Design(snapshot.Draft));
        var prepared = factory.PrepareEdit(content, new ContentEdit.SaveLocalDraft(draft));
        if (prepared is not ContentResult<IContentCandidate>.Success success)
        {
            report(prepared);
            return false;
        }
        using var candidate = success.Value;
        report(candidate.Report);
        return commit.TryCommit(candidate) && session.MarkSaved(snapshot);
    }

    /// <summary>另存嵌套对象由体素提取；宿主不递归遍历或改写布局。</summary>
    public SceneResult<DesignDraft> CaptureObject(EditElementId element) => session.CaptureObject(element);

    /// <summary>发布有效蓝图会改变固定内容；先结束旧编辑会话，再由宿主准备和提交目标。</summary>
    public EditSnapshot CaptureAndClose()
    {
        var snapshot = session.Capture();
        session.Dispose();
        return snapshot;
    }

    public SceneResult<ISceneFrame> Present(EditViewOptions options)
    {
        var presented = session.Present(options, displayedFrame);
        if (presented is SceneResult<ISceneFrame>.Success success && display.TryDisplay(success.Value))
            displayedFrame = success.Value;
        return presented;
    }

    /// <summary>编辑命中必须重新经会话核对；选区入口负责拒绝已删除身份。</summary>
    public SceneResult<EditState>? SelectCurrent(SceneRay ray, Action<ScenePick> showPick)
    {
        if (displayedFrame is null)
            return null;
        var picked = displayedFrame.Pick(ray);
        showPick(picked);
        if (picked is not ScenePick.Hit { Value.Subject: PickSubject.Editing subject })
            return null;
        var current = session.Read().Snapshot.Stamp;
        if (subject.Session != current.Session || subject.Revision != current.Revision)
            return null;
        return session.Select(new EditSelection.Elements([subject.Element]));
    }

    public void Dispose() => session.Dispose();
}
