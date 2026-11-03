using System.Collections.Immutable;
using Train.App.Editing;
using Train.App.Logging;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.App.Authoring;

internal sealed record AuthoringTarget(PackageId Package, string Directory, bool IsCore);

internal sealed partial class AuthoringService : IDisposable
{
    private readonly HostCoordinator host;
    private readonly EditorService editor;
    private readonly IScenesFactory scenes;
    private readonly TrainLogger logger;
    private readonly bool development;
    private readonly string? coreDirectory;
    private readonly Dictionary<AuthoringDefinitionAddress, PackageEdit> edits = [];
    private Task<(SavedContentDraft Draft, FileWriteResult Result)>? saving;

    private PackageSnapshot? snapshot;
    private IAuthoringPackage? package;
    private HostBinding? binding;
    private DraftId draftId;
    private EditRevision revision;
    private bool formDirty;
    private Action? afterFormClose;
    private long operation;
    private PackageWriteResult? written;
    private bool disposed;

    internal string ModsDirectory { get; }
    internal bool CanUse => development && host.Content is not null && host.GetStatus()?.IsReplay != true;
    internal bool CanWriteCore => CanUse && coreDirectory is not null;
    internal bool IsBusy => discovering is not null || opening is not null || creating is not null || saving is not null || operation != 0;
    internal IAuthoringPackage? Package => package;
    internal ImmutableArray<AuthoringDefinitionAddress> Definitions => package is null ? []
        : package.EditableDefinitions.Concat(edits.Keys).Concat(DirtyDefinition is { } dirty ? [dirty] : []).Distinct().ToImmutableArray();
    internal AuthoringTarget? Target { get; private set; }
    internal DefinitionDraft? Form { get; private set; }
    internal bool Leaving => afterFormClose is not null;
    internal ITemplatePreview? TemplatePreview { get; private set; }
    internal PackageWriteResult? LastWrite { get; private set; }
    internal string? Message { get; private set; }
    internal event Action? Changed;

    internal AuthoringService(HostCoordinator host, EditorService editor, IScenesFactory scenes,
        bool development, string modsDirectory, string? coreDirectory, TrainLogger logger, TimeProvider? timeProvider = null)
    {
        clock = timeProvider ?? TimeProvider.System;
        this.host = host; this.editor = editor; this.scenes = scenes;
        this.logger = logger;
        this.development = development;
        ModsDirectory = Path.GetFullPath(modsDirectory).TrimEnd(Path.DirectorySeparatorChar);
        this.coreDirectory = coreDirectory is null ? null : Path.GetFullPath(coreDirectory).TrimEnd(Path.DirectorySeparatorChar);
        editor.CloseForm = RequestCloseForm;
        editor.PackageDraftSaved += AcceptDraft;
        host.OperationEnded += OperationEnded;
    }

    internal void OpenDefinition(AuthoringDefinitionAddress address, bool grid = true)
    {
        if (!CanUse || IsBusy || package is null) return;
        if (editor.RequestClose(() =>
        {
            try
            {
                DefinitionDraft definition;
                if (edits.TryGetValue(address, out var edit) && edit is PackageEdit.WriteDefinition write) definition = write.Draft;
                else
                {
                    var read = package.Read(address);
                    if (read is ContentLookup<DefinitionDraft>.Found found) definition = found.Value;
                    else
                    {
                        if (read is ContentLookup<DefinitionDraft>.Missing missing)
                            sourceIssues = sourceIssues.Concat(missing.Diagnostics.Select(item => Issue(item, address))).Distinct().ToImmutableArray();
                        Report("定义不存在或无法读取，请检查源包问题。"); return;
                    }
                }
                ShowDefinition(definition, new(Guid.NewGuid()), grid);
            }
            catch (Exception error) { Report(Unexpected(error)); }
        }) is { } failure) Report(failure.Message);
    }

    private void OpenDefinition(DefinitionDraft definition, DraftId id, bool grid, bool unsaved = false)
    {
        if (editor.RequestClose(() =>
        {
            ShowDefinition(definition, id, grid, unsaved);
        }) is { } failure) Report(failure.Message);
    }

    private void ShowDefinition(DefinitionDraft definition, DraftId id, bool grid, bool unsaved = false)
    {
        if (definition is DefinitionDraft.Blueprint blueprint && grid)
            editor.Open(blueprint.Value, CreateDraftTarget(definition), unsaved: unsaved);
        else
        {
            if (host.BeginEditing() is { } problem) { Reject(problem); return; }
            Form = definition; draftId = id; revision = new(0); formDirty = unsaved;
            currentForm = definition; TemplatePreview = null; QueueFeedback(immediate: true); Changed?.Invoke();
        }
    }

    internal ImmutableArray<SavedContentDraft> Drafts() => package is null ? [] : editor.PackageDrafts(package.Package);

    internal void OpenDraft(SavedContentDraft saved, bool useCurrentSource = false)
    {
        if (!CanUse || IsBusy || package is null) return;
        if (saved.Target is not DraftTarget.PackageDefinition target || target.Package != package.Package) return;
        var changedSource = target.SourceVersion != package.SourceVersion;
        if (changedSource && !useCurrentSource)
        { Report("草稿来源与当前源包不同。请显式选择“在当前源包上打开”，手动处理修改后保存并重新校验；不会自动合并。"); return; }
        if (changedSource) saved = saved with { Target = target with { SourceVersion = package.SourceVersion } };
        if (saved.Payload is DraftPayload.Definition { Value: DefinitionDraft.Blueprint })
        {
            if (editor.RequestClose(() => editor.OpenDraft(saved, changedSource)) is { } failure) Report(failure.Message);
        }
        else if (saved.Payload is DraftPayload.Definition definition) OpenDefinition(definition.Value, saved.Id, false, changedSource);
    }

