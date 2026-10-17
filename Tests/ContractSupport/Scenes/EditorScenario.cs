using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using NVector3 = System.Numerics.Vector3;

namespace Train.Scenes.ContractSupport;

public sealed class EditorScenes : IScenesFactory
{
    public Func<IContentView, DesignEvaluation, SceneResult<IDesignEditSession>>? OpenEditorCall { get; set; }
    public Action<EditorSession>? ConfigureSession { get; set; }
    public EditorSession? Session => Sessions.LastOrDefault();
    public List<EditorSession> Sessions { get; } = [];
    public SceneResult<IDesignEditSession> OpenEditor(IContentView content, DesignEvaluation initial)
    {
        if (OpenEditorCall is { } call) return call(content, initial);
        var draft = initial is DesignEvaluation.Complete complete ? complete.Definition
            : throw new InvalidOperationException("UNCONFIGURED: 初始评估。");
        var session = new EditorSession(content.Generation, draft, initial);
        ConfigureSession?.Invoke(session);
        Sessions.Add(session);
        return new SceneResult<IDesignEditSession>.Success(session);
    }
    public SceneResult<IWorldSceneSession> OpenWorld(SceneBinding binding, SceneWorldSnapshot initial) => throw Unconfigured();
    public SceneResult<IDesignPreview> PreviewDesign(IContentView content, DesignEvaluation initial) => throw Unconfigured();
    public SceneResult<ITemplatePreview> PreviewTemplate(IContentView content, SettlementTemplateDraft draft, EditRevision revision) => throw Unconfigured();
    private static InvalidOperationException Unconfigured() => new("UNCONFIGURED: 场景入口。");
}

