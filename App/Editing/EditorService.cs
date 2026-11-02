using System.Collections.Immutable;
using System.Text;
using Train.App.Logging;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.App.Editing;

internal sealed partial class EditorService : IDisposable
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
    private EditState? _lastState;
    private bool _readFailed;

    internal HostCoordinator Host { get; }
    internal IDesignEditSession? Session { get; private set; }
    internal IContentView? FixedContent { get; private set; }
    internal EditState? State => _lastState;
    internal ImmutableArray<SceneProblem> FrameDiagnostics { get; private set; } = [];
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
    internal event Action? BlueprintSaved;
    internal event Action<SavedContentDraft>? PackageDraftSaved;
    internal Func<Action, SaveFailure?>? CloseForm { get; set; }

    internal EditorService(HostCoordinator host, IScenesFactory scenes, FileSaveStore store, TrainLogger logger)
    {
        Host = host;
        _scenes = scenes;
        _store = store;
        _logger = logger;
        host.OperationEnded += OperationEnded;
    }

    internal void Open(DesignDraft draft, DraftTarget target, BlueprintSaveTarget? saveTarget = null, bool unsaved = false)
    {
        if (IsOpen || IsBusy) { Report("编辑器已有操作，请先结束当前编辑。"); return; }
        ClearFeedback();
        _afterClose = null;
        _reopenMessage = null;
        _lastState = null;
        _readFailed = false;
        if (Host.BeginEditing() is { } problem) { Report(problem); return; }
        FixedContent = Host.Content!;
        Target = target;
        SaveTarget = saveTarget;
        _draft = new(Guid.NewGuid());
        _forceDirty = unsaved;
        var content = FixedContent;
        _opening = new(draft, Task.Run(() => Host.ContentFactory.EvaluateDesign(content, draft)));
        SceneUnavailable = false;
        Message = "正在准备编辑器…";
        Changed?.Invoke();
    }

    internal void OpenDraft(SavedContentDraft draft, bool unsaved = false)
    {
        var design = draft.Payload switch
        {
            DraftPayload.Design value => value.Value,
            DraftPayload.Definition { Value: DefinitionDraft.Blueprint value }
                when draft.Target is DraftTarget.PackageDefinition => value.Value,
            _ => null
        };
        if (design is null) { Report("该草稿不是可编辑的蓝图。"); return; }
        Open(design, draft.Target, TargetBlueprint(draft.Target), unsaved);
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
        if (draft is null)
        {
            _logger.Warning("editor", "editor_design_unavailable", $"实例设计不可用：{found.Value.Design.Value}");
            Report("实例设计缺失或不可用，暂时无法编辑。");
        }
        else Open(draft, target);
    }

    internal void Process()
    {
        if (_disposed) return;
        ReadState();
        while (_operations.TryDequeue(out var completed)) CompleteOperation(completed);
        if (_opening is { Work.IsCompleted: true } opening)
        {
            _opening = null;
            try
            {
                var result = opening.Work.GetAwaiter().GetResult();
                if (result is not ContentResult<DesignEvaluation>.Success evaluated)
                {
                    Close();
                    ReportResult(result);
                    return;
                }
                var opened = _scenes.OpenEditor(FixedContent!, evaluated.Value);
                if (opened is not SceneResult<IDesignEditSession>.Success success)
                {
                    Close();
                    ReportResult(opened);
                    return;
                }
                Session = success.Value;
                _lastState = Session.Read();
                _evaluating = _lastState.Snapshot.Stamp;
                Message = _reopenMessage;
                HasError = _reopenMessage is not null;
                _reopenMessage = null;
                LogDiagnostics(evaluated.Value);
                Changed?.Invoke();
            }
            catch (Exception error)
            {
                Close();
                Report(error.Message);
            }
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
        var state = State;
        if (!SceneUnavailable && Session is { } session && state is not null && state.Snapshot.Stamp != _evaluating)
        {
            try
            {
                var snapshot = session.Capture();
                _evaluating = snapshot.Stamp;
                var content = FixedContent!;
                _evaluations.Add(new(session, snapshot, Task.Run(() => Host.ContentFactory.EvaluateDesign(content, snapshot.Draft))));
                Changed?.Invoke();
            }
            catch (Exception error)
            {
                SceneFailed(new("editor_capture_failed", SceneStage.Edit, null, null, null, null, error.Message));
            }
        }
        if (_write is { IsCompleted: true } write)
        {
            _write = null;
            FileWriteResult result;
            try { result = write.GetAwaiter().GetResult(); }
            catch (Exception error) { result = new(false, "draft_write_failed", error.Message); }
            var saving = (PendingSave.ExternalDraft)_saving!;
            if (result.Published && saving.Draft.Target is DraftTarget.PackageDefinition)
                PackageDraftSaved?.Invoke(saving.Draft);
            CompleteSave(result, saving.Session, saving.Snapshot);
        }
    }

    private void ReadState()
    {
        if (Session is null || _readFailed) return;
        try
        {
            var state = Session.Read();
            var changed = state != _lastState;
            _lastState = state;
            if (changed) Changed?.Invoke();
        }
        catch (Exception error)
        {
            _readFailed = true;
            _forceDirty = true;
            SceneFailed(new("editor_read_failed", SceneStage.Edit, null, null, null, null, error.Message));
        }
    }

    internal SaveFailure? RequestClose(Action after)
    {
        if (IsBusy) return CloseFailure("busy", "请等待当前编辑操作结束。");
        if (!IsOpen) return CloseFormThen(after);
        if (!IsDirty)
        {
            if (!Close()) return new("editor_close_failed", Message!);
            return CloseFormThen(after);
        }
        if (_afterClose is not null)
            return CloseFailure("editor_close_pending", "请先保存、放弃或取消当前关闭请求。");
        _afterClose = after;
        Changed?.Invoke();
        return null;
    }

    private SaveFailure? CloseFormThen(Action after)
    {
        if (CloseForm is { } close) return close(after);
        after();
        return null;
    }

    private SaveFailure CloseFailure(string code, string message)
    {
        Report(message);
        return new(code, message);
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
        if (Close() && after is not null && CloseFormThen(after) is { } failure) Report(failure.Message);
    }

    private bool Close()
    {
        var session = Session;
        Session = null;
        _opening = null;
        _evaluating = null;
        _afterClose = null;
        _reopenMessage = null;
        _lastState = null;
        _readFailed = false;
        SceneUnavailable = false;
        FrameDiagnostics = [];
        FixedContent = null;
        _forceDirty = false;
        ClearFeedback();
        Exception? failure = null;
        try { session?.Dispose(); }
        catch (Exception error) { failure = error; }
        finally { Host.EndEditing(); Changed?.Invoke(); }
        if (failure is null) return true;
        Report(failure.Message);
        return false;
    }

    internal void ClearFeedback()
    {
        HasError = false;
        Message = null;
    }

    internal void SaveDraft()
    {
        if (IsBusy || Session is not { } session || Target is not { } target) return;
        ClearFeedback();
        try
        {
            var snapshot = session.Capture();
            DraftPayload payload = target is DraftTarget.PackageDefinition package
                ? new DraftPayload.Definition(new DefinitionDraft.Blueprint(package.LocalId, snapshot.Draft))
                : new DraftPayload.Design(snapshot.Draft);
            var saved = new SavedContentDraft(_draft, snapshot.Stamp.Revision, target, payload);
            if (target is DraftTarget.UserBlueprint or DraftTarget.PackageDefinition)
            {
                _saving = new PendingSave.ExternalDraft(session, snapshot, saved);
                _write = Task.Run(() => WriteExternalDraft(saved));
            }
            else
            {
                var begun = Host.BeginContentEdit(new ContentEdit.SaveLocalDraft(saved));
                if (begun.Problem is { } problem)
                {
                    _afterClose = null;
                    Report(problem);
                    return;
                }
                _saving = new PendingSave.LocalDraft(session, snapshot, begun.Operation);
            }
            Message = "正在保存草稿…";
            Changed?.Invoke();
        }
        catch (Exception error) { _afterClose = null; Report(error.Message); }
    }

    internal FileWriteResult WriteExternalDraft(SavedContentDraft draft)
    {
        var encoded = Host.ContentFactory.EncodeDraft(draft);
        if (encoded is not ContentResult<string>.Success success)
            return new(false, "draft_encoding_failed", LogContentFailure(encoded));
        return _store.Write(DraftPath(draft), path => FileSaveStore.WriteCandidate(path, success.Value), path =>
        {
            var decoded = Host.ContentFactory.DecodeDraft(File.ReadAllText(path, new UTF8Encoding(false, true)));
            if (decoded is not ContentResult<SavedContentDraft>.Success)
                throw new InvalidDataException(LogContentFailure(decoded));
        }, overwrite: true);
    }

    private string DraftPath(SavedContentDraft draft) => Path.Combine(draft.Target switch
    {
        DraftTarget.UserBlueprint => AuthoringPaths.UserDraftsDirectory(_store.Root),
        DraftTarget.PackageDefinition package => AuthoringPaths.PackageDraftsDirectory(_store.Root, package.Package),
        _ => throw new InvalidDataException("该草稿不是外部草稿。")
    }, draft.Id.Value.ToString("D") + ".json");

    internal ImmutableArray<SavedContentDraft> UserDrafts()
    {
        return ReadDrafts(AuthoringPaths.UserDraftsDirectory(_store.Root), target => target is DraftTarget.UserBlueprint);
    }

    internal ImmutableArray<SavedContentDraft> PackageDrafts(PackageId package) =>
        ReadDrafts(AuthoringPaths.PackageDraftsDirectory(_store.Root, package),
            target => target is DraftTarget.PackageDefinition value && value.Package == package);

    private ImmutableArray<SavedContentDraft> ReadDrafts(string path, Func<DraftTarget, bool> include)
    {
        if (!Directory.Exists(path)) return [];
        var drafts = ImmutableArray.CreateBuilder<SavedContentDraft>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*.json").Order(StringComparer.Ordinal))
            {
                try
                {
                    var result = Host.ContentFactory.DecodeDraft(File.ReadAllText(file, new UTF8Encoding(false, true)));
                    if (result is ContentResult<SavedContentDraft>.Success success)
                    {
                        if (include(success.Value.Target)) drafts.Add(success.Value);
                    }
                    else Report($"{file}：{LogContentFailure(result)}");
                }
                catch (Exception error) { Report($"{file}：{error.Message}"); }
            }
        }
        catch (Exception error) { Report(error.Message); }
        return drafts.ToImmutable();
    }

    internal bool SaveBlueprint(BlueprintSaveTarget target)
    {
        if (IsBusy || Session is not { } session || Target is not { } source) return false;
        ClearFeedback();
        if (Host.GetSaveStatus()?.Frozen == true)
        {
            Report("frozen：核心已冻结，不能保存有效蓝图。用户库草稿仍可写盘；本局草稿无法写入存档，只保留到离开本局。");
            return false;
        }
        try
        {
            var snapshot = session.Capture();
            var draft = _draft;
            SaveTarget = target;
            if (!Close())
            {
                Reopen(snapshot, source, target, draft, Message!);
                return false;
            }
            var begun = Host.BeginContentEdit(new ContentEdit.SaveBlueprint(target, snapshot.Draft), WriteRequired);
            if (begun.Problem is { } problem)
            {
                Reopen(snapshot, source, target, draft, CommandDiagnostics.Describe(problem, Host.Content), problem);
                return false;
            }
            _saving = new PendingSave.Blueprint(snapshot, source, target, draft, begun.Operation);
            Message = "正在保存有效蓝图…";
            Changed?.Invoke();
            return true;
        }
        catch (Exception error) { Report(error.Message); }
        return false;
    }

    internal void DeleteBlueprint(BlueprintAddress address)
    {
        if (IsOpen || IsBusy) { Report("请先结束编辑。"); return; }
        ClearFeedback();
        var begun = Host.BeginContentEdit(new ContentEdit.DeleteBlueprint(address), WriteRequired);
        if (begun.Problem is { } problem) Report(problem);
    }

    private FileWriteResult WriteRequired(IContentCandidate candidate)
    {
        foreach (var write in candidate.RequiredWrites)
        {
            var id = write.Blueprint.Value;
            if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id is "." or "..")
                throw new InvalidDataException("用户蓝图身份不能用作文件名。");
            var path = Path.Combine(AuthoringPaths.UserLibraryDirectory(_store.Root), id + ".blueprint.json");
            if (write.Bytes is not { } bytes) { File.Delete(path); continue; }
            var result = _store.Write(path, file =>
            {
                using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(bytes.AsSpan()); stream.Flush(true);
            }, file =>
            {
                if (!File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes.AsSpan())) throw new InvalidDataException("蓝图文件校验失败。");
            }, overwrite: true);
            if (!result.Published) return result;
        }
        return new(true);
    }

    private void OperationEnded(HostOperationResult result)
    {
        if (result.Kind == OperationKind.Edit) _operations.Enqueue(result);
    }

    private void CompleteOperation(HostOperationResult result)
    {
        if (result.Kind != OperationKind.Edit) return;
        switch (_saving)
        {
            case PendingSave.Blueprint saving when saving.Operation == result.Operation:
                _saving = null;
                if (result.Succeeded)
                {
                    ClearFeedback();
                    Message = "蓝图已保存。";
                    Changed?.Invoke();
                    BlueprintSaved?.Invoke();
                }
                else
                    Reopen(saving.Snapshot, saving.Source, saving.Target, saving.Draft, OperationMessage(result));
                break;
            case PendingSave.LocalDraft saving when saving.Operation == result.Operation:
                var message = result.Succeeded ? "本局草稿已保存；保存游戏后写入存档。" : OperationMessage(result);
                if (result.Succeeded && Host.GetSaveStatus()?.Frozen == true)
                    message = "本局草稿已保存在当前对局；核心已冻结，无法写入存档，离开本局后丢失。用户库草稿仍可写盘。";
                CompleteSave(new(result.Succeeded, result.FaultCode, message), saving.Session, saving.Snapshot);
                break;
            default:
                if (!result.Succeeded) Report(OperationMessage(result));
                else Changed?.Invoke();
                break;
        }
    }

    private static string OperationMessage(HostOperationResult result) =>
        result.FaultCode is not null ? "内容保存失败，详情见日志。"
        : !result.Diagnostics.IsEmpty ? string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Message))
        : result.Message ?? "内容保存失败，详情见日志。";

    private void Reopen(EditSnapshot snapshot, DraftTarget source, BlueprintSaveTarget target, DraftId draft, string message, CoreProblem? problem = null)
    {
        _saving = null;
        Open(snapshot.Draft, source, target, unsaved: true);
        if (_opening is not null)
        {
            _draft = draft;
            _reopenMessage = message;
            _logger.Warning("editor", "editor_operation_failed", message, problem is { } rejected
                ? new Dictionary<string, object?> { ["problems"] = new[] { CommandDiagnostics.ProblemRecord(rejected) } } : null);
        }
    }

    private void CompleteSave(FileWriteResult result, IDesignEditSession session, EditSnapshot snapshot)
    {
        _saving = null;
        if (result.Published)
        {
            try
            {
                if (ReferenceEquals(Session, session) && session.MarkSaved(snapshot))
                {
                    _forceDirty = false;
                    if (_lastState is not null) _lastState = _lastState with { IsDirty = false };
                }
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

    internal void Undo() => Change(session => session.Undo(), "没有可撤销的步骤。");
    internal void Redo() => Change(session => session.Redo(), "没有可重做的步骤。");
    internal void Select(EditSelection selection) => Change(session => session.Select(selection));
    internal void Enter(EditElementId? element) => Change(session => session.Enter(element));
    internal void Apply(EditOperation operation) => Change(session => session.Apply(operation));

    internal DesignDraft? CaptureObject(EditElementId element)
    {
        if (Session is null || SceneUnavailable || IsBusy) return null;
        try
        {
            var result = Session.CaptureObject(element);
            if (result is SceneResult<DesignDraft>.Success captured) return captured.Value;
            ReportResult(result);
        }
        catch (Exception error) { Report(error.Message); }
        return null;
    }
    private void Change(Func<IDesignEditSession, SceneResult<EditState>> operation, string? noHistory = null)
    {
        if (Session is null || SceneUnavailable || IsBusy) return;
        try
        {
            var result = operation(Session);
            if (result is SceneResult<EditState>.Success success) _lastState = success.Value;
            else if (noHistory is not null && result is SceneResult<EditState>.Failure) Report(noHistory);
            else ReportResult(result);
            Changed?.Invoke();
        }
        catch (Exception error) { Report(error.Message); }
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
    internal void SceneFailed(SceneProblem problem) => SceneFailures([problem]);
    internal void SceneFailures(IEnumerable<SceneProblem> problems)
    {
        SceneUnavailable = true;
        ReportProblems(problems);
    }

    internal void SetFrameDiagnostics(ImmutableArray<SceneProblem> problems)
    {
        if (FrameDiagnostics.SequenceEqual(problems)) return;
        FrameDiagnostics = problems;
        foreach (var problem in problems)
            _logger.Warning("editor", "editor_scene_diagnostic", Describe(problem));
        Changed?.Invoke();
    }
    internal void SceneRecovered()
    {
        SceneUnavailable = false;
        _readFailed = false;
        ClearFeedback();
        Changed?.Invoke();
    }

    internal void ReportProblems(IEnumerable<SceneProblem> problems)
    {
        var listed = problems.ToArray();
        if (listed.Length == 0) return;
        foreach (var problem in listed)
            _logger.Warning("editor", "editor_operation_failed", Describe(problem));
        Message = string.Join("\n", listed.Select(problem => Explain(problem, Host.Content)));
        HasError = true;
        Changed?.Invoke();
    }
    internal string LogRejected(string category, string eventId, CoreProblem problem) =>
        CommandDiagnostics.LogProblem(_logger, category, eventId, problem, Host.Content);

    internal void Report(CoreProblem problem)
    {
        Message = CommandDiagnostics.LogProblem(_logger, "editor", "editor_operation_failed", problem, Host.Content);
        HasError = true;
        Changed?.Invoke();
    }

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
    internal static string Explain(SceneProblem problem, IContentView? content)
    {
        var sentence = Sentence(problem.Code);
        return problem.Package is { } package ? sentence + PackageText(package, content) : sentence;
    }
    internal static string Explain(DesignEditLock locked, IContentView? content) =>
        "缺失定义，相关部分不能显示或编辑。" + PackageText(locked.Package, content)
        + "受阻操作：" + string.Join("、", locked.BlockedOperations.Select(OperationName)) + "。";
    private static string PackageText(PackageId package, IContentView? content)
    {
        var name = content?.Packages.FirstOrDefault(item => item.Metadata?.Id == package)?.Metadata?.Name;
        return string.IsNullOrWhiteSpace(name) ? "来源内容包不可用。" : $"来源内容包：{name}。";
    }
    private static string OperationName(LockedDesignOperation operation) => operation switch
    {
        LockedDesignOperation.Move => "移动选区",
        LockedDesignOperation.Rotate => "选区旋转 90°",
        LockedDesignOperation.Copy => "复制选区",
        LockedDesignOperation.Remove => "删除选区",
        LockedDesignOperation.Replace => "替换",
        LockedDesignOperation.Paint => "涂装",
        LockedDesignOperation.ChangeShape => "形状",
        LockedDesignOperation.Enter => "进入所选设备",
        _ => "未知操作"
    };
    private static string Sentence(string code) => code switch
    {
        "content_missing" or "reference_missing" => "引用的定义缺失，相关部分不能显示或编辑。",
        "occupancy_conflict" => "与其他部分的占格重叠。",
        "invalid_operation" => "当前对象不支持这个操作。",
        "edit_locked" => "缺失定义已锁定，不能进行这个操作。",
        "target_missing" => "目标已不存在，请重新选择。",
        "region_limit" => "选区超过上限，请缩小范围。",
        "outside_envelope" => "超出外形范围。",
        "design_invalid" => "设计不合法，不能开始编辑。",
        "editor_read_failed" => "无法读取当前设计，详情见日志。",
        "editor_capture_failed" => "无法捕获当前设计，详情见日志。",
        "editor_display_failed" => "场景显示失败，详情见日志。",
        "editor_pick_failed" => "无法拾取设计，详情见日志。",
        _ when code.StartsWith("stale_", StringComparison.Ordinal) => "场景已更新，请重新操作。",
        _ => "场景出现问题，详情见日志。"
    };
    private string LogContentFailure<T>(ContentResult<T> result) where T : notnull
    {
        switch (result)
        {
            case ContentResult<T>.Failure failure:
                foreach (var diagnostic in failure.Diagnostics)
                    _logger.Warning("editor", "editor_operation_failed", Describe(diagnostic));
                return failure.Diagnostics.IsEmpty ? "内容处理失败，详情见日志。"
                    : string.Join("\n", failure.Diagnostics.Select(diagnostic => diagnostic.Message));
            case ContentResult<T>.Faulted fault:
                _logger.Error("editor", "editor_operation_failed", $"{fault.Code}：{fault.Message}",
                    new Dictionary<string, object?> { ["diagnostic_stack_trace"] = fault.StackTrace });
                return "内容处理失败，详情见日志。";
            default:
                return "";
        }
    }
    private void ReportResult<T>(ContentResult<T> result) where T : notnull
    {
        Message = LogContentFailure(result);
        HasError = true;
        Changed?.Invoke();
    }
    private void ReportResult<T>(SceneResult<T> result) where T : notnull
    {
        if (result is SceneResult<T>.Failure failure) ReportProblems(failure.Problems);
        else if (result is SceneResult<T>.Faulted fault) ReportProblems([fault.Problem]);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Host.OperationEnded -= OperationEnded;
        if (_opening is { } opening) _ = opening.Work.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        if (_write is { } write) _ = write.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        Close();
        foreach (var evaluation in _evaluations) _ = evaluation.Work.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }
    private sealed record Opening(DesignDraft Draft, Task<ContentResult<DesignEvaluation>> Work);
    private sealed record Evaluation(IDesignEditSession Session, EditSnapshot Snapshot, Task<ContentResult<DesignEvaluation>> Work);
    private abstract record PendingSave
    {
        private PendingSave() { }
        internal sealed record ExternalDraft(IDesignEditSession Session, EditSnapshot Snapshot, SavedContentDraft Draft) : PendingSave;
        internal sealed record LocalDraft(IDesignEditSession Session, EditSnapshot Snapshot, long Operation) : PendingSave;
        internal sealed record Blueprint(EditSnapshot Snapshot, DraftTarget Source, BlueprintSaveTarget Target,
            DraftId Draft, long Operation) : PendingSave;
    }
}
