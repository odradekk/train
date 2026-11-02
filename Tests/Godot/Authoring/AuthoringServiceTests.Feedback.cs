using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Godot.Tests.Host;
using Train.App.Saves;
namespace Train.Godot.Tests.Authoring;
public sealed partial class AuthoringServiceTests
{
    [TestMethod]
    public void Form_validation_uses_captured_replacement_after_trailing_debounce()
    {
        var clock = new FormClock();
        using var fixture = new AuthoringFixture(clock: clock);
        fixture.Open(); fixture.Service.OpenDefinition(new(DefinitionKind.Blueprint, "blueprint"), false);
        var captured = Blueprint("changed form");
        ImmutableArray<PackageEdit> received = [];
        fixture.Host.Factory.ValidateAuthoringDefinitionCall = (_, _, edits, _) =>
        { received = edits; return new ContentResult<ImmutableArray<ContentDiagnostic>>.Failure([Diagnostic("invalid_field", "校验结果")]); };
        fixture.Service.MarkFormDirty(); fixture.Service.UpdateForm(captured);
        fixture.Service.Process();
        Assert.AreEqual(0, received.Length, "静默时间不足时不校验。");
        clock.Advance(199); fixture.Service.Process();
        Assert.AreEqual(0, received.Length);
        clock.Advance(1);
        fixture.Pump(() => fixture.Service.FormDiagnostics.Length > 0);
        Assert.AreEqual(captured, ((PackageEdit.WriteDefinition)received.Single()).Draft);
        Assert.AreEqual("invalid_field", fixture.Service.FormDiagnostics.Single().Code);
    }

    [TestMethod]
    public void Slow_validation_keeps_only_latest_edit_and_discards_its_old_result()
    {
        var clock = new FormClock();
        using var fixture = new AuthoringFixture(clock: clock);
        fixture.Open(); fixture.Service.OpenDefinition(new(DefinitionKind.Blueprint, "blueprint"), false);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var names = new List<string>(); var shown = new List<string>();
        fixture.Service.Changed += () => shown.AddRange(fixture.Service.FormDiagnostics.Select(d => d.Message));
        fixture.Host.Factory.ValidateAuthoringDefinitionCall = (_, _, edits, _) =>
        {
            var value = (DefinitionDraft.Blueprint)((PackageEdit.WriteDefinition)edits.Single()).Draft;
            lock (names) names.Add(value.Value.Display.Name);
            if (value.Value.Display.Name == "first") { entered.Set(); HostFixture.WaitForSignal(release, "释放旧校验"); }
            return new ContentResult<ImmutableArray<ContentDiagnostic>>.Failure([Diagnostic("invalid_field", value.Value.Display.Name)]);
        };
        fixture.Service.MarkFormDirty(); fixture.Service.UpdateForm(Blueprint("first")); clock.Advance(200); fixture.Service.Process();
        HostFixture.WaitForSignal(entered, "进入旧校验");
        try
        {
            fixture.Service.MarkFormDirty(); fixture.Service.UpdateForm(Blueprint("second")); clock.Advance(200); fixture.Service.Process();
            fixture.Service.MarkFormDirty(); fixture.Service.UpdateForm(Blueprint("latest")); clock.Advance(200); fixture.Service.Process();
            lock (names) CollectionAssert.AreEqual(new[] { "first" }, names);
        }
        finally { release.Set(); }
        fixture.Pump(() => fixture.Service.FormDiagnostics.FirstOrDefault()?.Message == "latest");
        lock (names) CollectionAssert.AreEqual(new[] { "first", "latest" }, names);
        Assert.IsFalse(shown.Contains("first"), "旧校验结果不能短暂发布到表单。");
    }

