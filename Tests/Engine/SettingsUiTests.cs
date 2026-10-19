using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Notifications;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class SettingsUiTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Saved_settings_apply_at_startup_and_focus_notifications_pause_the_host()
    {
        await LoadSettings(1, 1.25, 91, true, invalid: false);
        await LoadSettings(2, 1.25, 91, true, invalid: false);
        await LoadSettings(1, 1.25, 91, true, invalid: false, encounter: 2);
    }

    [Test]
    public async Task Invalid_setting_types_and_ranges_restore_defaults_at_startup()
    {
        await LoadSettings("fullscreen", 1.25, 91, true, invalid: true);
        await LoadSettings(1, "large", 91, true, invalid: true);
        await LoadSettings(1, 1.25, "fast", true, invalid: true);
        await LoadSettings(1, 1.25, 91, "yes", invalid: true);
        await LoadSettings(3, 1.25, 91, true, invalid: true);
        await LoadSettings(1, 3.0, 91, true, invalid: true);
        await LoadSettings(1, 1.25, -1, true, invalid: true);
        await LoadSettings(1, 1.25, long.MaxValue, true, invalid: true);
        await LoadSettings(1, 1.25, 91, true, invalid: true, encounter: "no");
    }

    private async Task LoadSettings(Variant mode, Variant scale, Variant fps, Variant focus, bool invalid,
        Variant? encounter = null)
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var path = ProjectSettings.GlobalizePath("user://settings.cfg");
        var original = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var previousMode = tree.Root.Mode;
        var previousScale = tree.Root.ContentScaleFactor;
        var previousFps = Godot.Engine.MaxFps;
        try
        {
            Godot.Engine.MaxFps = 73;
            using (var config = new ConfigFile())
            {
                config.SetValue("display", "mode", mode);
                config.SetValue("display", "scale", scale);
                config.SetValue("display", "fps", fps);
                config.SetValue("game", "focus_pause", focus);
                if (encounter is { } notification) config.SetValue("notifications", "encounter", notification);
                Require(config.Save("user://settings.cfg") == Error.Ok, "未能保存真实启动配置情景。");
            }
            var setup = new HostSimulationTests.HostSetup();
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await new UiTestInput(TestScene).WaitUntil(() => app.Host!.GetStatus() is not null);
            setup.AssertPrepared();
            var ui = app.GetNode<GameUi>("GameUi");
            var panel = ui.GetNode<SettingsPanel>("Root/SettingsPanel");
            var selectedMode = panel.FindChildren("WindowMode", "OptionButton", true, false)
                .Cast<OptionButton>().Single().Selected;
            var expectedMode = invalid ? 0 : mode.AsInt32();
            var expectedEncounter = invalid || encounter is null ? (int)NotificationAction.SlowToOne : encounter.Value.AsInt32();
            var encounterBox = panel.FindChildren("Notify_encounter", "OptionButton", true, false)
                .Cast<OptionButton>().Single();
            Require(encounterBox.Selected == expectedEncounter &&
                app.Notifications.Action(NotificationCategory.Encounter) == (NotificationAction)expectedEncounter,
                $"通知设置未与显示设置一起应用：selected={encounterBox.Selected}，expected={expectedEncounter}。");
            Require(selectedMode == expectedMode &&
                Mathf.IsEqualApprox(tree.Root.ContentScaleFactor, invalid ? 1 : 1.25f) &&
                Godot.Engine.MaxFps == (invalid ? 73 : 91) &&
                app.Host!.AutoPauseOnFocusLoss == !invalid,
                $"启动设置未应用或未回退：mode={selectedMode}/{expectedMode}，scale={tree.Root.ContentScaleFactor}，fps={Godot.Engine.MaxFps}，focus={app.Host!.AutoPauseOnFocusLoss}，invalid={invalid}。");
            if (DisplayServer.GetName() != "headless")
            {
                var expectedWindowMode = expectedMode switch
                {
                    1 => Window.ModeEnum.Fullscreen,
                    2 => Window.ModeEnum.ExclusiveFullscreen,
                    _ => Window.ModeEnum.Windowed
                };
                Require(tree.Root.Mode == expectedWindowMode, "真实窗口未应用启动时保存的窗口模式。");
            }
            else GD.Print("SettingsUiTests: headless 仅验证保存的模式选择；真实窗口模式需可见窗口运行验证。");
            app.Notification((int)Node.NotificationApplicationFocusOut);
            Require(app.Host!.GetStatus()!.FocusPaused == !invalid,
                "启动设置没有控制真实宿主失焦暂停状态。");
            app.Notification((int)Node.NotificationApplicationFocusIn);
            Require(!app.Host.GetStatus()!.FocusPaused, "恢复聚焦后仍残留失焦暂停。");
            Require(!invalid || (ui.ErrorText.Length > 0 &&
                ui.GetNode<Control>("Root/ErrorPanel").IsVisibleInTree()),
                "无效设置回退没有向玩家显示错误。");
            setup.Session.AssertComplete();
        }
        finally
        {
            await fixture.Cleanup();
            tree.Root.Mode = previousMode;
            tree.Root.ContentScaleFactor = previousScale;
            Godot.Engine.MaxFps = previousFps;
            if (original is null) File.Delete(path); else File.WriteAllBytes(path, original);
        }
    }
}
