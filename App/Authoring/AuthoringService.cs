using System.Collections.Immutable;
using Train.App.Editing;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.App.Authoring;

internal sealed record AuthoringTarget(PackageId Package, string Directory, bool IsCore);

internal sealed class AuthoringService : IDisposable
{
    private readonly HostCoordinator host;
    private readonly EditorService editor;
    private readonly IScenesFactory scenes;
    private readonly bool development;
    private readonly string? coreDirectory;
    private readonly Dictionary<AuthoringDefinitionAddress, PackageEdit> edits = [];
    private Task<(PackageSnapshot Snapshot, IAuthoringPackage Package)>? opening;
    private Task<(SavedContentDraft Draft, FileWriteResult Result)>? saving;
    private Task<(DraftId Draft, EditRevision Revision, IContentView Content, SceneResult<ITemplatePreview> Result)>? previewing;
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
    internal bool CanUse => development && host.GetStatus() is { IsReplay: false };
    internal bool CanWriteCore => CanUse && coreDirectory is not null;
    internal bool IsBusy => opening is not null || saving is not null || operation != 0;
    internal IAuthoringPackage? Package => package;
    internal ImmutableArray<AuthoringDefinitionAddress> Definitions => package is null ? []
        : package.EditableDefinitions.Concat(edits.Keys).Distinct().ToImmutableArray();
    internal AuthoringTarget? Target { get; private set; }
    internal DefinitionDraft? Form { get; private set; }
    internal bool Leaving => afterFormClose is not null;
    internal ITemplatePreview? TemplatePreview { get; private set; }
    internal PackageWriteResult? LastWrite { get; private set; }
    internal string? Message { get; private set; }
    internal event Action? Changed;

    internal AuthoringService(HostCoordinator host, EditorService editor, IScenesFactory scenes,
        bool development, string modsDirectory, string? coreDirectory)
    {
        this.host = host; this.editor = editor; this.scenes = scenes;
        this.development = development;
        ModsDirectory = Path.GetFullPath(modsDirectory).TrimEnd(Path.DirectorySeparatorChar);
        this.coreDirectory = coreDirectory is null ? null : Path.GetFullPath(coreDirectory).TrimEnd(Path.DirectorySeparatorChar);
        editor.CloseForm = RequestCloseForm;
        editor.PackageDraftSaved += AcceptDraft;
        host.OperationEnded += OperationEnded;
    }

    internal void OpenCore() { if (coreDirectory is not null) Open(coreDirectory, new("core")); else Report("当前构建的内置包只读。"); }

    internal void Open(string directory, PackageId id)
    {
        if (!CanUse || IsBusy) { Report("创作工具仅供开发构建的普通游戏会话使用，请等待当前操作完成。"); return; }
        try
        {
            var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            var isCore = coreDirectory is not null && SamePath(full, coreDirectory) && id.Value == "core";
            if (!isCore && (!SamePath(Path.GetDirectoryName(full)!, ModsDirectory) || id.Value == "core"))
            { Report("请选择 user://mods 下的本地 Mod 包目录。"); return; }
            if (editor.RequestClose(() => StartOpen(new(id, full, isCore))) is { } failure)
                Report(failure.Message);
        }
        catch (Exception error) { Report(error.Message); }
    }