    internal void NewDefinition(DefinitionDraft definition)
    {
        if (CanUse && !IsBusy && package is not null) OpenDefinition(definition, new(Guid.NewGuid()), definition is DefinitionDraft.Blueprint, unsaved: true);
    }

    internal void MarkFormDirty() { currentForm = null; TemplatePreview = null; captureIssue = null; formDirty = true; revision = new(checked(revision.Value + 1)); pendingFeedback = null; FormDiagnostics = []; Changed?.Invoke(); }

    internal void SaveForm(DefinitionDraft value)
    {
        if (!CanUse || IsBusy || Form is null) return;
        try
        {
            var saved = new SavedContentDraft(draftId, revision, CreateDraftTarget(value), new DraftPayload.Definition(value));
            saving = Task.Run(() => (saved, editor.WriteExternalDraft(saved)));
            Report("正在保存包草稿…");
        }
        catch (Exception error) { afterFormClose = null; Report(Unexpected(error)); }
    }

    private DraftTarget.PackageDefinition CreateDraftTarget(DefinitionDraft definition) =>
        new(package!.Package, Address(definition).Kind, Address(definition).LocalId, package.SourceVersion);

    internal static AuthoringDefinitionAddress Address(DefinitionDraft draft) => draft switch
    {
        DefinitionDraft.Blueprint value => new(DefinitionKind.Blueprint, value.LocalId),
        DefinitionDraft.Voxel value => new(DefinitionKind.Voxel, value.Value.LocalId),
        DefinitionDraft.SettlementType value => new(DefinitionKind.SettlementType, value.Value.LocalId),
        DefinitionDraft.LairType value => new(DefinitionKind.LairType, value.Value.LocalId),
        DefinitionDraft.SettlementTemplate value => new(DefinitionKind.SettlementTemplate, value.Value.LocalId),
        _ => throw new ArgumentException("未知定义种类。", nameof(draft))
    };

    private SaveFailure? RequestCloseForm(Action after)
    {
        if (operation != 0 || creating is not null || (Form is not null && IsBusy)) return new("authoring_busy", "请等待创作操作结束。");
        if (Form is null) { after(); return null; }
        if (!formDirty) { CloseForm(); after(); return null; }
        if (afterFormClose is not null) return new("authoring_close_pending", "请先保存、放弃或取消当前表单。");
        afterFormClose = after; Changed?.Invoke(); return null;
    }

    internal void ResolveClose(LeaveChoice choice, DefinitionDraft? captured)
    {
        if (afterFormClose is null || IsBusy) return;
        if (choice == LeaveChoice.Cancel) { afterFormClose = null; Changed?.Invoke(); }
        else if (choice == LeaveChoice.Save) SaveForm(captured ?? throw new ArgumentNullException(nameof(captured)));
        else { var after = afterFormClose; CloseForm(); after(); }
    }

    private void CloseForm()
    {
        currentForm = null; pendingFeedback = null; FormDiagnostics = [];
        Form = null; afterFormClose = null; formDirty = false; captureIssue = null;
        TemplatePreview = null; host.EndEditing(); Changed?.Invoke();
    }

    internal void DeleteDefinition(AuthoringDefinitionAddress address)
    {
        if (!CanUse || IsBusy || package is null) return;
        if (editor.RequestClose(() => { edits[address] = new PackageEdit.DeleteDefinition(address); Report("已标记删除，写包前仍须校验。"); }) is { } failure) Report(failure.Message);
    }

    internal Lookup<int> PickPlot(SceneRay ray) => displayedFeedback is { } displayed && IsCurrent(displayed)
        && TemplatePreview is { } preview && preview.Revision == revision
        && preview.Content == host.Content?.Generation ? preview.PickPlot(ray) : new Lookup<int>.Missing();

    internal void Process()
    {
        if (disposed) return;
        ProcessWriteProgress();
        ProcessPackages();
        if (saving is { IsCompleted: true } saved)
        {
            saving = null;
            try
            {
                var value = saved.GetAwaiter().GetResult();
                if (!value.Result.Published) { afterFormClose = null; Report(value.Result.Message ?? "草稿保存失败。"); }
                else
                {
                    AcceptDraft(value.Draft);
                    if (revision == value.Draft.Revision) formDirty = false;
                    var after = afterFormClose;
                    if (after is not null && !formDirty) { CloseForm(); after(); }
                    else Report("包草稿已保存，尚未写入有效包。");
                }
            }
            catch (Exception error) { afterFormClose = null; Report(Unexpected(error)); }
        }
        ProcessFeedback();
    }

    internal void Report(string message) { Message = message; Changed?.Invoke(); }
    private void Reject(CoreProblem problem) =>
        Report(editor.LogRejected("authoring", "authoring_operation_failed", problem));
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        editor.CloseForm = null; editor.PackageDraftSaved -= AcceptDraft;
        host.OperationEnded -= OperationEnded;
    }

}
