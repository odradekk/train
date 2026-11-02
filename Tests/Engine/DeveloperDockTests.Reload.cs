using System.Text.Json;
using Chickensoft.GoDotTest;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class DeveloperDockTests
{
    [Test]
    public async Task Train_input_errors_and_business_rejections_stay_in_the_console()
    {
        var view = EconomyFixture.Create();
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content(mapPresentation: true));
        setup.Session.QueueSubmit(new CoreCommand.StartTrain(new(7)), CommandSource.Console,
            new SubmissionResult.Completed(new(1, view.Position, view.Position, new CommandDecision.Rejected(
                [new("plan_missing", "Plan", new WorldObjectId.Train(new(7)), [])])), [], null), view);
        await Run(async (app, console) =>
        {
            var ui = app.GetNode<GameUi>("GameUi");
            var toasts = Find<Control>(ui, "Toasts");
            var count = toasts.GetChildCount();
            console.SubmitLine("train");
            console.SubmitLine("train start 7");
            await _input.Frames(2);
            Require(console.LogEvents.Any(item => item.EventId == "train_usage") &&
                console.LogEvents.Any(item => item.EventId == "train_command_failed" && item.Message.Contains("没有运行计划")),
                "语法失败和业务拒绝都必须在控制台留下中文原因。");
            Require(!ui.GetNode<Control>("Root/ErrorPanel").Visible && toasts.GetChildCount() == count,
                "普通列车命令失败不能弹错误面板或生成提示条。");
            setup.Session.AssertComplete();
        }, setup);
    }

    [Test]
    public async Task Reload_failure_stays_in_console_but_the_same_ui_failure_opens_the_error_panel()
    {
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(), EconomyFixture.Content(mapPresentation: true));
        await Run(async (app, console) =>
        {
            var host = app.Host!;
            var ui = app.GetNode<GameUi>("GameUi");
            var panel = ui.GetNode<Control>("Root/ErrorPanel");
            Require(!panel.Visible, "重载前不能存在错误面板。");
            var view = host.GetStatus()!.View;
            setup.Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(
                new FixedCoreCapture(new(view.Position, "fixed", "fixed"), HostCoreScenario.References,
                    "opaque", view.InputIdentity, view.Revision)));
            const string reason = "重载测试内容包缺少必需文件。";
            setup.ContentFactory!.PrepareLoadCall = (request, _, _) => request.Purpose is ContentLoadPurpose.Reload
                ? new ContentResult<IContentCandidate>.Failure([
                    new("fixture_missing", ContentDiagnosticSeverity.Error, "load", null, null, "profile", null, reason)])
                : throw new InvalidOperationException("UNCONFIGURED: " + request.Purpose);
            var ended = new List<HostOperationResult>();
            host.OperationEnded += ended.Add;
            console.SubmitLine("reload");
            await _input.WaitUntil(() => ended.Count == 1);
            await _input.Frames(2);
            Require(ended[0].Source == CommandSource.Console && ended[0].Status == HostOperationStatus.Rejected,
                "控制台重载必须经过真实后台准备并报告来源和拒绝结果。");
            Require(!panel.Visible, "控制台重载失败不能弹错误面板。");
            var warning = ReloadWarnings(app).Single();
            var message = warning.GetProperty("message").GetString();
            Require(warning.GetProperty("category").GetString() == "console" &&
                warning.GetProperty("level").GetString() == "warning" && message == reason,
                "重载失败必须在控制台分类留下中文 warning 原因。");
            Require(host.BeginReload(CommandSource.UserInterface).Problem is null, "UI 来源重载应进入后台准备。");
            await _input.WaitUntil(() => ended.Count == 2);
            await _input.Frames(2);
            Require(ended[1].Source == CommandSource.UserInterface && ended[1].Status == HostOperationStatus.Rejected &&
                panel.Visible && ui.ErrorText == message,
                "相同重载失败从 UI 发起时必须弹板，中文原因应与控制台完全一致。");
            Require(ReloadWarnings(app).Length == 1, "UI 来源失败不能另写控制台操作失败日志。");
        }, setup);
    }

    private static JsonElement[] ReloadWarnings(Train.App.App app)
    {
        using var stream = new FileStream(app.Logger.JsonlPath!, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
            .Where(log => log.GetProperty("event_id").GetString() == "console_operation_failed").ToArray();
    }
}
