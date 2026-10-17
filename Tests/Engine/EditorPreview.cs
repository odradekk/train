using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.App.Editing;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using NVector3 = System.Numerics.Vector3;

namespace Train.Engine.Tests;

// 有限契约替身只提供界面与适配层所需资料，不实现占格或设计合法性规则。
public partial class EditorPreview : Node
{
    internal Train.App.App App { get; private set; } = null!;
    internal EditorPreviewFactory Factory { get; } = new();
    internal EditorSceneController Controller => App.FindChildren("DesignEditor", "", true, false).OfType<DesignEditorPanel>().Single().Scene;
    internal EditorService Editor => App.Editor!;
    private bool _opened;
    public static void Start(Node root) => root.AddChild(new EditorPreview { Name = "EditorPreview" });
    public override void _Ready()
    {
        App = GD.Load<PackedScene>("res://App/App.tscn").Instantiate<Train.App.App>();
        App.CreateHost = logger =>
        {
            var content = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
            { Blueprints = [], LocalDrafts = [], Voxels = [], GenerationProfiles = [] };
            var host = WorldSceneFixture.CreateHost(new ScriptedCoreSession(WorldSceneFixture.EmptyWorld(new(0, 0))), content, logger);
            ((ScriptedContentFactory)host.ContentFactory).EvaluateDesignCall = (_, draft) => new ContentResult<DesignEvaluation>.Success(EditorPreviewSession.Evaluate(draft));
            return host;
        };
        App.CreateScenes = () => Factory;
        AddChild(App); App.Saves!.SetAutoSaveInterval(null); App.Host!.AutoPauseOnFocusLoss = false;
    }
    public override void _Process(double delta)
    {
        if (_opened || App.Host?.GetStatus() is null) return;
        _opened = true; App.Editor!.Open(EditorPreviewSession.Draft, new DraftTarget.LocalBlueprint(null));
    }
}

internal sealed class EditorPreviewFactory : IScenesFactory
{
    internal EditorPreviewSession? Session { get; private set; }
    public SceneResult<IDesignEditSession> OpenEditor(IContentView content, DesignEvaluation initial)
    {
        Session = new(content.Generation, ((DesignEvaluation.Complete)initial).Definition);
        return new SceneResult<IDesignEditSession>.Success(Session);
    }
    public SceneResult<IWorldSceneSession> OpenWorld(SceneBinding binding, SceneWorldSnapshot initial) => throw new InvalidOperationException("未配置世界场景。");
    public SceneResult<IDesignPreview> PreviewDesign(IContentView content, DesignEvaluation initial) => throw new InvalidOperationException("未配置只读预览。");
    public SceneResult<ITemplatePreview> PreviewTemplate(IContentView content, SettlementTemplateDraft draft, EditRevision revision) => throw new InvalidOperationException("未配置模板预览。");
}

