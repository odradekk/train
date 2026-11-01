using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.ContractSamples;

public sealed class StreamingConsumer : ISceneStreamingObserver
{
    private readonly ConcurrentQueue<(long Timestamp, SceneStreamingEvent Event)> events = new();

    public void OnEvent(SceneStreamingEvent value)
    {
        var timestamp = Stopwatch.GetTimestamp();
        events.Enqueue((timestamp, value));
    }

    public void Drain(Action<long, SceneStreamingEvent> consume)
    {
        while (events.TryDequeue(out var value))
            consume(value.Timestamp, value.Event);
    }

    public static ImmutableArray<SceneStreamingPartId>? FindTerrainParts(
        SceneStreamingEvent.Frame streamed, ISceneFrame displayedFrame, double x, double z)
    {
        if (streamed.Stamp != displayedFrame.Update.Frame)
            throw new ArgumentException("覆盖查询必须对应实际提交帧。", nameof(streamed));
        return streamed.Coverage?.FindTerrainParts(x, z);
    }
}

public interface ISceneDisplay
{
    bool TryDisplay(ISceneFrame frame);
}

public interface IContentCommit
{
    bool TryCommit(IContentCandidate candidate);
}

public static class DesignIconConsumer
{
    public static void Present(IScenesFactory factory, IContentView content,
        GoodId good, ISceneDisplay display, Action<object> report)
    {
        var presentation = content.FindGood(good);
        if (presentation is not ContentLookup<GoodPresentation>.Found { Value.DisplayDesign: { } hash })
        {
            report(presentation);
            return;
        }
        var design = content.FindDesign(hash);
        if (design is not ContentLookup<DesignEvaluation>.Found found)
        {
            report(design);
            return;
        }
        var opened = factory.PreviewDesign(content, found.Value);
        if (opened is not SceneResult<IDesignPreview>.Success success)
        {
            report(opened);
            return;
        }
        using var preview = success.Value;
        var presented = preview.Present();
        report(presented);
        if (presented is SceneResult<ISceneFrame>.Success frame
            && frame.Value.Readiness != SceneReadiness.Failed)
            display.TryDisplay(frame.Value);
    }
}

public sealed class TemplateConsumer(IScenesFactory factory, ISceneDisplay display)
{
    private ITemplatePreview? displayedPreview;

    public SceneResult<ITemplatePreview> Present(IContentView content,
        SettlementTemplateDraft draft, EditRevision revision)
    {
        var result = factory.PreviewTemplate(content, draft, revision);
        if (result is SceneResult<ITemplatePreview>.Success success
            && success.Value.Frame.Readiness != SceneReadiness.Failed && display.TryDisplay(success.Value.Frame))
            displayedPreview = success.Value;
        return result;
    }

    public Lookup<int>? PickCurrent(ContentGeneration content, EditRevision revision, SceneRay ray)
        => displayedPreview is { } preview && preview.Content == content && preview.Revision == revision
            ? preview.PickPlot(ray) : null;
}

public sealed class WorldConsumer(IWorldSceneSession session, ISceneDisplay display) : IAsyncDisposable
{
    private ISceneFrame? displayedFrame;
    private SceneFrameStamp? preparedStamp;
    private ScenePickRaySample? pickRay;

    public static SceneResult<IWorldSceneSession> Open(IScenesFactory factory,
        SceneBinding binding, ICoreReadView core, IContentView content)
        => factory.OpenWorld(binding, new SceneWorldSnapshot(core, content));

    public SceneResult<SceneRequestId> RequestView(WorldSceneTarget target)
    {
        var result = session.RequestView(target);
        if (result is SceneResult<SceneRequestId>.Success) pickRay = null;
        return result;
    }

    public Vector3 ConstrainNodeFocus(Vector3 currentFocus, Vector3 requestedFocus)
    {
        if (displayedFrame?.NodePanBounds is not { } bounds)
            return currentFocus;
        return new Vector3(Math.Clamp(requestedFocus.X, bounds.Minimum.X, bounds.Maximum.X),
            requestedFocus.Y, Math.Clamp(requestedFocus.Z, bounds.Minimum.Y, bounds.Maximum.Y));
    }

    public void ReadNodeOverview(Action<ScenePoint, ScenePanBounds, SceneNodeOverview> consume)
    {
        if (displayedFrame is { NodePanBounds: { } bounds, NodeOverview: { } overview } frame)
            consume(frame.Origin, bounds, overview);
    }

