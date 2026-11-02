using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests
{
    [Test]
    public async Task Multiple_vehicle_purchase_preserves_selection_order_and_preselects_formation()
    {
        var before = EconomyFixture.Create(extraForSale: [new(11), new(12)]);
        var first = EconomyFixture.Create(2, extraForSale: [new(11)], extraOwned: [new(12)]);
        var second = EconomyFixture.Create(3, vehicle: EconomyFixture.VehicleState.Owned,
            extraForSale: [new(11)], extraOwned: [new(12)]);
        var bought = EconomyFixture.Create(4, vehicle: EconomyFixture.VehicleState.Owned, extraOwned: [new(11), new(12)]);
        bought.FormationPreviews = [(null, [new(12), new(9), new(11)],
            new CoreResult<TrainFormationPreview>.Success(new(15, 30, 60, 90, [], null)))];
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueSubmit(new CoreCommand.BuyVehicle(new(1), new(12), new(917)), CommandSource.UserInterface,
            Completed(1, before, first, new CommandDecision.Accepted([], [])), first);
        setup.Session.QueueSubmit(new CoreCommand.BuyVehicle(new(1), new(9), new(917)), CommandSource.UserInterface,
            Completed(2, first, second, new CommandDecision.Accepted([], [])), second);
        setup.Session.QueueSubmit(new CoreCommand.BuyVehicle(new(1), new(11), new(917)), CommandSource.UserInterface,
            Completed(3, second, bought, new CommandDecision.Accepted([], [])), bought);
        await Run(setup, async (ui, panel) =>
        {
            var tabs = Find<TabContainer>(panel, "Pages");
            await _input.ChooseTab(tabs, Enumerable.Range(0, tabs.GetTabCount()).Single(i => tabs.GetTabTitle(i) == "车辆"));
            foreach (var id in new[] { 12, 9, 11 }) await _input.Activate(Find<CheckBox>(panel, $"BuyVehicle{id}"));
            Require(Find<Label>(panel, "PurchaseTotal").Text.Contains("2751"), "三辆车合计必须来自各自核心报价。");
            await _input.Activate(Find<Button>(panel, "BuyVehicles"));
            var confirmation = Find<ConfirmationDialog>(panel, "BuyVehiclesConfirmation");
            Require(setup.Session.SubmitCount == 0 && confirmation.Visible, "打开购买确认框不能提前买车。");
            await _input.Activate(confirmation.GetOkButton(), confirmation);
            var formation = Find<TrainFormationWorkspace>(ui, "TrainFormationWorkspace");
            Require(formation.IsVisibleInTree(), "全部买到后必须直接打开编组。");
            Require(new[] { 9, 11, 12 }.All(id => Find<CheckBox>(formation, $"Vehicle{id}").ButtonPressed), "购入的三辆车没有全部预选。");
            var ordered = Find<VBoxContainer>(formation, "OrderedVehicles");
            Require(ordered.GetChildCount() == 3 && setup.Session.SubmitCount == 3, "批量购买或编组预选不完整。");
        });
        setup.Session.AssertComplete();
    }
    [Test]
    public async Task Vehicle_purchase_stops_at_second_rejection_and_keeps_unbought_selection()
    {
        var before = EconomyFixture.Create(extraForSale: [new(11), new(12)]);
        var first = EconomyFixture.Create(2, extraForSale: [new(11)], extraOwned: [new(12)], vehicleTotal: 999);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueSubmit(new CoreCommand.BuyVehicle(new(1), new(12), new(917)), CommandSource.UserInterface,
            Completed(1, before, first, new CommandDecision.Accepted([], [])), first);
        setup.Session.QueueSubmit(new CoreCommand.BuyVehicle(new(1), new(9), new(917)), CommandSource.UserInterface,
            Completed(2, first, first, new CommandDecision.Rejected([new("insufficient_money", "wallet", null, [])])), first);
        await Run(setup, async (ui, panel) =>
        {
            await Page(panel, 2);
            foreach (var id in new[] { 12, 9, 11 }) await _input.Activate(Find<CheckBox>(panel, $"BuyVehicle{id}"));
            await _input.Activate(Find<Button>(panel, "BuyVehicles"));
            var dialog = Find<ConfirmationDialog>(panel, "BuyVehiclesConfirmation");
            await _input.Activate(dialog.GetOkButton(), dialog);
            await _input.Frames(3);
            Require(setup.Session.SubmitCount == 2, "第二辆被拒后不得购买第三辆或自动重试。");
            Require(!Find<TrainFormationWorkspace>(ui, "TrainFormationWorkspace").IsVisibleInTree(), "部分成功不得自动打开编组。");
            Require(Find<CheckBox>(panel, "ParkedVehicle12").IsVisibleInTree() &&
                Find<CheckBox>(panel, "BuyVehicle9").ButtonPressed && Find<CheckBox>(panel, "BuyVehicle11").ButtonPressed,
                "成功车辆应移入停放，未成交车辆应保持勾选。");
            Require(Find<Label>(panel, "VehicleResult").Text.Contains("资金不足"), "部分失败必须说明拒绝原因。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Canceling_vehicle_sale_submits_nothing_and_preserves_selection()
    {
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.Owned), EconomyFixture.Content());
        await Run(setup, async (_, panel) =>
        {
            await Page(panel, 2);
            await _input.Activate(Find<CheckBox>(panel, "ParkedVehicle9"));
            await _input.Activate(Find<Button>(panel, "SellVehicles"));
            var dialog = Find<ConfirmationDialog>(panel, "SellVehiclesConfirmation");
            Require(dialog.Visible && setup.Session.SubmitCount == 0, "出售必须先确认，不得提前提交。");
            await UiCapture.CaptureSizes(TestScene, "vehicles-sale-confirmation", dialog);
            await _input.Activate(dialog.GetCancelButton(), dialog);
            Require(!dialog.Visible && setup.Session.SubmitCount == 0 && Find<CheckBox>(panel, "ParkedVehicle9").ButtonPressed,
                "取消出售不得提交或丢失勾选。");
        });
        setup.Session.AssertComplete();
    }

}
