using System.Collections.Immutable;
using System.Text;
using Train.App.Logging;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.App.Editing;

internal sealed class EditorService : IDisposable
{
    private readonly IScenesFactory _scenes;
    private readonly FileSaveStore _store;
    private readonly TrainLogger _logger;
    private readonly List<Evaluation> _evaluations = [];
    private readonly Queue<HostOperationResult> _operations = [];
    private Opening? _opening;
    private EditStamp? _evaluating;
    private PendingSave? _saving;
    private Task<FileWriteResult>? _write;
    private Action? _afterClose;
    private DraftId _draft;
    private bool _forceDirty;
    private bool _disposed;
    private string? _reopenMessage;

    internal HostCoordinator Host { get; }
    internal IDesignEditSession? Session { get; private set; }
    internal IContentView? FixedContent { get; private set; }
    internal EditState? State => Session?.Read();
    internal DraftTarget? Target { get; private set; }
    internal BlueprintSaveTarget? SaveTarget { get; private set; }
    internal bool IsOpen => Session is not null || _opening is not null;
    internal bool IsBusy => _opening is not null || _saving is not null;
    internal bool IsDirty => _forceDirty || State?.IsDirty == true;
    internal bool Leaving => _afterClose is not null;
    internal bool SceneUnavailable { get; private set; }
    internal string? Message { get; private set; }
    internal bool HasError { get; private set; }
    internal event Action? Changed;

    internal EditorService(HostCoordinator host, IScenesFactory scenes, FileSaveStore store, TrainLogger logger)
    {
        Host = host; _scenes = scenes; _store = store; _logger = logger;
        host.OperationEnded += OperationEnded;
    }

    internal void Open(DesignDraft draft, DraftTarget target, BlueprintSaveTarget? saveTarget = null, bool unsaved = false)
    {
        if (IsOpen || IsBusy) { Report("编辑器已有操作，请先结束当前编辑。"); return; }
        if (Host.BeginEditing() is { } problem) { Report(CommandDiagnostics.Describe(problem)); return; }
        FixedContent = Host.Content!;
        Target = target; SaveTarget = saveTarget; _draft = new(Guid.NewGuid()); _forceDirty = unsaved;
        var content = FixedContent;
        _opening = new(draft, Task.Run(() => Host.ContentFactory.EvaluateDesign(content, draft)));
        SceneUnavailable = false; HasError = false; Message = "正在准备编辑器…"; Changed?.Invoke();
    }

    internal void OpenDraft(SavedContentDraft draft)
    {
        if (draft.Payload is not DraftPayload.Design design) { Report("该草稿不是玩家设计。"); return; }
        Open(design.Value, draft.Target, TargetBlueprint(draft.Target));
        if (_opening is not null) _draft = draft.Id;
    }

    private static BlueprintSaveTarget? TargetBlueprint(DraftTarget target) => target switch
    {
        DraftTarget.LocalBlueprint { Id: { } id } => new BlueprintSaveTarget.Overwrite(new(BlueprintLibrary.Local, id)),
        DraftTarget.UserBlueprint { Id: { } id } => new BlueprintSaveTarget.Overwrite(new(BlueprintLibrary.User, id)),
        _ => null
    };

    internal void OpenBlueprint(BlueprintAddress address)
    {
        if (Host.Content?.FindBlueprint(address) is ContentLookup<BlueprintRecord>.Found found)
        {
            DraftTarget target = address.Library == BlueprintLibrary.User
                ? new DraftTarget.UserBlueprint(address.Id) : new DraftTarget.LocalBlueprint(
                    address.Library == BlueprintLibrary.Local ? address.Id : null);
            Open(found.Value.Definition, target, address.Library == BlueprintLibrary.Packages ? null : new BlueprintSaveTarget.Overwrite(address));
        }
        else Report($"无法打开蓝图：{address.Id.Value}，内容缺失或不可用。");
    }

