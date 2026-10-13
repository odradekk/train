using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Saves;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class DeveloperToolsTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Real_app_registers_dev_window_and_condition_controls_stop_at_the_requested_boundary()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = Setup();
        try
        {
            var app = await Ready(fixture, setup);
            Require(OS.IsDebugBuild() && app.Development is not null, "Debug宿主未安装开发工具。");
            var inspector = app.GetChildren().OfType<DeveloperInspector>().Single();
            var console = app.GetChildren().OfType<GameConsole>().Single();
            console.SubmitLine("dev open");
            Require(inspector.Visible, "dev open没有打开真实窗口。");
            console.SubmitLine("dev close");
            Require(!inspector.Visible, "dev close没有关闭真实窗口。");
            ((LineEdit)inspector.FindChild("ConditionValue", true, false)).Text = "2";
            ((Button)inspector.FindChild("RunUntil", true, false)).EmitSignal(Button.SignalName.Pressed);
            await Until(() => !app.Development!.Running);
            Require(app.Host!.GetStatus()!.View.Position == new SimulationPosition(2, 0) &&
                setup.InitialSession.AdvanceCount == 2, "开发窗口没有在目标完整边界停止。");
            app._Process(0);
            Require(setup.InitialSession.AdvanceCount == 2, "停止后又推进了核心。");
            Require(app.Development!.Inspect(new WorldObjectId.Node(new(1)))?.Status == "found" &&
                app.Development.Inspect(new WorldObjectId.Train(new(1)))?.Status == "found", "对象检查没有使用当前契约读视图。");
        }
        finally { await fixture.Cleanup(); CleanFiles(setup); }
    }

    [Test]
    public async Task Opening_replay_uses_leave_protection_then_exports_and_restores_an_ordinary_session()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = Setup();
        try
        {
            var app = await Ready(fixture, setup);
            var tools = app.Development!;
            Require(app.Host!.RenameSave("unsaved fixture") is null, "无法准备离开保护。");
            var binding = app.Host.GetStatus()!.Binding;
            Require(tools.OpenReplay(setup.ReplayPath) is null && app.Saves!.Leaving is { Waiting: false },
                "打开复现包绕过了未保存对局的离开保护。");
            Require(setup.InitialSession.DisposeCount == 0 && app.Host.GetStatus()!.Binding == binding,
                "确认离开之前替换了核心。");
            app.Saves!.ChooseLeave(LeaveChoice.Discard);
            await Until(() => app.Host.GetStatus() is { IsReplay: true } && !tools.IsBusy);
            Require(setup.InitialSession.DisposeCount == 1 && app.Host.GetStatus()!.Binding != binding,
                "重放候选激活后没有退役原会话。");
            Require(app.Saves.Save()?.Code == "replay_read_only" && app.Host.Step(1).Problem?.Code == "replay_read_only",
                "重放允许普通保存或普通核心推进。");
            Require(tools.StartSteps(1) is null, "无法开始重放单步。");
            await Until(() => !tools.Running);
            Require(app.Host.GetStatus()!.View.Position == new SimulationPosition(1, 0), "重放单步边界错误。");
            Require(tools.AdvanceTo(new(2, 0)) is null, "无法推进到录制位置。");
            await Until(() => !tools.Running);
            Require(setup.Replay!.AdvanceCalls.SequenceEqual(new[] { (1u, (SimulationPosition?)null), (1u, (SimulationPosition?)new SimulationPosition(2, 0)) }),
                "重放没有使用单步预算与明确的停止位置。");
            Require(tools.ExportReplayBoundary(setup.ExportPath) is null, "无法导出重放边界。");
            await Until(() => !tools.IsBusy);
            Require(File.Exists(setup.ExportPath), "导出没有写入所选文件。");
            Require(tools.LoadSave(setup.ExportPath) is null, "导出的边界不能进入普通读档。");
            await Until(() => app.Host.GetStatus() is { IsReplay: false } && !app.Saves.IsBusy);
            Require(setup.Replay.DisposeCount == 1 && setup.RestoredSessions.Count == 1 &&
                setup.Candidates.All(candidate => candidate.ActivationCount == 1) &&
                app.Host.GetStatus()!.View.Position == new SimulationPosition(2, 0), "普通读档未激活独立候选或未释放重放。");
        }
        finally { await fixture.Cleanup(); CleanFiles(setup); }
    }

    [Test]
    public async Task Legacy_app_without_contract_host_has_no_developer_window()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            await fixture.AddToRoot(app);
            Require(app.Host is null && app.Development is null && !app.GetChildren().OfType<DeveloperInspector>().Any(),
                "没有新宿主时不应注册开发窗口。");
            app.GetChildren().OfType<GameConsole>().Single().SubmitLine("dev open");
            Require(!app.GetChildren().OfType<DeveloperInspector>().Any(), "旧启动路径的dev创建了开发窗口。");
        }
        finally { await fixture.Cleanup(); }
    }

    private static DeveloperToolsFixture Setup() => new(Path.Combine(Path.GetTempPath(), "train-engine-developer", Guid.NewGuid().ToString("N")));
    private static void CleanFiles(DeveloperToolsFixture setup)
    {
        File.Delete(setup.ReplayPath);
        File.Delete(setup.ExportPath);
        Directory.Delete(Path.GetDirectoryName(setup.ReplayPath)!);
    }
    private async Task<Train.App.App> Ready(Fixture fixture, DeveloperToolsFixture setup)
    {
        var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
        app.CreateHost = setup.CreateHost;
        app.CreateScenes = () => setup.Scenes;
        await fixture.AddToRoot(app);
        await Until(() => app.Host!.GetStatus() is not null);
        app.Saves!.SetAutoSaveInterval(null);
        return app;
    }
    private async Task Until(Func<bool> completed)
    {
        var limit = System.Diagnostics.Stopwatch.StartNew();
        while (!completed() && limit.Elapsed < TimeSpan.FromSeconds(30))
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
        Require(completed(), "开发工具真实引擎接线超时。");
    }
}
