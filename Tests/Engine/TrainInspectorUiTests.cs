using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class TrainInspectorUiTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Train_without_a_plan_opens_four_pages_and_explains_why_it_cannot_depart()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(), EconomyFixture.Content(mapPresentation: true));
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            var input = new UiTestInput(TestScene);
            await input.WaitUntil(() => app.Host!.GetStatus() is not null);
            var ui = app.GetNode<GameUi>("GameUi");
            await input.Activate(Find<Button>(ui, "Fleet"));
            await input.ClickItem(Find<Tree>(Find<PanelContainer>(ui, "FleetList"), "Objects"), 0);
            var panel = Find<GameShell>(ui, "*").InspectorContent!;
            var pages = panel.FindChild("Pages", true, false) as TabContainer;
            Require(pages is not null, "列车检查器没有提供分页。");
            Require(Enumerable.Range(0, pages!.GetTabCount()).Select(pages.GetTabTitle)
                .SequenceEqual(new[] { "概况", "计划", "货物", "车辆" }), "列车检查器的四页不完整或顺序错误。");
            Require(Find<Label>(panel, "Description").Text.Contains("没有运行计划") &&
                Find<Button>(panel, "Start").Disabled && Find<Button>(panel, "Start").Text == "发车" &&
                Find<Button>(panel, "EditPlan").ThemeTypeVariation == "PrimaryButton" &&
                Find<Label>(panel, "DepartureReason").Text.Contains("没有运行计划"),
                "无计划列车没有说明发车条件。");
            Require(!Find<Button>(panel, "OpenReport").Visible, "没有上次运行报告时不应显示查看明细。");
            await input.Activate(Find<Button>(panel, "EditPlan"));
            Require(Find<TrainPlanWorkspace>(ui, "*").IsVisibleInTree(), "编辑计划没有打开已有工作区。");
            Require(setup.Session.SubmitCount == 0, "查看检查器或编辑计划意外提交了命令。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Overview_shows_the_destination_and_latest_report_with_unfinished_rule_reasons()
    {
        var view = new InspectorView();
        var totals = new RunAccumulatorView(new(3600, 0), new(70), new(30), new(2), 38, 12, .5,
            [new(0, 0, TradeAction.Buy, EconomyFixture.Food, 40, 12, new(30),
                [new("insufficient_goods", "quantity", null, [])])]);
        view.Train = view.Train with
        {
            Activity = TrainActivity.Running,
            Position = new TrainPosition.Edge(new(1), 1, TravelDirection.Forward, 150),
            Plan = new(false, [new PlanStop.Station(new(2), 60, [])]),
            NextStopIndex = 0,
            CurrentRun = totals,
            RecentReports = [new(view.Train.Id, new(7200, 0), RunEndReason.PlanCompleted, totals),
                new(view.Train.Id, new(3600, 0), RunEndReason.PlanReplaced, totals with { NetCashFlow = -9 })]
        };
        await WithInspector(view, async (ui, panel, setup) =>
        {
            var text = Find<Label>(panel, "Description").Text;
            Require(text.Contains("青川") && text.Contains("石港") && text.Contains("50%"), "概况没有显示线路两端和行驶进度。");
            Require(text.Contains("38") && text.Contains("货物不足") && text.Contains("第1天 00:02") && !text.Contains("净现金流 -9"),
                "概况没有选择最近一次报告，或缺少未完成原因与游戏时间。");
            Require(text.Contains(EconomyPanels.ReservedStockHint) && text.Contains("实际金额 30"),
                "概况遗漏未完成规则的实际金额或聚居地保留库存说明。");
            Require(!text.Contains("insufficient_goods") && !text.Contains("步") && !text.Contains("fixture:"), "概况泄漏了代码或模拟时间。");
            Require(Find<Button>(panel, "Stop").IsVisibleInTree(), "运行列车没有停车入口。");
            await Task.CompletedTask;
        });
    }

    [Test]
    public async Task Reverse_travel_measures_progress_from_the_departure_end()
    {
        var view = new InspectorView();
        view.Train = view.Train with { Activity = TrainActivity.Running,
            Position = new TrainPosition.Edge(new(1), 1, TravelDirection.Reverse, 75) };
        await WithInspector(view, async (ui, panel, setup) =>
        {
            var text = Find<Label>(panel, "Description").Text;
            Require(text.Contains("前往 青川 · 75%") && text.Contains("经由 青川—石港"), "反向行驶的去向或进度错误。");
            await Task.CompletedTask;
        });
    }
}
