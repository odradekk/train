using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests
{
    [Test]
    public async Task Maximum_with_no_goods_preserves_quantity_and_core_reason_across_revisions()
    {
        TradeQuoteRequest Request(TradeAction action) => new(new(7), new(1), new(10), action, EconomyFixture.Food, 10);
        Dictionary<TradeQuoteRequest, CoreResult<TradeQuote>> Quotes(ulong revision) => new()
        {
            [Request(TradeAction.Buy)] = new CoreResult<TradeQuote>.Success(new(Request(TradeAction.Buy), new(revision), 10, new(731), [], [])),
            [Request(TradeAction.Sell)] = new CoreResult<TradeQuote>.Success(new(Request(TradeAction.Sell), new(revision), 0, null,
                [new("insufficient_goods", "Train.Inventory", null, [])], []))
        };
        var before = EconomyFixture.Create(foodQuantity: 0, tradeQuotes: Quotes(1));
        var after = EconomyFixture.Create(2, foodQuantity: 0, tradeQuotes: Quotes(2));
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (ui, panel) =>
        {
            await _input.ChooseTab(Find<TabContainer>(panel, "Pages"), 1);
            await _input.Activate(panel.FindChildren("SelectGood", "Button", true, false).Cast<Button>().First());
            await _input.Choose(Find<OptionButton>(panel, "Action"), 1);
            Require(Find<Label>(panel, "Quote").Text.Contains("货物不足"), "卖出报价应显示核心的货物不足原因。");
            await _input.Activate(Find<Button>(panel, "Maximum"));
            Check();
            setup.Session.QueueAdvance(new(1, after.Position, [], null, null), after);
            var host = ui.GetParent<Train.App.App>().Host!;
            Require(host.Step(1).Problem is null, "测试应能发布新核心修订。");
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, after, host.Content!);
            Check();

            void Check()
            {
                Require(Find<LineEdit>(panel, "Quantity").Text == "10", "无货物时最大操作应保留玩家输入的数量。");
                Require(Find<Label>(panel, "Quote").Text.Contains("货物不足"), "最大操作及后续修订应保留核心的货物不足原因。");
                Require(Find<Button>(panel, "ConfirmTrade").Disabled, "货物不足时不能确认交易。");
            }
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Maximum_with_zero_executable_quantity_keeps_quote_for_input_across_revisions()
    {
        TradeQuoteRequest Request(long quantity) => new(new(7), new(1), new(10), TradeAction.Buy, EconomyFixture.Food, quantity);
        Dictionary<TradeQuoteRequest, CoreResult<TradeQuote>> Quotes(ulong revision) => new()
        {
            [Request(10)] = new CoreResult<TradeQuote>.Success(new(Request(10), new(revision), 0, new(731),
                [new("insufficient_money", "Wallet", null, [])], [])),
            [Request(40)] = new CoreResult<TradeQuote>.Success(new(Request(40), new(revision), 0, new(2924),
                [new("insufficient_money", "Wallet", null, [])], []))
        };
        var before = EconomyFixture.Create(wallet: 0, tradeQuotes: Quotes(1));
        var after = EconomyFixture.Create(2, wallet: 0, tradeQuotes: Quotes(2));
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (ui, panel) =>
        {
            await _input.ChooseTab(Find<TabContainer>(panel, "Pages"), 1);
            await _input.Activate(panel.FindChildren("SelectGood", "Button", true, false).Cast<Button>().First());
            Check();
            await _input.Activate(Find<Button>(panel, "Maximum"));
            Check();
            setup.Session.QueueAdvance(new(1, after.Position, [], null, null), after);
            var host = ui.GetParent<Train.App.App>().Host!;
            Require(host.Step(1).Problem is null, "测试应能发布新核心修订。");
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, after, host.Content!);
            Check();

            void Check()
            {
                Require(Find<LineEdit>(panel, "Quantity").Text == "10", "无可执行数量时最大操作应保留玩家输入的数量。");
                var quote = Find<Label>(panel, "Quote").Text;
                Require(quote.Contains("总额：731") && quote.Contains("资金不足"), "报价应始终对应输入的十件，并保留核心的资金不足原因。");
                var confirm = Find<Button>(panel, "ConfirmTrade");
                Require(confirm.Text.Contains("10 件") && confirm.Text.Contains("731") && confirm.Disabled,
                    "确认按钮应对应输入的十件及其报价，资金不足时不能交易。");
            }
        });
        setup.Session.AssertComplete();
    }
}
