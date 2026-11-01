using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Notifications;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class GameShellAppTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task World_starts_with_chrome_only_and_navigation_selects_a_single_inspector()
    {
        await Run(async (ui, shell) =>
        {
            Require(shell.ListContent is null && shell.InspectorContent is null && shell.WorkspaceContent is null,
                "进入世界不能默认打开列表、检查器或工作区。");
            Require(ui.GetNode<MacroMap>("Root/World/MacroMap").IsVisibleInTree(), "默认应显示宏观地图。");
            foreach (var name in new[] { "Fleet", "Edges", "Nodes", "EconomyReports", "BlueprintLibrary", "Combats" })
                Require(Find<Button>(shell.Navigation, name).IsVisibleInTree(), "导航入口必须默认可见。");
            Require(Find<Button>(ui, "Fleet").TooltipText.Contains("1 列"), "车队数字应包含没有运行计划的列车。");
            await UiCapture.Capture(TestScene, "shell-default");
            await _input.Activate(Find<Button>(ui, "Nodes"));
            var list = shell.ListContent!;
            await _input.ClickItem(Find<ItemList>(list, "Objects"), 0);
            Require(shell.SelectedObject == new WorldObjectId.Settlement(new(1)), "聚居地应通过统一选择进入检查器。");
            Require(Find<TabContainer>(shell.InspectorContent!, "Pages").GetTabCount() == 8,
                "聚居地检查器应保留原有八页。");
            await _input.Activate(Find<Button>(ui, "Nodes"));
            Require(shell.ListContent is null && shell.InspectorContent is not null,
                "再次点导航只收起列表，不能丢掉当前检查器。");
            await _input.Activate(Find<Button>(ui, "Fleet"));
            await _input.ClickItem(Find<ItemList>(shell.ListContent!, "Objects"), 0);
            Require(shell.SelectedObject == new WorldObjectId.Train(new(7)) && !list.IsVisibleInTree(),
                "切换列表及对象应替换显示内容。");
        });
    }

    [Test]
    public async Task Reports_and_library_use_the_workspace_and_the_menu_opens_existing_settings()
    {
        await Run(async (ui, shell) =>
        {
            await _input.Activate(Find<Button>(ui, "EconomyReports"));
            Require(shell.WorkspaceContent is not null, "报告应在工作区打开。");
            await UiCapture.Capture(TestScene, "shell-reports");
            await _input.PressKey(Key.Escape);
            await _input.Activate(Find<Button>(ui, "BlueprintLibrary"));
            Require(shell.WorkspaceContent is BlueprintLibraryPanel &&
                Find<Button>(shell.WorkspaceContent, "NewDesign").IsVisibleInTree(), "设计工作区应提供原来的新建设计入口。");
            await UiCapture.Capture(TestScene, "shell-library");
            await _input.PressKey(Key.Escape);
            var menu = Find<MenuButton>(ui, "GameMenu");
            var popup = menu.GetPopup();
            var labels = Enumerable.Range(0, popup.ItemCount).Select(popup.GetItemText).ToArray();
            foreach (var label in new[] { "保存", "存档与读档", "设置", "报告问题", "返回主菜单", "退出游戏", "开发工具", "创作模式" })
                Require(labels.Contains(label), $"游戏菜单缺少入口：{label}。");
            await _input.ChooseMenu(menu, "设置");
            var settings = Find<SettingsPanel>(ui, "SettingsPanel");
            Require(settings.IsVisibleInTree(), "菜单应打开现有设置界面。");
            await _input.PressKey(Key.Escape);
            Require(!settings.Visible, "Esc 应关闭设置覆盖层。");
        });
    }

    [Test]
    public async Task Notification_selects_its_object_without_opening_a_scene()
    {
        await Run(async (ui, shell) =>
        {
            var app = ui.GetParent<Train.App.App>();
            var notices = Find<NotificationPanel>(ui, "NotificationPanel");
            notices.Add(new(NotificationCategory.Construction, NotificationLevel.Completion, "建造完成。", null,
                new WorldObjectId.Settlement(new(1))), app.Host!.GetStatus()!.View, app.Host.Content!);
            await _input.Frames(3);
            await _input.Activate(Find<Button>(Find<Control>(ui, "Toasts"), "Notice"));
            Require(shell.SelectedObject == new WorldObjectId.Settlement(new(1)) && shell.InspectorContent is not null,
                "通知应通过同一个选择操作显示检查器。");
            Require(!app.WorldScenes!.View.Visible, "点击通知只选中对象，不能隐式进入场景。");
        });
    }

    [Test]
    public async Task Save_overlay_handles_escape_without_closing_the_inspector_below_it()
    {
        await Run(async (ui, shell) =>
        {
            await _input.Activate(Find<Button>(ui, "Nodes"));
            await _input.ClickItem(Find<ItemList>(shell.ListContent!, "Objects"), 0);
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "存档与读档");
            var saves = Find<SavePanel>(ui.GetParent(), "*");
            Require(saves.Visible, "存档入口应打开现有存档覆盖层。");
            await _input.PressKey(Key.Escape, window: saves);
            Require(!saves.Visible && shell.SelectedObject is WorldObjectId.Settlement && shell.ListContent is not null,
                "子窗口的同一次 Esc 只能关闭存档，不能再关闭下面的检查器。");
        });
    }

    [Test]
    public async Task Settlement_and_list_remain_inside_the_window_at_both_required_ui_scales()
    {
        await Run(async (ui, shell) =>
        {
            var window = TestScene.GetTree().Root;
            var size = window.Size;
            try
            {
                window.Size = new(1920, 1080);
                await _input.Activate(Find<Button>(ui, "Nodes"));
                await _input.ClickItem(Find<ItemList>(shell.ListContent!, "Objects"), 0);
                foreach (var scale in new[] { 1f, 2f })
                {
                    window.ContentScaleFactor = scale;
                    await _input.Frames(6);
                    var list = shell.ListContent!.GetGlobalRect();
                    var inspector = shell.InspectorContent!.GetGlobalRect();
                    Require(list.End.X <= inspector.Position.X && inspector.End.X <= shell.Size.X &&
                        list.Position.Y >= shell.TopInset && inspector.Position.Y >= shell.TopInset && inspector.End.Y <= shell.Size.Y,
                        $"列表和聚居地检查器在缩放 {scale} 时不应重叠或越界：{list} / {inspector}。");
                    await _input.ChooseTab(Find<TabContainer>(shell.InspectorContent, "Pages"), 7);
                    await UiCapture.Capture(TestScene, $"shell-settlement-services-scale-{scale:0}", scale);
                    await _input.ChooseTab(Find<TabContainer>(shell.InspectorContent, "Pages"), 0);
                    await UiCapture.Capture(TestScene, $"shell-settlement-stock-scale-{scale:0}", scale);
                }
            }
            finally { window.Size = size; }
        });
    }

    [Test]
    public async Task Wallet_changes_show_the_delta_briefly_without_changing_the_balance()
    {
        var before = EconomyFixture.Create(wallet: 1000);
        var after = EconomyFixture.Create(2, wallet: 269);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueSubmit(new CoreCommand.BuyGoods(new(7), new(1), new(10), EconomyFixture.Food,
            EconomyFixture.LargeQuantity, new(731)), CommandSource.UserInterface,
            Completed(1, before, after, new CommandDecision.Accepted([], [])), after);
        await Run(async (ui, shell) =>
        {
            await _input.Activate(Find<Button>(ui, "Nodes"));
            await _input.ClickItem(Find<ItemList>(shell.ListContent!, "Objects"), 0);
            var inspector = shell.InspectorContent!;
            await _input.ChooseTab(Find<TabContainer>(inspector, "Pages"), 1);
            await _input.Choose(Find<OptionButton>(inspector, "Train"), 1);
            await _input.Activate(inspector.FindChildren("SelectGood", "Button", true, false).Cast<Button>().First());
            await _input.Enter(Find<LineEdit>(inspector, "Quantity"), EconomyFixture.LargeQuantity.ToString());
            await _input.Activate(Find<Button>(inspector, "QuoteTrade"));
            await _input.Activate(Find<Button>(inspector, "ConfirmTrade"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, after));
            var wallet = ui.GetNode<Label>("Root/World/Bar/TopBar/Wallet");
            await _input.WaitUntil(() => wallet.Text.Contains("269"));
            Require(wallet.Text.Contains("269") && wallet.Text.Contains("-731"), "成交后的顶栏应显示余额和本次变化。");
            ui._Process(3.1);
            Require(wallet.Text == "资金 269", "增减提示消失后应保留当前余额。");
            setup.Session.AssertComplete();
        }, setup);
    }

    private async Task Run(Func<GameUi, GameShell, Task> check, HostSimulationTests.HostSetup? scenario = null)
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        var setup = scenario ?? new HostSimulationTests.HostSetup(EconomyFixture.Create(), EconomyFixture.Content());
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            await _input.Frames(3);
            var ui = app.GetNode<GameUi>("GameUi");
            await check(ui, ui.GetNode<GameShell>("Root/World"));
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }
}