    public SceneResult<ISceneFrame> Update(SceneWorldSnapshot snapshot,
        ImmutableArray<EventEnvelope> newEvents, double interpolation,
        Vector3 cameraFocus, SceneDebugLayer debugLayers)
    {
        var observed = session.Observe(snapshot, newEvents);
        if (observed is SceneResult<SimulationPosition>.Failure failure)
            return new SceneResult<ISceneFrame>.Failure(failure.Problems);
        if (observed is SceneResult<SimulationPosition>.Faulted faulted)
            return new SceneResult<ISceneFrame>.Faulted(faulted.Problem);
        var camera = displayedFrame is null ? null : new SceneCameraSample(displayedFrame, cameraFocus, 6656);
        var request = new SceneFrameRequest(interpolation, camera, debugLayers, pickRay);
        var presented = session.Present(request, displayedFrame);
        if (presented is SceneResult<ISceneFrame>.Success candidate
            && candidate.Value.Readiness != SceneReadiness.Failed
            && candidate.Value.Update.Base is { } basis
            && basis != displayedFrame?.Update.Frame)
            presented = session.Present(request, null);
        if (presented is SceneResult<ISceneFrame>.Success success
            && success.Value.Readiness != SceneReadiness.Failed
            && (success.Value.Update.Base is null
                || success.Value.Update.Base == displayedFrame?.Update.Frame)
            && display.TryDisplay(success.Value))
        {
            if (pickRay is { } sample && sample.Frame.Update.Frame.Space != success.Value.Update.Frame.Space) pickRay = null;
            displayedFrame = success.Value;
        }
        return presented;
    }

    public SceneResult<SceneUpdate?> PrepareCandidate(Action<SceneVisual> prepareVisual)
    {
        var result = session.PresentCandidate(preparedStamp);
        if (result is SceneResult<SceneUpdate?>.Success { Value.Base: { } basis } && basis != preparedStamp)
            result = session.PresentCandidate(null);
        if (result is not SceneResult<SceneUpdate?>.Success success)
        {
            preparedStamp = null;
            return result;
        }
        if (success.Value is not { } update)
        {
            preparedStamp = null;
            return result;
        }
        // 只准备候选视觉；回执不替代已接入的 displayedFrame，也不调用显示入口。
        foreach (var item in update.Upserts)
        foreach (var visual in item.Visuals) prepareVisual(visual);
        preparedStamp = update.Frame;
        return result;
    }

    public SceneResult<PickSubject>? PickCurrent(SceneRay ray, Action<ScenePick> showPick)
    {
        if (displayedFrame is null)
            return null;
        var picked = displayedFrame.Pick(ray);
        pickRay = new ScenePickRaySample(displayedFrame, ray);
        showPick(picked);
        return picked is ScenePick.Hit hit ? session.Resolve(hit.Value) : null;
    }

    public ValueTask DisposeAsync() => session.DisposeAsync();
}

public sealed class EditorConsumer(IDesignEditSession session, ISceneDisplay display) : IDisposable
{
    private ISceneFrame? displayedFrame;

    public void ReadRotationMetadata(Action<EditStamp, ImmutableArray<RotationSweepProjection>?> consume)
    {
        var state = session.Read();
        var scene = state.CurrentEvaluation switch
        {
            DesignEvaluation.Complete complete => complete.Scene,
            DesignEvaluation.Partial partial => partial.Scene,
            _ => null
        };
        consume(state.Snapshot.Stamp, scene?.RotationSweeps);
    }

    public static SceneResult<IDesignEditSession> Open(IScenesFactory factory,
        IContentView content, DesignEvaluation initial) => factory.OpenEditor(content, initial);

    public SceneResult<IEditPreview> Preview(EditOperation operation) => session.Preview(operation);

    public SceneResult<EditState> Edit(EditOperation operation, Action<object> report)
    {
        var result = session.Apply(operation);
        report(result);
        return result;
    }

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

    public SceneResult<DesignDraft> CaptureObject(EditElementId element) => session.CaptureObject(element);

    public EditSnapshot CaptureAndClose()
    {
        var snapshot = session.Capture();
        session.Dispose();
        return snapshot;
    }

    public SceneResult<ISceneFrame> Present(EditViewOptions options)
    {
        var presented = session.Present(options, displayedFrame);
        if (presented is SceneResult<ISceneFrame>.Success success
            && success.Value.Readiness != SceneReadiness.Failed && display.TryDisplay(success.Value))
            displayedFrame = success.Value;
        return presented;
    }

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
