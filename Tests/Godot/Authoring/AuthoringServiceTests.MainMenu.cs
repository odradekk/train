using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Authoring;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Godot.Tests.Host;

namespace Train.Godot.Tests.Authoring;

public sealed partial class AuthoringServiceTests
{
    [TestMethod]
    public void Development_requires_loaded_content_and_exported_core_is_read_only()
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
    public void Menu_editing_and_writing_require_loaded_content_and_an_idle_host()
    {
        using var fixture = new AuthoringFixture(ready: false);
        var called = false;
        ContentResult<IContentCandidate> Write(ContentLoadRequest request, IContentFiles files, IAssetInspector assets)
        { called = true; throw new InvalidOperationException("前提不满足时不得准备写入。"); }
        Assert.AreEqual("content_unavailable", fixture.Coordinator.BeginEditing()?.Code);
        Assert.AreEqual("content_unavailable", fixture.Coordinator.BeginPackageWrite(Write).Problem?.Code);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Host.Load = _ =>
        {
            entered.Set(); HostFixture.WaitForSignal(release, "释放初始内容加载");
            return new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(fixture.Host.Content));
        };
        Assert.IsNull(fixture.Coordinator.BeginReload(CommandSource.AutomationTest).Problem);
        try
        {
            HostFixture.WaitForSignal(entered, "初始内容加载已开始");
            Assert.AreEqual("busy", fixture.Coordinator.BeginEditing()?.Code);
            Assert.AreEqual("busy", fixture.Coordinator.BeginPackageWrite(Write).Problem?.Code);
            Assert.IsFalse(called);
            Assert.IsNull(fixture.Coordinator.Content);
        }
        finally { release.Set(); }
        fixture.Pump(() => !fixture.Coordinator.IsBusy);
        Assert.IsNull(fixture.Coordinator.BeginEditing());
        Assert.IsNull(fixture.Coordinator.BeginEditing());
        Assert.IsFalse(fixture.Coordinator.IsEditing);
        fixture.Coordinator.EndEditing();
    }

    [TestMethod]
    public void Creating_a_mod_blocks_exit_even_without_an_open_form()
    {
        using var fixture = new AuthoringFixture();
        var open = fixture.Host.Factory.OpenAuthoringPackageCall!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Host.Factory.OpenAuthoringPackageCall = (root, id, version, files) =>
        {
            entered.Set(); HostFixture.WaitForSignal(release, "释放新建包校验");
            return open(root, id, version, files);
        };
        fixture.Service.CreateMod(new(new("cb970878-701a-4e87-95e4-769cc99bdf51"), "创建中", "0.1.0", ["测试"], []));
        try
        {
            HostFixture.WaitForSignal(entered, "新建包校验已开始");
            var closed = false;
            Assert.AreEqual("authoring_busy", fixture.Editor.RequestClose(() => closed = true)?.Code);
            Assert.IsFalse(closed);
            Assert.IsNull(fixture.Service.Form);
        }
        finally { release.Set(); }
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsNotNull(fixture.Service.Package, fixture.Service.Message);
    }

    [TestMethod]
    public void Authoring_pause_preserves_player_settings_and_other_overlay_sources()
    {
        using var fixture = new AuthoringFixture();
        Assert.IsNull(fixture.Coordinator.SetPaused(false));
        fixture.Service.SetOverlayOpen(true);
        Assert.IsTrue(fixture.Coordinator.GetStatus()!.IsPaused);
        Assert.IsFalse(fixture.Coordinator.GetStatus()!.PlayerPaused);
        fixture.Coordinator.SetOverlayPaused(Train.App.OverlayPauseSource.Settings, true);
        fixture.Service.SetOverlayOpen(false);
        Assert.IsTrue(fixture.Coordinator.GetStatus()!.OverlayPaused);
        fixture.Coordinator.SetOverlayPaused(Train.App.OverlayPauseSource.Settings, false);
        Assert.IsFalse(fixture.Coordinator.GetStatus()!.IsPaused);
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Main_menu_package_edit_can_close_then_start_a_game(bool grid)
    {
        using var fixture = new AuthoringFixture(ready: false);
        fixture.Host.Load = _ => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(fixture.Host.Content));
        Assert.IsNull(fixture.Coordinator.BeginReload(CommandSource.AutomationTest).Problem);
        fixture.Pump(() => !fixture.Coordinator.IsBusy);
        Assert.IsNull(fixture.Coordinator.GetStatus());
        Assert.IsTrue(fixture.Service.CanUse);
        fixture.Open();
        fixture.Service.OpenDefinition(new(DefinitionKind.Blueprint, "blueprint"), grid);
        fixture.Pump(() => grid ? fixture.Editor.IsOpen && !fixture.Editor.IsBusy : fixture.Service.Form is not null);
        Assert.IsNull(fixture.Editor.RequestClose(() => { }));
        Assert.IsFalse(fixture.Editor.IsOpen);
        Assert.IsNull(fixture.Service.Form);
        Assert.IsFalse(fixture.Coordinator.IsEditing);
        fixture.Host.World = (_, _, _, _) => new ContentResult<WorldGenerationInput>.Success(HostCoreScenario.World);
        fixture.Host.Core.PrepareNewCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(fixture.Host.CoreCandidate);
        fixture.Host.Copies = (content, _) => content;
        Assert.IsNull(fixture.Coordinator.BeginNewGame(new("fixture:profile"), 1, "fixture-generator", HostFixture.Options).Problem);
        fixture.Pump(() => !fixture.Coordinator.IsBusy);
        Assert.IsNotNull(fixture.Coordinator.GetStatus());
        Assert.IsFalse(fixture.Coordinator.IsEditing);
    }

    [TestMethod]
    public void Main_menu_can_write_a_saved_package_without_creating_a_world()
    {
        using var fixture = new AuthoringFixture(ready: false);
        fixture.Host.Load = _ => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(fixture.Host.Content));
        Assert.IsNull(fixture.Coordinator.BeginReload(CommandSource.AutomationTest).Problem);
        fixture.Pump(() => !fixture.Coordinator.IsBusy);
        fixture.Open();
        fixture.SaveForm();
        var saved = fixture.Service.Drafts().Single();
        fixture.ConfigureWrite();
        fixture.Service.Publish();
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsTrue(fixture.Service.LastWrite?.Success);
        Assert.AreSame(fixture.ReloadCandidate!.View, fixture.Coordinator.Content);
        Assert.IsNull(fixture.Coordinator.GetStatus());
        Assert.AreEqual("written", File.ReadAllText(fixture.Output));
        Assert.IsTrue(File.Exists(fixture.DraftPath(saved.Id)));
    }
}
