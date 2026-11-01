using Chickensoft.GoDotTest;
using Godot;
using Train.Ui;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests
{
    [Test]
    public async Task Neutral_settlement_starts_with_overview_and_keeps_only_its_five_pages()
    {
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(), EconomyFixture.Content(mapPresentation: true));
        await Run(setup, async (_, panel) =>
        {
            var tabs = Find<TabContainer>(panel, "Pages");
            var titles = Enumerable.Range(0, tabs.GetTabCount()).Select(tabs.GetTabTitle).ToArray();
            Require(titles.SequenceEqual(new[] { "概况", "市场", "现车", "仓库租用", "建造与工地" }),
                "非敌对聚居地应只显示概况、市场和暂时保留的三个页面。");
            Require(tabs.CurrentTab == 0, "选中聚居地后应先显示概况。");
            Require(!panel.FindChildren("OpenSettlementCombat", "Button", true, false).Any(), "概况已提供宣战，不应重复显示战斗入口。");
            Require(!panel.FindChildren("LocalInventory", "OptionButton", true, false).Any(), "非敌对聚居地顶部不应显示本地库存选择。");
            Require(Find<Label>(panel, "Title").Text == "聚居地 1", "检查器标题应使用内容提供的聚居地名称。");
            await UiCapture.Capture(TestScene, "settlement-overview");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Overview_summarizes_station_leases_and_market_without_showing_storage_identities()
    {
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(), EconomyFixture.Content());
        await Run(setup, async (_, panel) =>
        {
            Require(Find<Label>(panel, "PresentTrains").Text.Contains("列车 7"), "概况应显示本站列车并沿用车队列表名称。");
            Require(Find<Label>(panel, "Commissions").Text.Contains("没有进行中的委托"), "无委托时应显示空状态。");
            var lease = Find<Label>(panel, "LeaseSummary").Text;
            Require(lease.Contains("5/20") && lease.Contains("第2天 00:00") && !lease.Contains("库存 11"),
                "仓库摘要应显示用量和游戏到期时间，不显示库存身份。");
            var market = Find<Label>(panel, "MarketTrends").Text;
            Require(market.Contains("主食") && market.Contains("40") && market.Contains("20") && !market.Contains("fixture:"),
                "市场动向应直接说明核心供需，不显示缺失内容身份。");
            await _input.Activate(Find<Button>(panel, "OpenMarket"));
            var tabs = Find<TabContainer>(panel, "Pages");
            Require(tabs.GetTabTitle(tabs.CurrentTab) == "市场", "市场动向的入口应直接切到市场页。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Declaration_requires_a_consequence_confirmation_and_cancel_submits_nothing()
    {
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(), EconomyFixture.Content());
        await Run(setup, async (_, panel) =>
        {
            await _input.Activate(Find<Button>(panel, "DeclareWar"));
            var dialog = Find<ConfirmationDialog>(panel, "DeclareWarConfirmation");
            Require(dialog.Visible && dialog.DialogText.Contains("无法存取") && dialog.DialogText.Contains("租期继续") &&
                dialog.DialogText.Contains("交易") && dialog.DialogText.Contains("防御"), "宣战确认应说明交易、租约和战斗的具体后果。");
            Require(dialog.GetOkButton().Disabled && dialog.DialogText.Contains("玩家车辆"), "在场车辆必须离站后才能宣战，并说明原因。");
            await UiCapture.Capture(TestScene, "settlement-war-confirmation");
            await _input.Activate(dialog.GetCancelButton(), dialog);
            Require(!dialog.Visible && setup.Session.SubmitCount == 0, "取消宣战不得提交命令。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Confirming_war_submits_once_and_opens_the_existing_hostile_pages()
    {
        var before = EconomyFixture.Create(trainAtNode: false);
        var after = EconomyFixture.Create(2, trainAtNode: false, relation: SettlementRelation.Hostile);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueSubmit(new CoreCommand.DeclareWar(new(1)), CommandSource.UserInterface,
            Completed(1, before, after, new CommandDecision.Accepted([], [])), after);
        await Run(setup, async (ui, panel) =>
        {
            await _input.Activate(Find<Button>(panel, "DeclareWar"));
            var dialog = Find<ConfirmationDialog>(panel, "DeclareWarConfirmation");
            await _input.Activate(dialog.GetOkButton(), dialog);
            await _input.WaitUntil(() => ReferenceEquals(ui.View, after));
            var current = Find<PanelContainer>(ui, "Settlement");
            Require(Find<TabContainer>(current, "Pages").GetTabCount() == 8 && setup.Session.SubmitCount == 1,
                "宣战后应使用既有敌对页面，而且只提交一次。");
        });
        setup.Session.AssertComplete();
    }
}
