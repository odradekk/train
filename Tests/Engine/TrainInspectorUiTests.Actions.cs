using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class TrainInspectorUiTests
{
    [Test]
    public async Task Opening_report_from_inspector_confirms_before_discarding_a_plan_draft()
    {
        var view = new InspectorView();
        var totals = new RunAccumulatorView(new(0, 0), new(0), new(0), new(0), 0, 0, null, []);
        view.Train = view.Train with { RecentReports = [new(view.Train.Id, view.Position, RunEndReason.PlanCompleted, totals)] };
        await WithInspector(view, async (ui, inspector, setup) =>
        {
            var input = new UiTestInput(TestScene);
            await input.Activate(Find<Button>(inspector, "EditPlan"));
            var shell = (GameShell)ui.World;
            var plan = (TrainPlanWorkspace)shell.WorkspaceContent!;
            await input.Activate(Find<CheckBox>(plan, "Loop"));
            Require(plan.HasUnsavedChanges, "切换报告前必须先有未保存的计划改动。");
            await input.Activate(Find<Button>(inspector, "OpenReport"));
            var confirmation = Find<ConfirmationDialog>(shell, "WorkspaceSwitchConfirmation");
            Require(confirmation.Visible && ReferenceEquals(shell.WorkspaceContent, plan), "查看报告不能直接丢弃计划草稿。");
            await input.Activate(confirmation.GetCancelButton(), confirmation);
            Require(ReferenceEquals(shell.WorkspaceContent, plan) && plan.HasUnsavedChanges, "取消切换应保留计划草稿。");
            await input.Activate(Find<Button>(inspector, "OpenReport"));
            await input.Activate(confirmation.GetOkButton(), confirmation);
            Require(shell.WorkspaceContent?.Name == "EconomyReports" &&
                Find<VBoxContainer>(Find<VBoxContainer>(shell.WorkspaceContent!, "Train7"), "RunDetails").IsVisibleInTree(),
                "确认放弃草稿后应展开目标列车的报告。");
            Require(setup.Session.SubmitCount == 0, "切换报告不应保存或提交计划。");
        });
    }

    [Test]
    public async Task Overview_report_link_returns_to_the_train_page_and_expands_this_train()
    {
        var view = new InspectorView();
        var totals = new RunAccumulatorView(new(0, 0), new(137), new(29), new(5), 103, 7, .5, []);
        view.Train = view.Train with { RecentReports = [new(view.Train.Id, new(3600, 0), RunEndReason.PlanCompleted, totals)] };
        await WithInspector(view, async (ui, inspector, setup) =>
        {
            var input = new UiTestInput(TestScene);
            await input.Activate(Find<Button>(ui, "EconomyReports"));
            var shell = (GameShell)ui.World;
            await input.ChooseTab(Find<TabContainer>(shell.WorkspaceContent!, "ReportPages"), 1);
            await input.Activate(Find<Button>(inspector, "OpenReport"));
            var workspace = shell.WorkspaceContent!;
            var row = Find<VBoxContainer>(workspace, "Train7");
            Require(Find<TabContainer>(workspace, "ReportPages").CurrentTab == 0 &&
                Find<VBoxContainer>(row, "RunDetails").IsVisibleInTree(), "查看明细应回到列车页并展开这列车。");
            var text = Find<Label>(row, "ReportsText").Text;
            Require(text.Contains("净现金流 103") && text.Contains("销售收入 137") &&
                text.Contains("第1天 00:01") && !text.Contains("步"), "查看明细遗漏了这列车的运行报告。");
            Require(setup.Session.SubmitCount == 0, "查看运行明细提交了命令。");
        });
    }

    [Test]
    public async Task Overview_opens_the_combat_involving_this_train()
    {
        var view = new InspectorView();
        view.Train = view.Train with { Activity = TrainActivity.Combat };
        view.Combat = new(new(4), new WorldLocation.Node(new(1)), 0,
            [new(new CombatUnitId.Instance(new InstanceId.Vehicle(new(10))), false, 0, null)]);
        await WithInspector(view, async (ui, panel, setup) =>
        {
            Require(panel.FindChild("OpenCombat", true, false) is Button, "概况页没有查看当前战斗的入口。");
            await new UiTestInput(TestScene).Activate(Find<Button>(panel, "OpenCombat"));
            var combat = Find<CombatPanel>(ui, "CombatPanel");
            Require(combat.IsVisibleInTree() && combat.SelectedCombat == new CombatId(4), "查看战斗没有打开本列车参战的现有面板。");
            Require(setup.Session.SubmitCount == 0, "查看战斗提交了命令。");
        });
    }

    [Test]
    public async Task Departure_and_disband_controls_follow_every_activity()
    {
        foreach (var activity in Enum.GetValues<TrainActivity>())
        {
            var view = new InspectorView();
            view.Train = view.Train with { Activity = activity, Plan = new(true, [new PlanStop.Station(new(1), 60, [])]) };
            await WithInspector(view, async (ui, panel, setup) =>
            {
                Require(Find<Button>(panel, "Start").IsVisibleInTree() == (activity == TrainActivity.Stopped) &&
                    Find<Button>(panel, "Stop").IsVisibleInTree() == (activity != TrainActivity.Stopped), "发车和停车没有跟随活动状态：" + activity);
                await new UiTestInput(TestScene).ChooseTab(Find<TabContainer>(panel, "Pages"), 3);
                if (activity != TrainActivity.Stopped)
                    Require(Find<Button>(panel, "Disband").Disabled && Find<Label>(panel, "DisbandReason").Text == "先停车才能解编",
                        "运行中解编没有禁用并说明原因：" + activity);
                else
                    Require(Find<Button>(panel, "RepairAll").Disabled && Find<Label>(panel, "RepairReason").Text == "没有损伤，不需要修理",
                        "完好列车仍允许收费修理。");
            });
        }
    }

    [Test]
    public async Task Starting_then_stopping_submits_each_command_once_and_retains_the_selected_page()
    {
        var view = new InspectorView();
        view.Train = view.Train with { Plan = new(true, [new PlanStop.Station(new(1), 60, [])]), NextStopIndex = 0 };
        var running = new InspectorView { Train = view.Train with { Activity = TrainActivity.Running } };
        await WithInspector(view, async (ui, panel, setup) =>
        {
            var input = new UiTestInput(TestScene);
            await input.ChooseTab(Find<TabContainer>(panel, "Pages"), 2);
            setup.Session.QueueSubmit(new CoreCommand.StartTrain(view.Train.Id), CommandSource.UserInterface,
                Completed(1, view, running, new CommandDecision.Accepted([], [])), running);
            setup.Session.QueueSubmit(new CoreCommand.StopTrain(view.Train.Id), CommandSource.UserInterface,
                Completed(2, running, view, new CommandDecision.Accepted([], [])), view);
            await input.Activate(Find<Button>(panel, "Start"));
            Require(Find<Button>(panel, "Stop").IsVisibleInTree(), "发车后没有显示停车。");
            await input.Activate(Find<Button>(panel, "Stop"));
            Require(Find<Button>(panel, "Start").IsVisibleInTree() && Find<TabContainer>(panel, "Pages").CurrentTab == 2 &&
                setup.Session.SubmitCount == 2, "停车后状态、页签或提交次数错误。");
            setup.Session.AssertComplete();
        });
    }

    [Test]
    public async Task Formation_action_opens_the_existing_workspace_with_this_train_selected()
    {
        var view = new InspectorView();
        await WithInspector(view, async (ui, panel, setup) =>
        {
            var input = new UiTestInput(TestScene);
            await input.ChooseTab(Find<TabContainer>(panel, "Pages"), 3);
            await input.Activate(Find<Button>(panel, "Reform"));
            var workspace = Find<TrainFormationWorkspace>(ui, "TrainFormationWorkspace");
            Require(workspace.IsVisibleInTree() && Find<Button>(workspace, "Form").Text == "保存编组" &&
                Find<VBoxContainer>(workspace, "OrderedVehicles").GetChildCount() == 1,
                "调整编组没有打开已有工作区或没有带入当前列车。");
            Require(setup.Session.SubmitCount == 0, "打开编组工作区提交了命令。");
        });
    }

    [Test]
    public async Task Empty_cargo_with_a_reservation_still_cannot_be_disbanded()
    {
        var view = new InspectorView();
        view.Inventory = ((Lookup<InventoryView>.Found)view.FindInventory(view.Train.Inventory)).Value with
        { Stacks = [], Reservations = [new(new(1), 2, [])], Used = 0, Reserved = 2 };
        await WithInspector(view, async (ui, panel, setup) =>
        {
            await new UiTestInput(TestScene).ChooseTab(Find<TabContainer>(panel, "Pages"), 3);
            Require(Find<Button>(panel, "Disband").Disabled && Find<Label>(panel, "DisbandReason").Text.Contains("先卖出或卸下"),
                "预留非空的列车仍能解编。");
        });
    }

    [Test]
    public async Task Confirming_disband_replaces_the_removed_trains_visible_data_with_an_unavailable_notice()
    {
        var view = new InspectorView();
        view.Inventory = ((Lookup<InventoryView>.Found)view.FindInventory(view.Train.Inventory)).Value with
        { Stacks = [], Reservations = [], Used = 0, Reserved = 0 };
        var removed = new InspectorView { Train = view.Train, TrainExists = false };
        await WithInspector(view, async (ui, panel, setup) =>
        {
            var input = new UiTestInput(TestScene);
            var pages = Find<TabContainer>(panel, "Pages");
            await input.ChooseTab(pages, 3);
            var oldTitle = Find<Label>(panel, "Title").Text;
            Require(panel.FindChildren("VehicleDurability", "Label", true, false).OfType<Label>()
                .Any(label => label.IsVisibleInTree()), "解编前没有可见车辆行，无法验证旧数据是否清除。");
            setup.Session.QueueSubmit(new CoreCommand.DisbandTrain(view.Train.Id), CommandSource.UserInterface,
                Completed(1, view, removed, new CommandDecision.Accepted([], [])), removed);
            await input.Activate(Find<Button>(panel, "Disband"));
            var confirmation = Find<WorldPanels>(ui, "WorldPanels").GetNode<ConfirmationDialog>("DisbandConfirmation");
            Require(confirmation.DialogText.Contains("青川") &&
                confirmation.DialogText.Contains($"{view.Train.Vehicles.Length} 辆车停放在") &&
                confirmation.DialogText.Contains("车上无货物") && confirmation.DialogText.Contains("运行计划会删除"),
                "解编确认没有交代车辆数量、停放地点、货物状态和运行计划删除后果。");
            await input.Activate(confirmation.GetOkButton(), confirmation);
            Require(setup.Session.SubmitCount == 1 && !confirmation.Visible, "解编确认没有准确提交一次或没有关闭。");
            setup.Session.AssertComplete();
            Require(ReferenceEquals(setup.Session.CurrentView, removed) &&
                Find<Label>(panel, "ActionResult").Text.Contains("操作已受理"), "解编没有受理并发布移除列车后的读视图。");
            var title = Find<Label>(panel, "Title");
            Require(title.IsVisibleInTree() && title.Text != oldTitle && title.Text.Contains("已不存在"),
                "列车移除后仍显示旧标题。");
            var unavailable = Find<Label>(panel, "Unavailable");
            Require(unavailable.IsVisibleInTree() && !pages.IsAncestorOf(unavailable) && unavailable.Text.Contains("已不存在"),
                "车辆页看不到分页外的列车已不存在提示。");
            Require(!pages.IsVisibleInTree() && Find<VBoxContainer>(panel, "Vehicles").GetChildCount() == 0,
                "列车移除后仍显示旧分页数据或保留车辆行。");
        });
    }
}
