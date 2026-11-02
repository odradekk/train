using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class WorldListUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Named_fleet_filter_and_search_survive_closing_the_drawer()
    {
        await Run(async (app, ui, shell) =>
        {
            await Open(ui, "Fleet");
            var panel = shell.ListContent!;
            var list = Find<Tree>(panel, "Objects");
            var search = Find<LineEdit>(panel, "Search");
            Require(FilterButton(panel, "需要处理").Text.EndsWith(" 34"), "需要处理数量必须直接显示在按钮上。");
            await Filter(panel, "需要处理");
            await _input.Enter(search, "列车 8");
            Require(TreeItems(list).Length == 0 && Find<Label>(panel, "Empty").IsVisibleInTree(),
                "有计划且运行中的列车不能出现在需要处理搜索结果中，零结果必须显示提示。");
            await _input.Enter(search, "列车 9");
            await _input.ClickItem(list, 0);
            Require(shell.SelectedObject == new WorldObjectId.Train(new(9)), "按名称切换的筛选必须连接到需要处理条件。");
            await _input.PressKey(Key.Escape);
            await Open(ui, "Fleet");
            Require(shell.ListContent is null, "再次点击车队入口应收起抽屉。");
            await Open(ui, "Fleet");
            Require(FilterButton(shell.ListContent!, "需要处理").ButtonPressed &&
                Find<LineEdit>(shell.ListContent!, "Search").Text == "列车 9" && TreeItems(list).Length == 1,
                "关闭重开应保留筛选、搜索及匹配结果。");
        });
    }

    [Test]
    public async Task Named_edge_filters_select_the_corresponding_objects()
    {
        await Run(async (app, ui, shell) =>
        {
            await Open(ui, "Edges");
            var panel = shell.ListContent!;
            var list = Find<Tree>(panel, "Objects");
            await Filter(panel, "未建成");
            await _input.ClickItem(list, 0);
            Require(shell.SelectedObject == new WorldObjectId.Edge(new(3)), "未建成按钮必须连接零轨线路筛选。");
            await _input.PressKey(Key.Escape);
            await Filter(panel, "有断点");
            await _input.Enter(Find<LineEdit>(panel, "Search"), "北方");
            await _input.ClickItem(list, 0);
            Require(shell.SelectedObject == new WorldObjectId.Edge(new(2)), "有断点按钮必须与名称搜索共同筛选断点线路。");
        });
    }

    [Test]
    public async Task Ordinary_refresh_preserves_selection_and_scroll_without_moving_the_map()
    {
        await Run(async (app, ui, shell) =>
        {
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            var initialZoom = map.Zoom;
            var initialOffset = map.Offset;
            var point = map.GlobalPosition + map.Size / 2;
            await _input.MouseButton(point, MouseButton.WheelUp, true);
            await _input.MouseButton(point, MouseButton.WheelUp, false);
            await _input.Drag(point, new(45, 25));
            Require(map.Zoom != initialZoom && map.Offset != initialOffset, "测试输入必须先改变地图缩放和平移。");
            await Open(ui, "Fleet");
            var list = Find<Tree>(shell.ListContent!, "Objects");
            var zoom = map.Zoom;
            var offset = map.Offset;
            await _input.ClickItem(list, 1);
            Require(shell.SelectedObject == new WorldObjectId.Train(new(8)) && shell.InspectorContent is not null,
                "真实行点击必须打开所选列车检查器。");
            Require(map.Zoom == zoom && map.Offset == offset, "列表选择不能改变宏观地图缩放或平移。");
            await _input.PressKey(Key.Escape);
            Require(shell.InspectorContent is null && shell.ListContent is not null, "关闭检查器应保留抽屉。");
            var scroll = list.GetChildren(true).OfType<VScrollBar>().Single();
            await _input.WaitUntil(() => scroll.Visible && scroll.MaxValue > scroll.Page);
            var wheelPoint = list.GetGlobalRect().GetCenter();
            for (var i = 0; i < 4; i++)
            {
                await _input.MouseButton(wheelPoint, MouseButton.WheelDown, true);
                await _input.MouseButton(wheelPoint, MouseButton.WheelDown, false);
            }
            var before = scroll.Value;
            Require(before > 0, "真实滚轮输入必须让列表产生非零滚动。");
            var preceding = ((Lookup<TrainView>.Found)ui.View!.FindTrain(new(8))).Value with { Id = new(6) };
            var after = WorldListUiFixture.View(2, preceding, 777);
            var setup = _setup!;
            setup.Session.QueueAdvance(new(1, after.Position, [], null, null), after);
            Require(app.Host!.Step(1).Problem is null, "普通刷新必须发布新读视图。");
            await _input.WaitUntil(() => ReferenceEquals(ui.View, after));
            await _input.Frames(2);
            Require(list.GetSelected() is { } selected && selected.GetText(0).StartsWith("列车 8") &&
                selected.GetText(0).Contains("+777") && Math.Abs(scroll.Value - before) < .01,
                "普通刷新必须保留同一对象行的选择及滚动位置。");
            Require(map.Zoom == zoom && map.Offset == offset, "普通刷新不能改变宏观地图视角。");
            setup.Session.AssertComplete();
        });
    }

    [Test]
    public async Task Hidden_drawer_waits_until_reopened_to_update_rows()
    {
        await Run(async (app, ui, shell) =>
        {
            await Open(ui, "Fleet");
            var list = Find<Tree>(shell.ListContent!, "Objects");
            var original = TreeItems(list)[1].GetText(0);
            await Open(ui, "Fleet");
            var after = WorldListUiFixture.View(2, runningCashFlow: 999);
            _setup!.Session.QueueAdvance(new(1, after.Position, [], null, null), after);
            Require(app.Host!.Step(1).Problem is null, "隐藏期间必须发布新读视图。");
            await _input.WaitUntil(() => ReferenceEquals(ui.View, after));
            await _input.Frames(2);
            Require(TreeItems(list)[1].GetText(0) == original, "隐藏抽屉不能更新行。");
            await Open(ui, "Fleet");
            Require(TreeItems(list)[1].GetText(0).Contains("+999"), "重新打开必须立即显示最新读视图。");
            _setup.Session.AssertComplete();
        });
    }

    [Test]
    public async Task Node_group_headings_cannot_open_an_inspector()
    {
        await Run(async (app, ui, shell) =>
        {
            await Open(ui, "Nodes");
            var list = Find<Tree>(shell.ListContent!, "Objects");
            Require(TreeItems(list).Length > 2 && !TreeItems(list)[0].IsSelectable(0), "节点列表首行应为不可选择的分组标题。");
            await _input.ClickItem(list, 0);
            Require(shell.SelectedObject is null && shell.InspectorContent is null, "点击分组标题不能选择对象。");
            var rows = WorldListDrawer.Rows(WorldListKind.Nodes, ui.View!, app.Host!.Content!, "", 0);
            var row = Enumerable.Range(0, rows.Count).Single(index => rows[index].Object == new WorldObjectId.Node(new(1)));
            await _input.ClickItem(list, row);
            Require(shell.SelectedObject == new WorldObjectId.Node(new(1)) && shell.InspectorContent is not null,
                "分组下的真实节点仍须能打开检查器。");
        });
    }

    [Test]
    public async Task Drawer_filters_fit_the_shell_and_long_names_remain_reachable()
    {
        await Run(async (app, ui, shell) =>
        {
            var window = TestScene.GetTree().Root;
            var oldSize = window.Size;
            try
            {
                window.Size = new(1920, 1080);
                foreach (var entry in new[] { "Fleet", "Edges", "Nodes", "Combats" })
                {
                    await Open(ui, entry);
                    var panel = shell.ListContent!;
                    var count = entry is "Fleet" or "Edges" ? 4 : 1;
                    for (var index = 0; index < count; index++)
                    {
                        if (count > 1) await Filter(panel, (entry == "Fleet"
                            ? new[] { "全部", "需要处理", "运行中", "已停车" } : new[] { "全部", "已建成", "有断点", "未建成" })[index]);
                        await _input.Frames(3);
                        ShellGeometry.Check(ui);
                        Require(TreeItems(Find<Tree>(panel, "Objects")).All(item => item.GetTextAlignment(0) == HorizontalAlignment.Left),
                            "对象行和组标题必须左对齐。");
                        await UiCapture.Capture(TestScene, $"world-list-{entry.ToLowerInvariant()}-{index}");
                    }
                }
                window.Size = new(1280, 720);
                await Open(ui, "Edges");
                await Filter(shell.ListContent!, "全部");
                await _input.Frames(3);
                ShellGeometry.Check(ui);
                var list = Find<Tree>(shell.ListContent!, "Objects");
                Require(!list.ScrollHorizontalEnabled && TreeItems(list)[0].GetTooltipText(0).Contains("北方长名称河谷与山间运输枢纽平原"),
                    "窄窗口应保持列表宽度，完整长地名仍能通过提示读取。");
                await UiCapture.Capture(TestScene, "world-list-long-names-1280", size: new(1280, 720));
            }
            finally { window.Size = oldSize; }
        });
    }

    private HostSimulationTests.HostSetup? _setup;

    private static Button FilterButton(Control panel, string name) => panel.FindChildren("*", "Button", true, false)
        .Cast<Button>().Single(button => button.ToggleMode && (button.Text == name || button.Text.StartsWith(name + " ")));

    private async Task Filter(Control panel, string name)
    {
        var button = FilterButton(panel, name);
        Require(button.IsVisibleInTree(), "四个筛选必须常显。");
        await _input.Click(button.GetGlobalRect().GetCenter());
        Require(button.ButtonPressed, "单次点击必须切换筛选。");
    }

    private async Task Open(GameUi ui, string entry)
    {
        await _input.Activate(Find<Button>(ui, entry));
        await _input.Frames(2);
    }

    private async Task Run(Func<Train.App.App, GameUi, GameShell, Task> check)
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        _setup = new(WorldListUiFixture.View(), WorldListUiFixture.Content());
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => _setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            await _input.Frames(3);
            var ui = app.GetNode<GameUi>("GameUi");
            await check(app, ui, ui.GetNode<GameShell>("Root/World"));
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; _setup = null; }
    }
}