internal sealed class EditorPreviewSession : IDesignEditSession
{
    private static DesignDraft Child { get; } = new(new("嵌套设备", ""), new DesignSpecification.Assembly(null), new([], [], []));
    internal static DesignDraft Draft { get; } = new(new("编辑器契约预览", "有限场景替身，验证真实 Godot 界面与渲染适配。"), new DesignSpecification.Assembly(null), new([], [], [new NestedDesignItem.AssemblyCopy(new(8, 0, 0), VoxelOrientation.PzPy, Child)]));
    internal static DesignEvaluation Evaluate(DesignDraft draft) => new DesignEvaluation.Complete(new("fixture-design"), new("fixture-composition"), draft, null,
        new(null, new(new(0, 0, 0), new(1, 1, 1)), null, true, null, null, [], [], [], [], []), true, []);
    private EditState _state;
    private readonly List<ISceneFrame?> _baselines = [];
    internal IReadOnlyList<ISceneFrame?> Baselines => _baselines;
    internal bool FailPresent { get; set; }
    internal bool StalePick { get; set; }
    internal bool ConfirmationRequired { get; set; }
    internal Queue<GridPosition>? PickSamples { get; set; }
    internal List<EditOperation> PreviewCalls { get; } = [];
    internal List<(IEditPreview Preview, bool Confirmed)> ApplyCalls { get; } = [];
    private FixturePreview? _currentPreview;
    internal FixedSceneFrame? LastFrame { get; private set; }
    internal int Disposals { get; private set; }
    public EditSessionId Id { get; } = new(Guid.NewGuid());
    public ContentGeneration Content { get; }
    internal EditorPreviewSession(ContentGeneration generation, DesignDraft draft)
    { Content = generation; _state = new(new(new(Id, new(0), generation), draft), false, false, false, null, [], [new(new(1), null, EditElementKind.Cell, new(0, 0, 0), null, [], false), new(new(10), null, EditElementKind.Assembly, new(8, 0, 0), null, [0], false), new(new(20), null, EditElementKind.Assembly, new(0, 0, 0), null, [], true), new(new(30), null, EditElementKind.FunctionalComposite, new(0, 0, 0), null, [], false)], Evaluate(draft), []); }
    public EditState Read() => _state;
    public EditSnapshot Capture() => _state.Snapshot;
    public SceneResult<ISceneFrame> Present(EditViewOptions options, ISceneFrame? displayedFrame)
    {
        _baselines.Add(displayedFrame);
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
        _currentPreview = new(_state.Snapshot.Stamp, operation, ConfirmationRequired);
        return new SceneResult<IEditPreview>.Success(_currentPreview);
    }
    public SceneResult<EditState> Apply(IEditPreview preview, bool confirmed)
    {
        ApplyCalls.Add((preview, confirmed));
        if (!ReferenceEquals(preview, _currentPreview)) return Reject("stale_preview", "预览已过期，请重新预览。");
        if (preview.RequiresConfirmation && !confirmed) return Reject("confirmation_required", "该预览需要确认。");
        var draft = _currentPreview!.Operation is EditOperation.Properties properties ? _state.Snapshot.Draft with { Display = properties.Display } : _state.Snapshot.Draft;
        _state = _state with { Snapshot = new(_state.Snapshot.Stamp with { Revision = new(_state.Snapshot.Stamp.Revision.Value + 1) }, draft), IsDirty = true, CurrentEvaluation = null };
        _currentPreview = null;
        return new SceneResult<EditState>.Success(_state);
    }
    public bool AcceptEvaluation(EditStamp requested, DesignEvaluation result)
    { if (requested != _state.Snapshot.Stamp) return false; _state = _state with { CurrentEvaluation = result }; return true; }
    public bool MarkSaved(EditSnapshot saved) { if (saved.Stamp != _state.Snapshot.Stamp) return false; _state = _state with { IsDirty = false }; return true; }
    public SceneResult<EditState> Select(EditSelection selection)
    { _state = _state with { Selection = ((EditSelection.Elements)selection).Ids }; return new SceneResult<EditState>.Success(_state); }
    public SceneResult<EditState> Enter(EditElementId? element)
    {
        if (element == new EditElementId(20)) return Reject("edit_locked", "缺失定义锁定，不能进入设备。");
        if (element == new EditElementId(30)) return Reject("functional_composite_closed", "功能复合体是封闭引用，不能进入。");
        if (element is not null && element != new EditElementId(10)) throw new InvalidOperationException("有限替身只配置设备10、锁定项20与功能复合体30。");
        _state = _state with { EnteredObject = element, Selection = [] }; return new SceneResult<EditState>.Success(_state);
    }
    public SceneResult<DesignDraft> CaptureObject(EditElementId element) => throw new InvalidOperationException("未配置对象导出。");
    public SceneResult<EditState> Undo() => throw new InvalidOperationException("未配置撤销。");
    public SceneResult<EditState> Redo() => throw new InvalidOperationException("未配置重做。");
    public SceneResult<EditElementId> Locate(EditStamp stamp, DesignElementPath path) => throw new InvalidOperationException("未配置诊断定位。");
    public void Dispose() => Disposals++;
    private static SceneResult<EditState> Reject(string code, string detail) => new SceneResult<EditState>.Failure([new(code, SceneStage.Edit, null, null, null, null, detail)]);
    private sealed record FixturePreview(EditStamp Basis, EditOperation Operation, bool RequiresConfirmation) : IEditPreview
    {
        public int CoveredCells => RequiresConfirmation ? 513 : 1;
        public int AffectedCells => RequiresConfirmation ? 513 : 1;
        public int SkippedCells => 0;
        public bool OccupancyComplete => true;
        public bool CanApply => true;
        public ImmutableArray<EditConflict> Conflicts => [];
        public ImmutableArray<GridPosition> HighlightCells => [];
    }
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
