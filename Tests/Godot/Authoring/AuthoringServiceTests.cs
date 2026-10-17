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
        fixture.Scenes.Grid.Session!.Change(changed);
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
    }

    [TestMethod]
    public void Disk_write_survives_reload_rejection_while_old_content_and_draft_remain()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open();
        fixture.SaveForm();
        fixture.ConfigureWrite(rejectReload: true);
        fixture.Service.Publish();
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsTrue(fixture.Service.LastWrite!.Success);
        Assert.AreEqual("written", File.ReadAllText(fixture.Output));
        Assert.AreSame(fixture.Host.Content, fixture.Coordinator.Content);
        Assert.AreEqual(1, fixture.Service.Drafts().Length);
        StringAssert.Contains(fixture.Service.Message!, "内存内容未切换");
        StringAssert.Contains(fixture.Service.Message!, "reload rejected");
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
        fixture.Service.CaptureForm = () => Blueprint("captured form");
        fixture.Service.MarkFormDirty();
        var continued = 0;
        Assert.IsNull(fixture.Editor.RequestClose(() => continued++));
        Assert.IsTrue(fixture.Service.Leaving);
        fixture.Service.ResolveClose(choice);
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
        fixture.Scenes.Preview = (content, _, revision) => new SceneResult<ITemplatePreview>.Success(new TemplatePreview(content.Generation, new(revision.Value + 1)));
        fixture.Service.PreviewTemplate();
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
        fixture.Service.CaptureForm = () => changed;
        fixture.Service.MarkFormDirty();
        fixture.Service.OpenDefinition(address, grid);
        Assert.IsTrue(fixture.Service.Leaving);
        fixture.Service.ResolveClose(LeaveChoice.Save);
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
        fixture.Scenes.Preview = (content, draft, revision) =>
        {
            Assert.AreEqual("old-template", draft.LocalId);
            Assert.AreEqual(new EditRevision(0), revision);
            started.Set();
            Assert.IsTrue(release.Wait(HostFixture.HangTimeout), "旧模板预览未获释放。");
            return new SceneResult<ITemplatePreview>.Success(new TemplatePreview(content.Generation, revision));
        };
        try
        {
            fixture.Service.PreviewTemplate();
            HostFixture.WaitForSignal(started, "旧模板预览已开始");
            Assert.IsTrue(fixture.Service.IsPreviewing);
            var closed = false;
            Assert.IsNull(fixture.Editor.RequestClose(() => closed = true));
            fixture.Service.ResolveClose(LeaveChoice.Discard);
            Assert.IsTrue(closed);
            var next = new DefinitionDraft.SettlementTemplate(new("new-template", null, null, null, [], [], [], []));
            fixture.Service.NewDefinition(next);
            Assert.AreEqual(next, fixture.Service.Form);
        }
        finally { release.Set(); }
        fixture.Pump(() => !fixture.Service.IsPreviewing);
        Assert.IsNull(fixture.Service.TemplatePreview);
    }

    private static DesignDraft Draft(string name) => new(new(name, ""), new DesignSpecification.SceneModel(null), new([], [], []));
    private static DefinitionDraft.Blueprint Blueprint(string name) => new("blueprint", Draft(name));
    private sealed class AuthoringPackage(PackageId package, PackageSourceVersion version) : IAuthoringPackage
    {
        public PackageId Package => package;
        public PackageSourceVersion SourceVersion => version;
        public PackageMetadata Metadata { get; } = new(package, "test package", "1.0.0", [], []);
        public ImmutableArray<AuthoringDefinitionAddress> EditableDefinitions => [new(DefinitionKind.Blueprint, "blueprint")];
        public ContentLookup<DefinitionDraft> Read(AuthoringDefinitionAddress address) => address == EditableDefinitions[0]
            ? new ContentLookup<DefinitionDraft>.Found(Blueprint("original")) : new ContentLookup<DefinitionDraft>.NotFound();
    }
    private sealed class AuthoringScenes : IScenesFactory
    {
        internal EditorScenes Grid { get; } = new();
        internal Func<IContentView, SettlementTemplateDraft, EditRevision, SceneResult<ITemplatePreview>>? Preview { get; set; }
        public SceneResult<IDesignEditSession> OpenEditor(IContentView content, DesignEvaluation initial) => Grid.OpenEditor(content, initial);
        public SceneResult<IWorldSceneSession> OpenWorld(SceneBinding binding, SceneWorldSnapshot initial) => throw new InvalidOperationException("UNCONFIGURED: world");
        public SceneResult<IDesignPreview> PreviewDesign(IContentView content, DesignEvaluation initial) => throw new InvalidOperationException("UNCONFIGURED: design preview");
        public SceneResult<ITemplatePreview> PreviewTemplate(IContentView content, SettlementTemplateDraft draft, EditRevision revision) => (Preview ?? throw new InvalidOperationException("UNCONFIGURED: template preview"))(content, draft, revision);
    }
    private sealed class TemplatePreview(ContentGeneration content, EditRevision revision) : ITemplatePreview
    {
        public ContentGeneration Content => content;
        public EditRevision Revision => revision;
        public ISceneFrame Frame => throw new InvalidOperationException("UNCONFIGURED: frame");
        public ImmutableArray<TemplatePlotPreview> Plots => [];
        public ImmutableArray<SceneProblem> Diagnostics => [];
        public Lookup<int> PickPlot(SceneRay ray) => throw new InvalidOperationException("UNCONFIGURED: pick");
    }
    private sealed class AuthoringFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "train-authoring-service-" + Guid.NewGuid().ToString("N"));
        private readonly GodotMainThreadDispatcher dispatcher = new();
        internal HostFixture Host { get; } = new();
        internal HostCoordinator Coordinator { get; }
        internal AuthoringScenes Scenes { get; } = new();
        internal EditorService Editor { get; }
        internal AuthoringService Service { get; }
        internal string Core => Path.Combine(root, "core");
        internal string Mods => Path.Combine(root, "mods");
        internal string Output => Path.Combine(Core, "output.txt");
        internal ImmutableArray<PackageEdit> Edits { get; private set; }
        internal ScriptedContentCandidate? CheckCandidate { get; private set; }
        internal ScriptedContentCandidate? ReloadCandidate { get; private set; }
        internal int LoadCalls { get; private set; }
        internal AuthoringFixture(bool ready = true)
        {
            Directory.CreateDirectory(Core);
            File.WriteAllText(Path.Combine(Core, "source.txt"), "original");
            var files = new ContentFiles(dispatcher, new Dictionary<ContentRoot, ContentRootLocation>
            { [new("builtin")] = new ContentRootLocation.Disk(Core), [new("mods")] = new ContentRootLocation.Disk(Mods) });
            Coordinator = new(Host.Core, Host.Factory, files, new UnconfiguredAssetInspector(), new(new("builtin"), new("mods"), null, "test", []), Host.Logger, sessionOptions: HostFixture.Options);
            Editor = new(Coordinator, Scenes, new FileSaveStore(root), Host.Logger);
            Service = new(Coordinator, Editor, Scenes, true, Mods, Core);
            Host.Factory.OpenAuthoringPackageCall = (_, id, version, _) => new ContentResult<IAuthoringPackage>.Success(new AuthoringPackage(id, version));
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
            Service.CaptureForm = () => Blueprint("saved form");
            Service.MarkFormDirty(); Service.SaveForm(); Pump(() => !Service.IsBusy);
        }
        internal void ConfigureWrite(bool rejectReload = false)
        {
            Host.Factory.PreparePackageWriteCall = (basis, edits) =>
            { Edits = edits; return new ContentResult<PackageWritePlan>.Success(new(basis.Package, basis.SourceVersion, [new("output.txt", ImmutableArray.CreateRange(Encoding.UTF8.GetBytes("written")))], [])); };
            Host.Factory.PrepareLoadCall = (_, files, _) =>
            {
                LoadCalls++;
                Assert.AreEqual("written", Encoding.UTF8.GetString(ContentFilesSnapshot.Require(files.Read(new(new("builtin"), "output.txt"), 99)).AsSpan()));
                if (LoadCalls == 1)
                {
                    Assert.IsFalse(File.Exists(Output), "预校验不得先写磁盘。");
                    var status = new PackageStatus(new(new("core"), "core", "1.0.0", [], []), new("builtin"), PackageState.Available, []);
                    CheckCandidate = new(Host.Content, Host.Content, ContentChange.Presentation, new([status], [], [], []));
                    return new ContentResult<IContentCandidate>.Success(CheckCandidate);
                }
                Assert.AreEqual(1, CheckCandidate!.Disposals);
                Assert.AreEqual(0, CheckCandidate.Activations);
                Assert.AreEqual("written", File.ReadAllText(Output));
                if (rejectReload) return new ContentResult<IContentCandidate>.Faulted("reload-rejected", "reload rejected", null);
                ReloadCandidate = new(new FixedContentView(Host.Content.Core, new(2)), Host.Content, ContentChange.Presentation);
                return new ContentResult<IContentCandidate>.Success(ReloadCandidate);
            };
        }
        internal void Pump(Func<bool> done) => Assert.IsTrue(SpinWait.SpinUntil(() => { Coordinator.ProcessFrame(0); Editor.Process(); Service.Process(); return done(); }, HostFixture.HangTimeout), "创作操作超时：" + Service.Message);
        public void Dispose() { Service.Dispose(); Editor.Dispose(); Coordinator.Dispose(); Host.Dispose(); dispatcher.Dispose(); Directory.Delete(root, true); }
    }
}