    [TestMethod]
    public void Existing_template_previews_on_open_without_manual_request()
    {
        using var fixture = new AuthoringFixture();
        var template = new DefinitionDraft.SettlementTemplate(new("template", new("", ""), null, null, null, [], [], [], []));
        fixture.Host.Factory.OpenAuthoringPackageCall = (_, id, version, _) =>
            new ContentResult<IAuthoringPackage>.Success(new FixedAuthoringPackage(id, version, [template]));
        fixture.Open();
        fixture.Scenes.PreviewTemplateCall = (content, _, revision) => new SceneResult<ITemplatePreview>.Success(new FixedTemplatePreview(content.Generation, revision));
        fixture.Service.OpenDefinition(new(DefinitionKind.SettlementTemplate, "template"), grid: false);
        fixture.Pump(() => fixture.Service.TemplatePreview is not null);
        Assert.AreEqual(new EditRevision(0), fixture.Service.TemplatePreview!.Revision);
        fixture.Service.MarkFormDirty();
        Assert.IsInstanceOfType<Lookup<int>.Missing>(fixture.Service.PickPlot(new(new(0, 0, 0), new(0, 0, 1), 100)));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void Opening_grid_observes_the_form_leave_choice(int option)
    {
        using var fixture = new AuthoringFixture(); fixture.Open();
        fixture.Service.OpenDefinition(new(DefinitionKind.Blueprint, "blueprint"), false);
        var choice = (LeaveChoice)option;
        var captured = Blueprint("current captured form"); fixture.Service.MarkFormDirty(); fixture.Service.UpdateForm(captured);
        fixture.Service.OpenGrid(captured);
        Assert.IsTrue(fixture.Service.Leaving);
        fixture.Service.ResolveClose(choice, captured);
        if (choice == LeaveChoice.Cancel) { Assert.IsNotNull(fixture.Service.Form); Assert.IsFalse(fixture.Editor.IsOpen); return; }
        fixture.Pump(() => fixture.Editor.IsOpen && !fixture.Editor.IsBusy);
        Assert.AreEqual(choice == LeaveChoice.Save ? "current captured form" : "original", fixture.Editor.State!.Snapshot.Draft.Display.Name);
    }

    [TestMethod]
    public void Selected_asset_is_staged_beside_the_definition_and_never_overwrites_a_conflict()
    {
        using var fixture = new AuthoringFixture(); fixture.Open();
        fixture.Service.NewDefinition(new DefinitionDraft.SettlementType(new("town", new("", ""), null, false, null, [], null, null)));
        var source = Path.Combine(fixture.Mods, "image.png"); Directory.CreateDirectory(fixture.Mods); File.WriteAllBytes(source, [1, 2]);
        Assert.AreEqual("image.png", fixture.Service.SelectAsset(source));
        var staged = (PackageEdit.WriteAsset)fixture.Service.PendingChanges.Single();
        Assert.AreEqual("resources/settlement-types/town/image.png", staged.PackageRelativePath);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, staged.Bytes.ToArray());
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Core, staged.PackageRelativePath)));
        File.WriteAllBytes(source, [3]);
        Assert.IsNull(fixture.Service.SelectAsset(source));
        Assert.AreEqual(staged, fixture.Service.PendingChanges.Single());
    }

    [TestMethod]
    public void New_definition_id_uses_id_diagnostics_and_ignores_unrelated_fields()
    {
        using var fixture = new AuthoringFixture(); fixture.Open();
        fixture.Host.Factory.PreparePackageWriteCall = (source, _) => new ContentResult<PackageWritePlan>.Failure([Diagnostic("invalid_field", "内容层 ID 错误") with { Field = "/id" }]);
        Assert.AreEqual("内容层 ID 错误", fixture.Service.NewDefinitionProblem(new DefinitionDraft.Blueprint("bad id", Draft("name"))));
        fixture.Host.Factory.PreparePackageWriteCall = (source, _) => new ContentResult<PackageWritePlan>.Failure([Diagnostic("invalid_field", "其他字段尚未填写")]);
        Assert.IsNull(fixture.Service.NewDefinitionProblem(new DefinitionDraft.SettlementType(new("blueprint", new("", ""), null, false, null, [], null, null))));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void An_asset_added_after_opening_is_staged_with_its_definition_or_shared_path(bool shared)
    {
        using var fixture = new AuthoringFixture(); fixture.Open();
        fixture.Service.NewDefinition(new DefinitionDraft.SettlementType(new("town", new("", ""), null, false, null, [], null, null)));
        var relative = "resources/settlement-types/" + (shared ? "shared/icons" : "town") + "/map.png";
        var directory = Path.GetDirectoryName(Path.Combine(fixture.Core, relative))!; Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "map.png"); File.WriteAllBytes(source, [9]);
        Assert.AreEqual(shared ? "shared/icons/map.png" : "map.png", fixture.Service.SelectAsset(source));
        Assert.HasCount(1, fixture.Service.PendingChanges, "打开源包后才放入的资产必须暂存到固定候选。");
        var asset = (PackageEdit.WriteAsset)fixture.Service.PendingChanges.Single();
        Assert.AreEqual(relative, asset.PackageRelativePath);
        CollectionAssert.AreEqual(new byte[] { 9 }, asset.Bytes.ToArray());
    }

    [TestMethod]
    public void Reference_query_failure_keeps_its_reason_and_removes_the_previous_preview()
    {
        var clock = new FormClock(); using var fixture = new AuthoringFixture(clock: clock); fixture.Open();
        fixture.Scenes.PreviewTemplateCall = (content, _, revision) => new SceneResult<ITemplatePreview>.Success(new FixedTemplatePreview(content.Generation, revision) { PickResult = new Lookup<int>.Found(4) });
        var template = new DefinitionDraft.SettlementTemplate(new("template", new("", ""), null, null, null, [], [], [], []));
        fixture.Service.NewDefinition(template); fixture.Pump(() => fixture.Service.TemplatePreview is not null);
        Assert.IsInstanceOfType<Lookup<int>.Found>(fixture.Service.PickPlot(new(new(0, 0, 0), new(0, 0, 1), 100)));
        fixture.Host.Factory.QueryAuthoringReferencesCall = (_, _, _) => new ContentResult<ImmutableArray<AuthoringReferenceChoice>>.Failure([Diagnostic("dependency_missing", "缺少依赖")]);
        fixture.Service.MarkFormDirty(); fixture.Service.UpdateForm(template); clock.Advance(200);
        fixture.Pump(() => fixture.Service.FormDiagnostics.Any(d => d.Code == "dependency_missing"));
        Assert.IsNull(fixture.Service.TemplatePreview);
        StringAssert.Contains(fixture.Service.Message!, "缺少依赖");
        Assert.IsInstanceOfType<Lookup<int>.Missing>(fixture.Service.PickPlot(new(new(0, 0, 0), new(0, 0, 1), 100)));
    }

    [TestMethod]
    public void Delayed_preview_for_closed_draft_is_rejected_when_new_draft_has_same_revision()
    {
        using var fixture = new AuthoringFixture();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Open();
        FixedTemplatePreview? oldPreview = null;
        var acceptedOld = false;
        fixture.Service.Changed += () => acceptedOld |= oldPreview is not null && ReferenceEquals(oldPreview, fixture.Service.TemplatePreview);
        fixture.Scenes.PreviewTemplateCall = (content, draft, revision) =>
        {
            if (draft.LocalId == "old-template")
            {
                oldPreview = new(content.Generation, revision);
                started.Set(); HostFixture.WaitForSignal(release, "释放旧模板预览");
                return new SceneResult<ITemplatePreview>.Success(oldPreview);
            }
            return new SceneResult<ITemplatePreview>.Success(new FixedTemplatePreview(content.Generation, revision));
        };
        fixture.Service.NewDefinition(new DefinitionDraft.SettlementTemplate(new("old-template", new("", ""), null, null, null, [], [], [], [])));
        try
        {
            fixture.Service.Process(); HostFixture.WaitForSignal(started, "旧模板预览已开始");
            var closed = false;
            Assert.IsNull(fixture.Editor.RequestClose(() => closed = true));
            fixture.Service.ResolveClose(LeaveChoice.Discard, null);
            Assert.IsTrue(closed);
            var next = new DefinitionDraft.SettlementTemplate(new("new-template", new("", ""), null, null, null, [], [], [], []));
            fixture.Service.NewDefinition(next); Assert.AreEqual(next, fixture.Service.Form);
        }
        finally { release.Set(); }
        fixture.Pump(() => fixture.Service.TemplatePreview is not null);
        Assert.IsFalse(acceptedOld);
        Assert.AreNotSame(oldPreview, fixture.Service.TemplatePreview);
    }

    private sealed class FormClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(int milliseconds) => now = now.AddMilliseconds(milliseconds);
    }
}