    private static bool SamePath(string first, string second) => string.Equals(first, second,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void StartOpen(AuthoringTarget target)
    {
        Target = target; binding = host.GetStatus()!.Binding;
        package = null; snapshot = null; edits.Clear(); assetEdits.Clear();
        opening = Task.Run(() =>
        {
            var source = PackageSnapshot.Capture(target.Directory);
            var result = host.ContentFactory.OpenAuthoringPackage(new("authoring"), target.Package, source.Version, source);
            return result is ContentResult<IAuthoringPackage>.Success success
                ? (source, success.Value) : throw new InvalidDataException(Describe(result));
        });
        Report("正在读取源包…");
    }

    internal void OpenDefinition(AuthoringDefinitionAddress address, bool grid = true)
    {
        if (!CanUse || IsBusy || package is null) return;
        if (editor.RequestClose(() =>
        {
            try
            {
                var definition = edits.TryGetValue(address, out var edit) && edit is PackageEdit.WriteDefinition write
                    ? write.Draft : package.Read(address) is ContentLookup<DefinitionDraft>.Found found
                        ? found.Value : throw new InvalidDataException("定义不存在或无法读取。");
                ShowDefinition(definition, new(Guid.NewGuid()), grid);
            }
            catch (Exception error) { Report(error.Message); }
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
            TemplatePreview = null; Changed?.Invoke();
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

    internal void MarkFormDirty() { formDirty = true; revision = new(checked(revision.Value + 1)); TemplatePreview = null; Changed?.Invoke(); }

    internal void SaveForm(DefinitionDraft value)
    {
        if (!CanUse || IsBusy || Form is null) return;
        try
        {
            var saved = new SavedContentDraft(draftId, revision, CreateDraftTarget(value), new DraftPayload.Definition(value));
            saving = Task.Run(() => (saved, editor.WriteExternalDraft(saved)));
            Report("正在保存包草稿…");
        }
        catch (Exception error) { afterFormClose = null; Report(error.Message); }
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

    private void AcceptDraft(SavedContentDraft draft)
    {
        if (package is null || draft.Target is not DraftTarget.PackageDefinition target || target.Package != package.Package || target.SourceVersion != package.SourceVersion) return;
        if (draft.Payload is not DraftPayload.Definition definition) throw new ArgumentException("未知包草稿载荷。", nameof(draft));
        edits[Address(definition.Value)] = new PackageEdit.WriteDefinition(definition.Value);
        Changed?.Invoke();
    }

    private SaveFailure? RequestCloseForm(Action after)
    {
        if (operation != 0 || (Form is not null && IsBusy)) return new("authoring_busy", "请等待创作操作结束。");
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
        Form = null; afterFormClose = null; formDirty = false;
        TemplatePreview = null; host.EndEditing(); Changed?.Invoke();
    }

    internal void DeleteDefinition(AuthoringDefinitionAddress address)
    {
        if (!CanUse || IsBusy || package is null) return;
        if (editor.RequestClose(() => { edits[address] = new PackageEdit.DeleteDefinition(address); Report("已标记删除，写包前仍须校验。"); }) is { } failure) Report(failure.Message);
    }

    internal void AddAsset(string relativePath, string source)
    {
        if (!CanUse || IsBusy || package is null) return;
        try { assetEdits.Add(new PackageEdit.WriteAsset(relativePath, [.. File.ReadAllBytes(source)])); Report("资产已加入待写入修改。"); }
        catch (Exception error) { Report(error.Message); }
    }
    private readonly List<PackageEdit> assetEdits = [];

    internal void Publish()
    {
        if (!CanUse || IsBusy || package is null || snapshot is null || Target is null) { Report("当前没有可写的源包，或仍有操作进行中。"); return; }
        if (editor.RequestClose(BeginWrite) is { } failure) Report(failure.Message);
    }

    private void BeginWrite()
    {
        if (edits.Count == 0 && assetEdits.Count == 0) { Report("没有已保存的包修改。"); return; }
        var fixedPackage = package!; var fixedSource = snapshot!; var target = Target!;
        var changes = edits.Values.Concat(assetEdits).ToImmutableArray();
        written = null; LastWrite = null;
        var begun = host.BeginPackageWrite((request, files, assets) =>
        {
            var planResult = host.ContentFactory.PreparePackageWrite(fixedPackage, changes);
            if (planResult is not ContentResult<PackageWritePlan>.Success planned) return FailureCandidate(planResult);
            var plan = planned.Value;
            if (plan.Package != fixedPackage.Package || plan.SourceVersion != fixedSource.Version)
                throw new InvalidDataException("补丁与固定源包不匹配。");
            if (plan.Diagnostics.Any(d => d.Severity == ContentDiagnosticSeverity.Error))
                return new ContentResult<IContentCandidate>.Failure(plan.Diagnostics);
            var root = target.IsCore ? request.Installation.BuiltIn : request.Installation.Mods;
            var prefix = target.IsCore ? "" : Path.GetFileName(target.Directory);
            var checkedLoad = host.ContentFactory.PrepareLoad(request,
                new PackageSourceFiles(files, fixedSource.WithChanges(plan.Changes), root, prefix), assets);
            if (checkedLoad is not ContentResult<IContentCandidate>.Success candidate) return checkedLoad;
            using (var check = candidate.Value)
            {
                var status = check.Report.Packages.FirstOrDefault(p => p.Metadata?.Id == target.Package);
                if (status?.State != PackageState.Available)
                    return new ContentResult<IContentCandidate>.Failure(status?.Diagnostics ?? [new("authoring_package_unavailable",
                        ContentDiagnosticSeverity.Error, "authoring", null, new(target.Package, ""), "package", null,
                        "校验结果中没有目标源包，未写入文件。")]);
            }
            written = PackageWriter.Execute(fixedSource, plan, target.IsCore);
            if (!written.Success)
                return new ContentResult<IContentCandidate>.Failure([new(
                    written.Failed is null ? "stale_basis" : "package_write_failed",
                    ContentDiagnosticSeverity.Error, "authoring", null,
                    new(target.Package, written.Failed?.RelativePath ?? ""), "package", null,
                    written.Message ?? "包写入失败。")]);
            return host.ContentFactory.PrepareLoad(request, files, assets);
        });
        if (begun.Problem is { } problem) Reject(problem);
        else { operation = begun.Operation; Report("正在校验候选包并逐文件写入；期间请勿使用外部工具修改此包。"); }
    }

    private static ContentResult<IContentCandidate> FailureCandidate<T>(ContentResult<T> result) where T : notnull => result switch
    {
        ContentResult<T>.Failure failure => new ContentResult<IContentCandidate>.Failure(failure.Diagnostics),
        ContentResult<T>.Faulted failure => new ContentResult<IContentCandidate>.Faulted(failure.Code, failure.Message, failure.StackTrace),
        _ => throw new InvalidOperationException()
    };

    private void OperationEnded(HostOperationResult result)
    {
        if (operation == 0 || result.Operation != operation) return;
        operation = 0; LastWrite = written;
        var changedFiles = written is { Completed.IsEmpty: false };
        if (changedFiles) { package = null; snapshot = null; }
        if (changedFiles || written?.Success == true) { edits.Clear(); assetEdits.Clear(); }
        Report((written is null ? "文件尚未写入。" : !changedFiles ? "未写入任何文件。" : written.Success ? "文件已写入。" : "写入已停止，已完成项保留。")
            + (result.Succeeded ? "普通重载成功；草稿保留。" : "内存内容未切换；草稿保留。 " + result.Message + " "
                + string.Join("；", result.Diagnostics.Select(EditorService.Describe).Concat(result.Problems.Select(problem => CommandDiagnostics.Describe(problem, host.Content)))))
            + (changedFiles ? "请重新载入源包后继续创作。" : ""));
    }

    internal Task PreviewTemplate(DefinitionDraft value)
    {
        if (!CanUse || Form is null || IsBusy) return Task.CompletedTask;
        try
        {
            if (value is not DefinitionDraft.SettlementTemplate template) return Task.CompletedTask;
            var fixedRevision = revision; var content = host.Content!; var fixedDraft = draftId;
            previewing = Task.Run(() => (fixedDraft, fixedRevision, content, scenes.PreviewTemplate(content, template.Value, fixedRevision)));
            Report("正在准备模板预览…");
            return previewing;
        }
        catch (Exception error) { Report(error.Message); }
        return Task.CompletedTask;
    }

    internal Lookup<int> PickPlot(SceneRay ray) => TemplatePreview is { } preview && preview.Revision == revision
        && preview.Content == host.Content?.Generation ? preview.PickPlot(ray) : new Lookup<int>.Missing();

    internal void Process()
    {
        if (disposed) return;
        if (opening is { IsCompleted: true } opened)
        {
            opening = null;
            try { var value = opened.GetAwaiter().GetResult(); if (binding != host.GetStatus()?.Binding) return;
                snapshot = value.Snapshot; package = value.Package; edits.Clear(); assetEdits.Clear(); Report("源包已载入。最终复核到写入结束期间，请确保此包只有一个写入者。"); }
            catch (Exception error) { package = null; snapshot = null; Report(error.Message); }
        }
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
            catch (Exception error) { afterFormClose = null; Report(error.Message); }
        }
        if (previewing is { IsCompleted: true } preview)
        {
            previewing = null;
            try
            {
                var value = preview.GetAwaiter().GetResult();
                if (Form is not DefinitionDraft.SettlementTemplate || value.Draft != draftId || value.Revision != revision || !ReferenceEquals(value.Content, host.Content)) return;
                if (value.Result is SceneResult<ITemplatePreview>.Success success && success.Value.Revision == revision && success.Value.Content == value.Content.Generation)
                { TemplatePreview = success.Value; Report(string.Join("；", success.Value.Diagnostics.Select(EditorService.Describe))); }
                else Report(value.Result.ToString() ?? "模板预览失败。");
            }
            catch (Exception error) { Report(error.Message); }
        }
    }

    internal void Report(string message) { Message = message; Changed?.Invoke(); }
    private void Reject(CoreProblem problem) =>
        Report(editor.LogRejected("authoring", "authoring_operation_failed", problem));
    private static string Describe<T>(ContentResult<T> result) where T : notnull => result switch
    { ContentResult<T>.Failure failure => string.Join("；", failure.Diagnostics.Select(d => EditorService.Describe(d))),
        ContentResult<T>.Faulted failure => failure.Message, _ => "操作成功。" };

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        editor.CloseForm = null; editor.PackageDraftSaved -= AcceptDraft;
        host.OperationEnded -= OperationEnded;
    }

    private sealed class PackageSourceFiles(IContentFiles installation, IContentFiles source, ContentRoot root, string prefix) : IContentFiles
    {
        private bool Map(ContentPath path, out ContentPath local)
        {
            local = path;
            if (path.Root != root) return false;
            if (prefix.Length == 0) return true;
            if (path.RelativePath == prefix) { local = path with { RelativePath = "" }; return true; }
            if (!path.RelativePath.StartsWith(prefix + "/", StringComparison.Ordinal)) return false;
            local = path with { RelativePath = path.RelativePath[(prefix.Length + 1)..] }; return true;
        }
        public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory) =>
            Map(directory, out var local) ? source.Enumerate(local) : installation.Enumerate(directory);
        public ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes) =>
            Map(file, out var local) ? source.Read(local, maximumBytes) : installation.Read(file, maximumBytes);
    }
}
