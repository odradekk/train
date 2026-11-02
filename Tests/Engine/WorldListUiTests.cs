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
    public async Task Fleet_filter_and_search_intersect_and_survive_closing_the_drawer()
    {
        await Run(async (app, ui, shell) =>
        {
            await Open(ui, "Fleet");
            var panel = shell.ListContent!;
            var list = Find<ItemList>(panel, "Objects");
            var filter = Find<OptionButton>(panel, "Filter");
            var search = Find<LineEdit>(panel, "Search");
            Require(list.ItemCount == 36, "全部车队应显示夹具的36列列车。");
            await _input.Choose(filter, 2);
            Require(list.ItemCount == 1, "运行中筛选应只显示运行中的列车。");
            await _input.Enter(search, "列车 7");
            Require(list.ItemCount == 0, "搜索必须与运行中筛选叠加，不能重新显示已停车列车。");
            Require(panel.FindChildren("*", "Label", true, false).Cast<Label>()
                .Any(label => label.IsVisibleInTree() && label.Text.Contains("没有")), "零结果必须显示可见提示。");
            await _input.Choose(filter, 1);
            Require(list.ItemCount == 1, "需要处理筛选与相同搜索应找到无计划且已停车的列车7。");
            await Open(ui, "Fleet");
            Require(shell.ListContent is null, "再次点击车队入口应收起抽屉。");
            await Open(ui, "Fleet");
            Require(Find<OptionButton>(shell.ListContent!, "Filter").Selected == 1 &&
                Find<LineEdit>(shell.ListContent!, "Search").Text == "列车 7" &&
                Find<ItemList>(shell.ListContent!, "Objects").ItemCount == 1,
                "关闭重开应保留筛选、搜索及匹配结果。");
        });
    }

    [Test]
    public async Task Edge_filter_separates_built_broken_and_unbuilt_lines()
    {
        await Run(async (app, ui, shell) =>
        {
            await Open(ui, "Edges");
            var panel = shell.ListContent!;
            var filter = Find<OptionButton>(panel, "Filter");
            var list = Find<ItemList>(panel, "Objects");
            Require(list.ItemCount == 3, "全部线路应包括完整、有断点和未建成线路。");
            await _input.Choose(filter, 1);
            Require(list.ItemCount == 2, "已建成筛选应包括完整线路和有断点线路。");
            await _input.Choose(filter, 2);
            Require(list.ItemCount == 1, "有断点筛选应只保留有断点线路。");
            await _input.Enter(Find<LineEdit>(panel, "Search"), "没有此地名");
            Require(list.ItemCount == 0, "线路搜索必须与断点筛选叠加。");
            await _input.Enter(Find<LineEdit>(panel, "Search"), "北方");
            Require(list.ItemCount == 1, "线路应能按端点展示名称搜索。");
            await _input.Choose(filter, 3);
            Require(list.ItemCount == 1, "未建成筛选应只显示零轨线路。");
        });
    }

    [Test]
    public async Task Ordinary_refresh_preserves_selection_and_scroll_without_moving_the_map()
    {
        await Run(async (app, ui, shell) =>
        {
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            var point = map.GlobalPosition + map.Size / 2;
            await _input.MouseButton(point, MouseButton.WheelUp, true);
            await _input.MouseButton(point, MouseButton.WheelUp, false);
            await _input.Drag(point, new(45, 25));
            await Open(ui, "Fleet");
            var list = Find<ItemList>(shell.ListContent!, "Objects");
            var zoom = map.Zoom;
            var offset = map.Offset;
            await _input.ClickItem(list, 0);
            Require(shell.SelectedObject == new WorldObjectId.Train(new(7)) && shell.InspectorContent is not null,
                "真实行点击必须打开所选列车检查器。");
            Require(map.Zoom == zoom && map.Offset == offset, "列表选择不能改变宏观地图缩放或平移。");
            await _input.PressKey(Key.Escape);
            Require(shell.InspectorContent is null && shell.ListContent is not null, "关闭检查器应保留抽屉。");
            var scroll = list.GetVScrollBar();
            await _input.WaitUntil(() => scroll.Visible && scroll.MaxValue > scroll.Page);
            var wheelPoint = list.GetGlobalRect().GetCenter();
            for (var i = 0; i < 4; i++)
            {
                await _input.MouseButton(wheelPoint, MouseButton.WheelDown, true);
                await _input.MouseButton(wheelPoint, MouseButton.WheelDown, false);
            }
            var before = scroll.Value;
            Require(before > 0, "真实滚轮输入必须让列表产生非零滚动。");
            var after = WorldListUiFixture.View(2);
            var setup = _setup!;
            setup.Session.QueueAdvance(new(1, after.Position, [], null, null), after);
            Require(app.Host!.Step(1).Problem is null, "普通刷新必须发布新读视图。");
            await _input.WaitUntil(() => ReferenceEquals(ui.View, after));
            Require(list.GetSelectedItems().SequenceEqual(new[] { 0 }) && Math.Abs(scroll.Value - before) < .01,
                "普通刷新必须保留同一对象行的选择及滚动位置。");
            Require(map.Zoom == zoom && map.Offset == offset, "普通刷新不能改变宏观地图视角。");
            setup.Session.AssertComplete();
        });
    }

    [Test]
    public async Task Node_group_headings_cannot_open_an_inspector()
    {
        await Run(async (app, ui, shell) =>
        {
            await Open(ui, "Nodes");
            var list = Find<ItemList>(shell.ListContent!, "Objects");
            Require(list.ItemCount > 2 && !list.IsItemSelectable(0), "节点列表首行应为不可选择的分组标题。");
            await _input.ClickItem(list, 0);
            Require(shell.SelectedObject is null && shell.InspectorContent is null, "点击分组标题不能选择对象。");
            var rows = WorldListDrawer.Rows(WorldListKind.Nodes, ui.View!, app.Host!.Content!, "", 0);
            var row = Enumerable.Range(0, rows.Count).Single(index => rows[index].Object == new WorldObjectId.Node(new(1)));
            list.Select(row);
            list.EnsureCurrentIsVisible();
            await _input.Frames(2);
            Require(shell.InspectorContent is null, "定位行不能代替真实输入打开检查器。");
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
                        if (count > 1) await _input.Choose(Find<OptionButton>(panel, "Filter"), index);
                        await _input.Frames(3);
                        ShellGeometry.Check(ui);
                        await UiCapture.Capture(TestScene, $"world-list-{entry.ToLowerInvariant()}-{index}");
                    }
                }
                window.Size = new(1280, 720);
                await Open(ui, "Edges");
                await _input.Choose(Find<OptionButton>(shell.ListContent!, "Filter"), 0);
                await _input.Frames(3);
                ShellGeometry.Check(ui);
                var list = Find<ItemList>(shell.ListContent!, "Objects");
                Require(!list.GetHScrollBar().Visible && list.GetItemTooltip(0).Contains("北方长名称河谷与山间运输枢纽平原"),
                    "窄窗口应保持列表宽度，完整长地名仍能通过提示读取。");
                await UiCapture.Capture(TestScene, "world-list-long-names-1280", size: new(1280, 720));
            }
            finally { window.Size = oldSize; }
        });
    }

    private HostSimulationTests.HostSetup? _setup;

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
