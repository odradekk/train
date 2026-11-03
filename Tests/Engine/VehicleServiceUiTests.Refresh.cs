using Chickensoft.GoDotTest;
using Godot;
using Train.App;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class VehicleServiceUiTests
{
    [Test]
    public async Task Submission_rejection_survives_world_updates_until_sources_change()
    {
        var view = new ServiceView();
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        await Run(setup, async (ui, _) =>
        {
            var work = await OpenRepair(ui, new InstanceId.Vehicle(new(9)));
            setup.Session.QueueSubmit(new CoreCommand.CommissionVehicleWork(view.LastQuote!.Request, new(999)),
                CommandSource.UserInterface, Completed(1, view, view,
                    new CommandDecision.Rejected([new("price_changed", "MaximumPayment", null, [])])), view);
            await _input.Activate(Find<Button>(work, "ConfirmWork"));
            var rejection = Find<Label>(work, "WorkReason").Text;
            Require(rejection.Length > 0 && Find<Button>(work, "ConfirmWork").Disabled,
                "拒绝后应显示原因并禁止重复确认。");
            for (ulong revision = 2; revision <= 4; revision++)
            {
                PublishLiveView(setup, ui, new ServiceView(revision));
                await _input.Frames();
                Require(Find<Label>(work, "WorkReason").Text == rejection && Find<Button>(work, "ConfirmWork").Disabled,
                    "世界推进不得清除提交拒绝原因或恢复重复确认。");
            }
            await _input.Activate(Find<CheckBox>(work, "Source_12"));
            Require(Find<Label>(work, "WorkReason").Text.Length == 0 && !Find<Button>(work, "ConfirmWork").Disabled &&
                setup.Session.SubmitCount == 1, "修改来源后应清除旧拒绝原因，重新允许确认但不自动提交。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task World_refresh_updates_material_cells_without_replacing_them()
    {
        var view = new ServiceView();
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        await Run(setup, async (ui, _) =>
        {
            var work = await OpenRepair(ui, new InstanceId.Vehicle(new(9)));
            var purchase = Find<Label>(work, "MaterialPurchase_0");
            var payment = Find<Label>(work, "MaterialPayment_0");
            var sources = Find<Label>(work, "MaterialSources_0");
            PublishLiveView(setup, ui, new ServiceView(2, blocked: true, nullPayment: true));
            await _input.Frames(2);
            Require(ReferenceEquals(purchase, Find<Label>(work, "MaterialPurchase_0")) &&
                ReferenceEquals(payment, Find<Label>(work, "MaterialPayment_0")) &&
                ReferenceEquals(sources, Find<Label>(work, "MaterialSources_0")),
                "读视图更新应就地更新材料格，不替换控件。");
            Require(purchase.Text == "3" && payment.Text == "500" && sources.Text.Contains("还缺 2"),
                "保留材料格时也应更新新报价的材料缺口。");
        });
        setup.Session.AssertComplete();
    }
}