    internal void OpenInstance(InstanceId instance)
    {
        if (Host.Content is not { } content || Host.GetStatus() is not { } status) { Report("没有活动对局。"); return; }
        var target = new DraftTarget.Instance(instance);
        if (content.LocalDrafts.FirstOrDefault(saved => saved.Target == target) is { } saved) { OpenDraft(saved); return; }
        if (status.View.FindInstance(instance) is not Lookup<InstanceView>.Found found) { Report("实例已不存在。"); return; }
        var draft = content.FindDesign(found.Value.Design) switch
        {
            ContentLookup<DesignEvaluation>.Found { Value: DesignEvaluation.Complete value } => value.Definition,
            ContentLookup<DesignEvaluation>.Found { Value: DesignEvaluation.Partial value } => value.Definition,
            _ => null
        };
        if (draft is null) Report($"实例设计不可用：{found.Value.Design.Value}");
        else Open(draft, target);
    }

    internal void Process()
    {
        if (_disposed) return;
        while (_operations.TryDequeue(out var completed)) CompleteOperation(completed);
        if (_opening is { Work.IsCompleted: true } opening)
        {
            _opening = null;
            try
            {
                var result = opening.Work.GetAwaiter().GetResult();
                if (result is not ContentResult<DesignEvaluation>.Success evaluated) { ReportResult(result); Close(); return; }
                var opened = _scenes.OpenEditor(FixedContent!, evaluated.Value);
                if (opened is not SceneResult<IDesignEditSession>.Success success) { ReportResult(opened); Close(); return; }
                Session = success.Value;
                _evaluating = Session.Read().Snapshot.Stamp;
                Message = _reopenMessage;
                HasError = _reopenMessage is not null;
                _reopenMessage = null;
                LogDiagnostics(evaluated.Value);
                Changed?.Invoke();
            }
            catch (Exception error) { Report(error.Message); Close(); }
        }
        foreach (var evaluation in _evaluations.Where(item => item.Work.IsCompleted).ToArray())
        {
            _evaluations.Remove(evaluation);
            try
            {
                var result = evaluation.Work.GetAwaiter().GetResult();
                if (result is ContentResult<DesignEvaluation>.Success success)
                {
                    if (ReferenceEquals(Session, evaluation.Session) &&
                        evaluation.Session.AcceptEvaluation(evaluation.Snapshot.Stamp, success.Value))
                    { LogDiagnostics(success.Value); Changed?.Invoke(); }
                }
                else if (ReferenceEquals(Session, evaluation.Session) && State?.Snapshot.Stamp == evaluation.Snapshot.Stamp) ReportResult(result);
            }
            catch (Exception error)
            {
                if (ReferenceEquals(Session, evaluation.Session) && State?.Snapshot.Stamp == evaluation.Snapshot.Stamp) Report(error.Message);
                else _logger.Warning("editor", "editor_evaluation_discarded", error.Message);
            }
        }
        if (Session is { } session && session.Read().Snapshot.Stamp != _evaluating)
        {
            var snapshot = session.Capture();
            _evaluating = snapshot.Stamp;
            var content = FixedContent!;
            _evaluations.Add(new(session, snapshot, Task.Run(() => Host.ContentFactory.EvaluateDesign(content, snapshot.Draft))));
            Changed?.Invoke();
        }
        if (_write is { IsCompleted: true } write)
        {
            _write = null;
            FileWriteResult result;
            try { result = write.GetAwaiter().GetResult(); }
            catch (Exception error) { result = new(false, "draft_write_failed", error.Message); }
            CompleteSave(result);
        }
    }

    internal void RequestClose(Action after)
    {
        if (IsBusy) { Report("请等待当前编辑操作结束。"); return; }
        if (!IsOpen) { after(); return; }
        if (!IsDirty) { Close(); after(); return; }
        if (_afterClose is not null) return;
        _afterClose = after; Changed?.Invoke();
    }

    internal void ResolveClose(LeaveChoice choice)
    {
        if (_afterClose is null || IsBusy) return;
        if (choice == LeaveChoice.Cancel) { _afterClose = null; Changed?.Invoke(); }
        else if (choice == LeaveChoice.Save) SaveDraft();
        else FinishClose();
    }

    private void FinishClose()
    {
        var after = _afterClose;
        _afterClose = null;
        Close(); after?.Invoke();
    }

