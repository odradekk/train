using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public sealed class EconomyReportsWorkspaceTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Cash_totals_and_last_seven_days_use_the_persisted_entries()
    {
        var day = UiInput.StepsPerDay;
        var view = EconomyFixture.Create(completedSteps: 7 * day, cashFlows:
        [
            new(new(0, 0), CashFlowKind.GoodsSale, 30, new(1), new(7), EconomyFixture.Food, 3),
            new(new(6 * day, 0), CashFlowKind.GoodsPurchase, -20, new(1), new(7), EconomyFixture.Food, 2),
            new(new(7 * day, 0), CashFlowKind.Rent, -5, new(1), null, null, 0),
            new(new(7 * day, 1), CashFlowKind.LairReward, 12, null, null, null, 0)
        ]);
        await WithReports(view, async (ui, panel, setup) =>
        {
            Require(Find<Label>(panel, "NetCashFlow").Text.Contains("+17") &&
                Find<Label>(panel, "Income").Text == "收入 +42" && Find<Label>(panel, "Expenses").Text == "支出 -25" &&
                Find<Label>(panel, "Income").GetThemeColor("font_color") == UiStyle.Success &&
                Find<Label>(panel, "Expenses").GetThemeColor("font_color") == UiStyle.Error,
                "本局统计必须汇总全部核心收支，不能把初始资金算作收入。");
            var days = Find<HBoxContainer>(panel, "DailyCashFlow");
            Require(days.GetChildCount() == 7 && Find<Label>(days, "Day7").Text.Contains("第8天（至今）") &&
                Find<Label>(days, "Amount7").Text == "+7" && Find<Label>(days, "Amount6").Text == "-20",
                "图表应显示最近七个游戏日，午夜发生的收支归新的一天。");
            await _input.ChooseTab(Find<TabContainer>(panel, "ReportPages"), 1);
            var trading = Find<VBoxContainer>(panel, "TradeSettlement1");
            Require(Find<Label>(trading, "Sales").Text == "卖出 +30" &&
                Find<Label>(trading, "Purchases").Text == "买入 -20" &&
                Find<Label>(trading, "ServicesAndRent").Text == "服务与租金 -5" &&
                Find<Label>(trading, "Purchases").GetThemeColor("font_color") == UiStyle.Error,
                "其他聚居地应按项目归类收支，清剿奖励不能混入聚居地汇总。");
            Require(setup.Session.SubmitCount == 0, "查看现金流不应提交命令。");
            await _input.Frames(1);
        });
    }

    [Test]
    public async Task Owned_settlements_show_population_today_production_and_shared_attention_items()
    {
        var view = EconomyFixture.Create(relation: SettlementRelation.PlayerOwned,
            todayProduction: [new(EconomyFixture.Food, 3)]);
        await WithReports(view, async (ui, panel, setup) =>
        {
            await _input.ChooseTab(Find<TabContainer>(panel, "ReportPages"), 1);
            var text = Find<Label>(panel, "OwnedSettlement1").Text;
            Require(text.Contains("常住人口 10") && text.Contains("基本需求满足率 暂无统计") &&
                text.Contains("当天产出") && text.Contains("主食 3") && text.Contains("低于补货目标"),
                "自有聚居地应展示人口、未定义满足率、当天产出与共用的缺货事项。");
            Require(Find<Label>(panel, "EmptyCashFlows").Text.Contains("本局尚无收支记录") &&
                Find<Label>(panel, "NetCashFlow").Text.Contains("0") && setup.Session.SubmitCount == 0,
                "空记录应明确说明，不能把初始资金算作本局收入。");
        });
    }

    [Test]
    public async Task Ledger_shows_newest_first_and_loads_earlier_entries_in_batches_of_two_hundred()
    {
        var entries = Enumerable.Range(0, 201).Select(index => new CashFlowEntry(new((ulong)index, 0),
            CashFlowKind.GoodsSale, index + 1, new(1), new(7), EconomyFixture.Food, 1)).ToImmutableArray();
        await WithReports(EconomyFixture.Create(completedSteps: 200, cashFlows: entries), async (ui, panel, setup) =>
        {
            await _input.ChooseTab(Find<TabContainer>(panel, "ReportPages"), 2);
            var rows = Find<VBoxContainer>(panel, "CashFlowRows");
            Require(rows.GetChildCount() == 200 && rows.GetChild(0).Name == "CashFlow200" &&
                ((Label)rows.GetChild(0)).Text.Contains("+201"), "明细应最新在前，首次只创建200行。");
            var newest = rows.GetChild(0);
            await _input.Activate(Find<Button>(panel, "ShowEarlierCashFlows"));
            Require(rows.GetChildCount() == 201 && rows.GetChild(200).Name == "CashFlow0" &&
                ReferenceEquals(newest, rows.GetChild(0)), "显示更早记录应追加一批，保留已有行。");
            Require(!Find<Button>(panel, "ShowEarlierCashFlows").Visible && setup.Session.SubmitCount == 0,
                "已显示全部记录时应隐藏加载入口；查看明细不应提交命令。");
        });
    }

    [Test]
    public async Task A_large_backlog_reopens_at_the_latest_two_hundred_entries()
    {
        var entries = Enumerable.Range(0, 1000).Select(index => new CashFlowEntry(new((ulong)index, 0),
            CashFlowKind.LairReward, 1, null, null, null, 0)).ToImmutableArray();
        await WithReports(EconomyFixture.Create(completedSteps: 400, cashFlows: entries[..400]), async (ui, panel, setup) =>
        {
            await _input.ChooseTab(Find<TabContainer>(panel, "ReportPages"), 2);
            await _input.Activate(Find<Button>(panel, "ShowEarlierCashFlows"));
            var rows = Find<VBoxContainer>(panel, "CashFlowRows");
            Require(rows.GetChildCount() == 400, "应先显示两批已有记录。");
            panel.Hide();
            var next = EconomyFixture.Create(revision: 2, completedSteps: 1000, cashFlows: entries);
            var host = ui.GetParent<Train.App.App>().Host!;
            panel.Show();
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, next, host.Content!);
            Require(rows.GetChildCount() == 200 && rows.GetChild(0).Name == "CashFlow999" &&
                Find<Button>(panel, "ShowEarlierCashFlows").Visible,
                "大量新收支积压后也只能一次建立200行，其余通过显示更早记录加载。");
            Require(setup.Session.SubmitCount == 0, "刷新积压记录不应提交命令。");
        });
    }

    [Test]
    public async Task Summary_amounts_remain_readable_at_wide_and_narrow_window_sizes()
    {
        await WithReports(EconomyFixture.Create(), async (ui, panel, setup) =>
        {
            var window = TestScene.GetTree().Root;
            var originalSize = window.Size;
            var originalScale = window.ContentScaleFactor;
            try
            {
                foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
                {
                    window.Size = size;
                    window.ContentScaleFactor = 1920f / size.X;
                    await _input.Frames(6);
                    var amount = Find<Label>(panel, "NetCashFlow");
                    var textWidth = amount.GetThemeFont("font").GetStringSize(amount.Text,
                        fontSize: amount.GetThemeFontSize("font_size")).X;
                    Require(amount.Size.X >= textWidth && panel.GetGlobalRect().Encloses(amount.GetGlobalRect()),
                        "净现金流栏必须在工作区内完整显示，不能挤成一列窄字。");
                }
            }
            finally { window.Size = originalSize; window.ContentScaleFactor = originalScale; }
        });
    }

    [Test]
    public async Task Expanded_row_uses_the_same_train_name_as_the_inspector()
    {
        await WithReports(EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.Formed), async (ui, panel, setup) =>
        {
            await _input.Activate(Find<Button>(ui, "Fleet"));
            var shell = (GameShell)ui.World;
            await _input.ClickItem(Find<Tree>(shell.ListContent!, "Objects"), 0);
            var inspectorName = Find<Label>(shell.InspectorContent!, "Title").Text.Split('·')[0].Trim();
            Require(inspectorName == "列车 7", "检查器应显示列车的既有名称。");
            var row = Find<VBoxContainer>(panel, "Train7");
            await _input.Activate(Find<Button>(row, "Expand"));
            Require(Find<VBoxContainer>(row, "RunDetails").IsVisibleInTree() &&
                Find<Button>(row, "Expand").Text.Split('·')[0].Trim() == inspectorName,
                "展开行应与检查器使用同一个列车名称，不能按报告列表位置重新编号。");
        });
    }

    [Test]
    public async Task Refresh_retains_the_expanded_train_and_selected_page()
    {
        var view = EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.Formed);
        await WithReports(view, async (ui, panel, setup) =>
        {
            var row = Find<VBoxContainer>(panel, "Train7");
            await _input.Activate(Find<Button>(row, "Expand"));
            await _input.Activate(Find<Button>(Find<VBoxContainer>(panel, "Train8"), "Expand"));
            Require(!Find<VBoxContainer>(row, "RunDetails").Visible, "展开另一列车应收起上一列车。");
            var pages = Find<TabContainer>(panel, "ReportPages");
            await _input.ChooseTab(pages, 1);
            var economy = Find<EconomyPanels>(ui, "EconomyPanels");
            var host = ui.GetParent<Train.App.App>().Host!;
            var binding = host.GetStatus()!.Binding;
            var next = EconomyFixture.Create(revision: 2, vehicle: EconomyFixture.VehicleState.Formed, wallet: 100);
            economy.Refresh(binding, next, host.Content!);
            Require(pages.CurrentTab == 1 && Find<VBoxContainer>(Find<VBoxContainer>(panel, "Train8"), "RunDetails").Visible,
                "刷新不应丢失页签和展开状态。");
            Require(Find<Label>(panel, "Wallet").Text.Contains("100"), "报告资金未随读视图更新。");
            Require(setup.Session.SubmitCount == 0, "刷新只读报告提交了命令。");
        });
    }

    [Test]
    public async Task Invalid_binding_disables_report_actions_and_explains_the_reason()
    {
        var view = EconomyFixture.Create();
        await WithReports(view, async (ui, panel, setup) =>
        {
            var row = Find<VBoxContainer>(panel, "Train7");
            await _input.Activate(Find<Button>(row, "Expand"));
            var host = ui.GetParent<Train.App.App>().Host!;
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(new(Guid.NewGuid()), view, host.Content!);
            Require(Find<Label>(panel, "State").Text.Contains("已失效") &&
                Find<Button>(row, "EditPlan").Disabled && Find<Button>(row, "Expand").Disabled,
                "失效对局的报告应说明原因并禁用操作。");
            Require(setup.Session.SubmitCount == 0, "失效报告不应提交命令。");
        });
    }

    [Test]
    public async Task Removed_trains_keep_their_reports_and_explain_why_the_plan_cannot_open()
    {
        var view = EconomyFixture.Create();
        await WithReports(view, async (ui, panel, setup) =>
        {
            var row = Find<VBoxContainer>(panel, "Train7");
            await _input.Activate(Find<Button>(row, "Expand"));
            var totals = new RunAccumulatorView(new(0, 0), new(40), new(10), new(0), 30, 2, 0, []);
            var removed = new FixedCoreReadView(view.Position, view.InputIdentity, revision: new(2))
            {
                World = view.World with { Trains = [] },
                Economy = view.Economy with { RecentRunReports = [new(new(7), new(3600, 0), RunEndReason.TrainDisbanded, totals)] }
            };
            var host = ui.GetParent<Train.App.App>().Host!;
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, removed, host.Content!);
            Require(Find<Label>(row, "ReportsText").Text.Contains("列车已解编") &&
                Find<Button>(row, "EditPlan").Disabled && Find<Label>(row, "Reason").Text.Contains("列车已不存在"),
                "已解编列车应保留报告并禁用编辑计划。");
        });
    }

    private async Task WithReports(ICoreReadView view, Func<GameUi, PanelContainer, HostSimulationTests.HostSetup, Task> run)
    {
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content(mapPresentation: true));
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.Activate(Find<Button>(ui, "EconomyReports"));
            await run(ui, (PanelContainer)((GameShell)ui.World).WorkspaceContent!, setup);
        }
        finally { await fixture.Cleanup(); }
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Reports_open_with_three_pages_and_expand_the_selected_trains_run()
    {
        var view = EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.Formed);
        await WithReports(view, async (ui, panel, setup) =>
        {
            Require(panel.FindChild("ReportPages", true, false) is TabContainer,
                "经营报告应在工作区中分为列车、聚居地和收支明细三页。");
            var pages = Find<TabContainer>(panel, "ReportPages");
            Require(pages.GetTabCount() == 3 && pages.GetTabTitle(0) == "列车" &&
                pages.GetTabTitle(1) == "聚居地" && pages.GetTabTitle(2) == "收支明细", "报告分页名称错误。");
            var row = Find<VBoxContainer>(panel, "Train8");
            Require(!Find<Control>(row, "RunDetails").IsVisibleInTree(), "列车明细应默认收起。");
            await _input.Activate(Find<Button>(row, "Expand"));
            Require(Find<Label>(row, "ReportsText").Text.Contains("暂无已结束的运行报告") &&
                Find<Button>(row, "EditPlan").IsVisibleInTree(), "展开无报告的列车应说明状态，并保留编辑计划入口。");
            await _input.Activate(Find<Button>(row, "EditPlan"));
            var plan = ((GameShell)ui.World).WorkspaceContent;
            Require(plan is TrainPlanWorkspace && plan.FindChildren("*", "Label", true, false).OfType<Label>()
                .Any(label => label.Text == "运行计划 · 列车 8") &&
                Find<VBoxContainer>(plan, "Stops").GetChildCount() == 1,
                "编辑计划应打开这列车的现有运行计划工作区，并带入所在地。");
            Require(setup.Session.SubmitCount == 0, "查看报告和打开计划不应提交命令。");
        });
    }
}
