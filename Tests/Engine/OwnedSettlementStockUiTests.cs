using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class OwnedSettlementStockUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);
    [Test]
    public async Task Owned_stock_shows_local_quantity_target_and_only_owned_transfer_actions()
    {
        var setup = new HostSimulationTests.HostSetup(
            EconomyFixture.Create(relation: SettlementRelation.PlayerOwned, foodQuantity: 0, leaseCount: 2), EconomyFixture.Content());
        await Run(setup, async (_, panel) =>
        {
            var stock = await OwnedStock(panel);
            var text = string.Join("\n", stock.FindChildren("*", "Label", true, false).Cast<Label>().Select(label => label.Text));
            Require(text.Contains("补货目标") && text.Contains("50") && text.Contains("已用") && text.Contains("预留"),
                "库存页应显示仓储用量和补货目标。");
            var row = stock.FindChildren("SelectGood", "Button", true, false).Cast<Button>().First();
            var values = row.FindChildren("Amount", "Label", true, false).Cast<Label>().Select(label => label.Text).ToArray();
            Require(values.Contains("0") && values.Contains("50"), "零库存但有补货目标的主食必须保留，并使用本地库存数量而非市场库存42。");
            await _input.Activate(row);
            var action = Find<OptionButton>(stock, "Action");
            Require(action.ItemCount == 2 && action.GetItemText(0) == "装上列车" && action.GetItemText(1) == "卸到库存",
                "自有库存只能装上列车或卸到库存。");
            Require(!Find<OptionButton>(stock, "TradeLease").Visible, "自有装卸不能选择租用仓库。");
            var train = Find<OptionButton>(stock, "Train");
            Require(train.GetItemText(train.Selected).Contains("列车") && !train.GetItemText(train.Selected).Contains("7"),
                "在站列车应有展示名称，不显示内部编号。");
            await UiCapture.Capture(TestScene, "owned-stock");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Owned_stock_without_a_station_train_explains_disabled_transfer()
    {
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(
            relation: SettlementRelation.PlayerOwned, trainAtNode: false), EconomyFixture.Content());
        await Run(setup, async (_, panel) =>
        {
            var stock = await OwnedStock(panel);
            await _input.Activate(stock.FindChildren("SelectGood", "Button", true, false).Cast<Button>().First());
            Require(Find<Label>(stock, "Quote").Text.Contains("没有列车在站") && Find<Button>(stock, "ConfirmTrade").Disabled,
                "无在站列车时必须解释装卸不可用，并禁用确认。");
            Require(setup.Session.SubmitCount == 0, "无车时不得提交装卸。");
            await UiCapture.Capture(TestScene, "owned-stock-no-train");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Owned_transfer_maximum_uses_core_capacity_and_both_commands_use_settlement_inventory()
    {
        TradeQuoteRequest Request(TradeAction action, long quantity) => new(new(7), new(1), new(10), action, EconomyFixture.Food, quantity);
        var quotes = new Dictionary<TradeQuoteRequest, CoreResult<TradeQuote>>();
        foreach (var action in new[] { TradeAction.Load, TradeAction.Unload })
        foreach (var quantity in new[] { 10L, 4L, 2L })
        {
            var request = Request(action, quantity);
            quotes[request] = new CoreResult<TradeQuote>.Success(new(request, new(1), Math.Min(quantity, 2), new(0),
                quantity > 2 ? [new("insufficient_capacity", "Inventory.Capacity", null, [])] : [], []));
        }
        var before = EconomyFixture.Create(relation: SettlementRelation.PlayerOwned, tradeQuotes: quotes, leaseCount: 2);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueSubmit(new CoreCommand.LoadGoods(new(7), new(1), new(10), EconomyFixture.Food, 2),
            CommandSource.UserInterface, Completed(1, before, before, new CommandDecision.Accepted([], [])), before);
        setup.Session.QueueSubmit(new CoreCommand.UnloadGoods(new(7), new(1), new(10), EconomyFixture.Food, 2),
            CommandSource.UserInterface, Completed(2, before, before, new CommandDecision.Rejected([
                new("platform_required", "Train.Position", null, [])])), before);
        await Run(setup, async (_, panel) =>
        {
            var stock = await OwnedStock(panel);
            await _input.Activate(stock.FindChildren("SelectGood", "Button", true, false).Cast<Button>().First());
            Require(Find<Button>(stock, "ConfirmTrade").Disabled && Find<Label>(stock, "Quote").Text.Contains("可用容量不足"),
                "容量不足必须采用核心报价的拒绝原因。");
            await _input.Activate(Find<Button>(stock, "Maximum"));
            Require(Find<LineEdit>(stock, "Quantity").Text == "2", "最大必须按核心可执行数量收窄到两件。");
            await _input.Activate(Find<Button>(stock, "ConfirmTrade"));
            await _input.Choose(Find<OptionButton>(stock, "Action"), 1);
            await _input.Activate(Find<Button>(stock, "ConfirmTrade"));
            Require(setup.Session.SubmitCount == 2 && Find<Label>(stock, "TradeResult").Text.Length > 0,
                "双向装卸应提交自有库存，核心拒绝必须在原行反馈。");
            Require(Find<LineEdit>(stock, "Quantity").Text == "2", "拒绝后应保留输入，不自动重试。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Owned_stock_refresh_keeps_expanded_good_action_and_quantity()
    {
        var before = EconomyFixture.Create(relation: SettlementRelation.PlayerOwned);
        var after = EconomyFixture.Create(2, relation: SettlementRelation.PlayerOwned, foodQuantity: 9);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueAdvance(new(1, after.Position, [], null, null), after);
        await Run(setup, async (ui, panel) =>
        {
            var stock = await OwnedStock(panel);
            await _input.Activate(stock.FindChildren("SelectGood", "Button", true, false).Cast<Button>().First());
            await _input.Choose(Find<OptionButton>(stock, "Action"), 1);
            await _input.Enter(Find<LineEdit>(stock, "Quantity"), "1");
            var host = ui.GetParent<Train.App.App>().Host!;
            Require(host.Step(1).Problem is null, "测试必须发布新修订。");
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, after, host.Content!);
            Require(Find<VBoxContainer>(stock, "Trade").Visible && Find<LineEdit>(stock, "Quantity").Text == "1" &&
                Find<OptionButton>(stock, "Action").GetItemText(Find<OptionButton>(stock, "Action").Selected) == "卸到库存" &&
                Find<Label>(stock, "TrainCargo").Text.Contains("9"), "刷新应保留展开货物、动作和数量，并更新货物数据。");
        });
        setup.Session.AssertComplete();
    }

    private async Task<Control> OwnedStock(Control panel)
    {
        var tabs = Find<TabContainer>(panel, "Pages");
        await _input.ChooseTab(tabs, Enumerable.Range(0, tabs.GetTabCount()).Single(index => tabs.GetTabTitle(index) == "库存"));
        return tabs.GetChild<Control>(tabs.CurrentTab);
    }

    private async Task Run(HostSimulationTests.HostSetup setup, Func<GameUi, Control, Task> body)
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            await _input.Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.Activate(Find<Button>(ui, "Nodes"));
            await _input.ClickItem(Find<ItemList>(Find<PanelContainer>(ui, "NodesList"), "Objects"), 0);
            await body(ui, Find<PanelContainer>(ui, "Settlement"));
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }
}
