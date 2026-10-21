using System.Collections.Immutable;
using System.Diagnostics;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Recording;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class RecordingTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Report_panel_exports_the_memory_window_and_remains_available_after_a_fault()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var input = new UiTestInput(TestScene);
        UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
        var setup = new HostSimulationTests.HostSetup();
        var start = new RecordingStart(new(1), new FixedCoreCapture(new(new(0, 0), "engine start", "start sha"),
            HostCoreScenario.References, "engine original state", HostCoreScenario.Input, new(1)));
        var window = new CoreRecordingWindow(new(0, 0), [start], [], [start.State.Checkpoint], [], [], start.State.References);
        var runtime = new RuntimeIdentity("fixture", "fixture", "fixture", "fixture", "fixture");
        var confirms = 0;
        var reportRoot = Path.GetDirectoryName(System.Environment.GetEnvironmentVariable("TRAIN_ENGINE_REPORT"))!;
        var exportDirectory = Path.Combine(reportRoot, "recording-artifacts");
        Directory.CreateDirectory(exportDirectory);
        Train.App.App? app = null;
        try
        {
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger =>
            {
                var host = setup.Create(logger);
                setup.Session.ReadRecordingCall = () => window;
                setup.Session.ConfirmRecordingStartCall = (point, outcome) =>
                {
                    Require(point == start.Id && outcome == RecordingWriteOutcome.PublishedWithDependencies && ++confirms == 1,
                        "初始起点没有在发布成功后恰好确认一次。");
                    return new([], []);
                };
                return host;
            };
            await fixture.AddToRoot(app);
            await FramesUntil(() => app.Recording!.Status?.HasUsableStart == true && !app.Recording.IsBusy);
            var recording = app.Recording!;
            Require(File.Exists(recording.SnapshotPath), "初始录制起点未自动落盘。");
            var menu = app.GetNode<MainMenu>("MainMenu");
            var ui = app.GetNode<GameUi>("GameUi");
            Require(!menu.IsVisibleInTree(), "对局中仍显示主菜单。");
            await input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Settings"));
            var settings = ui.GetNode<SettingsPanel>("Root/SettingsPanel");
            var button = settings.GetNode<Button>("Settings/Layout/Content/Body/ReportIssue");
            Require(button.IsVisibleInTree(), "新宿主没有显示报告问题入口。");
            await UiCapture.Capture(TestScene, "session-report-settings");
            await input.Activate(button);
            var panel = settings.GetNode<IssueReportPanel>("IssueReportPanel");
            await FramesUntil(() => panel.Visible && Labels(panel).Any(label => label.Text == "正在录制"));
            Require(Labels(panel).Any(label => label.Text.Contains("0:0 → 0:0", StringComparison.Ordinal)) &&
                Labels(panel).Any(label => label.Text.Contains(recording.EvidenceDirectory, StringComparison.Ordinal)),
                "报告面板没有显示录制范围和证据入口。");
            if (DisplayServer.GetName() != "headless" &&
                System.Environment.GetEnvironmentVariable("TRAIN_UI_CAPTURE_DIR") is { Length: > 0 } captureDirectory)
            {
                await TestScene.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = panel.GetTexture().GetImage();
                Require(image.SavePng(Path.Combine(captureDirectory, "session-issue-report.png")) == Error.Ok,
                    "报告窗口截图保存失败。");
            }
            panel.Export(Path.Combine(exportDirectory, "report.zip"));
            await FramesUntil(() => !recording.IsBusy);
            Package(window).Validate(Path.Combine(exportDirectory, "report.zip"));

            var fault = new CoreFault("engine_recording_fault", new(0, 0), "step", "fixture", new WorldObjectId.Train(new(1)));
            var checkpoint = new CoreCheckpoint(fault.Position, "engine frozen boundary", "fault sha");
            var segment = new FaultRecordingSegment(start, [], [start.State.Checkpoint, checkpoint], fault, start.State.References);
            window = window with { Checkpoints = segment.Checkpoints, FaultSegments = [segment] };
            setup.Session.QueueAdvance(new(0, fault.Position, [], fault, new("recording fault", "engine stack")),
                new FixedCoreReadView(fault.Position, HostCoreScenario.Input, fault)
                { Economy = UiWorldFixture.Economy, World = app.Host!.GetStatus()!.View.World });
            app.Host!.Step(1);
            await FramesUntil(() => !recording.IsBusy && recording.Status?.Frozen == true);
            var faultPath = Directory.GetFiles(recording.EvidenceDirectory,
                $"fault-{app.Host.GetStatus()!.Binding.Session:N}-*.zip").Single();
            Package(window).Validate(faultPath);
            Require(Labels(panel).Any(label => label.Text == "录制已停止在故障边界（核心已冻结，仍可导出）") &&
                !Labels(panel).Any(label => label.Text.Contains("正在录制", StringComparison.Ordinal)) &&
                !panel.FindChildren("*", "Button", true, false).Cast<Button>().Single(candidate => candidate.Text == "导出复现 ZIP").Disabled,
                "冻结后报告入口或导出被禁用。");
            panel.Export(Path.Combine(exportDirectory, "frozen-report.zip"));
            await FramesUntil(() => !recording.IsBusy);
            Package(window).Validate(Path.Combine(exportDirectory, "frozen-report.zip"));
            Require(File.Exists(faultPath) && confirms == 1, "报告导出裁剪或删除了独立故障证据。");
            panel.Hide();
            Require(app.Host.LockLeaving() is null, "离开测试会话没有取得保护锁。");
            app.Host.LeaveSession();
            app.Host.UnlockLeaving();
            await input.WaitUntil(() => menu.IsVisibleInTree() && !settings.IsVisibleInTree());
            await input.Activate(menu.GetNode<Button>("%Settings"));
            await input.Activate(button);
            Require(panel.Visible && Labels(panel).Any(label => label.Text == "当前没有活动会话"),
                "主菜单设置不能打开报告问题，或仍显示旧对局录制状态。");
            Require(!app.GetChildren().OfType<GameConsole>().Single().LogEvents
                .Any(log => log.EventId == "host_consumer_failed"), "录制故障边界未成功接入界面。");
            GD.Print("ISSUE_322_RECORDING_REPORT_VERIFIED");
        }
        finally
        {
            if (app?.Recording is { } recording)
            {
                var drained = false;
                recording.Close(() => drained = true);
                await FramesUntil(() => drained);
            }
            await fixture.Cleanup();
        }

        RecordingPackage Package(CoreRecordingWindow recorded) => RecordingPackage.Capture(recorded,
            new FixedContentReplayCapture([], "engine recording content"), runtime);
    }

    private static IEnumerable<Label> Labels(IssueReportPanel panel) => panel.FindChildren("*", "Label", true, false)
        .Cast<Label>().Where(label => label.GetWindow() == panel);

    private async Task FramesUntil(Func<bool> completed)
    {
        var guard = Stopwatch.StartNew();
        while (!completed() && guard.Elapsed < TimeSpan.FromSeconds(30))
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
        Require(completed(), "真实 Godot 帧未完成报告状态或文件发布。");
    }
}
