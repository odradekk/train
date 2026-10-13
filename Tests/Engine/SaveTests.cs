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

public class SaveTests(Node testScene) : TestClass(testScene)
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
            var panel = menu.GetChildren().OfType<SavePanel>().Single();
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Saves"));
            await _input.Activate(UiTestInput.SaveButton(panel, "保存"), panel);
            await _input.WaitUntil(() => !saves.HasPendingWork);
            var store = new FileSaveStore(OS.GetUserDataDir());
            Require(!saves.Status!.Unsaved && store.ReadHeader(store.PathFor(identity.Id)).Id == identity.Id,
                "真实保存输入未发布本次测试拥有的存档文件。");
            var previous = setup.Session!;
            await _input.Activate(UiTestInput.SaveButton(panel, "返回菜单"), panel);
            await _input.WaitUntil(() => app.Host!.GetStatus() is null && menu.IsVisibleInTree());
            panel.Hide();
            await _input.Frames();
            await _input.Activate(menu.GetNode<Button>("%Saves"));
            Require(panel.Visible, "无活动会话时主菜单真实输入未打开存档面板。");
            var list = panel.FindChildren("*", "ItemList", true, false).Cast<ItemList>()
                .Single(candidate => candidate.GetWindow() == panel);
            var row = Enumerable.Range(0, list.ItemCount).Single(index =>
                list.GetItemText(index).StartsWith(identity.Name + " ·", StringComparison.Ordinal));
            await _input.ClickItem(list, row, window: panel);
            Require(list.GetSelectedItems().SequenceEqual(new[] { row }), "真实列表点击未选中本次存档。");
            await _input.Activate(UiTestInput.SaveButton(panel, "读取选中存档"), panel);
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
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Nodes"));
            var nodes = panels.GetNode<ItemList>("NodesList/Layout/Body/Objects");
            await _input.ClickItem(nodes, 0);
            await _input.Activate(panels.FindChildren("Form", "Button", true, false).Cast<Button>().Single());
            var formation = ui.GetNode<TrainFormationPanel>("Root/World/TrainFormationPanel");
            var available = formation.FindChildren("AvailableVehicles", "ItemList", true, false).Cast<ItemList>().Single();
            await _input.ClickItem(available, 0);
            await _input.Activate(formation.FindChildren("AddSelected", "Button", true, false).Cast<Button>().Single());
            var form = formation.FindChildren("Form", "Button", true, false).Cast<Button>().Single();
            Require(formation.IsVisibleInTree() && !form.Disabled &&
                formation.FindChildren("OrderedVehicles", "ItemList", true, false).Cast<ItemList>().Single().ItemCount == 1,
                "未建立可提交的编组草稿。");
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Fleet"));
            var fleet = panels.GetNode<ItemList>("FleetList/Layout/Body/Objects");
            await _input.ClickItem(fleet, 0);
            await _input.Activate(panels.FindChildren("EditPlan", "Button", true, false).Cast<Button>().Single());
            var plan = ui.GetNode<TrainPlanPanel>("Root/World/TrainPlanPanel");
            await _input.Activate(plan.GetNode<CheckBox>("Plan/Layout/Body/Loop"));
            var apply = plan.FindChildren("Apply", "Button", true, false).Cast<Button>().Single();
            var descriptions = panels.FindChildren("Description", "Label", true, false).Cast<Label>().ToArray();
            Require(plan.IsVisibleInTree() && !apply.Disabled && descriptions.Length == 2 &&
                descriptions.All(label => !label.Text.Contains("已不存在", StringComparison.Ordinal)),
                "未建立有效节点、列车详情和可提交的计划草稿。");
            var session = setup.Session!;
            var menu = app.GetNode<MainMenu>("MainMenu");
            var panel = menu.GetChildren().OfType<SavePanel>().Single();
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Saves"));
            Require(panel.Visible, "游戏顶栏真实输入未打开存档面板。");
            await _input.Activate(UiTestInput.SaveButton(panel, "返回菜单"), panel);
            await _input.WaitUntil(() => Dialog<ConfirmationDialog>(panel, "未保存的更改").Visible);
            Require(app.Host!.GetStatus() is not null && session.DisposeCount == 0 &&
                app.Saves!.Leaving is { CanSave: true, Waiting: false }, "返回菜单绕过了未保存确认。");
            var leave = Dialog<ConfirmationDialog>(panel, "未保存的更改");
            var discard = leave.FindChildren("*", "Button", true, false).Cast<Button>().Single(button => button.Text == "不保存");
            await _input.Activate(discard, leave);
            await _input.WaitUntil(() => app.Host.GetStatus() is null && menu.IsVisibleInTree());
            Require(app.Saves.Status is null && app.Saves.Leaving is null && session.DisposeCount == 1 &&
                !ui.GetNode<Control>("Root/World").IsVisibleInTree() &&
                !ui.GetNode<MacroMap>("Root/World/MacroMap").IsVisibleInTree() &&
                !ui.GetNode<Control>("Root/World/Bar").IsVisibleInTree() &&
                !panels.IsVisibleInTree() && !formation.IsVisibleInTree() && !plan.IsVisibleInTree(),
                "不保存离开后仍保留活动会话或游戏界面。");
            await _input.Frames();
            Require(descriptions.All(label => !GodotObject.IsInstanceValid(label)) &&
                panels.FindChildren("Description", "Label", true, false).Count == 0 &&
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
    public async Task Save_and_save_as_buttons_publish_files_and_load_requires_missing_confirmation()
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
            var panel = menu.GetChildren().OfType<SavePanel>().Single();
            Require(panel.Visible, "存档入口未打开生产面板。");
            Require(panel.FindChildren("*", "Label", true, false).Cast<Label>().Any(label =>
                label.GetWindow() == panel && label.Text.Contains("基于全局体素世界的旧存档已不支持", StringComparison.Ordinal)),
                "列表没有立即显示旧格式兼容性诊断。");
            Press(panel, "保存");
            await _input.WaitUntil(() => !saves.HasPendingWork);
            var first = saves.Status!;
            Require(!first.Unsaved && File.Exists(new FileSaveStore(OS.GetUserDataDir()).PathFor(first.Identity.Id)),
                "保存按钮未发布真实文件并确认已保存状态。");
            Require(setup.Session!.CaptureCount == 1 && setup.Content!.CaptureCount == 1,
                "保存没有同时捕获核心与内容。");
            Require(panel.FindChildren("*", "Label", true, false).Cast<Label>().Any(label =>
                label.GetWindow() == panel && label.Text == "保存完成。"), "列表诊断覆盖了保存结果。");
            Require(app.Host!.RenameSave("changed before failed save") is null, "无法准备保存失败的未保存状态。");
            var original = File.ReadAllBytes(store.PathFor(first.Identity.Id));
            using (var locked = new FileStream(store.PathFor(first.Identity.Id), FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.None))
            {
                Press(panel, "保存");
                await _input.WaitUntil(() => !saves.HasPendingWork);
                Require(saves.Status!.Unsaved && !string.IsNullOrWhiteSpace(saves.LastMessage), "写盘失败没有保留未保存状态及原因。");
                Require(panel.FindChildren("*", "Label", true, false).Cast<Label>().Any(label =>
                    label.GetWindow() == panel && label.Text == saves.LastMessage), "面板未显示保存失败原因。");
                Require(panel.FindChildren("*", "Label", true, false).Cast<Label>().Any(label =>
                    label.GetWindow() == panel && label.Text.StartsWith("存档列表：", StringComparison.Ordinal)), "列表诊断未独立显示。");
            }
            Require(original.SequenceEqual(File.ReadAllBytes(store.PathFor(first.Identity.Id))), "保存失败改写了旧目标。");
            Press(panel, "另存为");
            var nameDialog = Dialog<AcceptDialog>(panel, "另存为");
            var name = "engine-save-" + Guid.NewGuid().ToString("N");
            nameDialog.GetChildren().OfType<LineEdit>().Single().Text = name;
            nameDialog.Hide();
            nameDialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await _input.WaitUntil(() => !saves.HasPendingWork);
            var second = saves.Status!;
            owned.Add(second.Identity.Id);
            Require(second.Identity.Id != first.Identity.Id && second.Identity.Name == name && !second.Unsaved,
                "另存为没有切换为新身份或没有确认保存版本。");
            Require(store.ReadHeader(store.PathFor(first.Identity.Id)).Id == first.Identity.Id &&
                store.ReadHeader(store.PathFor(second.Identity.Id)).Name == name,
                "另存为覆盖了原文件或新文件头部不正确。");
            var list = panel.FindChildren("*", "ItemList", true, false).Cast<ItemList>()
                .Single(candidate => candidate.GetWindow() == panel);
            var row = Enumerable.Range(0, list.ItemCount).Single(index => list.GetItemText(index).StartsWith(name + " ·", StringComparison.Ordinal));
            list.Select(row);
            setup.MissingOnRestore = true;
            var previousSession = setup.Session;
            var previousBinding = second.Binding;
            Press(panel, "读取选中存档");
            await _input.WaitUntil(() => saves.Missing is not null);
            Require(saves.Status!.Binding == previousBinding && previousSession!.DisposeCount == 0,
                "确认缺失内容前替换了活动会话。");
            await _input.WaitUntil(() => Dialog<ConfirmationDialog>(panel, "缺失内容").Visible);
            var missing = Dialog<ConfirmationDialog>(panel, "缺失内容");
            Require(missing.DialogText.Contains("fixture:missing", StringComparison.Ordinal), "缺失弹窗未显示固定引用。");
            missing.Hide();
            missing.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await _input.WaitUntil(() => !saves.HasPendingWork);
            Require(saves.Status!.Identity == second.Identity && saves.Status.Binding != previousBinding &&
                previousSession.DisposeCount == 1 && !saves.Status.Unsaved,
                "缺失确认后未完成读档替换或未保留存档身份。");
            Require(app.Host!.RenameSave("discard before menu") is null, "无法准备未保存的离开情景。");
            saves.ReturnToMenu();
            await _input.WaitUntil(() => Dialog<ConfirmationDialog>(panel, "未保存的更改").Visible);
            var discard = Dialog<ConfirmationDialog>(panel, "未保存的更改")
                .FindChildren("*", "Button", true, false).Cast<Button>().Single(button => button.Text == "不保存");
            Require(!discard.Disabled && discard.IsVisibleInTree(), "不保存按钮不可用。");
            discard.EmitSignal(BaseButton.SignalName.Pressed);
            await _input.WaitUntil(() => panel.FindChildren("*", "Button", true, false).Cast<Button>()
                .Where(button => button.GetWindow() == panel && button.Text is "导出文件" or "返回菜单")
                .All(button => !button.IsVisibleInTree()));
            Require(saves.Status is null, "返回菜单后仍保留活动会话。");
        }
        finally { await Cleanup(fixture, app, owned); }
    }

    [Test]
    public async Task Window_close_and_console_quit_share_protection_and_quit_only_after_publication()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new SaveSetup();
        var owned = new HashSet<Guid>();
        Train.App.App? app = null;
        try
        {
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = setup.Create;
            var quitRequests = 0;
            var publishedAtQuit = false;
            app.QuitAction = () =>
            {
                quitRequests++;
                var status = app.Saves!.Status!;
                var store = new FileSaveStore(OS.GetUserDataDir());
                publishedAtQuit = !status.Unsaved && File.Exists(store.PathFor(status.Identity.Id)) &&
                    store.ReadHeader(store.PathFor(status.Identity.Id)).Id == status.Identity.Id;
            };
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Saves!.Status is not null);
            var saves = app.Saves!;
            owned.Add(saves.Status!.Identity.Id);
            var panel = app.GetNode<MainMenu>("MainMenu").GetChildren().OfType<SavePanel>().Single();
            app.Notification((int)Node.NotificationWMCloseRequest);
            await _input.WaitUntil(() => Dialog<ConfirmationDialog>(panel, "未保存的更改").Visible);
            Require(quitRequests == 0 && saves.Leaving is { CanSave: true, Waiting: false }, "窗口关闭绕过了未保存保护。");
            var leave = Dialog<ConfirmationDialog>(panel, "未保存的更改");
            leave.Hide();
            leave.EmitSignal(AcceptDialog.SignalName.Canceled);
            Require(saves.Leaving is null && quitRequests == 0 && !app.Host!.GetStatus()!.OperationLocked,
                "取消关闭没有恢复宿主或仍请求退出。");
            app.GetChildren().OfType<GameConsole>().Single().SubmitLine("quit");
            await _input.WaitUntil(() => leave.Visible);
            Require(quitRequests == 0 && saves.Leaving is { CanSave: true, Waiting: false }, "控制台退出没有使用同一保存保护。");
            leave.Hide();
            leave.EmitSignal(AcceptDialog.SignalName.Confirmed);
            Require(quitRequests == 0 && saves.HasPendingWork, "退出没有等待异步文件发布。");
            await _input.WaitUntil(() => quitRequests == 1);
            Require(publishedAtQuit && !saves.HasPendingWork && saves.Leaving is null,
                "保存成功前执行了退出动作或文件不可读取。");
            Require(app.Host!.RenameSave("discard before quit") is null, "无法准备未保存的退出情景。");
            app.Notification((int)Node.NotificationWMCloseRequest);
            await _input.WaitUntil(() => leave.Visible);
            var discard = leave.FindChildren("*", "Button", true, false).Cast<Button>()
                .Single(button => button.Text == "不保存");
            Require(!discard.Disabled && discard.IsVisibleInTree(), "退出时不保存按钮不可用。");
            discard.EmitSignal(BaseButton.SignalName.Pressed);
            Require(quitRequests == 2 && saves.Status!.Unsaved && !saves.HasPendingWork && saves.Leaving is null,
                "不保存按钮未执行退出回调，或错误地确认了保存。");
        }
        finally { await Cleanup(fixture, app, owned); }
    }

    private static T Dialog<T>(SavePanel panel, string title) where T : Window =>
        panel.GetChildren().OfType<T>().Single(dialog => dialog.Title == title);

    private static void Press(SavePanel panel, string text)
    {
        var button = UiTestInput.SaveButton(panel, text);
        Require(!button.Disabled && button.Visible, $"按钮不可用：{text}。");
        button.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private async Task Cleanup(Fixture fixture, Train.App.App? app, HashSet<Guid> owned)
    {
        try
        {
            if (app?.Saves is { } saves)
            {
                if (saves.Status is { } status) owned.Add(status.Identity.Id);
                saves.Cancel();
                await _input.WaitUntil(() => !saves.HasPendingWork);
            }
        }
        finally
        {
            try { await fixture.Cleanup(); }
            finally
            {
                UiTestInput.DeleteSaves(owned);
            }
        }
    }

    private sealed class SaveSetup
    {
        internal ScriptedCoreSession? Session { get; private set; }
        internal ScriptedContentFactory? Content { get; private set; }
        internal bool MissingOnRestore { get; set; }

        internal HostCoordinator Create(TrainLogger logger)
        {
            var capture = new FixedCoreCapture(new(new(0, 0), "engine save", "engine sha"),
                HostCoreScenario.References, "engine core", HostCoreScenario.Input, new(0));
            var core = new ScriptedCoreFactory
            {
                DecodeStateCall = payload => payload == "engine core"
                    ? new CoreResult<ICoreCapture>.Success(capture) : throw new InvalidOperationException("UNCONFIGURED: core payload"),
                PrepareRestoreCall = (_, projection, _) =>
                {
                    Require(projection.Identity == HostCoreScenario.Input, "恢复时收到错误内容投影。");
                    Session = new(UiWorldFixture.Save(projection.Identity));
                    Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(capture));
                    Session.ReadRecordingCall = () => new(new(0, 0), [new(new(1), capture)], [], [capture.Checkpoint], [], [], capture.References);
                    Session.ConfirmRecordingStartCall = (point, _) => point.Value == 1 ? new([], [])
                        : throw new InvalidOperationException("UNCONFIGURED: recording point");
                    return new CoreResult<ICoreSessionCandidate>.Success(new ScriptedCoreCandidate(Session, HostCoreScenario.References));
                }
            };
            Content = new()
            {
                CaptureReplayCall = (history, _) => new ContentResult<IContentReplayCapture>.Success(new FixedContentReplayCapture(history, "engine recording content")),
                CaptureCall = (view, _) => new ContentResult<IContentCapture>.Success(new FixedContentCapture("engine content", view.PersistentRevision)),
                DecodeCaptureCall = payload => payload == "engine content"
                    ? new ContentResult<IContentCapture>.Success(new FixedContentCapture(payload)) : throw new InvalidOperationException("UNCONFIGURED: content payload"),
                PrepareLoadCall = (request, _, _) =>
                {
                    Require(request.Purpose is ContentLoadPurpose.Restore, "存档引擎情景只允许恢复。");
                    var view = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
                    {
                        Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
                        { [new("engine-vehicle")] = new ContentLookup<DesignEvaluation>.NotFound() }
                    };
                    var report = new ContentPreparationReport([], MissingOnRestore
                        ? [new(new ContentReference.Generation(new("fixture:missing")), null, "runtime", [])] : [], [], []);
                    return new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(view, report: report));
                }
            };
            var options = new CoreSessionOptions(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1);
            var host = new HostCoordinator(core, Content, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
                new(new("builtin"), new("mods"), null, "engine-save", []), logger, sessionOptions: options);
            Require(host.BeginRestore("engine core", "engine content", options).Problem is null, "无法开始初始恢复。");
            return host;
        }

    }
}
