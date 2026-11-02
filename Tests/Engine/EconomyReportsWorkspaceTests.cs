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
