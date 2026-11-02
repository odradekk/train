using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Saves;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SaveTests
{
    [Test]
    public async Task Keyboard_focus_stays_in_the_overlay_and_then_in_its_confirmation()
    {
        var fixture = new Fixture(TestScene.GetTree());
        Train.App.App? app = null;
        var owned = new HashSet<Guid>();
        try
        {
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = new SaveSetup().Create;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Saves!.Status is not null);
            owned.Add(app.Saves!.Status!.Identity.Id);
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "存档与读档");
            var panel = ui.GetNode<SavePanel>("Root/SavePanel");
            var count = panel.FindChildren("*", "Control", true, false).Count;
            for (var i = 0; i <= count; i++)
            {
                await _input.PressKey(Key.Tab);
                Require(panel.IsAncestorOf(panel.GetViewport().GuiGetFocusOwner()), "Tab 不能把焦点移到覆盖层下面。");
            }
            app.RequestQuit();
            await _input.WaitUntil(() => UiTestInput.SavePromptVisible(panel, "未保存的更改"));
            var prompt = panel.GetNode<Control>("PromptMask/Prompt");
            for (var i = 0; i <= count; i++)
            {
                await _input.PressKey(Key.Tab);
                Require(prompt.IsAncestorOf(panel.GetViewport().GuiGetFocusOwner()), "确认期间 Tab 不能离开询问区域。");
            }
            await _input.PressKey(Key.Escape);
        }
        finally { await Cleanup(fixture, app, owned); }
    }

    [Test]
    public async Task Save_overlay_is_shared_and_escape_preserves_the_players_pause_and_focus()
    {
        var fixture = new Fixture(TestScene.GetTree());
        Train.App.App? app = null;
        var owned = new HashSet<Guid>();
        try
        {
            UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = new SaveSetup().Create;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Saves!.Status is not null);
            owned.Add(app.Saves!.Status!.Identity.Id);
            var ui = app.GetNode<GameUi>("GameUi");
            var opener = Find<MenuButton>(ui, "GameMenu");
            await _input.ChooseMenu(opener, "存档与读档");
            var panel = app.FindChildren("*", "", true, false).OfType<SavePanel>().Single();
            Require((Node)panel is Control control && control.IsVisibleInTree(), "存档必须显示为游戏内覆盖层。");
            Require(app.Host!.GetStatus() is { PlayerPaused: true, OverlayPaused: true }, "覆盖层应有独立暂停来源。");
            await UiCapture.Capture(TestScene, "saves-empty-in-game");
            await _input.PressKey(Key.Escape);
            Require(!panel.Visible && app.Host.GetStatus() is { PlayerPaused: true, OverlayPaused: false },
                "Esc 关闭覆盖层不能解除玩家暂停。");
            Require(opener.HasFocus(), "关闭覆盖层后焦点应回到打开它的控件。");
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
            var panel = app.GetNode<SavePanel>("GameUi/Root/SavePanel");
            app.Notification((int)Node.NotificationWMCloseRequest);
            await _input.WaitUntil(() => UiTestInput.SavePromptVisible(panel, "未保存的更改"));
            Require(quitRequests == 0 && saves.Leaving is { CanSave: true, Waiting: false }, "窗口关闭绕过了未保存保护。");
            Require(UiTestInput.SavePromptButton(panel, "Confirm").Text == "保存后退出" &&
                UiTestInput.SavePromptButton(panel, "Discard").Text == "不保存，直接退出", "窗口关闭询问必须写明退出操作。");
            UiTestInput.SavePromptButton(panel, "Cancel").EmitSignal(BaseButton.SignalName.Pressed);
            Require(saves.Leaving is null && quitRequests == 0 && !app.Host!.GetStatus()!.OperationLocked,
                "取消关闭没有恢复宿主或仍请求退出。");
            app.GetChildren().OfType<GameConsole>().Single().SubmitLine("quit");
            await _input.WaitUntil(() => UiTestInput.SavePromptVisible(panel, "未保存的更改"));
            Require(quitRequests == 0 && saves.Leaving is { CanSave: true, Waiting: false }, "控制台退出没有使用同一保存保护。");
            UiTestInput.SavePromptButton(panel, "Confirm").EmitSignal(BaseButton.SignalName.Pressed);
            Require(quitRequests == 0 && saves.HasPendingWork, "退出没有等待异步文件发布。");
            await _input.WaitUntil(() => quitRequests == 1);
            Require(publishedAtQuit && !saves.HasPendingWork && saves.Leaving is null,
                "保存成功前执行了退出动作或文件不可读取。");
            Require(app.Host!.RenameSave("discard before quit") is null, "无法准备未保存的退出情景。");
            app.Notification((int)Node.NotificationWMCloseRequest);
            await _input.WaitUntil(() => UiTestInput.SavePromptVisible(panel, "未保存的更改"));
            var discard = UiTestInput.SavePromptButton(panel, "Discard");
            Require(!discard.Disabled && discard.IsVisibleInTree(), "退出时不保存按钮不可用。");
            discard.EmitSignal(BaseButton.SignalName.Pressed);
            Require(quitRequests == 2 && saves.Status!.Unsaved && !saves.HasPendingWork && saves.Leaving is null,
                "不保存按钮未执行退出回调，或错误地确认了保存。");
        }
        finally { await Cleanup(fixture, app, owned); }
    }
}
