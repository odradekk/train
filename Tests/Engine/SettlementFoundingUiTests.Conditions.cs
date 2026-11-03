using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;
using static Train.Engine.Tests.SettlementFoundingUiFixture;

namespace Train.Engine.Tests;

public partial class SettlementFoundingUiTests
{
    [Test]
    public async Task Empty_node_shows_material_shortfall_in_its_inspector()
    {
        var setup = new HostSimulationTests.HostSetup(World(1, null, materialQuantity: 0), EconomyFixture.Content());
        await Run(setup, async ui =>
        {
            await OpenDetail(ui, 0);
            var inspector = LatestDetail(ui);
            var conditions = Find<Label>(inspector, "FoundingConditions");
            Require(Find<Label>(inspector, "Description").Text.Contains("主食：37"), "检查器没有列出节点资源储量。");
            Require(conditions.Text.Contains("0 / 12"), "空节点检查器没有显示每种材料的已有和需要数量。");
            Require(conditions.Text.Contains("✓ 列车 7 停在这里") && conditions.Text.Contains("× 主食") && conditions.Text.Contains("还差 12"),
                "条件清单没有区分已停靠列车和缺少的材料。");
            Require(Find<Button>(inspector, "FoundSettlement").Disabled &&
                Find<Label>(inspector, "FoundingReason").Text.Contains("还差 12"), "材料不足时必须置灰并解释缺项。");
            Require(Find<GameShell>(ui, "World").WorkspaceContent is null && setup.Session.SubmitCount == 0,
                "查看建立条件不应打开工作区或提交命令。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task First_parked_train_is_selected_and_cancel_consumes_nothing()
    {
        var setup = new HostSimulationTests.HostSetup(World(1, null), EconomyFixture.Content());
        await Run(setup, async ui =>
        {
            await OpenDetail(ui, 0);
            var trains = Find<OptionButton>(LatestDetail(ui), "MaterialSource");
            Require(trains.ItemCount == 1 && trains.Selected == 0 && trains.GetItemText(0) == "列车 7",
                "唯一停靠列车应默认选中，不能显示选择列车占位项。");
            var conditions = Find<Label>(LatestDetail(ui), "FoundingConditions").Text;
            Require(conditions.Contains("✓ 列车 7 停在这里") && conditions.Contains("12 / 12") && !conditions.Contains("×"),
                "打开检查器后应直接显示材料列车和已满足的全部条件。");
            await _input.Activate(Find<Button>(LatestDetail(ui), "FoundSettlement"));
            var confirmation = Find<ConfirmationDialog>(LatestDetail(ui), "FoundingConfirmation");
            Require(confirmation.Visible && confirmation.DialogText.Contains("主食 × 12") &&
                confirmation.DialogText.Contains("列车 7") && setup.Session.SubmitCount == 0,
                "打开确认框必须说明扣除材料，不能提前提交。");
            await _input.Activate(confirmation.GetCancelButton(), confirmation);
            Require(!confirmation.Visible && setup.Session.SubmitCount == 0 &&
                !Find<Button>(LatestDetail(ui), "FoundSettlement").Disabled, "取消确认不应消耗材料或改变可建立状态。");
            await UiCapture.CaptureSizes(TestScene, "founding-ready");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Confirmation_rechecks_materials_when_the_world_changes()
    {
        var before = World(1, null);
        var after = World(2, null, materialQuantity: 11);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async ui =>
        {
            await OpenDetail(ui, 0);
            await _input.Activate(Find<Button>(LatestDetail(ui), "FoundSettlement"));
            var confirmation = Find<ConfirmationDialog>(LatestDetail(ui), "FoundingConfirmation");
            await UiCapture.CaptureSizes(TestScene, "founding-confirmation", confirmation);
            setup.Session.QueueAdvance(new(1, after.Position, [], null, null), after);
            Require(ui.GetParent<Train.App.App>().Host!.Step(1).Problem is null, "无法发布材料减少。");
            await _input.Frames(3);
            Require(confirmation.GetOkButton().Disabled && confirmation.DialogText.Contains("还差 1") &&
                Find<Label>(LatestDetail(ui), "FoundingConditions").Text.Contains("11 / 12"),
                "确认框打开期间必须重新核对材料，不能确认过期条件。");
            await _input.Activate(confirmation.GetCancelButton(), confirmation);
            Require(setup.Session.SubmitCount == 0, "材料不足的确认路径提交了命令。");
            await UiCapture.CaptureSizes(TestScene, "founding-shortage");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Material_source_is_one_train_and_selection_survives_refresh()
    {
        var before = World(1, null, secondTrain: true);
        var after = World(2, null, materialQuantity: 20, secondTrain: true);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async ui =>
        {
            await OpenDetail(ui, 0);
            var trains = Find<OptionButton>(LatestDetail(ui), "MaterialSource");
            Require(trains.ItemCount == 2 && trains.Selected == 0 && trains.GetItemText(0) == "列车 7" && trains.GetItemText(1) == "列车 8",
                "材料来源只能列本节点列车，不能附带速度或其他内部文本。");
            Require(!Find<Button>(LatestDetail(ui), "FoundSettlement").Disabled, "第一列车材料足够却不可建立。");
            await _input.Choose(trains, 1);
            Require(Find<Button>(LatestDetail(ui), "FoundSettlement").Disabled &&
                Find<Label>(LatestDetail(ui), "FoundingConditions").Text.Contains("0 / 12"),
                "不能合计多列车库存，也不能沿用上一列车的材料。");
            setup.Session.QueueAdvance(new(1, after.Position, [], null, null), after);
            Require(ui.GetParent<Train.App.App>().Host!.Step(1).Problem is null, "无法刷新材料列车。");
            await _input.Frames(3);
            Require(trains.Selected == 1 && Find<Button>(LatestDetail(ui), "FoundSettlement").Disabled,
                "刷新不应替玩家换成另一列材料足够的列车。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Selected_material_train_leaving_selects_first_remaining_train()
    {
        var before = World(1, null, secondTrain: true);
        var after = World(2, null);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async ui =>
        {
            await OpenDetail(ui, 0);
            var trains = Find<OptionButton>(LatestDetail(ui), "MaterialSource");
            await _input.Choose(trains, 1);
            Require(Find<Label>(LatestDetail(ui), "FoundingConditions").Text.Contains("✓ 列车 8 停在这里"),
                "玩家改选后没有采用列车 8。");
            setup.Session.QueueAdvance(new(1, after.Position, [], null, null), after);
            Require(ui.GetParent<Train.App.App>().Host!.Step(1).Problem is null, "无法发布材料列车离场。");
            await _input.Frames(3);
            var conditions = Find<Label>(LatestDetail(ui), "FoundingConditions").Text;
            Require(trains.ItemCount == 1 && trains.Selected == 0 && trains.GetItemText(0) == "列车 7" &&
                conditions.Contains("✓ 列车 7 停在这里") && conditions.Contains("12 / 12") &&
                !Find<Button>(LatestDetail(ui), "FoundSettlement").Disabled,
                "已选列车离场后应采用剩下的第一列，并重新核对它的材料。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Readiness_matches_core_ownership_stationary_and_construction_locks()
    {
        var basis = World(1, null);
        var train = ((Lookup<TrainView>.Found)basis.FindTrain(new(7))).Value;
        var vehicle = ((Lookup<InstanceView>.Found)basis.FindInstance(new InstanceId.Vehicle(new(7)))).Value;
        var cases = new[]
        {
            (train, vehicle with { Lock = InstanceLock.Combat }, false, "战斗锁不能冒充施工锁"),
            (train, vehicle with { Lock = InstanceLock.UnderRefit }, true, "改装中的列车不能供料"),
            (train, vehicle with { Lock = InstanceLock.UnderRepair }, true, "修理中的列车不能供料"),
            (train, vehicle with { Lock = InstanceLock.UnderDeconstruction }, true, "拆解中的列车不能供料"),
            (train with { SpeedMetersPerSecond = .001 }, vehicle, true, "速度不为零不能供料"),
            (train with { Vehicles = [new(7), new(17)] }, vehicle with { Id = new InstanceId.Vehicle(new(17)), Ownership = SettlementRelation.NonHostile }, true,
                "混合归属列车的每辆车都必须归玩家")
        };
        foreach (var (candidate, instance, blocked, reason) in cases)
        {
            var view = World(1, null, materialTrain: candidate, changedVehicle: instance);
            var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
            await Run(setup, async ui =>
            {
                await OpenDetail(ui, 0);
                Require(Find<Button>(LatestDetail(ui), "FoundSettlement").Disabled == blocked, reason);
                if (blocked) Require(Find<Label>(LatestDetail(ui), "FoundingReason").Text.Length > 0, "置灰必须说明原因。");
            });
            setup.Session.AssertComplete();
        }
    }
}