    private void Close()
    {
        var session = Session;
        Session = null; _opening = null; _evaluating = null;
        SceneUnavailable = false; FixedContent = null; _forceDirty = false;
        try { session?.Dispose(); }
        catch (Exception error) { Report(error.Message); }
        finally { Host.EndEditing(); Changed?.Invoke(); }
    }

    internal void SaveDraft()
    {
        if (IsBusy || Session is not { } session || Target is not { } target) return;
        try
        {
            var snapshot = session.Capture();
            var saved = new SavedContentDraft(_draft, snapshot.Stamp.Revision, target, new DraftPayload.Design(snapshot.Draft));
            if (target is DraftTarget.UserBlueprint)
            {
                _saving = new(session, snapshot, target, SaveTarget, false, 0, _draft);
                _write = Task.Run(() => WriteDraft(saved));
            }
            else
            {
                var begun = Host.BeginContentEdit(new ContentEdit.SaveLocalDraft(saved));
                if (begun.Problem is { } problem) { Report(CommandDiagnostics.Describe(problem)); _afterClose = null; return; }
                _saving = new(session, snapshot, target, SaveTarget, false, begun.Operation, _draft);
            }
            Message = "正在保存草稿…"; Changed?.Invoke();
        }
        catch (Exception error) { _afterClose = null; Report(error.Message); }
    }

    private FileWriteResult WriteDraft(SavedContentDraft draft)
    {
        var encoded = Host.ContentFactory.EncodeDraft(draft);
        if (encoded is not ContentResult<string>.Success success) return new(false, "draft_encoding_failed", Describe(encoded));
        return _store.Write(DraftPath(draft.Id), path => FileSaveStore.WriteCandidate(path, success.Value), path =>
        {
            var decoded = Host.ContentFactory.DecodeDraft(File.ReadAllText(path, new UTF8Encoding(false, true)));
            if (decoded is not ContentResult<SavedContentDraft>.Success) throw new InvalidDataException(Describe(decoded));
        }, overwrite: true);
    }

    private string DraftPath(DraftId id) => Path.Combine(_store.Root, "authoring-drafts", id.Value.ToString("D") + ".json");

