using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class TrainInspectorUiTests
{
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
    public async Task Confirming_disband_submits_the_selected_empty_train_once()
    {
        var view = new InspectorView();
        view.Inventory = ((Lookup<InventoryView>.Found)view.FindInventory(view.Train.Inventory)).Value with
        { Stacks = [], Reservations = [], Used = 0, Reserved = 0 };
        await WithInspector(view, async (ui, panel, setup) =>
        {
            var input = new UiTestInput(TestScene);
            await input.ChooseTab(Find<TabContainer>(panel, "Pages"), 3);
            setup.Session.QueueSubmit(new CoreCommand.DisbandTrain(view.Train.Id), CommandSource.UserInterface,
                Completed(1, view, view, new CommandDecision.Accepted([], [])), view);
            await input.Activate(Find<Button>(panel, "Disband"));
            var confirmation = Find<WorldPanels>(ui, "WorldPanels").GetNode<ConfirmationDialog>("DisbandConfirmation");
            await input.Activate(confirmation.GetOkButton(), confirmation);
            Require(setup.Session.SubmitCount == 1 && !confirmation.Visible, "解编确认没有准确提交一次或没有关闭。");
            setup.Session.AssertComplete();
        });
    }
}
