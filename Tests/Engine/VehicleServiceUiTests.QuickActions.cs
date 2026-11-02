using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class VehicleServiceUiTests
{
    [Test]
    public async Task Parked_vehicle_repair_buys_local_materials_at_the_displayed_payment()
    {
        var view = new ServiceView(completed: true) { DamagedParkedVehicle = true };
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        var request = new JobRequest.Repair(new(new(20), CapabilityKind.Construction), new InstanceId.Vehicle(new(9)), [], 0);
        setup.Session.QueueSubmit(new CoreCommand.CommissionVehicleWork(request, new(999)), CommandSource.UserInterface,
            Completed(1, view, view, new CommandDecision.Accepted([], [])), view);
        await Run(setup, async (_, panel) =>
        {
            Require(view.QuoteCount == 0, "车辆页隐藏时不应获取快捷修理报价。");
            await Vehicles(panel);
            await _input.Activate(Find<CheckBox>(panel, "ParkedVehicle9"));
            Require(view.LastQuote is { Funding: JobFunding.NpcCommission, Request: JobRequest.Repair repair } &&
                repair.Facility == request.Facility && repair.Instance == request.Instance && repair.Sources.IsEmpty && repair.Priority == 0,
                "停放车修理应自动报价且不消费列车或租库材料。");
            var repairButton = Find<Button>(panel, "RepairVehicles");
            Require(!repairButton.Disabled && repairButton.Text.Contains("999"), "修理按钮应显示核心报价的精确总付款。");
            Require(panel.FindChildren("DismantleVehicles", "Button", true, false).Cast<Button>().All(button => !button.IsVisibleInTree()),
                "NPC 聚居地不提供拆解。");
            await UiCapture.CaptureSizes(TestScene, "vehicles-neutral-parked");
            await _input.Activate(repairButton);
            Require(setup.Session.SubmitCount == 1 && Find<Label>(panel, "VehicleResult").Text.Length > 0,
                "金额已明确的修理应直接提交一次，并就地显示结果。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Owned_parked_vehicle_repair_uses_only_settlement_inventory()
    {
        var view = new ServiceView(owned: true, completed: true);
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        await Run(setup, async (_, panel) =>
        {
            await Vehicles(panel);
            await _input.Activate(Find<CheckBox>(panel, "ParkedVehicle9"));
            Require(view.LastQuote is { Funding: JobFunding.OwnInventory, Request: JobRequest.Repair repair } &&
                repair.Sources.SequenceEqual(new InventoryId[] { new(10) }), "自有修理只应取当地库存 10，不取列车 12 或租库 11。");
            setup.Session.QueueSubmit(new CoreCommand.StartOwnedJob(view.LastQuote!.Request), CommandSource.UserInterface,
                Completed(1, view, view, new CommandDecision.Accepted([], [])), view);
            var button = Find<Button>(panel, "RepairVehicles");
            Require(!button.Disabled && button.Text.Contains("库存材料") && !button.Text.Contains("999"), "自有修理应说明使用库存材料。");
            Require(panel.FindChildren("SellVehicles", "Button", true, false).Cast<Button>().All(button => !button.IsVisibleInTree()),
                "自有聚居地不提供出售。");
            await UiCapture.CaptureSizes(TestScene, "vehicles-owned-selected");
            await _input.Activate(button);
            Require(setup.Session.SubmitCount == 1, "自有修理应直接提交自有施工命令。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Parked_vehicle_dismantling_requires_confirmation_and_cancel_submits_nothing()
    {
        var view = new ServiceView(owned: true, completed: true);
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        var request = new JobRequest.Dismantle(new(new(20), CapabilityKind.Construction), new InstanceId.Vehicle(new(9)), 0);
        setup.Session.QueueSubmit(new CoreCommand.StartOwnedJob(request), CommandSource.UserInterface,
            Completed(1, view, view, new CommandDecision.Accepted([], [])), view);
        await Run(setup, async (_, panel) =>
        {
            await Vehicles(panel);
            await _input.Activate(Find<CheckBox>(panel, "ParkedVehicle9"));
            await _input.Activate(Find<Button>(panel, "DismantleVehicles"));
            var dialog = Find<ConfirmationDialog>(panel, "DismantleVehiclesConfirmation");
            Require(dialog.Visible && setup.Session.SubmitCount == 0, "拆解必须先确认。");
            await UiCapture.CaptureSizes(TestScene, "vehicles-dismantle-confirmation", dialog);
            await _input.Activate(dialog.GetCancelButton(), dialog);
            Require(!dialog.Visible && setup.Session.SubmitCount == 0, "取消拆解不得提交命令。");
            await _input.Activate(Find<Button>(panel, "DismantleVehicles"));
            await _input.Activate(dialog.GetOkButton(), dialog);
            Require(setup.Session.SubmitCount == 1 && Find<Label>(panel, "VehicleResult").Text.Length > 0,
                "确认拆解应提交一次并就地显示结果。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Vehicle_jobs_show_the_same_progress_and_time_as_the_overview()
    {
        var view = new ServiceView();
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        await Run(setup, async (_, panel) =>
        {
            var overview = Find<Label>(panel, "Commissions").Text.Split('\n').Skip(1).Single();
            await Vehicles(panel);
            var jobs = Find<Label>(panel, "VehicleJobs").Text;
            Require(jobs.Contains(overview) && jobs.Contains("20%"), "车辆页和概况应显示同一份委托进度与预计时间说明。");
            Require(setup.Session.SubmitCount == 0 && view.QuoteCount == 0, "仅浏览委托不应提交命令或请求修理报价。");
        });
        setup.Session.AssertComplete();
    }

    private async Task Vehicles(Control panel)
    {
        var tabs = Find<TabContainer>(panel, "Pages");
        await _input.ChooseTab(tabs, Enumerable.Range(0, tabs.GetTabCount()).Single(i => tabs.GetTabTitle(i) == "车辆"));
        await _input.Frames();
    }
}
