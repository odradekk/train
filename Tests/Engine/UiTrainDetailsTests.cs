using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class UiTests
{
    [Test]
    public async Task Train_list_displays_rejection_in_the_selected_inspector()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        var plan = new RunPlan(false, [new PlanStop.Waypoint(new(1))]);
        var view = EconomyFixture.Create(trainActivity: TrainActivity.Waiting, trainPlan: plan);
        var rejected = EconomyFixture.Create(2, trainActivity: TrainActivity.Waiting, trainPlan: plan);
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        setup.Session.QueueSubmit(new CoreCommand.StopTrain(new(7)), CommandSource.UserInterface,
            new SubmissionResult.Completed(new(1, view.Position, rejected.Position,
                new CommandDecision.Rejected([new("no_route", "train.plan", new WorldObjectId.Train(new(7)), [])])), [], null), rejected);
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            await _input.Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            Find<Button>(ui, "Fleet").GrabFocus();
            await _input.PressKey(Key.Space);
            var list = Find<Tree>(Find<PanelContainer>(ui, "FleetList"), "Objects");
            await _input.ClickItem(list, 0);
            var details = Find<PanelContainer>(ui, "Details");
            var stop = Find<Button>(details, "Stop");
            Require(stop.IsVisibleInTree() && !stop.Disabled, "点击车队列表没有打开可停车的列车详情。");
            stop.GrabFocus();
            await _input.PressKey(Key.Space);
            var reason = Find<Label>(details, "ActionResult");
            Require(setup.Session.SubmitCount == 1 && reason.IsVisibleInTree() &&
                reason.Text.Contains("没有可达路径。") &&
                !reason.Text.Contains("no_route"), "真实停车命令的拒绝没有显示中文理由。");
            setup.Session.AssertComplete();
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

    [Test]
    public async Task Disband_cancel_and_session_replacement_cannot_submit_old_train_actions()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        var view = EconomyFixture.Create(emptyTrainInventory: true);
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content(mapPresentation: true));
        var replacement = UiWorldFixture.Session(EconomyFixture.Create(emptyTrainInventory: true));
        setup.ReplacementCandidate = new(replacement, HostCoreScenario.References);
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            await _input.Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.Activate(Find<Button>(ui, "Fleet"));
            await _input.ClickItem(Find<Tree>(Find<PanelContainer>(ui, "FleetList"), "Objects"), 0);
            var details = Find<PanelContainer>(ui, "Details");
            await _input.ChooseTab(Find<TabContainer>(details, "Pages"), 3);
            var disband = Find<Button>(details, "Disband");
            Require(!disband.Disabled, "停在聚居地的空载列车不可解编。");
            disband.GrabFocus();
            await _input.PressKey(Key.Space);
            var panels = ui.GetNode<WorldPanels>("Root/World/WorldPanels");
            var confirmation = panels.GetNode<ConfirmationDialog>("DisbandConfirmation");
            Require(confirmation.Visible && !confirmation.GetOkButton().Disabled,
                "真实解编输入没有打开可确认的原生窗口。");
            Require(confirmation.DialogText.Contains("1") && confirmation.DialogText.Contains("青川") &&
                confirmation.DialogText.Contains("无货") && confirmation.DialogText.Contains("计划"),
                "解编确认缺少车辆数量、地点、空载状态或计划处理说明。");
            confirmation.GetCancelButton().GrabFocus();
            await _input.PressKey(Key.Space, window: confirmation);
            Require(!confirmation.Visible && setup.Session.SubmitCount == 0,
                "取消原生解编确认提交了命令或未关闭窗口。");
            disband.GrabFocus();
            await _input.PressKey(Key.Space);
            Require(confirmation.Visible, "再次解编没有打开确认窗口。");
            var binding = app.Host!.GetStatus()!.Binding;
            var result = app.Host.BeginRestore("opaque core", "opaque content",
                new(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1));
            Require(result.Problem is null, "替换对局的有限情景未被接受。");
            await _input.WaitUntil(() => app.Host.GetStatus()!.Binding != binding);
            await _input.Frames();
            Require(!confirmation.Visible && !GodotObject.IsInstanceValid(disband) &&
                ui.FindChildren("Description", "Label", true, false).Count == 0 &&
                replacement.SubmitCount == 0 && setup.Session.SubmitCount == 0,
                "替换对局后旧详情或解编确认窗口仍然打开，或产生额外提交。");
            setup.Session.AssertComplete();
            replacement.AssertComplete();
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }
}
