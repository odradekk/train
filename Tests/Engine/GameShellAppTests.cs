using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Notifications;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class GameShellAppTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task World_starts_with_chrome_only_and_navigation_selects_a_single_inspector()
    {
        await Run(async (ui, shell) =>
        {
            Require(ui.ErrorText.Length == 0, "默认世界截图必须没有内容错误覆盖层。");
            Require(shell.ListContent is null && shell.InspectorContent is null && shell.WorkspaceContent is null,
                "进入世界不能默认打开列表、检查器或工作区。");
            Require(ui.GetNode<MacroMap>("Root/World/MacroMap").IsVisibleInTree(), "默认应显示宏观地图。");
            foreach (var name in new[] { "Fleet", "Edges", "Nodes", "EconomyReports", "BlueprintLibrary", "Combats" })
                Require(Find<Button>(shell.Navigation, name).IsVisibleInTree(), "导航入口必须默认可见。");
            await UiCapture.Capture(TestScene, "shell-default");
            await _input.Activate(Find<Button>(ui, "Nodes"));
            var list = shell.ListContent!;
            await _input.ClickItem(Find<Tree>(list, "Objects"), FirstSelectableItem(Find<Tree>(list, "Objects")));
            Require(shell.SelectedObject == new WorldObjectId.Settlement(new(1)), "聚居地应通过统一选择进入检查器。");
            Require(Find<TabContainer>(shell.InspectorContent!, "Pages").GetTabCount() == 4,
                "非敌对聚居地检查器应显示四页。");
            await _input.Activate(Find<Button>(ui, "Nodes"));
            Require(shell.ListContent is null && shell.InspectorContent is not null,
                "再次点导航只收起列表，不能丢掉当前检查器。");
            await _input.Activate(Find<Button>(ui, "Fleet"));
            await _input.ClickItem(Find<Tree>(shell.ListContent!, "Objects"), 0);
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
            Require(shell.WorkspaceContent is null, "Esc 必须实际关闭报告工作区。");
            await _input.Activate(Find<Button>(ui, "BlueprintLibrary"));
            Require(shell.WorkspaceContent is BlueprintLibraryPanel &&
                Find<Button>(shell.WorkspaceContent, "NewDesign").IsVisibleInTree(), "设计工作区应提供原来的新建设计入口。");
            await UiCapture.Capture(TestScene, "shell-library");
            await _input.PressKey(Key.Escape);
            Require(shell.WorkspaceContent is null, "Esc 必须实际关闭蓝图库工作区。");
            var menu = Find<MenuButton>(ui, "GameMenu");
            var popup = menu.GetPopup();
            var labels = Enumerable.Range(0, popup.ItemCount).Select(popup.GetItemText).ToArray();
            foreach (var label in new[] { "保存", "存档与读档", "设置", "报告问题", "返回主菜单", "退出游戏", "开发工具", "创作模式" })
                Require(labels.Contains(label), $"游戏菜单缺少入口：{label}。");
            await _input.ChooseMenu(menu, "设置");
            var settings = Find<SettingsOverlay>(ui, "SettingsOverlay");
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
            await _input.Activate(Find<Button>(ui, "Notices"));
            var log = Find<Control>(notices, "NoticeLog");
            Require(log.IsVisibleInTree(), "通知记录应实际打开。");
            await _input.PressKey(Key.Escape);
            Require(!log.Visible && shell.SelectedObject is WorldObjectId.Settlement,
                "根视口 Esc 应只关闭通知记录，保留当前检查器。");
        });
    }

    [Test]
    public async Task Save_overlay_handles_escape_without_closing_the_inspector_below_it()
    {
        await Run(async (ui, shell) =>
        {
            await _input.Activate(Find<Button>(ui, "Nodes"));
            await _input.ClickItem(Find<Tree>(shell.ListContent!, "Objects"), FirstSelectableItem(Find<Tree>(shell.ListContent!, "Objects")));
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "存档与读档");
            var saves = ui.GetNode<SavePanel>("Root/SavePanel");
            Require(saves.Visible, "存档入口应打开现有存档覆盖层。");
            var search = Find<LineEdit>(saves, "Search");
            search.GrabFocus();
            await _input.Frames(2);
            Require(search.HasFocus(), "存档搜索框必须拿到焦点后再从根视口发送 Esc。");
            await _input.PressKey(Key.Escape);
            Require(!saves.Visible && shell.SelectedObject is WorldObjectId.Settlement && shell.ListContent is not null,
                "同一次 Esc 只能关闭存档覆盖层，不能再关闭下面的检查器。");
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
                await _input.ClickItem(Find<Tree>(shell.ListContent!, "Objects"), FirstSelectableItem(Find<Tree>(shell.ListContent!, "Objects")));
                var app = ui.GetParent<Train.App.App>();
                Find<NotificationPanel>(ui, "NotificationPanel").Add(new(NotificationCategory.Destruction,
                    NotificationLevel.Severe, "布局检查通知。", null, null), app.Host!.GetStatus()!.View, app.Host.Content!);
                foreach (var scale in new[] { 1f, 2f })
                {
                    window.ContentScaleFactor = scale;
                    await _input.Frames(6);
                    var toasts = Find<Control>(ui, "Toasts");
                    Require(toasts.GetChildCount() > 0, "布局检查必须包含真实通知提示。");
                    var map = Find<MacroMap>(ui, "MacroMap");
                    var tools = map.GetNode<Control>("MapTools").GetGlobalRect();
                    var sceneArea = new Rect2(shell.GlobalPosition + shell.SceneArea.Position, shell.SceneArea.Size);
                    Require(Math.Abs(tools.Position.X - sceneArea.Position.X) < .1f && Math.Abs(tools.End.Y - sceneArea.End.Y) < .1f,
                        $"地图工具必须贴齐场景区域左下角：scale={scale}，tools={tools}，scene={sceneArea}。");
                    await _input.Activate(map.GetNode<Button>("MapTools/Buttons/Layers"));
                    var layers = map.GetNode<PopupPanel>("InformationLayers");
                    Require(layers.Visible && shell.SceneArea.Encloses(new Rect2(layers.Position, layers.Size)),
                        $"宏观地图信息层弹窗必须完整留在场景区域内：scale={scale}，popup={new Rect2(layers.Position, layers.Size)}，scene={shell.SceneArea}。");
                    ShellGeometry.Check(ui);
                    await _input.PressKey(Key.Escape);
                    Require(!layers.Visible, "Esc 必须先关闭地图信息层弹窗。");
                    await ShellGeometry.CheckNavigationAndListText(TestScene, ui);
                    var pages = Find<TabContainer>(shell.InspectorContent!, "Pages");
                    if (scale == 2f && pages.GetTabBar().GetOffsetButtonsVisible())
                    {
                        await _input.ClickTab(pages, 0);
                        await _input.ClickLastTabWithScrollArrow(pages);
                    }
                    for (var page = 0; page < pages.GetTabCount(); page++)
                    {
                        await _input.ClickTab(pages, page);
                        ShellGeometry.Check(ui);
                        if (page == 0)
                            await UiCapture.Capture(TestScene, $"shell-settlement-overview-scale-{scale:0}", scale);
                    }
                    await _input.ClickTab(pages, Enumerable.Range(0, pages.GetTabCount()).Single(page => pages.GetTabTitle(page) == "车辆"));
                    await _input.Activate(Find<Button>(shell.InspectorContent!, "CommissionVehicle"));
                    await _input.Frames(3);
                    Require(Find<Control>(ui, "ConstructionWorkspace").IsVisibleInTree(), "制造入口未打开施工工作区。");
                    ShellGeometry.Check(ui);
                    await UiCapture.Capture(TestScene, $"shell-construction-workspace-scale-{scale:0}", scale);
                    await _input.PressKey(Key.Escape);
                    Require(shell.WorkspaceContent is null, "Esc 必须关闭施工工作区。");
                    foreach (var entry in new[] { "EconomyReports", "BlueprintLibrary" })
                    {
                        await _input.Activate(Find<Button>(ui, entry));
                        await _input.Frames(3);
                        ShellGeometry.Check(ui);
                        await UiCapture.Capture(TestScene, $"shell-{entry}-scale-{scale:0}", scale);
                        await _input.PressKey(Key.Escape);
                        Require(shell.WorkspaceContent is null, $"Esc 必须关闭 {entry} 工作区。");
                    }

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
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content(mapPresentation: true));
        setup.Session.QueueSubmit(new CoreCommand.BuyGoods(new(7), new(1), new(10), EconomyFixture.Food,
            EconomyFixture.LargeQuantity, new(731)), CommandSource.UserInterface,
            Completed(1, before, after, new CommandDecision.Accepted([], [])), after);
        await Run(async (ui, shell) =>
        {
            await _input.Activate(Find<Button>(ui, "Nodes"));
            await _input.ClickItem(Find<Tree>(shell.ListContent!, "Objects"), FirstSelectableItem(Find<Tree>(shell.ListContent!, "Objects")));
            var inspector = shell.InspectorContent!;
            await _input.ChooseTab(Find<TabContainer>(inspector, "Pages"), 1);
            await _input.Activate(inspector.FindChildren("SelectGood", "Button", true, false).Cast<Button>().First());
            await _input.Enter(Find<LineEdit>(inspector, "Quantity"), EconomyFixture.LargeQuantity.ToString());
            await _input.Activate(Find<Button>(inspector, "ConfirmTrade"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, after));
            var wallet = ui.GetNode<Label>("Root/World/Bar/TopBar/Wallet");
            var balance = after.Economy.Wallet.Value.ToString();
            var change = (after.Economy.Wallet.Value - before.Economy.Wallet.Value).ToString();
            await _input.WaitUntil(() => wallet.Text.Contains(balance));
            Require(wallet.Text.Contains(change), "成交后的顶栏应显示余额和本次变化。");
            ui._Process(60);
            Require(wallet.Text.Contains(balance) && !wallet.Text.Contains(change),
                "短暂的增减提示应在足够长时间后消失，当前余额仍须保留。");
            setup.Session.AssertComplete();
        }, setup);
    }

    private async Task Run(Func<GameUi, GameShell, Task> check, HostSimulationTests.HostSetup? scenario = null)
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        var setup = scenario ?? new HostSimulationTests.HostSetup(EconomyFixture.Create(), EconomyFixture.Content(mapPresentation: true));
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
