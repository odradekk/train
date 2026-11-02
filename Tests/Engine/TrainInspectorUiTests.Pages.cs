using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class TrainInspectorUiTests
{
    [Test]
    public async Task Plan_page_lists_stations_waypoints_and_all_trade_rules_in_order()
    {
        var view = new InspectorView();
        view.Train = view.Train with { Plan = new(true,
        [
            new PlanStop.Station(new(1), 150,
            [new(new AutomaticGoodsAction.Buy(new(14)), EconomyFixture.Food, new(11), 40),
             new(new AutomaticGoodsAction.Sell(new(10)), EconomyFixture.Food, new(11), 0),
             new(new AutomaticGoodsAction.Load(), EconomyFixture.Equipment, new(11), 5),
             new(new AutomaticGoodsAction.Unload(), EconomyFixture.Equipment, new(11), 2),
             new(new AutomaticGoodsAction.Buy(null), EconomyFixture.Equipment, new(11), 1),
             new(new AutomaticGoodsAction.Sell(null), EconomyFixture.Equipment, new(11), 1)]),
            new PlanStop.Waypoint(new(2)), new PlanStop.Station(new(1), 0, [])
        ]) };
        await WithInspector(view, async (ui, panel, setup) =>
        {
            await new UiTestInput(TestScene).ChooseTab(Find<TabContainer>(panel, "Pages"), 1);
            Require(panel.FindChild("PlanText", true, false) is Label, "计划页没有显示停靠顺序。");
            var text = Find<Label>(panel, "PlanText").Text;
            Require(text.Contains("循环") && text.IndexOf("青川", StringComparison.Ordinal) < text.IndexOf("石港", StringComparison.Ordinal) &&
                text.Contains("途经点") && text.Contains("停留 2.5 秒") && text.Contains("停留 0 秒"), "计划页没有保留所有停靠和途经设置。");
            foreach (var sentence in new[] { "买入主食，直到车上有 40；单价高于 14 不买", "卖出主食，直到车上剩 0；单价低于 10 不卖",
                "从租用仓库装载设备，直到车上有 5", "卸载设备到租用仓库，直到车上剩 2" })
                Require(text.Contains(sentence), "计划页没有复述规则：" + sentence);
            Require(text.Contains("买入设备，直到车上有 1\n") && text.Contains("卖出设备，直到车上剩 1\n"), "无价格条件的规则添加了限价。");
            Require(setup.Session.SubmitCount == 0, "只读计划页提交了命令。");
        });
    }

    [Test]
    public async Task Cargo_page_shows_total_capacity_and_named_quantities_without_an_inventory_window()
    {
        await WithInspector(new InspectorView(), async (ui, panel, setup) =>
        {
            await new UiTestInput(TestScene).ChooseTab(Find<TabContainer>(panel, "Pages"), 2);
            Require(panel.FindChild("Capacity", true, false) is Label, "货物页没有显示总量和载量。");
            var capacity = Find<Label>(panel, "Capacity").Text;
            Require(capacity.Contains("5 / 100"), "货物总量或载量不符。");
            var rows = panel.FindChildren("GoodName", "Label", true, false).OfType<Label>().Select(label => label.Text).ToArray();
            Require(rows.Any(text => text.Contains("主食") && text.Contains("4")) && rows.Any(text => text.Contains("设备") && text.Contains("1")),
                "货物页没有显示各货物的名称和数量。");
            Require(ui.FindChild("TrainInventory", true, false) is null && panel.FindChild("Inventory", true, false) is null,
                "旧列车库存窗口或入口仍存在。");
        });
    }
}
