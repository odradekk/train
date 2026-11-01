using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests
{
    [Test]
    public async Task Buying_ten_uses_three_clicks_and_reports_actual_payment_in_the_expanded_row()
    {
        var before = EconomyFixture.Create();
        var after = EconomyFixture.Create(2, foodQuantity: 14, wallet: 1233867);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        var result = new SubmissionResult.Completed(new(1, before.Position, after.Position, new CommandDecision.Accepted([], [])),
            [new(after.Position, 0, new CoreEvent.GoodsTransferred(TradeAction.Buy, EconomyFixture.Food, 10,
                new(10), new(12), new(700), new(7), new(1)))], null);
        setup.Session.QueueSubmit(new CoreCommand.BuyGoods(new(7), new(1), new(10), EconomyFixture.Food, 10, new(731)),
            CommandSource.UserInterface, result, after);
        await Run(setup, async (ui, panel) =>
        {
            var tabs = Find<TabContainer>(panel, "Pages");
            await _input.ClickTab(tabs, 1);
            var row = panel.FindChildren("SelectGood", "Button", true, false).Cast<Button>().First();
            await _input.ClickAcrossRevision(row, () => { });
            var quantity = Find<LineEdit>(panel, "Quantity");
            Require(quantity.Text == "10" && Find<OptionButton>(panel, "Action").GetSelectedId() == (int)TradeAction.Buy,
                "展开后应默认买入十件。");
            Require(Find<OptionButton>(panel, "Train").GetItemText(1).Contains("列车 7"), "列车名称应和车队列表一致。");
            Require(!panel.FindChildren("QuoteTrade", "Button", true, false).Any(), "市场不应再有获取报价按钮。");
            var confirm = Find<Button>(panel, "ConfirmTrade");
            Require(confirm.Text.Contains("10") && confirm.Text.Contains("731") && confirm.GetParent().GetParent() == row.GetParent(),
                "数量、金额和交易控件应显示在所点货物行下。");
            Require(Find<Label>(panel, "Quote").Text.Contains("1233836"), "交易后资金应按核心报价实时预览。");
            await _input.ClickAcrossRevision(confirm, () => { });
            await _input.WaitUntil(() => ReferenceEquals(ui.View, after));
            var text = Find<Label>(panel, "TradeResult").Text;
            Require(text.Contains("700") && !text.Contains("731") && text.Contains("10"), "成交结果应使用实际数量与实际成交额。");
            Require(!ui.ErrorText.Contains("700") && setup.Session.SubmitCount == 1, "成功应在原行反馈且只提交一次。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Maximum_uses_core_executable_quantity_until_capacity_and_money_blockers_are_resolved()
    {
        TradeQuoteRequest Request(long quantity) => new(new(7), new(1), new(10), TradeAction.Buy, EconomyFixture.Food, quantity);
        CoreResult<TradeQuote> Quote(long quantity, long executable, long? payment, params CoreProblem[] blockers) =>
            new CoreResult<TradeQuote>.Success(new(Request(quantity), new(1), executable, payment is { } amount ? new(amount) : null, [.. blockers], []));
        var quotes = new Dictionary<TradeQuoteRequest, CoreResult<TradeQuote>>
        {
            [Request(10)] = Quote(10, 3, 731, new CoreProblem("insufficient_money", "Wallet", null, [])),
            [Request(40)] = Quote(40, 20, null, new CoreProblem("insufficient_capacity", "Train.Inventory.Capacity", null, [])),
            [Request(20)] = Quote(20, 3, 1500, new CoreProblem("insufficient_money", "Wallet", null, [])),
            [Request(3)] = Quote(3, 3, 200)
        };
        var before = EconomyFixture.Create(wallet: 250, tradeQuotes: quotes);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueSubmit(new CoreCommand.BuyGoods(new(7), new(1), new(10), EconomyFixture.Food, 3, new(200)),
            CommandSource.UserInterface, Completed(1, before, before, new CommandDecision.Accepted([], [])), before);
        await Run(setup, async (_, panel) =>
        {
            await _input.ChooseTab(Find<TabContainer>(panel, "Pages"), 1);
            await _input.Activate(panel.FindChildren("SelectGood", "Button", true, false).Cast<Button>().First());
            Require(Find<Button>(panel, "ConfirmTrade").Disabled, "资金不足时不能确认。");
            await _input.Activate(Find<Button>(panel, "Maximum"));
            Require(Find<LineEdit>(panel, "Quantity").Text == "3", "最大数量应由核心报价从40收窄至20再至3。");
            Require(Find<Label>(panel, "Quote").Text.Contains("50"), "最大数量成交后的资金应来自最终核心报价。");
            await _input.Activate(Find<Button>(panel, "ConfirmTrade"));
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Empty_station_explains_why_trade_is_unavailable()
    {
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(trainAtNode: false), EconomyFixture.Content());
        await Run(setup, async (_, panel) =>
        {
            await _input.ChooseTab(Find<TabContainer>(panel, "Pages"), 1);
            await _input.Activate(panel.FindChildren("SelectGood", "Button", true, false).Cast<Button>().First());
            Require(Find<Label>(panel, "Quote").Text.Contains("没有列车在站"), "无车时应在展开处说明到站后才能交易。");
            Require(Find<Button>(panel, "ConfirmTrade").Disabled && setup.Session.SubmitCount == 0, "没有在站列车时不得交易。");
        });
        setup.Session.AssertComplete();
    }
}
