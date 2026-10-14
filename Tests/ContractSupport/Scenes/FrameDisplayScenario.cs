using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.ContractSupport;

public sealed class FixedSceneFrame : ISceneFrame
{
    public required SceneUpdate Update { get; init; }
    public ScenePoint Origin { get; init; }
    public ScenePanBounds? NodePanBounds { get; init; }
    public SceneReadiness Readiness { get; init; } = SceneReadiness.Ready;
    public double SimulationSeconds { get; init; }
    public ImmutableArray<SceneProblem> Diagnostics { get; init; } = [];
    public ImmutableArray<SceneDebugLine> DebugLines { get; init; } = [];
    public ScenePick? PickResult { get; init; }
    public ImmutableDictionary<SceneRay, ScenePick> Picks { get; init; } = ImmutableDictionary<SceneRay, ScenePick>.Empty;
    public ImmutableDictionary<WorldObjectId, Lookup<ScenePose>> Poses { get; init; } = ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty;
    public ImmutableDictionary<Vector3, Lookup<WorldLocation>> Locations { get; init; } = ImmutableDictionary<Vector3, Lookup<WorldLocation>>.Empty;
    public ImmutableDictionary<(double X, double Z), Lookup<double>> Heights { get; init; } = ImmutableDictionary<(double, double), Lookup<double>>.Empty;
    public Lookup<double>? TerrainHeightResult { get; init; }
    public SceneRay? LastPickRay { get; private set; }

    public ScenePick Pick(SceneRay ray)
    {
        LastPickRay = ray;
        return Picks.TryGetValue(ray, out var result) ? result : PickResult ?? throw FrameDisplayScenario.Unconfigured("拾取射线");
    }

    public Lookup<ScenePose> FindPose(WorldObjectId subject) => Poses.TryGetValue(subject, out var value)
        ? value : throw FrameDisplayScenario.Unconfigured("对象姿态");
    public Lookup<WorldLocation> Locate(Vector3 point) => Locations.TryGetValue(point, out var value)
        ? value : throw FrameDisplayScenario.Unconfigured("位置映射");
    public Lookup<double> TerrainHeight(double x, double z) => Heights.TryGetValue((x, z), out var value)
        ? value : TerrainHeightResult ?? throw FrameDisplayScenario.Unconfigured("地形高度");
}

public sealed record DisplayPresentCall(SceneFrameRequest Request, ISceneFrame? DisplayedFrame);
public sealed record DisplayObserveCall(SceneWorldSnapshot Snapshot, ImmutableArray<EventEnvelope> Events);
public sealed record DisplayOpenCall(SceneBinding Binding, SceneWorldSnapshot Snapshot);
public sealed record DisplayPreviewCall(IContentView Content, DesignEvaluation Initial);

public sealed class FixedDisplaySession : IWorldSceneSession
{
    private int presentIndex;
    private int observeIndex;
    private readonly HashSet<WorldSceneTarget> requestedTargets = [];
    private bool disposed;
    public required SceneSessionId Id { get; init; }
    public required SceneBinding Binding { get; init; }
    public ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>> RequestResults { get; init; } = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty;
    public ImmutableArray<SceneResult<ISceneFrame>> PresentResults { get; init; } = [];
    public ImmutableArray<SceneResult<SimulationPosition>> ObserveResults { get; init; } = [];
    public ImmutableDictionary<SceneHit, SceneResult<PickSubject>> ResolveResults { get; init; } = ImmutableDictionary<SceneHit, SceneResult<PickSubject>>.Empty;
    public Task? DisposeGate { get; init; }
    public List<DisplayPresentCall> PresentCalls { get; } = [];
    public List<DisplayObserveCall> ObserveCalls { get; } = [];
    public List<WorldSceneTarget> RequestCalls { get; } = [];
    public List<SceneHit> ResolveCalls { get; } = [];
    public int DisposeCalls { get; private set; }
    public int? DisposeThreadId { get; private set; }
    public bool IsDisposed => disposed;

    public SceneResult<SceneRequestId> RequestView(WorldSceneTarget target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RequestCalls.Add(target);
        return RequestResults.TryGetValue(target, out var value) && requestedTargets.Add(target)
            ? value : throw FrameDisplayScenario.Unconfigured("观察目标");
    }

    public SceneResult<SimulationPosition> Observe(SceneWorldSnapshot snapshot, ImmutableArray<EventEnvelope> events)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ObserveCalls.Add(new(snapshot, events));
        return observeIndex < ObserveResults.Length ? ObserveResults[observeIndex++] : throw FrameDisplayScenario.Unconfigured("场景观察");
    }

    public SceneResult<ISceneFrame> Present(SceneFrameRequest request, ISceneFrame? displayedFrame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        PresentCalls.Add(new(request, displayedFrame));
        return presentIndex < PresentResults.Length ? PresentResults[presentIndex++] : throw FrameDisplayScenario.Unconfigured("显示帧");
    }

    public SceneResult<PickSubject> Resolve(SceneHit hit)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ResolveCalls.Add(hit);
        return ResolveResults.TryGetValue(hit, out var value) ? value : throw FrameDisplayScenario.Unconfigured("命中核对");
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        DisposeCalls++;
        DisposeThreadId = Environment.CurrentManagedThreadId;
        return DisposeGate is null ? ValueTask.CompletedTask : new ValueTask(DisposeGate);
    }
}

public sealed class FixedDisplayFactory : IScenesFactory
{
    private int openIndex;
    public ImmutableArray<SceneResult<IWorldSceneSession>> OpenWorldResults { get; init; } = [];
    public Func<SceneBinding, SceneWorldSnapshot, SceneResult<IWorldSceneSession>>? OpenWorldCall { get; init; }
    public List<DisplayOpenCall> OpenCalls { get; } = [];
    public SceneResult<IWorldSceneSession> OpenWorld(SceneBinding binding, SceneWorldSnapshot initial)
    {
        OpenCalls.Add(new(binding, initial));
        if (OpenWorldCall is not null) return OpenWorldCall(binding, initial);
        return openIndex < OpenWorldResults.Length ? OpenWorldResults[openIndex++] : throw FrameDisplayScenario.Unconfigured("打开世界");
    }

    public SceneResult<IDesignEditSession> OpenEditor(IContentView content, DesignEvaluation initial) => throw FrameDisplayScenario.Unconfigured("编辑会话");
    public Func<IContentView, DesignEvaluation, SceneResult<IDesignPreview>>? PreviewDesignCall { get; init; }
    public List<DisplayPreviewCall> PreviewCalls { get; } = [];
    public SceneResult<IDesignPreview> PreviewDesign(IContentView content, DesignEvaluation initial)
    {
        PreviewCalls.Add(new(content, initial));
        return PreviewDesignCall is not null ? PreviewDesignCall(content, initial) : throw FrameDisplayScenario.Unconfigured("设计预览");
    }
    public SceneResult<ITemplatePreview> PreviewTemplate(IContentView content, SettlementTemplateDraft draft, EditRevision revision) => throw FrameDisplayScenario.Unconfigured("模板预览");
}

public static class FrameDisplayScenario
{
    internal static InvalidOperationException Unconfigured(string operation) => new($"UNCONFIGURED: {operation}未配置。");
}
