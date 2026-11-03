using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests
{
    [Test]
    public async Task Three_vehicle_round_trip_departs_with_at_most_sixteen_mouse_clicks()
    {
        var before = EconomyFixture.Create(extraForSale: [new(11), new(12)], routeToSecondSettlement: true);
        var first = EconomyFixture.Create(2, vehicle: EconomyFixture.VehicleState.Owned, extraForSale: [new(11), new(12)], routeToSecondSettlement: true);
        var second = EconomyFixture.Create(3, vehicle: EconomyFixture.VehicleState.Owned, extraForSale: [new(12)], extraOwned: [new(11)], routeToSecondSettlement: true);
        var bought = EconomyFixture.Create(4, vehicle: EconomyFixture.VehicleState.Owned, extraOwned: [new(11), new(12)], routeToSecondSettlement: true);
        bought.FormationPreviews = [(null, [new(9), new(11), new(12)],
            new CoreResult<TrainFormationPreview>.Success(new(15, 30, 60, 90, [], null)))];
        var formed = EconomyFixture.Create(5, vehicle: EconomyFixture.VehicleState.Formed, extraOwned: [new(11), new(12)], routeToSecondSettlement: true);
        var plan = new RunPlan(true, [new PlanStop.Station(new(1), 0, []), new PlanStop.Station(new(2), 0, [])]);
        var planned = EconomyFixture.Create(6, vehicle: EconomyFixture.VehicleState.Formed, extraOwned: [new(11), new(12)], routeToSecondSettlement: true, purchasedPlan: plan);
        var running = EconomyFixture.Create(7, vehicle: EconomyFixture.VehicleState.Formed, extraOwned: [new(11), new(12)], routeToSecondSettlement: true, purchasedPlan: plan, purchasedRunning: true);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content(vehiclePresentation: true));
        setup.Session.QueueSubmit(new CoreCommand.BuyVehicle(new(1), new(9), new(917)), CommandSource.UserInterface,
            Completed(1, before, first, new CommandDecision.Accepted([], [])), first);
        setup.Session.QueueSubmit(new CoreCommand.BuyVehicle(new(1), new(11), new(917)), CommandSource.UserInterface,
            Completed(2, first, second, new CommandDecision.Accepted([], [])), second);
        setup.Session.QueueSubmit(new CoreCommand.BuyVehicle(new(1), new(12), new(917)), CommandSource.UserInterface,
            Completed(3, second, bought, new CommandDecision.Accepted([], [])), bought);
        setup.Session.QueueSubmit(new CoreCommand.FormTrain([new(9), new(11), new(12)]), CommandSource.UserInterface,
            Completed(4, bought, formed, new CommandDecision.Accepted([new WorldObjectId.Train(new(8))], [])), formed);
        setup.Session.QueueSubmit(new CoreCommand.ReplaceRunPlan(new(8), plan), CommandSource.UserInterface,
            Completed(5, formed, planned, new CommandDecision.Accepted([], [])), planned);
        setup.Session.QueueSubmit(new CoreCommand.StartTrain(new(8)), CommandSource.UserInterface,
            Completed(6, planned, running, new CommandDecision.Accepted([], [])), running);
        await Run(setup, async (ui, panel) =>
        {
            var clicks = 0;
            async Task Click(Control control, Window? dialog = null)
            {
                await _input.Frames(2);
                Require(control.IsVisibleInTree(), "点击目标必须可见：" + control.Name);
                if (control is BaseButton button) Require(!button.Disabled, "点击目标必须可用：" + button.Name);
                await _input.Click(control.GetGlobalRect().GetCenter(), window: dialog); clicks++;
            }
            var tabs = Find<TabContainer>(panel, "Pages");
            await _input.ClickTab(tabs, Enumerable.Range(0, tabs.GetTabCount()).Single(i => tabs.GetTabTitle(i) == "车辆")); clicks++;
            foreach (var id in new[] { 9, 11, 12 }) await Click(Find<CheckBox>(panel, $"BuyVehicle{id}"));
            await UiCapture.CaptureSizes(TestScene, "vehicles-three-selected");
            await Click(Find<Button>(panel, "BuyVehicles"));
            var dialog = Find<ConfirmationDialog>(panel, "BuyVehiclesConfirmation");
            Require(dialog.Visible && dialog.DialogText.Contains("2751"), "确认应显示三辆车的总价。");
            await UiCapture.CaptureSizes(TestScene, "vehicles-three-confirmation", dialog);
            await Click(dialog.GetOkButton(), dialog);
            var formation = Find<TrainFormationWorkspace>(ui, "TrainFormationWorkspace");
            Require(formation.IsVisibleInTree() && new[] { 9, 11, 12 }.All(id => Find<CheckBox>(formation, $"Vehicle{id}").ButtonPressed),
                $"购买后必须直接打开已预选三车的编组：提交 {setup.Session.SubmitCount} 次，确认框可见 {dialog.Visible}，结果 {Find<Label>(panel, "VehicleResult").Text}。");
            await UiCapture.CaptureSizes(TestScene, "vehicles-three-formation");
            await Click(Find<Button>(formation, "Form"));
            var workspace = Find<TrainPlanWorkspace>(ui, "TrainPlanWorkspace");
            Require(workspace.IsVisibleInTree(), "编组完成必须直接进入运行计划。");
            await _input.Frames(3);
            var map = Find<TrainPlanMap>(workspace, "RouteMap");
            await _input.Click(map.GlobalPosition + map.MapToScreen(formed.World.Nodes.Single(node => node.Id.Value == 2).Position)); clicks++;
            Require(Find<VBoxContainer>(workspace, "Stops").GetChildCount() == 2, "地图点击必须添加第二个停靠站。");
            await Click(Find<CheckBox>(workspace, "Loop"));
            await Click(Find<Button>(workspace, "Apply"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, running));
            var train = ((Lookup<TrainView>.Found)ui.View!.FindTrain(new(8))).Value;
            Require(train.Vehicles.SequenceEqual(new VehicleId[] { new(9), new(11), new(12) }) && train.Activity == TrainActivity.Running &&
                train.Plan is { Loop: true } saved && saved.Stops.SequenceEqual(plan.Stops),
                "最终列车必须带着所买三辆车执行两站循环计划并已发车。");
            Require(clicks <= 16 && setup.Session.SubmitCount == 6, $"从已选聚居地到发车使用 {clicks} 次鼠标点击，应不超过16次。");
        });
        setup.Session.AssertComplete();
    }
}
