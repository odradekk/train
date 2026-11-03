using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class VehicleServiceUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Workspace_refreshed_with_stale_host_binding_cannot_submit()
    {
        var view = new ServiceView();
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        await Run(setup, async (ui, _) =>
        {
            var work = await OpenRepair(ui, new InstanceId.Vehicle(new(9)));
            Require(!Find<Button>(work, "ConfirmWork").Disabled, "陈旧绑定测试必须先可确认。");
            var host = ui.GetParent<Train.App.App>().Host!;
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(new(Guid.NewGuid()), view, host.Content!);
            await _input.Frames(2);
            var position = Find<Button>(work, "ConfirmWork").GetGlobalRect().GetCenter();
            await _input.MouseButton(position, Godot.MouseButton.Left, true);
            await _input.MouseButton(position, Godot.MouseButton.Left, false);
            Require(Find<Button>(work, "ConfirmWork").Disabled && setup.Session.SubmitCount == 0,
                "陈旧会话绑定仍允许确认施工命令。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Work_automatically_chooses_first_unblocked_facility()
    {
        var view = new ServiceView(secondFacility: true);
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        await Run(setup, async (ui, _) =>
        {
            var work = await OpenRepair(ui, new InstanceId.Vehicle(new(9)));
            Require(view.LastQuote!.Request is JobRequest.Repair { Facility.Building.Value: 22 } &&
                view.LastQuote.Blockers.IsEmpty && !Find<Button>(work, "ConfirmWork").Disabled,
                "没有跳过首个受阻设施并使用后续可用设施。");
            setup.Session.QueueSubmit(new CoreCommand.CommissionVehicleWork(view.LastQuote.Request, new(999)), CommandSource.UserInterface,
                Completed(1, view, view, new CommandDecision.Accepted([], [])), view);
            await _input.Activate(Find<Button>(work, "ConfirmWork"));
            Require(setup.Session.SubmitCount == 1, "未提交自动选定设施的报价。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Vehicle_manufacture_opens_workspace_and_close_submits_nothing()
    {
        var view = new ServiceView();
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        await Run(setup, async (ui, panel) =>
        {
            var work = await OpenManufacture(ui, panel);
            Require(work.IsVisibleInTree(), "制造入口未打开施工工作区。");
            Require(view.QuoteCount == 0, "尚未选择蓝图就获取了报价。");
            await _input.Activate(Find<Button>(work, "CloseWork"));
            Require(!work.IsVisibleInTree() && setup.Session.SubmitCount == 0, "关闭工作区提交了命令。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Repair_automatically_quotes_default_sources_and_reorders_without_retrying_rejection()
    {
        var view = new ServiceView();
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        await Run(setup, async (ui, panel) =>
        {
            Require(view.QuoteCount == 0, "浏览聚居地不应获取施工报价。");
            var work = await OpenRepair(ui, new InstanceId.Vehicle(new(9)));
            Require(view.LastQuote?.Request is JobRequest.Repair initial &&
                initial.Sources.SequenceEqual(new InventoryId[] { new(12), new(11) }),
                "委托应默认使用当地列车和租库。");
            Require(Find<CheckBox>(work, "Source_11").ButtonPressed && Find<CheckBox>(work, "Source_12").ButtonPressed,
                "可用来源应默认全选。");
            await _input.Activate(Find<Button>(work, "MoveSourceUp_11"));
            Require(view.LastQuote!.Request is JobRequest.Repair reordered &&
                reordered.Sources.SequenceEqual(new InventoryId[] { new(11), new(12) }), "来源排序未自动重报。");
            await _input.Activate(Find<CheckBox>(work, "Source_12"));
            Require(view.LastQuote.Request is JobRequest.Repair selected && selected.Sources.SequenceEqual(new InventoryId[] { new(11) }),
                "取消来源勾选没有自动重新评估。");
            var quotes = view.QuoteCount;
            await _input.Frames(4);
            Require(view.QuoteCount == quotes, "同一输入逐帧重复报价。");
            var quote = view.LastQuote;
            Require(Find<Label>(work, "WorkSummary").Text.Contains("999") && Find<Label>(work, "WorkSummary").Text.Split('\n').Contains("工期：约 50 秒"),
                "合计应使用核心精确付款，预计工期应为100工作量÷2能力=50秒。");
            setup.Session.QueueSubmit(new CoreCommand.CommissionVehicleWork(quote.Request, new(999)), CommandSource.UserInterface,
                Completed(1, view, view, new CommandDecision.Rejected([new("price_changed", "MaximumPayment", null, [])])), view);
            await _input.Activate(Find<Button>(work, "ConfirmWork"));
            await _input.Frames(3);
            Require(setup.Session.SubmitCount == 1 && view.QuoteCount == quotes && Find<Button>(work, "ConfirmWork").Disabled,
                "拒绝后自动重报、重试或仍可确认。");
            Require(Find<Label>(work, "WorkReason").Text.Length > 0 && !Find<Label>(work, "WorkReason").Text.Contains("price_changed"),
                "拒绝应在工作区显示中文原因。");
            Require(Find<CheckBox>(work, "Source_11").ButtonPressed && !Find<CheckBox>(work, "Source_12").ButtonPressed,
                "拒绝后丢失材料来源输入。");
            await _input.Activate(Find<Button>(work, "CloseWork"));
            Require(setup.Session.SubmitCount == 1, "关闭工作区又提交了命令。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Revision_automatically_requotes_and_missing_facility_disables_confirmation()
    {
        var before = new ServiceView(blocked: true);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (ui, _) =>
        {
            var work = await OpenRepair(ui, new InstanceId.Vehicle(new(9)));
            Require(Find<Label>(work, "WorkReason").Text.Contains("货物不足") && Find<Button>(work, "ConfirmWork").Disabled,
                "阻塞报价仍可提交或未显示缺料。");
            var next = new ServiceView(2, nullPayment: true);
            var host = ui.GetParent<Train.App.App>().Host!;
            setup.Session.QueueAdvance(new(1, next.Position, [], null, null), next);
            Require(host.Step(1).Problem is null, "无法发布新边界。");
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, next, host.Content!);
            await _input.Frames(2);
            Require(next.QuoteCount > 0 && Find<Button>(work, "ConfirmWork").Disabled, "新边界未自动报价或空付款仍可委托。");
            var gone = new ServiceView(3, missingFacility: true);
            setup.Session.QueueAdvance(new(1, gone.Position, [], null, null), gone);
            Require(host.Step(1).Problem is null, "无法发布设施移除边界。");
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, gone, host.Content!);
            await _input.Frames(2);
            Require(gone.QuoteCount == 0 && Find<Button>(work, "ConfirmWork").Disabled &&
                Find<Label>(work, "WorkReason").Text.Contains("设施"), "设施移除后仍报价或没有解释。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Partial_local_purchase_and_shortage_are_shown_without_a_confirmable_total()
    {
        var view = new ServiceView(blocked: true, nullPayment: true);
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        await Run(setup, async (ui, _) =>
        {
            var work = await OpenRepair(ui, new InstanceId.Vehicle(new(9)));
            Require(Find<Label>(work, "MaterialPurchase_0").Text == "3" &&
                Find<Label>(work, "MaterialPayment_0").Text == "500" &&
                Find<Label>(work, "MaterialSources_0").Text.Contains("还缺 2"),
                "材料行应分别展示当地可购数量、金额与缺口。");
            var reason = Find<Label>(work, "WorkReason").Text;
            Require(reason.Contains("货物不足") && reason.Contains("可用容量不足") && !reason.Contains("fixture:"),
                "应展示全部核心阻断，隐藏内部编号。");
            Require(Find<Label>(work, "WorkReason").Text.Contains("2"), "缺口应展示核心返回的2件材料。");
            Require(Find<Button>(work, "ConfirmWork").Disabled && view.LastQuote!.TotalPayment is null,
                "部分可购仍缺料时不得确认。");
            await _input.Activate(Find<Button>(work, "CloseWork"));
            Require(setup.Session.SubmitCount == 0, "关闭缺料报价创建了任务。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Core_completion_removes_queue_row_and_opens_existing_formation()
    {
        var before = new ServiceView(); var after = new ServiceView(2, completed: true);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (ui, panel) =>
        {
            await Vehicles(panel);
            Require(Find<Control>(panel, "ConstructionJob_4").IsVisibleInTree(), "车辆委托未显示。");
            var completion = new CoreEvent.ConstructionCompleted(new(4), ConstructionKind.Construction,
                new ConstructionTarget.NewVehicle(new("fixture-vehicle")), new InstanceId.Vehicle(new(9)), []);
            setup.Session.QueueAdvance(new(1, after.Position, [new(after.Position, 0, completion)], null, null), after);
            var host = ui.GetParent<Train.App.App>().Host!;
            Require(host.Step(1).Problem is null, "无法发布完工事件。");
            await _input.Frames(3);
            Require(!panel.FindChildren("ConstructionJob_4", "", true, false).Any(), "完工任务仍显示。");
            await _input.Activate(Find<CheckBox>(panel, "ParkedVehicle9"));
            await _input.Activate(Find<Button>(panel, "FormParked"));
            Require(Find<CheckBox>(Find<VBoxContainer>(ui, "AvailableVehicles"), "Vehicle9").IsVisibleInTree(),
                "完工车辆没有进入现有编组入口。");
        });
        setup.Session.AssertComplete();
    }
}
