using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests
{
    [Test]
    public async Task Multiple_trains_require_a_choice_and_action_quantity_train_and_revision_update_the_quote()
    {
        TradeQuoteRequest Request(ulong train, TradeAction action, long quantity) =>
            new(new(train), new(1), new(10), action, EconomyFixture.Food, quantity);
        CoreResult<TradeQuote> Quote(ulong train, TradeAction action, long quantity, long total, ulong revision = 1) =>
            new CoreResult<TradeQuote>.Success(new(Request(train, action, quantity), new(revision), quantity, new(total), [], []));
        var quotes = new Dictionary<TradeQuoteRequest, CoreResult<TradeQuote>>
        {
            [Request(7, TradeAction.Buy, 10)] = Quote(7, TradeAction.Buy, 10, 731),
            [Request(7, TradeAction.Sell, 10)] = Quote(7, TradeAction.Sell, 10, 431),
            [Request(7, TradeAction.Sell, 2)] = Quote(7, TradeAction.Sell, 2, 50),
            [Request(8, TradeAction.Sell, 2)] = Quote(8, TradeAction.Sell, 2, 54)
        };
        var before = EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.Formed, tradeQuotes: quotes, wallet: 1000);
        var changed = EconomyFixture.Create(2, vehicle: EconomyFixture.VehicleState.Formed, wallet: 1000,
            tradeQuotes: new Dictionary<TradeQuoteRequest, CoreResult<TradeQuote>>(quotes)
                { [Request(8, TradeAction.Sell, 2)] = Quote(8, TradeAction.Sell, 2, 60, 2) });
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (ui, panel) =>
        {
            await _input.ChooseTab(Find<TabContainer>(panel, "Pages"), 1);
            await _input.Activate(panel.FindChildren("SelectGood", "Button", true, false).Cast<Button>().First());
            Require(Find<Button>(panel, "ConfirmTrade").Disabled && Find<OptionButton>(panel, "Train").Selected == 0,
                "多列车在站时不能替玩家选择。");
            await _input.Choose(Find<OptionButton>(panel, "Train"), 1);
            Require(Find<Label>(panel, "Quote").Text.Contains("269"), "选择列车后应自动显示买入后的余额。");
            await _input.Choose(Find<OptionButton>(panel, "Action"), 1);
            Require(Find<Label>(panel, "Quote").Text.Contains("1431"), "切换卖出后应自动显示收入后的余额。");
            await _input.Enter(Find<LineEdit>(panel, "Quantity"), "2");
            Require(Find<Label>(panel, "Quote").Text.Contains("1050"), "输入数量应实时重新报价。");
            await _input.Choose(Find<OptionButton>(panel, "Train"), 2);
            Require(Find<Label>(panel, "Quote").Text.Contains("1054"), "切换列车应按新请求重新报价。");
            setup.Session.QueueAdvance(new(1, changed.Position, [], null, null), changed);
            var host = ui.GetParent<Train.App.App>().Host!;
            Require(host.Step(1).Problem is null, "测试应能发布新核心修订。");
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, changed, host.Content!);
            Require(Find<Label>(panel, "Quote").Text.Contains("1060"), "核心修订变化后应自动更新报价。");
            await _input.Enter(Find<LineEdit>(panel, "Quantity"), "0");
            Require(Find<Button>(panel, "ConfirmTrade").Disabled && Find<Label>(panel, "Quote").Text.Contains("正整数"),
                "数量为零时应说明输入要求，不提交报价或命令。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Releasing_outside_the_trade_button_updates_the_deferred_quote_without_submitting()
    {
        var before = EconomyFixture.Create();
        var after = EconomyFixture.Create(2, tradeTotal: 1100);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueAdvance(new(1, after.Position, [], null, null), after);
        await Run(setup, async (ui, panel) =>
        {
            await Market(panel);
            var button = Find<Button>(panel, "ConfirmTrade");
            var point = button.GetGlobalRect().GetCenter();
            await _input.MouseButton(point, MouseButton.Left, true);
            var host = ui.GetParent<Train.App.App>().Host!;
            Require(host.Step(1).Problem is null, "测试应能发布新核心修订。");
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, after, host.Content!);
            Require(Find<Label>(panel, "Quote").Text.Contains("731"), "按住按钮时应保留已看到的报价。");
            await _input.MouseButton(button.GetGlobalRect().End + new Vector2(10, 10), MouseButton.Left, false);
            await _input.Frames(2);
            Require(Find<Label>(panel, "Quote").Text.Contains("1100") && setup.Session.SubmitCount == 0,
                "取消点击后应更新延后的报价，并且不能提交交易。");
        });
        setup.Session.AssertComplete();
    }
}
