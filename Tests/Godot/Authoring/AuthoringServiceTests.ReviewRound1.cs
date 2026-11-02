using System.Collections.Immutable;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Authoring;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;

namespace Train.Godot.Tests.Authoring;

public sealed partial class AuthoringServiceTests
{
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
        fixture.ConfigureWrite();
        fixture.Service.Publish(); fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsTrue(fixture.Service.LastPublishSucceeded);
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
