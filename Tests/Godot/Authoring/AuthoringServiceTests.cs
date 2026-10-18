using System.Collections.Immutable;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Assets;
using Train.App.Authoring;
using Train.App.Editing;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Godot.Tests.Host;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;

namespace Train.Godot.Tests.Authoring;

[TestClass]
[TestCategory("integration")]
public sealed class AuthoringServiceTests
{
    [TestMethod]
    public void Development_requires_active_game_and_exported_core_is_read_only()
    {
        using var fixture = new AuthoringFixture(ready: false);
        Assert.IsFalse(fixture.Service.CanUse);
        fixture.Start();
        Assert.IsTrue(fixture.Service.CanUse);
        using var release = new AuthoringService(fixture.Coordinator, fixture.Editor, fixture.Scenes, false, fixture.Mods, fixture.Core);
        Assert.IsFalse(release.CanUse);
        release.OpenCore();
        Assert.IsNull(release.Target);
        using var exported = new AuthoringService(fixture.Coordinator, fixture.Editor, fixture.Scenes, true, fixture.Mods, null);
        Assert.IsFalse(exported.CanWriteCore);
        exported.OpenCore();
        StringAssert.Contains(exported.Message!, "只读");
        Assert.IsNull(exported.Target);
    }

    [TestMethod]
    public void Grid_blueprint_draft_event_becomes_write_definition_and_reload_uses_disk()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open();
        fixture.Service.OpenDefinition(new(DefinitionKind.Blueprint, "blueprint"));
        fixture.Pump(() => fixture.Editor.IsOpen && !fixture.Editor.IsBusy);
        Assert.IsInstanceOfType<DraftTarget.PackageDefinition>(fixture.Editor.Target);
        var changed = Draft("changed in grid");
        fixture.Scenes.Session!.Change(changed);
        fixture.Editor.Process();
        fixture.Editor.SaveDraft();
        fixture.Pump(() => !fixture.Editor.IsBusy);
        Assert.AreEqual(1, fixture.Service.Drafts().Length);
        fixture.ConfigureWrite();
        fixture.Service.Publish();
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.AreEqual(1, fixture.Edits.Length);
        Assert.AreEqual(changed, ((DefinitionDraft.Blueprint)((PackageEdit.WriteDefinition)fixture.Edits[0]).Draft).Value);
        Assert.IsTrue(fixture.Service.LastWrite!.Success);
        Assert.AreEqual(0, fixture.CheckCandidate!.Activations);
        Assert.AreEqual(1, fixture.CheckCandidate.Disposals);
        Assert.AreEqual(1, fixture.ReloadCandidate!.Activations);
        Assert.AreSame(fixture.ReloadCandidate.View, fixture.Coordinator.Content);
        Assert.AreEqual(2, fixture.LoadCalls);
        Assert.AreEqual("written", File.ReadAllText(fixture.Output));
    }

    [TestMethod]
    public void External_source_change_refuses_all_writes_and_retains_draft()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open();
        fixture.SaveForm();
        File.WriteAllText(Path.Combine(fixture.Core, "source.txt"), "external");
        fixture.ConfigureWrite();
        fixture.Service.Publish();
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsFalse(fixture.Service.LastWrite!.Success);
        Assert.AreEqual(0, fixture.Service.LastWrite.Completed.Length);
        Assert.IsFalse(File.Exists(fixture.Output));
        Assert.AreEqual(1, fixture.LoadCalls);
        Assert.AreSame(fixture.Host.Content, fixture.Coordinator.Content);
        Assert.AreEqual(1, fixture.Service.Drafts().Length);
        StringAssert.Contains(fixture.Service.Message!, "未写入任何文件");
    }

    [TestMethod]
    public void Disk_write_survives_reload_rejection_while_old_content_and_draft_remain()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open();
        fixture.SaveForm();
        var savedDraft = fixture.Service.Drafts().Single();
        fixture.ConfigureWrite(rejectReload: true);
        fixture.Service.Publish();
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsTrue(fixture.Service.LastWrite!.Success);
        Assert.AreEqual("written", File.ReadAllText(fixture.Output));
        Assert.AreSame(fixture.Host.Content, fixture.Coordinator.Content);
        Assert.IsTrue(File.Exists(fixture.DraftPath(savedDraft.Id)));
        Assert.IsNull(fixture.Service.Package);
        StringAssert.Contains(fixture.Service.Message!, "内存内容未切换");
        StringAssert.Contains(fixture.Service.Message!, "reload rejected");
        Assert.AreEqual(HostOperationStatus.Rejected, fixture.LastOperation!.Status);
    }

    [TestMethod]
    public void Excluded_target_is_rejected_with_its_diagnostics_and_candidate_is_disposed()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.SaveForm();
        var diagnostic = Diagnostic("target-dependency-missing", "目标包的依赖不可用");
        var status = new PackageStatus(new(new("core"), "core", "1.0.0", [], []), new("builtin"), PackageState.DependencyUnavailable, [diagnostic]);
        fixture.ConfigureWrite(report: new([status], [], [], [diagnostic]));
        fixture.Service.Publish();
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.AreEqual(HostOperationStatus.Rejected, fixture.LastOperation!.Status);
        CollectionAssert.AreEqual(new[] { diagnostic }, fixture.LastOperation.Diagnostics.ToArray());
        Assert.AreEqual(1, fixture.CheckCandidate!.Disposals);
        Assert.AreEqual(0, fixture.CheckCandidate.Activations);
        Assert.AreEqual(1, fixture.LoadCalls);
        Assert.IsFalse(File.Exists(fixture.Output));
        Assert.IsNull(fixture.Service.LastWrite);
        StringAssert.Contains(fixture.Service.Message!, diagnostic.Message);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Available_target_can_be_written_despite_unrelated_mod_or_user_library_errors(bool userLibrary)
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.SaveForm();
        var target = new PackageStatus(new(new("core"), "core", "1.0.0", [], []), new("builtin"), PackageState.Available, []);
        var diagnostic = Diagnostic(userLibrary ? "bad-user-blueprint" : "bad-unrelated-mod", "无关条目不可用")
            with { Source = null };
        var unrelated = new PackageStatus(new(new("7ce365e4-ec17-4fef-9451-96a0d6f20373"), "bad mod", "1.0.0", [], []), new("mods"), PackageState.Invalid, [diagnostic]);
        fixture.ConfigureWrite(report: new(userLibrary ? [target] : [target, unrelated], [], [], [diagnostic]));
        fixture.Service.Publish();
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsTrue(fixture.LastOperation!.Succeeded);
        Assert.IsTrue(fixture.Service.LastWrite!.Success);
        Assert.AreEqual("written", File.ReadAllText(fixture.Output));
        Assert.AreEqual(1, fixture.CheckCandidate!.Disposals);
        Assert.AreEqual(0, fixture.CheckCandidate.Activations);
        Assert.AreEqual(1, fixture.ReloadCandidate!.Activations);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Candidate_validation_does_not_snapshot_locked_unrelated_mod_files()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.SaveForm(); fixture.ConfigureWrite();
        var unrelated = Path.Combine(fixture.Mods, "7ce365e4-ec17-4fef-9451-96a0d6f20373");
        Directory.CreateDirectory(unrelated);
        var lockedPath = Path.Combine(unrelated, "unreadable.txt");
        File.WriteAllText(lockedPath, "unrelated");
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var dependency = Path.Combine(unrelated, "live.txt");
        File.WriteAllText(dependency, "old");
        var prepare = fixture.Host.Factory.PrepareLoadCall!;
        fixture.Host.Factory.PrepareLoadCall = (request, files, assets) =>
        {
            File.WriteAllText(dependency, "current");
            var bytes = ContentFilesSnapshot.Require(files.Read(new(new("mods"), "7ce365e4-ec17-4fef-9451-96a0d6f20373/live.txt"), 99));
            Assert.AreEqual("current", Encoding.UTF8.GetString(bytes.AsSpan()), "非目标包必须读取实时安装文件。");
            return prepare(request, files, assets);
        };
        fixture.Service.Publish(); fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsTrue(fixture.Service.LastWrite!.Success);
        Assert.IsTrue(fixture.LastOperation!.Succeeded);
        Assert.AreEqual("written", File.ReadAllText(fixture.Output));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Preflight_failure_and_fault_keep_the_original_rejection_or_fault(bool faulted)
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.SaveForm(); fixture.ConfigureWrite();
        var diagnostic = Diagnostic("preflight-rejected", "候选被拒绝");
        fixture.Host.Factory.PrepareLoadCall = (_, _, _) => faulted
            ? new ContentResult<IContentCandidate>.Faulted("preflight-fault", "原始故障", "original-stack")
            : new ContentResult<IContentCandidate>.Failure([diagnostic]);
        fixture.Service.Publish();
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.AreEqual(faulted ? HostOperationStatus.Faulted : HostOperationStatus.Rejected, fixture.LastOperation!.Status);
        if (faulted) StringAssert.Contains(fixture.Service.Message!, "原始故障");
        else CollectionAssert.AreEqual(new[] { diagnostic }, fixture.LastOperation.Diagnostics.ToArray());
        Assert.IsFalse(File.Exists(fixture.Output));
        Assert.IsNull(fixture.Service.LastWrite);
    }

    [TestMethod]
    public void A_written_package_requires_reload_and_saved_draft_can_explicitly_use_the_new_source()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.SaveForm();
        var saved = fixture.Service.Drafts().Single();
        var previous = ((DraftTarget.PackageDefinition)saved.Target).SourceVersion;
        fixture.ConfigureWrite(); fixture.Service.Publish(); fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsNull(fixture.Service.Package);
        Assert.IsTrue(File.Exists(fixture.DraftPath(saved.Id)));
        fixture.Service.OpenDefinition(new(DefinitionKind.Blueprint, "blueprint"));
        Assert.IsFalse(fixture.Editor.IsOpen);
        fixture.Open();
        Assert.AreNotEqual(previous, fixture.Service.Package!.SourceVersion);
        fixture.Service.OpenDraft(saved);
        Assert.IsFalse(fixture.Editor.IsOpen);
        StringAssert.Contains(fixture.Service.Message!, "不会自动合并");
        fixture.Service.OpenDraft(saved, useCurrentSource: true);
        fixture.Pump(() => fixture.Editor.IsOpen && !fixture.Editor.IsBusy);
        Assert.AreEqual(fixture.Service.Package.SourceVersion, ((DraftTarget.PackageDefinition)fixture.Editor.Target!).SourceVersion);
        Assert.AreEqual(((DefinitionDraft.Blueprint)((DraftPayload.Definition)saved.Payload).Value).Value, fixture.Editor.State!.Snapshot.Draft);
        Assert.IsTrue(fixture.Editor.IsDirty);
        fixture.Editor.RequestClose(() => { });
        Assert.IsTrue(fixture.Editor.Leaving);
        fixture.Editor.ResolveClose(LeaveChoice.Cancel);
        fixture.Editor.SaveDraft(); fixture.Pump(() => !fixture.Editor.IsBusy);
        Assert.IsFalse(fixture.Editor.IsDirty);
        var resaved = fixture.Service.Drafts().Single();
        Assert.AreEqual(saved.Id, resaved.Id);
        Assert.AreEqual(fixture.Service.Package.SourceVersion, ((DraftTarget.PackageDefinition)resaved.Target).SourceVersion);
        fixture.ConfigureWrite(); fixture.Service.Publish(); fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsTrue(fixture.Service.LastWrite!.Success);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Partial_write_invalidates_source_and_retains_the_actual_package_draft()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.SaveForm();
        var saved = fixture.Service.Drafts().Single();
        using var locked = new FileStream(Path.Combine(fixture.Core, "source.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
        fixture.ConfigureWrite();
        fixture.Host.Factory.PreparePackageWriteCall = (basis, _) => new ContentResult<PackageWritePlan>.Success(new(basis.Package, basis.SourceVersion,
            [new("output.txt", ImmutableArray.CreateRange(Encoding.UTF8.GetBytes("written"))), new("source.txt", ImmutableArray.CreateRange(Encoding.UTF8.GetBytes("blocked")))], []));
        fixture.Service.Publish(); fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsFalse(fixture.Service.LastWrite!.Success);
        Assert.AreEqual("output.txt", fixture.Service.LastWrite.Completed.Single().RelativePath);
        Assert.IsNull(fixture.Service.Package);
        Assert.IsTrue(File.Exists(fixture.DraftPath(saved.Id)));
        Assert.AreSame(fixture.Host.Content, fixture.Coordinator.Content);
        StringAssert.Contains(fixture.Service.Message!, "请重新载入源包");
    }

    [TestMethod]
    public void Reading_package_without_a_form_does_not_block_editor_close()
    {
        using var fixture = new AuthoringFixture();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var open = fixture.Host.Factory.OpenAuthoringPackageCall!;
        fixture.Host.Factory.OpenAuthoringPackageCall = (root, package, version, files) =>
        {
            started.Set();
            Assert.IsTrue(release.Wait(HostFixture.HangTimeout), "读取源包未获释放。");
            return open(root, package, version, files);
        };
        try
        {
            fixture.Service.OpenCore(); HostFixture.WaitForSignal(started, "读取源包开始");
            var closed = false;
            Assert.IsNull(fixture.Editor.RequestClose(() => closed = true));
            Assert.IsTrue(closed);
        }
        finally { release.Set(); }
        fixture.Pump(() => !fixture.Service.IsBusy);
    }

    [TestMethod]
    public void Form_save_encoding_failure_keeps_input_and_does_not_continue_close()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open();
        fixture.Service.OpenDefinition(new(DefinitionKind.Blueprint, "blueprint"), grid: false);
        fixture.Service.MarkFormDirty();
        fixture.Host.Factory.EncodeDraftCall = _ => new ContentResult<string>.Failure([Diagnostic("draft-invalid", "表单草稿编码失败")]);
        var continued = false;
        Assert.IsNull(fixture.Editor.RequestClose(() => continued = true));
        fixture.Service.ResolveClose(LeaveChoice.Save, Blueprint("unsaved form"));
        Assert.IsNotNull(fixture.Editor.RequestClose(() => continued = true), "保存仍进行时不能关闭表单。");
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsFalse(continued);
        Assert.IsNotNull(fixture.Service.Form);
        Assert.AreEqual(0, fixture.Service.Drafts().Length);
        StringAssert.Contains(fixture.Service.Message!, "表单草稿编码失败");
        Assert.IsNull(fixture.Editor.RequestClose(() => continued = true));
        Assert.IsTrue(fixture.Service.Leaving, "保存失败不能清掉未保存输入。");
    }

    [TestMethod]
    public void Passive_draft_refresh_after_loaded_package_grid_save_failure_keeps_editor_feedback()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open();
        fixture.Editor.Changed += () => fixture.Service.Drafts();
        fixture.Service.OpenDefinition(new(DefinitionKind.Blueprint, "blueprint"));
        fixture.Pump(() => fixture.Editor.IsOpen && !fixture.Editor.IsBusy);
        fixture.Host.Factory.EncodeDraftCall = _ => new ContentResult<string>.Failure([Diagnostic("draft-invalid", "网格草稿编码失败")]);
        fixture.Editor.SaveDraft(); fixture.Pump(() => !fixture.Editor.IsBusy);
        StringAssert.Contains(fixture.Editor.Message!, "网格草稿编码失败");
    }

    [TestMethod]
    public void Native_unimplemented_open_reports_original_exception()
    {
        using var fixture = new AuthoringFixture();
        fixture.Host.Factory.OpenAuthoringPackageCall = (_, _, _, _) => throw new NotImplementedException("native authoring unavailable");
        fixture.Service.OpenCore();
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsNull(fixture.Service.Package);
        StringAssert.Contains(fixture.Service.Message!, "native authoring unavailable");
    }

    [TestMethod]
    public void Native_unimplemented_write_reports_original_exception_without_writing()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open();
        fixture.SaveForm();
        fixture.Host.Factory.PreparePackageWriteCall = (_, _) => throw new NotImplementedException("native package encoder unavailable");
        fixture.Service.Publish();
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsFalse(File.Exists(fixture.Output));
        Assert.AreSame(fixture.Host.Content, fixture.Coordinator.Content);
        Assert.AreEqual(1, fixture.Service.Drafts().Length);
        StringAssert.Contains(fixture.Service.Message!, "native package encoder unavailable");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void Form_close_save_discard_cancel_preserves_the_chosen_boundary(int choiceValue)
    {
        var choice = (LeaveChoice)choiceValue;
        using var fixture = new AuthoringFixture();
        fixture.Open();
        fixture.Service.OpenDefinition(new(DefinitionKind.Blueprint, "blueprint"), grid: false);
        fixture.Service.MarkFormDirty();
        var continued = 0;
        Assert.IsNull(fixture.Editor.RequestClose(() => continued++));
        Assert.IsTrue(fixture.Service.Leaving);
        fixture.Service.ResolveClose(choice, choice == LeaveChoice.Save ? Blueprint("captured form") : null);
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.AreEqual(choice == LeaveChoice.Cancel ? 0 : 1, continued);
        Assert.AreEqual(choice == LeaveChoice.Cancel, fixture.Service.Form is not null);
        Assert.AreEqual(choice == LeaveChoice.Save ? 1 : 0, fixture.Service.Drafts().Length);
        if (choice == LeaveChoice.Save)
            Assert.AreEqual(Blueprint("captured form"), ((DraftPayload.Definition)fixture.Service.Drafts()[0].Payload).Value);
    }

    [TestMethod]
    public void Preview_with_stale_revision_is_not_accepted()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open();
        fixture.Service.NewDefinition(new DefinitionDraft.SettlementTemplate(new("template", null, null, null, [], [], [], [])));
        fixture.Scenes.PreviewTemplateCall = (content, _, revision) => new SceneResult<ITemplatePreview>.Success(new FixedTemplatePreview(content.Generation, new(revision.Value + 1)));
        fixture.Service.PreviewTemplate(fixture.Service.Form!).GetAwaiter().GetResult();
        fixture.Pump(() => fixture.Service.Message != "正在准备模板预览…");
        Assert.IsNull(fixture.Service.TemplatePreview);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Reopening_same_definition_after_close_save_uses_just_saved_value(bool grid)
    {
        using var fixture = new AuthoringFixture();
        fixture.Open();
        var address = new AuthoringDefinitionAddress(DefinitionKind.Blueprint, "blueprint");
        fixture.Service.OpenDefinition(address, grid: false);
        var changed = Blueprint("saved while reopening");
        fixture.Service.MarkFormDirty();
        fixture.Service.OpenDefinition(address, grid);
        Assert.IsTrue(fixture.Service.Leaving);
        fixture.Service.ResolveClose(LeaveChoice.Save, changed);
        fixture.Pump(() => !fixture.Service.IsBusy && !fixture.Editor.IsBusy);
        Assert.AreEqual(changed, ((DraftPayload.Definition)fixture.Service.Drafts().Single().Payload).Value);
        if (grid)
            Assert.AreEqual(changed.Value, fixture.Editor.State!.Snapshot.Draft);
        else
            Assert.AreEqual(changed, fixture.Service.Form);
    }

    [TestMethod]
    public void Delayed_preview_for_closed_draft_is_rejected_when_new_draft_has_same_revision()
    {
        using var fixture = new AuthoringFixture();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Open();
        fixture.Service.NewDefinition(new DefinitionDraft.SettlementTemplate(new("old-template", null, null, null, [], [], [], [])));
        fixture.Scenes.PreviewTemplateCall = (content, draft, revision) =>
        {
            Assert.AreEqual("old-template", draft.LocalId);
            Assert.AreEqual(new EditRevision(0), revision);
            started.Set();
            Assert.IsTrue(release.Wait(HostFixture.HangTimeout), "旧模板预览未获释放。");
            return new SceneResult<ITemplatePreview>.Success(new FixedTemplatePreview(content.Generation, revision));
        };
        Task? preview = null;
        try
        {
            preview = fixture.Service.PreviewTemplate(fixture.Service.Form!);
            HostFixture.WaitForSignal(started, "旧模板预览已开始");
            var closed = false;
            Assert.IsNull(fixture.Editor.RequestClose(() => closed = true));
            fixture.Service.ResolveClose(LeaveChoice.Discard, null);
            Assert.IsTrue(closed);
            var next = new DefinitionDraft.SettlementTemplate(new("new-template", null, null, null, [], [], [], []));
            fixture.Service.NewDefinition(next);
            Assert.AreEqual(next, fixture.Service.Form);
        }
        finally { release.Set(); }
        Assert.IsTrue(preview!.Wait(HostFixture.HangTimeout), "旧模板预览未完成。");
        fixture.Service.Process();
        Assert.IsNull(fixture.Service.TemplatePreview);
    }

    private static DesignDraft Draft(string name) => new(new(name, ""), new DesignSpecification.SceneModel(null), new([], [], []));
    private static DefinitionDraft.Blueprint Blueprint(string name) => new("blueprint", Draft(name));
    private sealed class AuthoringFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "train-authoring-service-" + Guid.NewGuid().ToString("N"));
        private readonly GodotMainThreadDispatcher dispatcher = new();
        internal HostFixture Host { get; } = new();
        internal HostCoordinator Coordinator { get; }
        internal EditorScenes Scenes { get; } = new();
        internal EditorService Editor { get; }
        internal AuthoringService Service { get; }
        internal string Core => Path.Combine(root, "core");
        internal string Mods => Path.Combine(root, "mods");
        internal string Output => Path.Combine(Core, "output.txt");
        internal ImmutableArray<PackageEdit> Edits { get; private set; }
        internal ScriptedContentCandidate? CheckCandidate { get; private set; }
        internal ScriptedContentCandidate? ReloadCandidate { get; private set; }
        internal int LoadCalls { get; private set; }
        internal HostOperationResult? LastOperation { get; private set; }
        internal string DraftPath(DraftId id) => Path.Combine(AuthoringPaths.PackageDraftsDirectory(root, new("core")), id.Value.ToString("D") + ".json");
        internal AuthoringFixture(bool ready = true)
        {
            Directory.CreateDirectory(Core);
            File.WriteAllText(Path.Combine(Core, "source.txt"), "original");
            var files = new ContentFiles(dispatcher, new Dictionary<ContentRoot, ContentRootLocation>
            { [new("builtin")] = new ContentRootLocation.Disk(Core), [new("mods")] = new ContentRootLocation.Disk(Mods) });
            Coordinator = new(Host.Core, Host.Factory, files, new UnconfiguredAssetInspector(), new(new("builtin"), new("mods"), null, "test", []), Host.Logger, sessionOptions: HostFixture.Options);
            Editor = new(Coordinator, Scenes, new FileSaveStore(root), Host.Logger);
            Service = new(Coordinator, Editor, Scenes, true, Mods, Core);
            Coordinator.OperationEnded += result => LastOperation = result;
            Host.Factory.OpenAuthoringPackageCall = (_, id, version, _) => new ContentResult<IAuthoringPackage>.Success(new FixedAuthoringPackage(id, version, [Blueprint("original")]));
            Host.Factory.EvaluateDesignCall = (_, draft) => new ContentResult<DesignEvaluation>.Success(new DesignEvaluation.Complete(new("design"), new("composition"), draft, null, new(null, null, null, true, null, null, [], [], [], [], []), true, []));
            SavedContentDraft? encoded = null;
            Host.Factory.EncodeDraftCall = draft => { encoded = draft; return new ContentResult<string>.Success("opaque draft"); };
            Host.Factory.DecodeDraftCall = text => text == "opaque draft" && encoded is not null ? new ContentResult<SavedContentDraft>.Success(encoded) : throw new InvalidOperationException("UNCONFIGURED: draft payload");
            if (ready) Start();
        }
        internal void Start()
        {
            Assert.IsNull(Coordinator.BeginRestore("core-state", "content-state", HostFixture.Options).Problem);
            Pump(() => Coordinator.GetStatus() is not null);
        }
        internal void Open() { Service.OpenCore(); Pump(() => !Service.IsBusy); Assert.IsNotNull(Service.Package); }
        internal void SaveForm()
        {
            Service.OpenDefinition(new(DefinitionKind.Blueprint, "blueprint"), grid: false);
            Service.MarkFormDirty(); Service.SaveForm(Blueprint("saved form")); Pump(() => !Service.IsBusy);
        }
        internal void ConfigureWrite(bool rejectReload = false, ContentPreparationReport? report = null)
        {
            LoadCalls = 0;
            var initialOutput = File.Exists(Output) ? File.ReadAllText(Output) : null;
            Host.Factory.PreparePackageWriteCall = (basis, edits) =>
            { Edits = edits; return new ContentResult<PackageWritePlan>.Success(new(basis.Package, basis.SourceVersion, [new("output.txt", ImmutableArray.CreateRange(Encoding.UTF8.GetBytes("written")))], [])); };
            Host.Factory.PrepareLoadCall = (_, files, _) =>
            {
                LoadCalls++;
                Assert.AreEqual("written", Encoding.UTF8.GetString(ContentFilesSnapshot.Require(files.Read(new(new("builtin"), "output.txt"), 99)).AsSpan()));
                if (LoadCalls == 1)
                {
                    Assert.AreEqual(initialOutput, File.Exists(Output) ? File.ReadAllText(Output) : null, "预校验不得先写磁盘。");
                    var status = new PackageStatus(new(new("core"), "core", "1.0.0", [], []), new("builtin"), PackageState.Available, []);
                    CheckCandidate = new(Host.Content, Host.Content, ContentChange.Presentation, report ?? new([status], [], [], []));
                    return new ContentResult<IContentCandidate>.Success(CheckCandidate);
                }
                Assert.AreEqual(1, CheckCandidate!.Disposals);
                Assert.AreEqual(0, CheckCandidate.Activations);
                Assert.AreEqual("written", File.ReadAllText(Output));
                if (rejectReload) return new ContentResult<IContentCandidate>.Failure([Diagnostic("reload-rejected", "reload rejected")]);
                ReloadCandidate = new(new FixedContentView(Host.Content.Core, new(2)), Host.Content, ContentChange.Presentation);
                return new ContentResult<IContentCandidate>.Success(ReloadCandidate);
            };
        }
        internal void Pump(Func<bool> done) => Assert.IsTrue(SpinWait.SpinUntil(() => { Coordinator.ProcessFrame(0); Editor.Process(); Service.Process(); return done(); }, HostFixture.HangTimeout), "创作操作超时：" + Service.Message);
        public void Dispose() { Service.Dispose(); Editor.Dispose(); Coordinator.Dispose(); Host.Dispose(); dispatcher.Dispose(); Directory.Delete(root, true); }
    }
    private static ContentDiagnostic Diagnostic(string code, string message) =>
        new(code, ContentDiagnosticSeverity.Error, "loading", null, new(new("core"), "definitions/test.json"), "field", null, message);
}
