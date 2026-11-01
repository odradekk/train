using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Logging;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SaveTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Main_menu_load_real_input_enters_paused_macro_map_without_active_session()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new SaveSetup();
        var owned = new HashSet<Guid>();
        Train.App.App? app = null;
        try
        {
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = setup.Create;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Saves!.Status is not null);
            var saves = app.Saves!;
            var identity = saves.Status!.Identity;
            owned.Add(identity.Id);
            var menu = app.GetNode<MainMenu>("MainMenu");
            var ui = app.GetNode<GameUi>("GameUi");
            var panel = app.GetNode<SavePanel>("GameUi/Root/SavePanel");
            var previous = setup.Session!;
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "存档与读档");
            await _input.Activate(UiTestInput.SaveButton(panel, "保存"));
            await _input.WaitUntil(() => !saves.HasPendingWork);
            var store = new FileSaveStore(OS.GetUserDataDir());
            Require(!saves.Status!.Unsaved && store.ReadHeader(store.PathFor(identity.Id)).Id == identity.Id,
                "真实保存输入未发布本次测试拥有的存档文件。");
            await _input.Activate(UiTestInput.SaveButton(panel, "关闭（Esc）"));
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "返回主菜单");
            await _input.WaitUntil(() => app.Host!.GetStatus() is null && menu.IsVisibleInTree());
            panel.Hide();
            await _input.Frames();
            await _input.Activate(menu.GetNode<Button>("%Saves"));
            Require(panel.Visible, "无活动会话时主菜单真实输入未打开存档面板。");
            var list = panel.FindChildren("*", "ItemList", true, false).Cast<ItemList>()
                .Single(candidate => candidate.GetWindow() == panel.GetWindow());
            var row = Enumerable.Range(0, list.ItemCount).Single(index =>
                list.GetItemText(index).StartsWith(identity.Name + " · ", StringComparison.Ordinal));
            await _input.ClickItem(list, row);
            Require(list.GetSelectedItems().SequenceEqual(new[] { row }), "真实列表点击未选中本次存档。");
            await _input.Activate(UiTestInput.SaveButton(panel, "读取"));
            await _input.WaitUntil(() => !saves.HasPendingWork && saves.Status is not null);
            await _input.WaitUntil(() => ui.GetNode<MacroMap>("Root/World/MacroMap").IsVisibleInTree());
            var status = app.Host!.GetStatus()!;
            Require(status.IsPaused && status.PlayerPaused && saves.Status!.Identity == identity &&
                !saves.Status.Unsaved && previous.DisposeCount == 1 && !menu.IsVisibleInTree() &&
                ReferenceEquals(ui.GetNode<MacroMap>("Root/World/MacroMap").View, status.View),
                "主菜单读档成功未进入暂停地图、发布读取视图或保留存档身份。");
        }
        finally { await Cleanup(fixture, app, owned); }
    }

    [Test]
    public async Task Loading_a_save_in_game_closes_the_previous_sessions_panels()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new SaveSetup();
        var owned = new HashSet<Guid>();
        Train.App.App? app = null;
        try
        {
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = setup.Create;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Saves!.Status is not null);
            owned.Add(app.Saves!.Status!.Identity.Id);
            var ui = app.GetNode<GameUi>("GameUi");
            var combat = Find<CombatPanel>(ui, "CombatPanel");
            await _input.Activate(Find<Button>(ui, "Nodes"));
            await _input.ClickItem(Find<ItemList>(Find<PanelContainer>(ui, "NodesList"), "Objects"), 0);
            await _input.Activate(Find<Button>(ui, "Fleet"));
            await _input.ClickItem(Find<ItemList>(Find<PanelContainer>(ui, "FleetList"), "Objects"), 0);
            await _input.Activate(Find<Button>(((GameShell)ui.World).InspectorContent!, "OpenCombat"));
            await _input.Activate(Find<Button>(ui, "EconomyReports"));
            Require(ui.FindChildren("Description", "Label", true, false).Count == 1 && combat.Visible &&
                combat.ContextTrain == new TrainId(7) && Find<PanelContainer>(ui, "EconomyReports").IsVisibleInTree(),
                "没有打开上一局的节点和列车详情、战斗面板与经营报告。");
            var binding = app.Host!.GetStatus()!.Binding;
            var panel = app.GetNode<SavePanel>("GameUi/Root/SavePanel");
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "存档与读档");
            await _input.Activate(UiTestInput.SaveButton(panel, "保存"));
            await _input.WaitUntil(() => !app.Saves.HasPendingWork && panel.Entries.Count > 0);
            var identity = app.Saves.Status!.Identity;
            var list = Find<ItemList>(panel, "Saves");
            var row = Enumerable.Range(0, list.ItemCount).Single(index =>
                list.GetItemText(index).StartsWith(identity.Name + " · ", StringComparison.Ordinal));
            await _input.ClickItem(list, row);
            await _input.Activate(UiTestInput.SaveButton(panel, "读取"));
            await _input.WaitUntil(() => app.Host.GetStatus() is { } status && status.Binding != binding);
            await _input.Frames();
            Require(ui.FindChildren("Description", "Label", true, false).Count == 0 && !combat.Visible &&
                combat.ContextTrain is null && combat.SelectedCombat is null &&
                ui.FindChildren("EconomyReports", "PanelContainer", true, false).Count == 0,
                "读档后仍留着上一局的详情、战斗面板或经营报告。");
        }
        finally { await Cleanup(fixture, app, owned); }
    }

    [Test]
    public async Task In_game_saves_real_input_discards_unsaved_session_and_invalidates_old_drafts()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new SaveSetup();
        var owned = new HashSet<Guid>();
        Train.App.App? app = null;
        try
        {
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = setup.Create;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Saves!.Status is not null);
            owned.Add(app.Saves!.Status!.Identity.Id);
            var ui = app.GetNode<GameUi>("GameUi");
            var panels = ui.GetNode<WorldPanels>("Root/World/WorldPanels");
            await _input.Activate(Find<Button>(ui, "Nodes"));
            var nodes = Find<ItemList>(Find<PanelContainer>(ui, "NodesList"), "Objects");
            await _input.ClickItem(nodes, 0);
            await _input.Activate(Find<Button>(((GameShell)ui.World).InspectorContent!, "Form"));
            var formation = Find<TrainFormationPanel>(ui, "TrainFormationPanel");
            var available = Find<ItemList>(formation, "AvailableVehicles");
            await _input.ClickItem(available, 0);
            await _input.Activate(Find<Button>(formation, "AddSelected"));
            var form = Find<Button>(formation, "Form");
            Require(formation.IsVisibleInTree() && !form.Disabled &&
                Find<ItemList>(formation, "OrderedVehicles").ItemCount == 1,
                "未建立可提交的编组草稿。");
            await _input.PressKey(Key.Escape);
            await _input.Activate(Find<Button>(ui, "Fleet"));
            var fleet = Find<ItemList>(Find<PanelContainer>(ui, "FleetList"), "Objects");
            await _input.ClickItem(fleet, 0);
            await _input.Activate(Find<Button>(((GameShell)ui.World).InspectorContent!, "EditPlan"));
            var plan = Find<TrainPlanWorkspace>(ui, "TrainPlanWorkspace");
            var apply = Find<Button>(plan, "Apply");
            var descriptions = ui.FindChildren("Description", "Label", true, false).Cast<Label>().ToArray();
            Require(plan.IsVisibleInTree() && !apply.Disabled && descriptions.Length == 1 &&
                descriptions.All(label => !label.Text.Contains("已不存在", StringComparison.Ordinal)),
                "未建立有效节点、列车详情和可提交的计划草稿。");
            var session = setup.Session!;
            var menu = app.GetNode<MainMenu>("MainMenu");
            var panel = app.GetNode<SavePanel>("GameUi/Root/SavePanel");
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "存档与读档");
            Require(panel.Visible, "游戏顶栏真实输入未打开存档面板。");
            await _input.Activate(UiTestInput.SaveButton(panel, "关闭（Esc）"));
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "返回主菜单");
            await _input.WaitUntil(() => UiTestInput.SavePromptVisible(panel, "未保存的更改"));
            Require(app.Host!.GetStatus() is not null && session.DisposeCount == 0 &&
                app.Saves!.Leaving is { CanSave: true, Waiting: false }, "返回菜单绕过了未保存确认。");
            await _input.Activate(UiTestInput.SavePromptButton(panel, "Discard"));
            await _input.WaitUntil(() => app.Host.GetStatus() is null && menu.IsVisibleInTree());
            Require(app.Saves.Status is null && app.Saves.Leaving is null && session.DisposeCount == 1 &&
                !ui.GetNode<Control>("Root/World").IsVisibleInTree() &&
                !ui.GetNode<MacroMap>("Root/World/MacroMap").IsVisibleInTree() &&
                !ui.GetNode<Control>("Root/World/Bar").IsVisibleInTree() &&
                !panels.IsVisibleInTree() && !formation.IsVisibleInTree() && !plan.IsVisibleInTree(),
                "不保存离开后仍保留活动会话或游戏界面。");
            await _input.Frames();
            Require(descriptions.All(label => !GodotObject.IsInstanceValid(label)) &&
                ui.FindChildren("Description", "Label", true, false).Count == 0 &&
                !panels.IsObjectOpen(new WorldObjectId.Node(new(1))) &&
                !panels.IsObjectOpen(new WorldObjectId.Train(new(7))) && form.Disabled && apply.Disabled &&
                formation.FindChildren("Reason", "Label", true, false).Cast<Label>().Single().Text.Contains("已不存在") &&
                plan.FindChildren("Reason", "Label", true, false).Cast<Label>().Single().Text.Contains("已不存在") &&
                session.SubmitCount == 0,
                "离开对局后旧详情或编组、计划草稿仍可提交。");
        }
        finally { await Cleanup(fixture, app, owned); }
    }

    [Test]
    public async Task Save_with_current_and_changed_name_publishes_files_and_load_requires_missing_confirmation()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new SaveSetup();
        var owned = new HashSet<Guid>();
        Train.App.App? app = null;
        try
        {
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = setup.Create;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Saves!.Status is not null);
            var saves = app.Saves!;
            var previousSession = setup.Session!;
            owned.Add(saves.Status!.Identity.Id);
            var store = new FileSaveStore(OS.GetUserDataDir());
            var damagedId = Guid.NewGuid();
            owned.Add(damagedId);
            Directory.CreateDirectory(store.DirectoryFor(damagedId));
            File.WriteAllText(store.PathFor(damagedId),
                "{\"id\":\"e4a0ca31-b542-4f3e-bde2-bc4a27148a2b\",\"bounds\":{\"x\":16,\"y\":8,\"z\":16},\"display_name\":\"old\",\"save_format_version\":5,\"seed\":42}");
            var menu = app.GetNode<MainMenu>("MainMenu");
            var entry = menu.GetNode<Button>("%Saves");
            Require(entry.Visible && !entry.Disabled, "新宿主的存档入口未显示。");
            entry.EmitSignal(BaseButton.SignalName.Pressed);
            var panel = app.GetNode<SavePanel>("GameUi/Root/SavePanel");
            Require(panel.Visible, "存档入口未打开生产面板。");
            Require(panel.FindChildren("*", "Label", true, false).Cast<Label>().Any(label =>
                label.GetWindow() == panel.GetWindow() && label.Text.Contains("基于全局体素世界的旧存档已不支持", StringComparison.Ordinal)),
                "列表没有立即显示旧格式兼容性诊断。");
            Press(panel, "保存");
            await _input.WaitUntil(() => !saves.HasPendingWork);
            var first = saves.Status!;
            Require(!first.Unsaved && File.Exists(new FileSaveStore(OS.GetUserDataDir()).PathFor(first.Identity.Id)),
                "保存按钮未发布真实文件并确认已保存状态。");
            Require(previousSession.CaptureCount == 1 && setup.Content!.CaptureCount == 1,
                "保存没有同时捕获核心与内容。");
            Require(panel.FindChildren("*", "Label", true, false).Cast<Label>().Any(label =>
                label.GetWindow() == panel.GetWindow() && label.Text == "保存完成。"), "列表诊断覆盖了保存结果。");
            var name = "engine-save-" + Guid.NewGuid().ToString("N");
            Find<LineEdit>(panel, "SaveName").Text = name;
            await _input.WaitUntil(() => !UiTestInput.SaveButton(panel, "保存").Disabled);
            Press(panel, "保存");
            await _input.WaitUntil(() => !saves.HasPendingWork);
            var second = saves.Status!;
            owned.Add(second.Identity.Id);
            Require(second.Identity.Id != first.Identity.Id && second.Identity.Name == name && !second.Unsaved,
                "另存为没有切换为新身份或没有确认保存版本。");
            Require(store.ReadHeader(store.PathFor(first.Identity.Id)).Id == first.Identity.Id &&
                store.ReadHeader(store.PathFor(second.Identity.Id)).Name == name,
                "另存为覆盖了原文件或新文件头部不正确。");
            var list = panel.FindChildren("*", "ItemList", true, false).Cast<ItemList>()
                .Single(candidate => candidate.GetWindow() == panel.GetWindow());
            var row = Enumerable.Range(0, list.ItemCount).Single(index => list.GetItemText(index).StartsWith(name + " · ", StringComparison.Ordinal));
            await _input.ClickItem(list, row);
            setup.MissingOnRestore = true;
            var previousBinding = second.Binding;
            Press(panel, "读取");
            await _input.WaitUntil(() => saves.Missing is not null);
            Require(saves.Status!.Binding == previousBinding && previousSession.DisposeCount == 0,
                "确认缺失内容前替换了活动会话。");
            await _input.WaitUntil(() => UiTestInput.SavePromptVisible(panel, "缺失内容"));
            var missingMessage = Find<Label>(panel, "PromptMessage").Text;
            Require(missingMessage.Contains("世界生成配置", StringComparison.Ordinal) &&
                missingMessage.Contains("占位", StringComparison.Ordinal) && missingMessage.Contains("失效", StringComparison.Ordinal) &&
                !missingMessage.Contains("fixture:", StringComparison.Ordinal), "缺失确认应说明缺失类别与后果，不显示内部标识。");
            UiTestInput.SavePromptButton(panel, "Confirm").EmitSignal(BaseButton.SignalName.Pressed);
            await _input.WaitUntil(() => !saves.HasPendingWork);
            Require(saves.Status!.Identity == second.Identity && saves.Status.Binding != previousBinding &&
                previousSession.DisposeCount == 1 && !saves.Status.Unsaved,
                "缺失确认后未完成读档替换或未保留存档身份。");
            Require(app.Host!.RenameSave("discard before menu") is null, "无法准备未保存的离开情景。");
            saves.ReturnToMenu();
            await _input.WaitUntil(() => UiTestInput.SavePromptVisible(panel, "未保存的更改"));
            var discard = UiTestInput.SavePromptButton(panel, "Discard");
            Require(!discard.Disabled && discard.IsVisibleInTree(), "不保存按钮不可用。");
            discard.EmitSignal(BaseButton.SignalName.Pressed);
            await _input.WaitUntil(() => !panel.IsVisibleInTree());
            Require(saves.Status is null, "返回菜单后仍保留活动会话。");
        }
        finally { await Cleanup(fixture, app, owned); }
    }

    [Test]
    public async Task Save_button_publication_failure_preserves_unsaved_status_identity_message_and_old_file()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new SaveSetup();
        var owned = new HashSet<Guid>();
        Train.App.App? app = null;
        SaveService? saves = null;
        SavePanel? panel = null;
        try
        {
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = setup.Create;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Saves!.Status is not null);
            owned.Add(app.Saves!.Status!.Identity.Id);
            const string reason = "injected save publication failure";
            var failPublication = false;
            var store = new FileSaveStore(OS.GetUserDataDir(), FileSaveStore.WriteCandidate, (candidate, target, overwrite) =>
            {
                if (failPublication) throw new IOException(reason);
                File.Move(candidate, target, overwrite);
            });
            var damagedId = Guid.NewGuid();
            owned.Add(damagedId);
            Directory.CreateDirectory(store.DirectoryFor(damagedId));
            File.WriteAllText(store.PathFor(damagedId), "{}");
            saves = new SaveService(app.Host!, store, app.Editor!);
            panel = new SavePanel();
            app.GetNode<Control>("GameUi/Root").AddChild(panel);
            panel.Configure(saves);
            panel.Open();
            await _input.Frames();
            Press(panel, "保存");
            await _input.WaitUntil(() => { saves.Process(); return !saves.HasPendingWork; });
            Require(!saves.Status!.Unsaved, "首次真实保存没有确认已保存状态。");
            Require(app.Host!.RenameSave("changed before failed save") is null, "无法准备保存失败的未保存状态。");
            var before = saves.Status!;
            var original = File.ReadAllBytes(store.PathFor(before.Identity.Id));
            failPublication = true;
            Find<LineEdit>(panel, "SaveName").Text = before.Identity.Name;
            await _input.WaitUntil(() => !UiTestInput.SaveButton(panel, "保存").Disabled);
            Press(panel, "保存");
            await _input.WaitUntil(() => { saves.Process(); return !saves.HasPendingWork; });
            Require(saves.Status!.Unsaved && saves.Status.Identity == before.Identity &&
                saves.PlayerMessage?.Contains("保存失败", StringComparison.Ordinal) == true,
                "写盘失败没有保留未保存状态、存档身份及失败提示。");
            Require(panel.FindChildren("*", "Label", true, false).Cast<Label>().Any(label =>
                label.GetWindow() == panel.GetWindow() && label.IsVisibleInTree() && label.Text == saves.PlayerMessage), "面板未显示保存失败提示。");
            Require(panel.FindChildren("*", "Label", true, false).Cast<Label>().Any(label =>
                label.GetWindow() == panel.GetWindow() && label.IsVisibleInTree() &&
                label.Text.Contains("部分存档无法读取", StringComparison.Ordinal)), "列表诊断未独立显示。");
            Require(original.SequenceEqual(File.ReadAllBytes(store.PathFor(before.Identity.Id))), "保存失败改写了旧目标。");
        }
        finally
        {
            try
            {
                if (saves is not null)
                {
                    saves.Cancel();
                    await _input.WaitUntil(() => { saves.Process(); return !saves.HasPendingWork; });
                }
            }
            finally
            {
                if (panel is not null)
                {
                    panel.GetParent()?.RemoveChild(panel);
                    panel.Free();
                }
                saves?.Dispose();
                await Cleanup(fixture, app, owned);
            }
        }
    }

}
