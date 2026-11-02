using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests
{
    [Test]
    public async Task Purchased_vehicle_enters_existing_formation_with_same_identity()
    {
        var before = EconomyFixture.Create();
        var owned = EconomyFixture.Create(2, vehicle: EconomyFixture.VehicleState.Owned);
        owned.FormationPreviews = TrainFormationTestScenario.Vehicle9;
        var formed = EconomyFixture.Create(3, vehicle: EconomyFixture.VehicleState.Formed);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueSubmit(new CoreCommand.BuyVehicle(new(1), new(9), new(917)), CommandSource.UserInterface,
            Completed(1, before, owned, new CommandDecision.Accepted([], [])), owned);
        setup.Session.QueueSubmit(new CoreCommand.FormTrain([new(9)]), CommandSource.UserInterface,
            Completed(2, owned, formed, new CommandDecision.Accepted([new WorldObjectId.Train(new(8)), new WorldObjectId.Inventory(new(13))], [])), formed);
        await Run(setup, async (ui, economy) =>
        {
            await Page(economy, 2);
            await _input.Activate(Find<CheckBox>(economy, "BuyVehicle9"));
            Require(Find<Label>(economy, "PurchaseTotal").Text.Contains("917"), "车辆总额未按核心报价自动显示。");
            await UiCapture.Capture(TestScene, "economy-vehicle");
            await _input.Activate(Find<Button>(economy, "BuyVehicles"));
            var confirmation = Find<ConfirmationDialog>(economy, "BuyVehiclesConfirmation");
            Require(confirmation.Visible && confirmation.DialogText.Contains("917"), "购买确认没有显示已选车辆的总额。");
            await _input.Activate(Find<Button>(confirmation, "BuyOnly"), confirmation);
            await _input.WaitUntil(() => ReferenceEquals(ui.View, owned));
            var result = Find<Label>(economy, "VehicleResult").Text;
            Require(result.Contains("已买入") && !result.Contains("已不在") && !result.Contains("已不存在") &&
                !result.Contains("车辆 9"), "买入成交后应显示成功，不得把已成交车辆说成不存在或显示内部编号：" + result);
            Require(!Find<Label>(economy, "PurchaseTotal").Text.Contains("917"), "成交后仍显示旧购买总额。");
            Require(Find<CheckBox>(economy, "ParkedVehicle9").ButtonPressed, "只购买后应保持停放车辆的勾选。");
            await _input.Activate(Find<Button>(economy, "FormParked"));
            var formation = Find<TrainFormationWorkspace>(ui, "TrainFormationWorkspace");
            var available = Find<VBoxContainer>(formation, "AvailableVehicles");
            Require(available.GetChildCount() == 1 && Find<CheckBox>(available, "Vehicle9").IsVisibleInTree(),
                "买后原车辆未进入编组候选。");
            Require(Find<CheckBox>(available, "Vehicle9").ButtonPressed &&
                Find<VBoxContainer>(formation, "OrderedVehicles").GetChildCount() == 1, "停放页勾选的车辆没有预选进入编组。");
            await _input.Activate(Find<Button>(formation, "FormOnly"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, formed));
            Require(formed.FindTrain(new(8)) is Lookup<TrainView>.Found purchased &&
                purchased.Value.Vehicles.SequenceEqual(new VehicleId[] { new(9) }) &&
                formed.FindTrain(new(7)) is Lookup<TrainView>.Found existing &&
                existing.Value.Vehicles.SequenceEqual(new VehicleId[] { new(10) }) &&
                formed.FindInstance(new InstanceId.Vehicle(new(9))) is Lookup<InstanceView>.Found
                    { Value.Details: InstanceDetails.Vehicle { Train: { Value: 8 } } },
                "购入车辆没有进入新列车8，或原列车7被替换。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Sold_vehicle_result_reports_the_sale_instead_of_a_missing_selection()
    {
        var before = EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.Owned);
        var sold = EconomyFixture.Create(2);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueSubmit(new CoreCommand.SellVehicle(new(1), new(9), new(917)), CommandSource.UserInterface,
            Completed(1, before, sold, new CommandDecision.Accepted([], [])), sold);
        await Run(setup, async (ui, economy) =>
        {
            await Page(economy, 2);
            await _input.Activate(Find<CheckBox>(economy, "ParkedVehicle9"));
            await _input.Activate(Find<Button>(economy, "SellVehicles"));
            var confirmation = Find<ConfirmationDialog>(economy, "SellVehiclesConfirmation");
            Require(confirmation.Visible && confirmation.DialogText.Contains("917"), "出售确认没有显示核心报价总额。");
            await _input.Activate(confirmation.GetOkButton(), confirmation);
            await _input.WaitUntil(() => ReferenceEquals(ui.View, sold));
            var result = Find<Label>(economy, "VehicleResult").Text;
            Require(Find<CheckBox>(economy, "BuyVehicle9").IsVisibleInTree() && result.Contains("已卖出") &&
                !result.Contains("已不在") && !result.Contains("已不存在") && !result.Contains("车辆 9"),
                "卖出成交后应显示成功，并把原车辆列入在售现车，不得误报消失或显示内部编号：" + result);
        });
        setup.Session.AssertComplete();
    }

}
