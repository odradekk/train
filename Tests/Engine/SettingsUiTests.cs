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

    [Test]
    public async Task CameraShakeDefaultsOnPersistsAndRejectsInvalidValues()
    {
        await LoadSettings(1, 1.25, 91, true, invalid: false, cameraShake: false);
        await LoadSettings(1, 1.25, 91, true, invalid: true, cameraShake: "no");
    }

    [Test]
    public async Task Quality_and_autosave_apply_at_startup_persist_changes_and_reject_invalid_values()
    {
        await LoadSettings(1, 1.25, 91, true, invalid: false, quality: 1, autoSave: 0);
        await LoadSettings(1, 1.25, 91, true, invalid: false, quality: 0, autoSave: 30, changeThroughUi: true);
        await LoadSettings(1, 1.25, 91, true, invalid: true, quality: 2);
        await LoadSettings(1, 1.25, 91, true, invalid: true, autoSave: 7);
        await LoadSettings(1, 1.25, 91, true, invalid: true, autoSave: "5");
    }

    private async Task LoadSettings(Variant mode, Variant scale, Variant fps, Variant focus, bool invalid,
        Variant? encounter = null, Variant? cameraShake = null, Variant? quality = null, Variant? autoSave = null,
        bool changeThroughUi = false)
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
                if (cameraShake is { } shake) config.SetValue("game", "camera_shake", shake);
                if (encounter is { } notification) config.SetValue("notifications", "encounter", notification);
                if (quality is { } qualityValue) config.SetValue("display", "quality", qualityValue);
                if (autoSave is { } autoSaveValue) config.SetValue("game", "autosave_minutes", autoSaveValue);
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
            var expectedShake = !invalid && cameraShake is { } shakeValue ? shakeValue.AsBool() : true;
            var shakeBox = panel.FindChildren("CameraShake", "CheckButton", true, false).Cast<CheckButton>().Single();
            var shakeEnabled = app.WorldScenes!.View.CameraRig.ShakeEnabled;
            Require(selectedMode == expectedMode &&
                Mathf.IsEqualApprox(tree.Root.ContentScaleFactor, invalid ? 1 : 1.25f) &&
                Godot.Engine.MaxFps == (invalid ? 73 : 91) &&
                app.Host!.AutoPauseOnFocusLoss == !invalid,
                $"启动设置未应用或未回退：mode={selectedMode}/{expectedMode}，scale={tree.Root.ContentScaleFactor}，fps={Godot.Engine.MaxFps}，focus={app.Host!.AutoPauseOnFocusLoss}，invalid={invalid}。");
            Require(shakeBox.Text == "镜头抖动" && shakeBox.ButtonPressed == expectedShake && shakeEnabled == expectedShake,
                $"镜头抖动未按设置应用到相机：显示={shakeBox.ButtonPressed}，相机={shakeEnabled}，期望={expectedShake}。");
            var expectedHigh = !invalid && quality is { } highValue && highValue.AsInt32() == 1;
            var expectedAutoSave = invalid || autoSave is null ? 5 : autoSave.Value.AsInt32();
            AssertQualityAndAutoSave(app, ui, panel, expectedHigh, expectedAutoSave);
            if (changeThroughUi)
            {
                Select(panel, "Quality", expectedHigh ? 0 : 1);
                Select(panel, "AutoSave", 0);
                AssertQualityAndAutoSave(app, ui, panel, !expectedHigh, 0);
                using var saved = new ConfigFile();
                Require(saved.Load("user://settings.cfg") == Error.Ok &&
                    saved.GetValue("display", "quality").AsInt32() == (expectedHigh ? 0 : 1) &&
                    saved.GetValue("game", "autosave_minutes").AsInt32() == 0,
                    "界面修改的画质或自动存档没有写入设置文件。");
            }
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

    private static void AssertQualityAndAutoSave(Train.App.App app, GameUi ui, SettingsPanel panel, bool high, int autoSaveMinutes)
    {
        var views = new[] { app.WorldScenes?.View, app.TemplateScenes?.View, ui.GetNode<DesignEditorPanel>("Root/DesignEditor").Scene.View }
            .OfType<WorldSceneView>().ToArray();
        Require(views.Length >= 2, "缺少世界或编辑器场景视图，无法检查画质。");
        foreach (var view in views)
        {
            var environment = view.CurrentEnvironment;
            var sun = view.GetNode<DirectionalLight3D>("Sun");
            Require(environment.SsilEnabled == high && environment.SsrEnabled == high && sun.LightAngularDistance == 0,
                $"{view.Name} 的画质没有按设置应用：SSIL={environment.SsilEnabled}，SSR={environment.SsrEnabled}，" +
                $"太阳角直径={sun.LightAngularDistance}，期望高档={high}。");
        }
        TimeSpan? expected = autoSaveMinutes == 0 ? null : TimeSpan.FromMinutes(autoSaveMinutes);
        Require(Option(panel, "Quality").Selected == (high ? 1 : 0) && app.Saves!.AutoSaveInterval == expected &&
            Option(panel, "AutoSave").GetItemText(Option(panel, "AutoSave").Selected) ==
                (autoSaveMinutes == 0 ? "关闭" : $"{autoSaveMinutes} 分钟"),
            $"画质或自动存档的显示与生效值不一致：间隔={app.Saves!.AutoSaveInterval}，期望={expected}。");
    }

    private static OptionButton Option(SettingsPanel panel, string name) =>
        panel.FindChildren(name, "OptionButton", true, false).Cast<OptionButton>().Single();

    private static void Select(SettingsPanel panel, string name, int index)
    {
        var option = Option(panel, name);
        option.Select(index);
        option.EmitSignal(OptionButton.SignalName.ItemSelected, index);
    }
}
