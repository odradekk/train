using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Godot.Tests.Editing;

internal sealed class EditorScenes : IScenesFactory
{
    internal Func<IContentView, DesignEvaluation, SceneResult<IDesignEditSession>>? OpenEditorCall { get; set; }
    internal List<EditorSession> Sessions { get; } = [];
    public SceneResult<IDesignEditSession> OpenEditor(IContentView content, DesignEvaluation initial)
    {
        if (OpenEditorCall is { } call) return call(content, initial);
        var draft = initial is DesignEvaluation.Complete complete ? complete.Definition
            : throw new InvalidOperationException("UNCONFIGURED: 初始评估。");
        var session = new EditorSession(content.Generation, draft, initial);
        Sessions.Add(session);
        return new SceneResult<IDesignEditSession>.Success(session);
    }
    public SceneResult<IWorldSceneSession> OpenWorld(SceneBinding binding, SceneWorldSnapshot initial) => throw Unconfigured();
    public SceneResult<IDesignPreview> PreviewDesign(IContentView content, DesignEvaluation initial) => throw Unconfigured();
    public SceneResult<ITemplatePreview> PreviewTemplate(IContentView content, SettlementTemplateDraft draft, EditRevision revision) => throw Unconfigured();
    private static InvalidOperationException Unconfigured() => new("UNCONFIGURED: 场景入口。");
}

// 只模拟修订与保存标记的契约；不模拟体素占格、编辑合法性或撤销算法。
internal sealed class EditorSession : IDesignEditSession
{
    private EditState _state;
    internal int Disposals { get; private set; }
    internal List<EditSnapshot> Saved { get; } = [];
    internal List<EditStamp> Evaluations { get; } = [];
    public EditSessionId Id { get; } = new(Guid.NewGuid());
    public ContentGeneration Content { get; }
    internal EditorSession(ContentGeneration content, DesignDraft draft, DesignEvaluation initial)
    {
        Content = content;
        _state = new(new(new(Id, new(0), content), draft), false, false, false, null, [], [], initial, []);
    }
    internal void Change(DesignDraft draft) => _state = _state with
    {
        Snapshot = new(new(Id, new(_state.Snapshot.Stamp.Revision.Value + 1), Content), draft),
        IsDirty = true, CurrentEvaluation = null
    };
    public EditState Read() => _state;
    public EditSnapshot Capture() => _state.Snapshot;
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
    public void Dispose() => Disposals++;
    public SceneResult<DesignDraft> CaptureObject(EditElementId element) => throw Unconfigured();
    public SceneResult<EditState> Select(EditSelection selection) => throw Unconfigured();
    public SceneResult<EditState> Enter(EditElementId? element) => throw Unconfigured();
    public SceneResult<IEditPreview> Preview(EditOperation operation) => throw Unconfigured();
    public SceneResult<EditState> Apply(IEditPreview preview, bool confirmed) => throw Unconfigured();
    public SceneResult<EditState> Undo() => throw Unconfigured();
    public SceneResult<EditState> Redo() => throw Unconfigured();
    public SceneResult<ISceneFrame> Present(EditViewOptions options, ISceneFrame? displayedFrame) => throw Unconfigured();
    public SceneResult<EditElementId> Locate(EditStamp stamp, DesignElementPath path) => throw Unconfigured();
    private static InvalidOperationException Unconfigured() => new("UNCONFIGURED: 编辑会话入口。");
}
