using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Content.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SettingsUiTests
{
    [Test]
    public async Task Cancelling_quit_keeps_settings_paused_until_the_settings_close()
    {
        await WithOverlay(async (app, overlay, input) =>
        {
            var host = app.Host!;
            foreach (var playerPaused in new[] { false, true })
            {
                foreach (var focusPaused in new[] { false, true })
                {
                    overlay.Open();
                    host.SetPaused(playerPaused);
                    host.AutoPauseOnFocusLoss = true;
                    host.SetFocused(!focusPaused);
                    var position = host.GetStatus()!.View.Position;
                    app.RequestQuit();
                    var saves = app.GetNode<SavePanel>("GameUi/Root/SavePanel");
                    await input.WaitUntil(() => UiTestInput.SavePromptVisible(saves, "未保存的更改"));
                    await input.PressKey(Key.Escape);
                    Require(!saves.Visible && app.Saves!.Leaving is null && overlay.Visible && host.GetStatus()!.OverlayPaused,
                        "取消退出后，设置仍打开时世界必须保持覆盖层暂停。");
                    Require(overlay.IsAncestorOf(overlay.GetViewport().GuiGetFocusOwner()), "取消退出后焦点应回到设置。");
                    host.ProcessFrame(1);
                    Require(host.GetStatus()!.View.Position == position, "取消退出后世界仍在推进。");
                    overlay.Hide();
                    var status = host.GetStatus()!;
                    Require(!status.OverlayPaused && status.PlayerPaused == playerPaused && status.FocusPaused == focusPaused &&
                        status.IsPaused == (playerPaused || focusPaused), "关闭设置后不能改变玩家暂停或失焦暂停。");
                    host.SetPaused(true);
                }
            }
        });
    }

    [Test]
    public async Task Settings_has_five_pages_and_escape_returns_focus_to_its_opener()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var input = new UiTestInput(TestScene);
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            var setup = new HostSimulationTests.HostSetup();
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await input.WaitUntil(() => app.Host!.GetStatus() is not null);
            var ui = app.GetNode<GameUi>("GameUi");
            var opener = ui.FindChildren("GameMenu", "MenuButton", true, false).Cast<MenuButton>().Single();
            await input.ChooseMenu(opener, "设置");
            var overlay = ui.FindChildren("SettingsOverlay", "Control", true, false).Cast<Control>().SingleOrDefault();
            Require(overlay is { Visible: true }, "设置应替换为 SettingsOverlay 覆盖层。");
            foreach (var page in new[] { "Display", "Game", "Notifications", "Keys", "About" })
            {
                var button = overlay!.FindChildren(page, "Button", true, false).Cast<Button>().Single();
                await input.Activate(button);
                var visiblePage = overlay.FindChildren(page + "Page", "ScrollContainer", true, false).Cast<Control>().Single();
                Require(visiblePage.IsVisibleInTree(), $"设置页没有显示：{page}。");
            }
            var count = overlay!.FindChildren("*", "Control", true, false).Count;
            for (var index = 0; index <= count; index++)
            {
                await input.PressKey(Key.Tab);
                Require(overlay.IsAncestorOf(overlay.GetViewport().GuiGetFocusOwner()), "Tab 不能离开设置覆盖层。");
            }
            await input.PressKey(Key.Escape);
            Require(!overlay.Visible && opener.HasFocus(), "Esc 关闭设置后应恢复打开它的控件焦点。");
        }
        finally { await fixture.Cleanup(); }
    }

    private async Task WithOverlay(Func<Train.App.App, SettingsOverlay, UiTestInput, Task> check, IContentView? content = null)
    {
        var tree = TestScene.GetTree();
        var window = tree.Root;
        var fixture = new Fixture(tree);
        var path = ProjectSettings.GlobalizePath("user://settings.cfg");
        var original = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var previous = (window.Mode, window.Size, window.ContentScaleFactor, Godot.Engine.MaxFps);
        try
        {
            File.Delete(path);
            window.Mode = Window.ModeEnum.Windowed;
            window.Size = new(1920, 1080);
            window.ContentScaleFactor = 1;
            Godot.Engine.MaxFps = 120;
            var setup = new HostSimulationTests.HostSetup(content: content);
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            var input = new UiTestInput(TestScene);
            await input.WaitUntil(() => app.Host!.GetStatus() is not null);
            var overlay = app.GetNode<SettingsOverlay>("GameUi/Root/SettingsOverlay");
            overlay.Open();
            await check(app, overlay, input);
        }
        finally
        {
            await fixture.Cleanup();
            window.Mode = previous.Mode; window.Size = previous.Size;
            window.ContentScaleFactor = previous.ContentScaleFactor; Godot.Engine.MaxFps = previous.MaxFps;
            if (original is null) File.Delete(path); else File.WriteAllBytes(path, original);
        }
    }
}