    internal ImmutableArray<SavedContentDraft> UserDrafts()
    {
        var path = Path.Combine(_store.Root, "authoring-drafts");
        if (!Directory.Exists(path)) return [];
        var drafts = ImmutableArray.CreateBuilder<SavedContentDraft>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*.json").Order(StringComparer.Ordinal))
            {
                var result = Host.ContentFactory.DecodeDraft(File.ReadAllText(file, new UTF8Encoding(false, true)));
                if (result is ContentResult<SavedContentDraft>.Success success && success.Value.Target is DraftTarget.UserBlueprint) drafts.Add(success.Value);
                else Report($"{file}：{Describe(result)}");
            }
        }
        catch (Exception error) { Report(error.Message); }
        return drafts.ToImmutable();
    }

    internal void SaveBlueprint(BlueprintSaveTarget target)
    {
        if (IsBusy || Session is not { } session || Target is not { } source) return;
        if (target is BlueprintSaveTarget.New { Library: BlueprintLibrary.Packages } or
            BlueprintSaveTarget.Overwrite { Address.Library: BlueprintLibrary.Packages }) { Report("内容包蓝图只读，请复制为新蓝图。"); return; }
        if ((target is BlueprintSaveTarget.New { Library: BlueprintLibrary.User } or
            BlueprintSaveTarget.Overwrite { Address.Library: BlueprintLibrary.User }) && !Host.UserLibraryEnabled)
        { Report("用户蓝图库暂不可用，请保存为本局蓝图或草稿。"); return; }
        try
        {
            var snapshot = session.Capture();
            SaveTarget = target;
            _afterClose = null;
            Close();
            var begun = Host.BeginContentEdit(new ContentEdit.SaveBlueprint(target, snapshot.Draft), WriteRequired);
            _saving = new(session, snapshot, source, target, true, begun.Operation, _draft);
            if (begun.Problem is { } problem) { Reopen(CommandDiagnostics.Describe(problem)); return; }
            Message = "正在保存有效蓝图…"; Changed?.Invoke();
        }
        catch (Exception error) { Report(error.Message); }
    }

    internal void DeleteBlueprint(BlueprintAddress address)
    {
        if (IsOpen || IsBusy) { Report("请先结束编辑。"); return; }
        if (address.Library == BlueprintLibrary.Packages) { Report("内容包蓝图只读。"); return; }
        if (address.Library == BlueprintLibrary.User && !Host.UserLibraryEnabled) { Report("用户蓝图库暂不可用。"); return; }
        var begun = Host.BeginContentEdit(new ContentEdit.DeleteBlueprint(address), WriteRequired);
        if (begun.Problem is { } problem) Report(CommandDiagnostics.Describe(problem));
    }

    private ContentResult<bool> WriteRequired(IContentCandidate candidate)
    {
        foreach (var write in candidate.RequiredWrites)
        {
            var id = write.Blueprint.Value;
            if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id is "." or "..")
                throw new InvalidDataException("用户蓝图身份不能用作文件名。");
            var path = Path.Combine(_store.Root, "content-library", id + ".json");
            if (write.Bytes is not { } bytes) { File.Delete(path); continue; }
            var result = _store.Write(path, file =>
            {
                using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(bytes.AsSpan()); stream.Flush(true);
            }, file =>
            {
                if (!File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes.AsSpan())) throw new InvalidDataException("蓝图文件校验失败。");
            }, overwrite: true);
            if (!result.Published) return new ContentResult<bool>.Faulted(result.Code ?? "blueprint_write_failed", result.Message ?? "蓝图写入失败。", null);
        }
        return new ContentResult<bool>.Success(true);
    }

    private void OperationEnded(HostOperationResult result)
    {
        if (result.Kind == OperationKind.Edit) _operations.Enqueue(result);
    }

    private void CompleteOperation(HostOperationResult result)
    {
        if (result.Kind != OperationKind.Edit) return;
        if (_saving is not { } saving || saving.Operation != result.Operation)
        {
            if (!result.Succeeded) Report(result.Message ?? result.FaultCode ?? string.Join("\n", result.Diagnostics.Select(Describe)));
            else Changed?.Invoke();
            return;
        }
        if (saving.Blueprint)
        {
            if (result.Succeeded) { _saving = null; HasError = false; Message = "蓝图已保存。"; Changed?.Invoke(); }
            else Reopen(result.Message ?? result.FaultCode ?? string.Join("\n", result.Diagnostics.Select(Describe)));
        }
        else CompleteSave(new(result.Succeeded, result.FaultCode, result.Succeeded ? "本局草稿已保存；保存游戏后写入存档。" :
            result.Message ?? string.Join("\n", result.Diagnostics.Select(Describe))));
    }

    private void Reopen(string message)
    {
        var saving = _saving!;
        _saving = null;
        Report(message);
        _reopenMessage = message;
        Open(saving.Snapshot.Draft, saving.Target, saving.SaveTarget, unsaved: true);
        if (_opening is not null) _draft = saving.Draft;
    }

    private void CompleteSave(FileWriteResult result)
    {
        var saving = _saving!;
        _saving = null;
        if (result.Published)
        {
            try
            {
                if (ReferenceEquals(Session, saving.Session) && saving.Session.MarkSaved(saving.Snapshot)) _forceDirty = false;
            }
            catch (Exception error)
            { _afterClose = null; Report($"草稿已保存，但编辑状态更新失败：{error.Message}"); return; }
            Message = result.Message ?? "草稿已保存。";
            HasError = false;
            if (_afterClose is not null && !IsDirty) FinishClose();
            else if (_afterClose is not null) _afterClose = null;
        }
        else { _afterClose = null; Report(result.Message ?? "草稿保存失败。"); }
        if (result.CleanupMessage is { } cleanup) Report(cleanup);
        Changed?.Invoke();
    }

    internal void Undo() => Change(session => session.Undo());
    internal void Redo() => Change(session => session.Redo());
    internal void Select(EditSelection selection) => Change(session => session.Select(selection));
    internal void Enter(EditElementId? element) => Change(session => session.Enter(element));
    internal void Apply(IEditPreview preview, bool confirmed) => Change(session => session.Apply(preview, confirmed));
    private void Change(Func<IDesignEditSession, SceneResult<EditState>> operation)
    {
        if (Session is null || SceneUnavailable || IsBusy) return;
        try { var result = operation(Session); if (result is not SceneResult<EditState>.Success) ReportResult(result); Changed?.Invoke(); }
        catch (Exception error) { Report(error.Message); }
    }
    internal SceneResult<IEditPreview>? Preview(EditOperation operation)
    {
        if (Session is null || SceneUnavailable || IsBusy) return null;
        try { var result = Session.Preview(operation); if (result is not SceneResult<IEditPreview>.Success) ReportResult(result); return result; }
        catch (Exception error) { Report(error.Message); return null; }
    }
    internal void Locate(DesignElementPath path)
    {
        if (State is not { } state) return;
        try
        {
            var result = Session!.Locate(state.Snapshot.Stamp, path);
            if (result is SceneResult<EditElementId>.Success found) Select(new EditSelection.Elements([found.Value]));
            else ReportResult(result);
        }
        catch (Exception error) { Report(error.Message); }
    }
    internal void SceneFailed(SceneProblem problem) { SceneUnavailable = true; Report(Describe(problem)); }
    internal void SceneRecovered() { SceneUnavailable = false; Changed?.Invoke(); }
    internal void Report(string message)
    {
        Message = message;
        HasError = true;
        _logger.Warning("editor", "editor_operation_failed", message);
        Changed?.Invoke();
    }
    private void LogDiagnostics(DesignEvaluation evaluation)
    {
        var diagnostics = evaluation switch
        {
            DesignEvaluation.Complete value => value.Diagnostics,
            DesignEvaluation.Partial value => value.Diagnostics,
            DesignEvaluation.Failed value => value.Diagnostics,
            _ => ImmutableArray<ContentDiagnostic>.Empty
        };
        foreach (var diagnostic in diagnostics)
            _logger.Warning("editor", "editor_design_diagnostic", Describe(diagnostic));
        var scene = evaluation switch { DesignEvaluation.Complete value => value.Scene, DesignEvaluation.Partial value => value.Scene, _ => null };
        foreach (var locked in scene?.EditLocks ?? [])
            _logger.Warning("editor", "editor_design_locked", $"{locked.Reference} · 包：{locked.Package.Value} · {locked.Element} · 禁止：{string.Join("、", locked.BlockedOperations)}");
    }
    internal static string Describe(ContentDiagnostic diagnostic) =>
        $"{diagnostic.Code}：{diagnostic.Message} · {diagnostic.Subject} · 包：{diagnostic.Source?.Package.Value ?? "未知"} · {diagnostic.Field} · {diagnostic.Element}";
    internal static string Describe(SceneProblem problem) =>
        $"{problem.Code}：{problem.Detail} · {problem.Stage} · {problem.Content} · 包：{problem.Package?.Value ?? "未知"} · {problem.Element}";
    private static string Describe<T>(ContentResult<T> result) where T : notnull => result switch
    {
        ContentResult<T>.Failure failure => string.Join("\n", failure.Diagnostics.Select(Describe)),
        ContentResult<T>.Faulted fault => $"{fault.Code}：{fault.Message}",
        _ => ""
    };
    private void ReportResult<T>(ContentResult<T> result) where T : notnull => Report(Describe(result));
    private void ReportResult<T>(SceneResult<T> result) where T : notnull => Report(result switch
    {
        SceneResult<T>.Failure failure => string.Join("\n", failure.Problems.Select(Describe)),
        SceneResult<T>.Faulted fault => Describe(fault.Problem),
        _ => ""
    });
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Host.OperationEnded -= OperationEnded;
        if (_opening is { } opening) _ = opening.Work.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        if (_write is { } write) _ = write.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        _afterClose = null; Close();
        foreach (var evaluation in _evaluations) _ = evaluation.Work.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }
    private sealed record Opening(DesignDraft Draft, Task<ContentResult<DesignEvaluation>> Work);
    private sealed record Evaluation(IDesignEditSession Session, EditSnapshot Snapshot, Task<ContentResult<DesignEvaluation>> Work);
    private sealed record PendingSave(IDesignEditSession Session, EditSnapshot Snapshot, DraftTarget Target,
        BlueprintSaveTarget? SaveTarget, bool Blueprint, long Operation, DraftId Draft);
}
