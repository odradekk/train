using System.Collections.Immutable;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Authoring;
using Train.App.Saves;
using Train.Godot.Tests.Host;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;

namespace Train.Godot.Tests.Authoring;

public sealed partial class AuthoringServiceTests
{
    [TestMethod]
    public void A_written_package_reopens_and_saved_draft_can_explicitly_use_the_new_source()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.SaveForm();
        var saved = fixture.Service.Drafts().Single();
        var previous = ((DraftTarget.PackageDefinition)saved.Target).SourceVersion;
        fixture.ConfigureWrite(); fixture.Service.Publish(); fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsNotNull(fixture.Service.Package);
        Assert.IsTrue(File.Exists(fixture.DraftPath(saved.Id)));
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
    public void Disabled_candidate_without_diagnostics_reports_why_no_files_were_written()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.SaveForm();
        var status = new PackageStatus(new(new("core"), "core", "1.0.0", [], []), new("builtin"), PackageState.Disabled, []);
        fixture.ConfigureWrite(report: new([status], [], [], []));
        fixture.Service.Publish(); fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsFalse(fixture.Service.LastPublishSucceeded);
        Assert.IsFalse(File.Exists(fixture.Output));
        var diagnostic = fixture.LastOperation!.Diagnostics.Single();
        Assert.AreEqual("authoring_package_unavailable", diagnostic.Code);
        Assert.AreEqual(new PackageId("core"), diagnostic.Source!.Package);
        Assert.IsFalse(string.IsNullOrWhiteSpace(diagnostic.Message));
    }

    [TestMethod]
    public void Mod_copied_after_startup_is_enabled_when_opened_and_can_be_written_and_loaded()
    {
        using var fixture = new AuthoringFixture();
        var id = new PackageId("464a2c1b-7477-46fe-bf28-b6dc2b6c89fa");
        var directory = DiscoverMod(fixture, id);
        Assert.AreEqual(PackageState.Disabled, fixture.Service.Sources.Single().State);
        fixture.Service.Open(directory, id); fixture.Pump(() => !fixture.Service.IsBusy);
        fixture.SaveForm();
        ConfigureModWrite(fixture, id);
        fixture.Service.Publish(); fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsTrue(fixture.Service.LastPublishSucceeded, fixture.Service.Message);
        Assert.AreEqual("written", File.ReadAllText(Path.Combine(directory, "output.txt")));
        Assert.AreEqual(PackageState.Available, fixture.Coordinator.Content!.Packages.Single(status => status.Metadata?.Id == id).State);
    }

    [TestMethod]
    public void Reopening_after_repair_keeps_manifest_warnings_without_restoring_old_loaded_errors()
    {
        using var fixture = new AuthoringFixture(ready: false);
        var id = new PackageId("0f4e12c7-4426-4f4e-ad4d-2d2cd0c0f92a");
        var oldIssue = Diagnostic("old-load-error", "修复前的加载错误");
        var oldStatus = ModStatus(id, PackageState.Invalid, [oldIssue]);
        fixture.Host.Load = _ => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(
            new FixedContentView(fixture.Host.Content.Core, new(1), packages: [oldStatus])));
        fixture.Start();
        fixture.Coordinator.EnableAuthoringPackage(id);
        var manifestIssue = Diagnostic("manifest-warning", "清单警告仍然存在") with { Severity = ContentDiagnosticSeverity.Warning };
        var directory = DiscoverMod(fixture, id, [manifestIssue]);
        fixture.Service.Open(directory, id); fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsTrue(fixture.Service.Issues.Any(issue => issue.Diagnostic == oldIssue));
        fixture.SaveForm(); ConfigureModWrite(fixture, id);
        fixture.Service.Publish(); fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsTrue(fixture.Service.LastPublishSucceeded, fixture.Service.Message);
        Assert.AreEqual(0, fixture.Service.PublishIssues.Length, "此前已有的源包诊断不属于本次写入结果。");
        fixture.Service.Open(directory, id); fixture.Pump(() => !fixture.Service.IsBusy);
        CollectionAssert.AreEqual(new[] { manifestIssue }, fixture.Service.Issues.Select(issue => issue.Diagnostic).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Unsaved_new_definition_is_visible_and_marked_dirty(bool grid)
    {
        using var fixture = new AuthoringFixture();
        fixture.Open();
        DefinitionDraft draft = grid ? new DefinitionDraft.Blueprint("new-blueprint", Draft("new"))
            : new DefinitionDraft.SettlementTemplate(new("new-template", new("", ""), null, null, null, [], [], [], []));
        fixture.Service.NewDefinition(draft);
        fixture.Pump(() => !fixture.Editor.IsBusy);
        var address = AuthoringService.Address(draft);
        Assert.AreEqual(address, fixture.Service.DirtyDefinition);
        CollectionAssert.Contains(fixture.Service.Definitions.ToArray(), address);
        Assert.IsFalse(fixture.Service.IsPending(address));
    }

    [TestMethod]
    public void Successful_write_reopens_the_changed_source_and_keeps_its_write_result()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.SaveForm();
        var previous = fixture.Service.Package!.SourceVersion;
        var targetIssue = Diagnostic("target-warning", "目标包警告") with { Severity = ContentDiagnosticSeverity.Warning };
        var unrelatedIssue = Diagnostic("other-warning", "其他包警告") with
        {
            Severity = ContentDiagnosticSeverity.Warning,
            Source = new(new("other-package"), "definitions/test.json")
        };
        fixture.ConfigureWrite(reloadReport: new([], [], [], [unrelatedIssue, targetIssue]));
        fixture.Service.Publish(); fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsTrue(fixture.Service.LastPublishSucceeded);
        CollectionAssert.AreEqual(new[] { targetIssue }, fixture.Service.PublishIssues.Select(issue => issue.Diagnostic).ToArray());
        Assert.IsTrue(fixture.Service.LastWrite!.Success);
        Assert.IsNotNull(fixture.Service.Package, "写入修改了磁盘后应自动重新读取源包。");
        Assert.AreNotEqual(previous, fixture.Service.Package.SourceVersion);
        Assert.AreEqual(1, fixture.Service.Drafts().Length);
        Assert.AreEqual(0, fixture.Service.PendingChanges.Length);
        StringAssert.StartsWith(fixture.Service.Message!, "文件已写入，内容重载成功；草稿还在。");
    }

    [TestMethod]
    public void Write_result_message_does_not_repeat_its_diagnostic_rows()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.SaveForm();
        var diagnostic = Diagnostic("target-invalid", "只应在诊断行显示的详细原因");
        var status = new PackageStatus(new(new("core"), "core", "1.0.0", [], []), new("builtin"), PackageState.Invalid, [diagnostic]);
        fixture.ConfigureWrite(report: new([status], [], [], [diagnostic]));
        fixture.Service.Publish(); fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.AreEqual(diagnostic, fixture.Service.Issues.Single().Diagnostic);
        Assert.AreEqual(diagnostic, fixture.Service.PublishIssues.Single().Diagnostic);
        Assert.IsFalse(fixture.Service.Message!.Contains(diagnostic.Message, StringComparison.Ordinal));
        Assert.IsFalse(fixture.Service.Message.Contains(diagnostic.Code, StringComparison.Ordinal));
        StringAssert.StartsWith(fixture.Service.Message, "一个文件也没写，游戏里的内容保持原样，草稿还在。");
    }

    [TestMethod]
    public void Source_refresh_after_write_must_finish_before_another_mod_can_be_created()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.SaveForm(); fixture.ConfigureWrite();
        var existing = new PackageId("63973caf-2668-48aa-a6de-54cc2a153822");
        Directory.CreateDirectory(Path.Combine(fixture.Mods, existing.Value));
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Host.Factory.DiscoverCall = (installation, _) =>
        {
            started.Set();
            Assert.IsTrue(release.Wait(HostFixture.HangTimeout), "源列表刷新未获释放。");
            return new ContentResult<ImmutableArray<PackageStatus>>.Success(
                [ModStatus(existing, PackageState.Disabled, []) with { Root = installation.Mods }]);
        };
        try
        {
            fixture.Service.Publish();
            fixture.Pump(() => fixture.Service.LastPublishSucceeded == true && fixture.Service.Package is not null);
            HostFixture.WaitForSignal(started, "写入后的源列表刷新开始");
            Assert.IsTrue(fixture.Service.IsBusy, "等待操作结束也必须等待写入后的源列表刷新。");
            var next = new PackageId("9cfa778b-59a1-4a93-8fc7-7611b2ba7873");
            fixture.Service.CreateMod(new(next, "下一模组", "0.1.0", [], []));
            Assert.AreEqual(new PackageId("core"), fixture.Service.Target!.Package);
            StringAssert.Contains(fixture.Service.Message!, "请等待");
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Mods, next.Value)));
        }
        finally { release.Set(); }
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.AreEqual(existing, fixture.Service.Sources.Single().Package);
    }

    [TestMethod]
    public void Created_manifest_preserves_chinese_names_and_authors_as_readable_text()
    {
        using var fixture = new AuthoringFixture();
        var metadata = new PackageMetadata(new("9f66aad2-7a6d-4fc4-bbdd-eac35214082d"), "中文模组", "0.1.0", ["中文作者"], []);
        var target = LocalAuthoringPackages.Create(fixture.Host.Factory, fixture.Mods, metadata);
        var json = File.ReadAllText(Path.Combine(target.Directory, "mod.json"));
        StringAssert.Contains(json, metadata.Name);
        StringAssert.Contains(json, metadata.Authors.Single());
    }

    private static string DiscoverMod(AuthoringFixture fixture, PackageId id, ImmutableArray<ContentDiagnostic> manifestIssues = default)
    {
        var directory = Path.Combine(fixture.Mods, id.Value);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "mod.json"), "manifest");
        fixture.Host.Factory.DiscoverCall = (installation, _) => new ContentResult<ImmutableArray<PackageStatus>>.Success(
            [ModStatus(id, PackageState.Disabled, manifestIssues.IsDefault ? [] : manifestIssues) with { Root = installation.Mods }]);
        fixture.Service.RefreshSources(); fixture.Pump(() => fixture.Service.Sources.Length == 1);
        return directory;
    }

    private static PackageStatus ModStatus(PackageId id, PackageState state, ImmutableArray<ContentDiagnostic> diagnostics) =>
        new(new(id, "本地模组", "0.1.0", [], []), new("mods"), state, diagnostics);

    private static void ConfigureModWrite(AuthoringFixture fixture, PackageId id)
    {
        var contentBasis = fixture.Coordinator.Content;
        fixture.Host.Factory.PreparePackageWriteCall = (basis, _) => new ContentResult<PackageWritePlan>.Success(
            new(basis.Package, basis.SourceVersion, [new("output.txt", [.. Encoding.UTF8.GetBytes("written")])], []));
        fixture.Host.Factory.PrepareLoadCall = (request, files, _) =>
        {
            var status = ModStatus(id, request.Installation.EnabledMods.Contains(id) ? PackageState.Available : PackageState.Disabled, []);
            var view = new FixedContentView(fixture.Host.Content.Core, new(2), packages: [status]);
            Assert.AreEqual("written", Encoding.UTF8.GetString(ContentFilesSnapshot.Require(files.Read(new(request.Installation.Mods, id.Value + "/output.txt"), 99)).AsSpan()));
            return new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(view, contentBasis, ContentChange.Presentation));
        };
    }
}
