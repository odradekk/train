using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using System.Text.RegularExpressions;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SaveTests
{
    [Test]
    public async Task Search_and_details_use_player_text_and_double_click_protects_unsaved_progress()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new SaveSetup();
        var owned = new HashSet<Guid>();
        Train.App.App? app = null;
        try
        {
            UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = setup.Create;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Saves!.Status is not null);
            owned.Add(app.Saves!.Status!.Identity.Id);
            var activeSession = setup.Session!;
            var binding = app.Host!.GetStatus()!.Binding;
            var original = UiWorldFixture.Save(HostCoreScenario.Input);
            var detailView = new FixedCoreReadView(original.Position, original.InputIdentity)
            {
                World = original.World with { Nodes = original.World.Nodes.Select(node => node with
                    { Occupant = new NodeOccupant.Settlement(new(node.Id.Value), SettlementRelation.PlayerOwned) }).ToImmutableArray() },
                Economy = original.Economy with { Wallet = new(37) }
            };
            setup.Core.PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(
                new ScriptedCoreCandidate(new ScriptedCoreSession(detailView), HostCoreScenario.References));
            var package = new PackageMetadata(new("fixture:save-pack"), "测试内容包", "1.2.3", [], []);
            setup.Content!.DecodeCaptureCall = payload => payload == "engine content"
                ? new ContentResult<IContentCapture>.Success(new FixedContentCapture(payload, referencedPackages: [package]))
                : throw new InvalidOperationException("UNCONFIGURED: content payload");
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "存档与读档");
            var panel = app.GetNode<SavePanel>("GameUi/Root/SavePanel");
            const string name = "搜索验证存档";
            await _input.Enter(Find<LineEdit>(panel, "SaveName"), name);
            await _input.Activate(UiTestInput.SaveButton(panel, "保存"));
            await _input.WaitUntil(() => !app.Saves.HasPendingWork && !app.Saves.Status!.Unsaved);
            owned.Add(app.Saves.Status!.Identity.Id);
            var search = Find<LineEdit>(panel, "Search");
            var list = Find<ItemList>(panel, "Saves");
            await _input.Enter(search, "no-match-" + Guid.NewGuid().ToString("N"));
            Require(list.ItemCount == 0 && panel.FindChildren("*", "Label", true, false).OfType<Label>()
                .Any(label => label.IsVisibleInTree() && label.Text == "没有符合条件的存档。"),
                "搜索无匹配时没有显示空结果说明。");
            await UiCapture.Capture(TestScene, "saves-search-no-match");
            await _input.Enter(search, name);
            RequireNoSaveIdentity(panel, app.Saves.Status!.Identity.Id);
            Require(list.ItemCount == 1 && list.GetItemText(0).StartsWith(name + " · ", StringComparison.Ordinal),
                "搜索没有只保留匹配的手动存档。");
            await _input.ClickItem(list, 0);
            var map = Find<SaveMapPreview>(panel, "MapPreview");
            await _input.WaitUntil(() => app.Saves.Details is not null && map.Visible && map.World is not null);
            Require(map.IsVisibleInTree() && map.World is { WidthMeters: 1000, HeightMeters: 600 } world &&
                world.Nodes.Length == 2 && world.Trains.Length == 1,
                "详情没有显示固定存档的世界地图。");
            Require(panel.FindChildren("*", "Label", true, false).OfType<Label>().Any(label =>
                label.IsVisibleInTree() && Regex.IsMatch(label.Text, @"资金\s+37\b") &&
                Regex.IsMatch(label.Text, @"列车\s+1\b") &&
                Regex.IsMatch(label.Text, @"自有聚居地\s+2\b") &&
                label.Text.Contains(package.Name + " " + package.Version, StringComparison.Ordinal)),
                "详情没有用玩家文本显示资金、数量和内容包名称。");
            RequireNoSaveIdentity(panel, app.Saves.Status!.Identity.Id);
            Require(app.Host.GetStatus()!.Binding == binding && activeSession.DisposeCount == 0,
                "仅选择和预读详情替换了活动会话。");
            await UiCapture.Capture(TestScene, "saves-selected-details");
            Require(app.Host.RenameSave("unsaved search fixture") is null, "无法准备未保存读档情景。");
            await _input.DoubleClickItem(list, 0);
            await _input.WaitUntil(() => UiTestInput.SavePromptVisible(panel, "未保存的更改"));
            Require(UiTestInput.SavePromptButton(panel, "Confirm").Text == "保存后读取" &&
                UiTestInput.SavePromptButton(panel, "Discard").Text == "不保存，直接读取" &&
                UiTestInput.SavePromptButton(panel, "Cancel").Text == "取消" &&
                app.Saves.Leaving is { CanSave: true, Waiting: false },
                "双击读取没有明确提供保存、放弃进度和取消三种选择。");
            await UiCapture.Capture(TestScene, "saves-load-unsaved-confirmation");
            await _input.PressKey(Key.Escape);
            Require(panel.IsVisibleInTree() && !panel.GetNode<Control>("PromptMask").Visible &&
                app.Saves.Leaving is null && app.Saves.Status!.Unsaved &&
                app.Host.GetStatus()!.Binding == binding && activeSession.DisposeCount == 0,
                "Esc 没有只关闭读取询问并保留旧会话和存档覆盖层。");
        }
        finally { await Cleanup(fixture, app, owned); }
    }

    [Test]
    public async Task Delete_confirmation_cancels_without_writing_and_deletes_only_the_selected_file()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var owned = new HashSet<Guid>();
        Train.App.App? app = null;
        try
        {
            UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = new SaveSetup().Create;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Saves!.Status is not null);
            owned.Add(app.Saves!.Status!.Identity.Id);
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "存档与读档");
            var panel = app.GetNode<SavePanel>("GameUi/Root/SavePanel");
            const string name = "删除验证存档";
            await _input.Enter(Find<LineEdit>(panel, "SaveName"), name);
            await _input.Activate(UiTestInput.SaveButton(panel, "保存"));
            await _input.WaitUntil(() => !app.Saves.HasPendingWork && !app.Saves.Status!.Unsaved);
            var identity = app.Saves.Status!.Identity;
            owned.Add(identity.Id);
            var store = new FileSaveStore(OS.GetUserDataDir());
            var manual = store.PathFor(identity.Id);
            var automatic = store.AutoPathFor(identity.Id, 0);
            Require(store.Write(automatic, store.Read(manual), false).Published, "无法准备同一对局的自动存档。");
            var manualBytes = File.ReadAllBytes(manual);
            var automaticBytes = File.ReadAllBytes(automatic);
            await _input.Activate(UiTestInput.SaveButton(panel, "关闭（Esc）"));
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "返回主菜单");
            var menu = app.GetNode<MainMenu>("MainMenu");
            await _input.WaitUntil(() => app.Saves.Status is null && menu.IsVisibleInTree());
            await UiCapture.Capture(TestScene, "saves-main-menu");
            await _input.Activate(menu.GetNode<Button>("%Saves"));
            await _input.Enter(Find<LineEdit>(panel, "Search"), name);
            var list = Find<ItemList>(panel, "Saves");
            Require(list.ItemCount == 2, "列表没有同时显示同一对局的手动和自动存档。");
            var row = Enumerable.Range(0, list.ItemCount).Single(index =>
                list.GetItemText(index).StartsWith(name + " · 自动 · ", StringComparison.Ordinal));
            await _input.ClickItem(list, row);
            await UiCapture.Capture(TestScene, "saves-main-menu-list");
            await _input.Activate(UiTestInput.SaveButton(panel, "删除"));
            await _input.WaitUntil(() => UiTestInput.SavePromptVisible(panel, "删除存档"));
            RequireNoSaveIdentity(panel, identity.Id);
            Require(Find<Label>(panel, "PromptMessage").Text.Contains(name, StringComparison.Ordinal) &&
                File.ReadAllBytes(automatic).SequenceEqual(automaticBytes), "删除确认前改写了选中文件或未指明目标。");
            await UiCapture.Capture(TestScene, "saves-delete-confirmation");
            await _input.PressKey(Key.Escape);
            Require(panel.IsVisibleInTree() && !panel.GetNode<Control>("PromptMask").Visible &&
                File.ReadAllBytes(automatic).SequenceEqual(automaticBytes) && File.ReadAllBytes(manual).SequenceEqual(manualBytes),
                "Esc 没有取消删除并保留两份文件和覆盖层。");
            await _input.Activate(UiTestInput.SaveButton(panel, "删除"));
            await _input.WaitUntil(() => UiTestInput.SavePromptVisible(panel, "删除存档"));
            await _input.Activate(UiTestInput.SavePromptButton(panel, "Cancel"));
            Require(panel.IsVisibleInTree() && !panel.GetNode<Control>("PromptMask").Visible &&
                File.ReadAllBytes(automatic).SequenceEqual(automaticBytes) && File.ReadAllBytes(manual).SequenceEqual(manualBytes),
                "取消删除没有保留两份文件和覆盖层。");
            await _input.Activate(UiTestInput.SaveButton(panel, "删除"));
            await _input.WaitUntil(() => UiTestInput.SavePromptVisible(panel, "删除存档"));
            await _input.Activate(UiTestInput.SavePromptButton(panel, "Confirm"));
            await _input.WaitUntil(() => !app.Saves.HasPendingWork && list.ItemCount == 1);
            Require(!File.Exists(automatic) && File.ReadAllBytes(manual).SequenceEqual(manualBytes) &&
                list.GetItemText(0).StartsWith(name + " · ", StringComparison.Ordinal) && panel.IsVisibleInTree(),
                "确认删除没有只删除选中的自动文件并在列表保留手动文件。");
        }
        finally { await Cleanup(fixture, app, owned); }
    }

    [Test]
    public async Task Rename_prompt_updates_the_file_and_current_name_without_changing_identity()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var owned = new HashSet<Guid>();
        Train.App.App? app = null;
        try
        {
            UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = new SaveSetup().Create;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Saves!.Status is not null);
            var identity = app.Saves!.Status!.Identity;
            owned.Add(identity.Id);
            var binding = app.Host!.GetStatus()!.Binding;
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "存档与读档");
            var panel = app.GetNode<SavePanel>("GameUi/Root/SavePanel");
            await _input.Activate(UiTestInput.SaveButton(panel, "保存"));
            await _input.WaitUntil(() => !app.Saves.HasPendingWork && !app.Saves.Status!.Unsaved);
            await _input.Enter(Find<LineEdit>(panel, "Search"), identity.Name);
            var list = Find<ItemList>(panel, "Saves");
            var row = Enumerable.Range(0, list.ItemCount).Single(index =>
                list.GetItemText(index).StartsWith(identity.Name + " · ", StringComparison.Ordinal));
            await _input.ClickItem(list, row);
            await _input.Activate(UiTestInput.SaveButton(panel, "重命名"));
            await _input.WaitUntil(() => UiTestInput.SavePromptVisible(panel, "重命名存档"));
            const string name = "改名验证存档";
            var edit = Find<LineEdit>(panel, "PromptName");
            Require(edit.IsVisibleInTree() && edit.Text == identity.Name, "重命名没有预填选中存档名称。");
            await _input.Enter(edit, name);
            await UiCapture.Capture(TestScene, "saves-rename-confirmation");
            await _input.PressKey(Key.Enter);
            var currentName = Find<LineEdit>(panel, "SaveName");
            await _input.WaitUntil(() => !app.Saves.HasPendingWork && currentName.Text == name);
            var store = new FileSaveStore(OS.GetUserDataDir());
            var header = store.ReadHeader(store.PathFor(identity.Id));
            Require(header.Id == identity.Id && header.Name == name && app.Saves.Status!.Identity.Id == identity.Id &&
                app.Saves.Status.Identity.Name == name && app.Host.GetStatus()!.Binding == binding &&
                !panel.GetNode<Control>("PromptMask").Visible && panel.IsVisibleInTree(),
                "重命名没有同步真实文件、当前名称和顶部输入框，或改变了身份和活动会话。");
            await _input.Enter(Find<LineEdit>(panel, "Search"), name);
            Require(list.ItemCount == 1 && list.GetItemText(0).StartsWith(name + " · ", StringComparison.Ordinal),
                "改名后的存档不能通过新名称搜索。");
        }
        finally { await Cleanup(fixture, app, owned); }
    }

    private static void RequireNoSaveIdentity(SavePanel panel, Guid identity)
    {
        var list = Find<ItemList>(panel, "Saves");
        var text = Enumerable.Range(0, list.ItemCount).Select(list.GetItemText)
            .Concat(panel.FindChildren("*", "Label", true, false).OfType<Label>()
                .Where(label => label.IsVisibleInTree()).Select(label => label.Text));
        Require(text.All(value => !value.Contains(identity.ToString(), StringComparison.OrdinalIgnoreCase) &&
            !value.Contains(identity.ToString("N"), StringComparison.OrdinalIgnoreCase)),
            "存档列表、详情或确认提示泄露了存档身份编号。");
    }
}
