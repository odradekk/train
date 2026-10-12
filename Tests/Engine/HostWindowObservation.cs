using System.Text.Json;
using System.Runtime.InteropServices;
using Chickensoft.GodotTestDriver;
using Godot;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

// 单独图形运行的补充观察，不混入 headless 引擎测试。
public static class HostWindowObservation
{
    public static async Task Run(Node scene)
    {
        Require(DisplayServer.GetName() != "headless", "窗口观察必须使用真实窗口。");
        var fixture = new Fixture(scene.GetTree());
        var setup = new HostSimulationTests.HostSetup();
        setup.QueueSteps(10000);
        var samples = new List<object>();
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger.Log);
            await fixture.AddToRoot(app);
            var window = scene.GetWindow();
            async Task Until(Func<bool> condition)
            {
                var deadline = Time.GetTicksMsec() + 5000;
                while (!condition() && Time.GetTicksMsec() < deadline)
                    await scene.ToSignal(scene.GetTree(), SceneTree.SignalName.ProcessFrame);
                Require(condition(), $"真实窗口未达到预期状态：mode={window.Mode} focused={window.HasFocus()} hostFocusPaused={app.Host?.GetStatus()?.FocusPaused}。");
            }
            await Until(() => app.Host!.GetStatus() is not null);
            var host = app.Host!;
            async Task Sample(string phase, bool advances)
            {
                var before = host.GetStatus()!.View.Position.CompletedSteps;
                var started = Time.GetTicksMsec();
                var frames = Godot.Engine.GetProcessFrames();
                while (Time.GetTicksMsec() - started < 1500)
                    await scene.ToSignal(scene.GetTree(), SceneTree.SignalName.ProcessFrame);
                var after = host.GetStatus()!.View.Position.CompletedSteps;
                var elapsed = (Time.GetTicksMsec() - started) / 1000.0;
                Require(advances ? after > before : after == before, $"{phase} 模拟推进与暂停不符。");
                samples.Add(new { phase, before, after, elapsed, mode = window.Mode.ToString(),
                    focused = window.HasFocus(), focusPaused = host.GetStatus()!.FocusPaused,
                    playerPaused = host.GetStatus()!.PlayerPaused, reportedFps = Godot.Engine.GetFramesPerSecond(),
                    observedFps = (Godot.Engine.GetProcessFrames() - frames) / elapsed });
                GD.Print("HOST_WINDOW_SAMPLE " + JsonSerializer.Serialize(samples[^1]));
            }
            window.Mode = Window.ModeEnum.Windowed;
            window.GrabFocus();
            await Until(window.HasFocus);
            host.SetPaused(false);
            await Sample("focused_running", true);
            window.Mode = Window.ModeEnum.Minimized;
            await Until(() => window.Mode == Window.ModeEnum.Minimized);
            await Sample("minimized_auto_pause_off", true);
            window.Mode = Window.ModeEnum.Windowed;
            window.GrabFocus();
            await Until(window.HasFocus);
            // Windows 最小化不一定发送失焦；把前台切到桌面以观察真实 OS 通知。
            Require(OS.HasFeature("windows") && SetForegroundWindow(GetShellWindow()), "未能把前台切换到 Windows 桌面。");
            await Until(() => !window.HasFocus());
            await Sample("unfocused_auto_pause_off", true);
            host.AutoPauseOnFocusLoss = true;
            await Until(() => host.GetStatus()!.FocusPaused);
            await Sample("unfocused_auto_pause_on", false);
            host.SetPaused(true);
            window.GrabFocus();
            await Until(() => window.HasFocus() && !host.GetStatus()!.FocusPaused);
            await Sample("focused_player_pause_preserved", false);
            host.SetPaused(false);
            await Sample("focused_resumed", true);
            var json = JsonSerializer.Serialize(new { outcome = "passed", engine = Godot.Engine.GetVersionInfo()["string"].AsString(), samples },
                new JsonSerializerOptions { WriteIndented = true });
            var directory = ProjectSettings.GlobalizePath("res://.godot/validation/issue-317");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "window-observation.json"), json);
            GD.Print("HOST_WINDOW_OBSERVATION " + json);
        }
        finally { await fixture.Cleanup(); }
    }

    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
