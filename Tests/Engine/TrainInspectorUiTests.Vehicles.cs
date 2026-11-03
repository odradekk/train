using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class TrainInspectorUiTests
{
    [Test]
    public async Task Repair_quotes_are_requested_only_for_the_visible_vehicle_page()
    {
        InspectorView Damaged()
        {
            var view = new InspectorView();
            var instance = ((Lookup<InstanceView>.Found)view.FindInstance(new InstanceId.Vehicle(view.Train.Vehicles[0]))).Value;
            view.Instances[instance.Id] = instance with { Structure = new(50, 100) };
            view.Settlement = ((Lookup<SettlementView>.Found)view.FindSettlement(new(1))).Value with
            { Plots = [new(new(1), 1, PlotSize.Small, [], new PlotOccupant.Building(new(40)))] };
            var request = new JobRequest.Repair(new(new(40), CapabilityKind.Construction), instance.Id, [], 0);
            view.RepairQuotes[view.Train.Vehicles[0]] = new(request, JobFunding.NpcCommission, view.Revision,
                1, 1, [], [], [], [], new(2), new(0), new(2), []);
            return view;
        }
        var view = Damaged();
        await WithInspector(view, async (ui, panel, setup) =>
        {
            Require(view.RepairQuoteRequests == 0, "打开概况页时已请求修理报价。");
            var input = new UiTestInput(TestScene);
            var pages = Find<TabContainer>(panel, "Pages");
            await input.ChooseTab(pages, 3);
            Require(view.RepairQuoteRequests > 0, "车辆页未获取受损车辆的修理报价。");
            var requests = view.RepairQuoteRequests;
            var host = ui.GetParent<Train.App.App>().Host!;
            var panels = Find<WorldPanels>(ui, "WorldPanels");
            panels.Refresh(host.GetStatus()!.Binding, view, host.Content!);
            Require(view.RepairQuoteRequests == requests, "未变化的车辆页重复获取修理报价。");
            await input.ChooseTab(pages, 0);
            var later = Damaged();
            panels.Refresh(host.GetStatus()!.Binding, later, host.Content!);
            Require(later.RepairQuoteRequests == 0, "概况页刷新时仍请求修理报价。");
            await input.ChooseTab(pages, 3);
            panel.Hide();
            panels.Refresh(host.GetStatus()!.Binding, later, host.Content!);
            Require(later.RepairQuoteRequests == 0, "关闭检查器后仍刷新修理报价。");
            Require(setup.Session.SubmitCount == 0, "检查报价可见性提交了命令。");
        });
    }

    [Test]
    public async Task Vehicles_show_ordered_durability_totals_and_keep_cargo_from_being_disbanded()
    {
        var view = new InspectorView();
        view.Train = view.Train with { Vehicles = [new(10), new(9)] };
        var second = ((Lookup<InstanceView>.Found)view.FindInstance(new InstanceId.Vehicle(new(9)))).Value;
        view.Instances.Add(second.Id, second with { Structure = new(60, 100), Details = new InstanceDetails.Vehicle(SizeClass.Medium, view.Train.Id) });
        view.Settlement = ((Lookup<SettlementView>.Found)view.FindSettlement(new(1))).Value with { Relation = SettlementRelation.PlayerOwned };
        await WithInspector(view, async (ui, panel, setup) =>
        {
            await new UiTestInput(TestScene).ChooseTab(Find<TabContainer>(panel, "Pages"), 3);
            Require(panel.FindChild("VehicleStats", true, false) is Label, "车辆页没有提供编组统计。");
            var statistics = Find<Label>(panel, "VehicleStats").Text;
            Require(statistics.Contains("总长：20") && statistics.Contains("总质量：10") && statistics.Contains("牵引力：100") &&
                statistics.Contains("载量：100"), "车辆页没有使用列车读视图中的统计。");
            var rows = panel.FindChildren("VehicleDurability", "Label", true, false).OfType<Label>().Select(label => label.Text).ToArray();
            Require(rows.Length == 2 && rows[0].Contains("第 1 辆车") && rows[0].Contains("100/100") &&
                rows[1].Contains("第 2 辆车") && rows[1].Contains("60/100"), "车辆名称、编组顺序或耐久错误。");
            Require(Find<Button>(panel, "Disband").Disabled && Find<Label>(panel, "DisbandReason").Text == "车上还有货物，先卖出或卸下才能解编",
                "有货列车没有禁止解编或说明卸货要求。");
            Require(!Find<Button>(panel, "Reform").Disabled, "停站列车没有调整编组入口。");
        });
    }

    [Test]
    public async Task Full_repair_stops_on_the_first_rejection_and_keeps_the_commissioned_vehicle_visible()
    {
        var view = new InspectorView();
        view.Train = view.Train with { Vehicles = [new(10), new(9), new(11)] };
        foreach (var id in view.Train.Vehicles)
        {
            var vehicle = ((Lookup<InstanceView>.Found)view.FindInstance(new InstanceId.Vehicle(new(10)))).Value
                with { Id = new InstanceId.Vehicle(id) };
            view.Instances[vehicle.Id] = vehicle with { Ownership = SettlementRelation.PlayerOwned,
                Details = new InstanceDetails.Vehicle(SizeClass.Medium, view.Train.Id), Structure = new(50, 100) };
            var request = new JobRequest.Repair(new(new(40), CapabilityKind.Construction), vehicle.Id, [], 0);
            var money = new Money(id.Value == 10 ? 4 : id.Value == 9 ? 7 : 8);
            view.RepairQuotes[id] = new(request, JobFunding.NpcCommission, view.Revision, 2, 1, [], [], [], [], money, new(0), money, []);
        }
        view.Settlement = ((Lookup<SettlementView>.Found)view.FindSettlement(new(1))).Value with
        { Plots = [new(new(1), 1, PlotSize.Small, [], new PlotOccupant.Building(new(40)))] };
        await WithInspector(view, async (ui, panel, setup) =>
        {
            await new UiTestInput(TestScene).ChooseTab(Find<TabContainer>(panel, "Pages"), 3);
            Require(panel.FindChild("RepairAll", true, false) is Button, "车辆页没有全部修理按钮。");
            var button = Find<Button>(panel, "RepairAll");
            Require(!button.Disabled && button.Text.Contains("19"), "全部修理没有显示核心报价的合计。");
            setup.Session.QueueSubmit(new CoreCommand.CommissionVehicleWork(view.RepairQuotes[new(10)].Request, new(4)), CommandSource.UserInterface,
                Completed(1, view, view, new CommandDecision.Accepted([], [])), view);
            setup.Session.QueueSubmit(new CoreCommand.CommissionVehicleWork(view.RepairQuotes[new(9)].Request, new(7)), CommandSource.UserInterface,
                Completed(2, view, view, new CommandDecision.Rejected([new("insufficient_goods", "Materials", null, [])])), view);
            await new UiTestInput(TestScene).Activate(button);
            var result = Find<Label>(panel, "ActionResult").Text;
            Require(setup.Session.SubmitCount == 2 && result.Contains("已委托") && result.Contains("第 1 辆车") &&
                result.Contains("第 2 辆车") && result.Contains("货物不足"), "部分受理没有逐车说明结果，或拒绝后重试了。");
            setup.Session.AssertComplete();
        });
    }
}