// 有限情景替身不实现占格、合法性或撤销规则；未配置操作明确失败。
public sealed class EditorSession : IDesignEditSession
{
    private EditState _state;
    private readonly List<ISceneFrame?> _baselines = [];
    public IReadOnlyList<ISceneFrame?> Baselines => _baselines;
    public bool FailPresent { get; set; }
    public bool StalePick { get; set; }
    public bool ConfirmationRequired { get; set; }
    public Queue<GridPosition>? PickSamples { get; set; }
    public List<EditOperation> PreviewCalls { get; } = [];
    public List<(IEditPreview Preview, bool Confirmed)> ApplyCalls { get; } = [];
    private FixedEditPreview? _currentPreview;
    public FixedSceneFrame? LastFrame { get; private set; }
    public int Disposals { get; private set; }
    public EditSessionId Id { get; } = new(Guid.NewGuid());
    public ContentGeneration Content { get; }
    public EditorSession(ContentGeneration generation, DesignDraft draft, DesignEvaluation initial)
    {
        Content = generation;
        _state = new(new(new(Id, new(0), generation), draft), false, false, false, null, [], [], initial, []);
    }
    public Func<EditOperation, FixedEditPreview>? PreviewCall { get; set; }
    public Func<EditElementId, SceneResult<DesignDraft>>? CaptureObjectCall { get; set; }
    public Func<EditState>? ReadCall { get; set; }
    public Func<EditSnapshot>? CaptureCall { get; set; }
    public Func<SceneResult<EditState>>? UndoCall { get; set; }
    public Func<SceneResult<EditState>>? RedoCall { get; set; }
    public Func<EditViewOptions, ISceneFrame?, SceneResult<ISceneFrame>>? PresentCall { get; set; }
    public Func<EditStamp, DesignElementPath, SceneResult<EditElementId>>? LocateCall { get; set; }
    public List<EditSnapshot> Saved { get; } = [];
    public List<EditStamp> Evaluations { get; } = [];
    public void SetState(Func<EditState, EditState> update) => _state = update(_state);
    public void Change(DesignDraft draft, bool dirty = true) => _state = _state with
    {
        Snapshot = new(_state.Snapshot.Stamp with { Revision = new(_state.Snapshot.Stamp.Revision.Value + 1) }, draft),
        IsDirty = dirty, CurrentEvaluation = null
    };
    public EditState Read() => ReadCall is { } call ? call() : _state;
    public EditSnapshot Capture() => CaptureCall is { } call ? call() : _state.Snapshot;
    public SceneResult<ISceneFrame> Present(EditViewOptions options, ISceneFrame? displayedFrame)
    {
        _baselines.Add(displayedFrame);
        if (PresentCall is { } call) return call(options, displayedFrame);
        if (FailPresent) return new SceneResult<ISceneFrame>.Faulted(new("fixture_scene_failure", SceneStage.Present, null, null, null, null, "预览故障；保存草稿与手动重试仍可用。"));
        var stamp = new SceneFrameStamp(new(Id.Value), new(1), new(1), _state.Snapshot.Stamp.Revision.Value);
        var revision = StalePick ? new EditRevision(ulong.MaxValue) : _state.Snapshot.Stamp.Revision;
        LastFrame = new()
        {
            Update = new(null, stamp, NVector3.Zero, [], [], new([], []), []),
            TerrainHeightResult = new Lookup<double>.Missing(),
            PickResult = new ScenePick.Hit(new(stamp, null, new PickSubject.Editing(Id, revision, new(1)), new(0, 0, 0), GridFace.PositiveY, NVector3.Zero, NVector3.UnitY, 1, [])),
            DebugLines = _state.Snapshot.Stamp.Revision.Value == 0 ? [new(new(-3, 0, -3), new(_baselines.Count, 3, 3), new(1, 0.3f, 0.1f, 1))] : []
        };
        return new SceneResult<ISceneFrame>.Success(PickSamples is { } samples ? new SampledFrame(LastFrame, samples) : LastFrame);
    }
    public SceneResult<IEditPreview> Preview(EditOperation operation)
    {
        if (operation is not EditOperation.Properties && operation is not EditOperation.Cells) throw new InvalidOperationException("有限替身只配置Properties与Cells输入。");
        PreviewCalls.Add(operation);
        _currentPreview = PreviewCall?.Invoke(operation) ?? new(_state.Snapshot.Stamp, operation, ConfirmationRequired);
        return new SceneResult<IEditPreview>.Success(_currentPreview);
    }
    public SceneResult<EditState> Apply(IEditPreview preview, bool confirmed)
    {
        ApplyCalls.Add((preview, confirmed));
        if (!ReferenceEquals(preview, _currentPreview)) return Reject("stale_preview", "预览已过期，请重新预览。");
        if (preview.RequiresConfirmation && !confirmed) return Reject("confirmation_required", "该预览需要确认。");
        var draft = _currentPreview!.Operation is EditOperation.Properties properties ? _state.Snapshot.Draft with { Display = properties.Display } : _state.Snapshot.Draft;
        Change(draft);
        _currentPreview = null;
        return new SceneResult<EditState>.Success(_state);
    }
    public bool AcceptEvaluation(EditStamp requested, DesignEvaluation result)
    {
        Evaluations.Add(requested);
        if (requested != _state.Snapshot.Stamp) return false;
        _state = _state with { CurrentEvaluation = result };
        return true;
    }
    public bool MarkSaved(EditSnapshot saved)
    {
        Saved.Add(saved);
        if (saved.Stamp != _state.Snapshot.Stamp) return false;
        _state = _state with { IsDirty = false };
        return true;
    }
    public SceneResult<EditState> Select(EditSelection selection)
    {
        _state = _state with { Selection = ((EditSelection.Elements)selection).Ids };
        return new SceneResult<EditState>.Success(_state);
    }
    public SceneResult<EditState> Enter(EditElementId? element)
    {
        if (element == new EditElementId(20)) return Reject("edit_locked", "缺失定义锁定，不能进入设备。");
        if (element == new EditElementId(30)) return Reject("functional_composite_closed", "功能复合体是封闭引用，不能进入。");
        if (element is not null && element != new EditElementId(10)) throw new InvalidOperationException("有限替身只配置设备10、锁定项20与功能复合体30。");
        _state = _state with { EnteredObject = element, Selection = [] };
        return new SceneResult<EditState>.Success(_state);
    }
    public SceneResult<DesignDraft> CaptureObject(EditElementId element) => CaptureObjectCall?.Invoke(element) ?? throw new InvalidOperationException("UNCONFIGURED: 对象导出。");
    public SceneResult<EditState> Undo() => UndoCall?.Invoke() ?? throw new InvalidOperationException("UNCONFIGURED: 撤销。");
    public SceneResult<EditState> Redo() => RedoCall?.Invoke() ?? throw new InvalidOperationException("UNCONFIGURED: 重做。");
    public SceneResult<EditElementId> Locate(EditStamp stamp, DesignElementPath path) => LocateCall?.Invoke(stamp, path) ?? throw new InvalidOperationException("UNCONFIGURED: 诊断定位。");
    public void Dispose() => Disposals++;
    private static SceneResult<EditState> Reject(string code, string detail) => new SceneResult<EditState>.Failure([new(code, SceneStage.Edit, null, null, null, null, detail)]);
    private sealed class SampledFrame(FixedSceneFrame frame, Queue<GridPosition> samples) : ISceneFrame
    {
        public ScenePoint Origin => frame.Origin;
        public ScenePanBounds? NodePanBounds => frame.NodePanBounds;
        public SceneReadiness Readiness => frame.Readiness;
        public double SimulationSeconds => frame.SimulationSeconds;
        public SceneUpdate Update => frame.Update;
        public ImmutableArray<SceneProblem> Diagnostics => frame.Diagnostics;
        public ImmutableArray<SceneDebugLine> DebugLines => frame.DebugLines;
        public ScenePick Pick(SceneRay ray)
        {
            var hit = ((ScenePick.Hit)frame.Pick(ray)).Value;
            return new ScenePick.Hit(hit with { Cell = samples.Dequeue() });
        }
        public Lookup<ScenePose> FindPose(WorldObjectId subject) => frame.FindPose(subject);
        public Lookup<WorldLocation> Locate(NVector3 point) => frame.Locate(point);
        public Lookup<double> TerrainHeight(double x, double z) => frame.TerrainHeight(x, z);
    }

}

public sealed record FixedEditPreview(EditStamp Basis, EditOperation Operation, bool RequiresConfirmation = false) : IEditPreview
{
    public int CoveredCells { get; init; } = RequiresConfirmation ? 513 : 1;
    public int AffectedCells { get; init; } = RequiresConfirmation ? 513 : 1;
    public int SkippedCells { get; init; }
    public bool OccupancyComplete { get; init; } = true;
    public bool CanApply { get; init; } = true;
    public ImmutableArray<EditConflict> Conflicts { get; init; } = [];
    public ImmutableArray<GridPosition> HighlightCells { get; init; } = [];
}
