using Chickensoft.GoDotTest;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class GameShellAppTests
{
    [Test]
    public async Task Founding_content_remains_reachable_in_the_narrow_workspace()
    {
        var setup = new HostSimulationTests.HostSetup(SettlementFoundingUiFixture.World(1, null),
            EconomyFixture.Content(mapPresentation: true));
        await Run(async (ui, shell) =>
        {
            await _input.Activate(Find<Button>(ui, "Nodes"));
            var rows = WorldListDrawer.Rows(WorldListKind.Nodes, ui.View!, ui.GetParent<Train.App.App>().Host!.Content!, "", 0);
            var row = Enumerable.Range(0, rows.Count).Single(index => rows[index].Object == new WorldObjectId.Node(new(1)));
            await _input.ClickItem(Find<Tree>(shell.ListContent!, "Objects"), row);
            Require(shell.SelectedObject == new WorldObjectId.Node(new(1)), "建城检查必须选中原来的空节点。");
            await _input.Activate(Find<Button>(shell.InspectorContent!, "FoundSettlement"));
            await CheckNarrowScrollableContent(ui, shell.WorkspaceContent!);
        }, setup);
    }

    [Test]
    public async Task Railway_work_content_remains_reachable_in_the_narrow_inspector()
    {
        var setup = new HostSimulationTests.HostSetup(RailwayWorkUiFixture.WithManyBreaks(), RailwayWorkUiFixture.Content());
        await Run(async (ui, shell) =>
        {
            await _input.Activate(Find<Button>(ui, "Edges"));
            await _input.ClickItem(Find<Tree>(shell.ListContent!, "Objects"), 2);
            Require(shell.SelectedObject == new WorldObjectId.Edge(new(RailwayWorkUiFixture.ActiveEdge)),
                "铁路施工检查必须选中包含全部断点的线路。");
            Require(shell.InspectorContent?.Name == "LineInspector" && shell.WorkspaceContent is null,
                "选中线路应直接打开检查器，不应创建施工工作区。");
            await CheckNarrowScrollableContent(ui, shell.InspectorContent!);
        }, setup);
    }

    private async Task CheckNarrowScrollableContent(GameUi ui, Control content)
    {
        var window = TestScene.GetTree().Root;
        var size = window.Size;
        try
        {
            window.Size = new(1280, 720);
            window.ContentScaleFactor = 2;
            await _input.Frames(6);
            ShellGeometry.Check(ui);
            var scroll = Find<ScrollContainer>(content, "Content");
            scroll.ScrollHorizontal = (int)scroll.GetHScrollBar().MaxValue;
            scroll.ScrollVertical = (int)scroll.GetVScrollBar().MaxValue;
            await _input.Frames(3);
            var area = scroll.GetGlobalRect();
            if (scroll.GetHScrollBar().IsVisibleInTree()) area.Size -= new Vector2(0, scroll.GetHScrollBar().Size.Y);
            if (scroll.GetVScrollBar().IsVisibleInTree()) area.Size -= new Vector2(scroll.GetVScrollBar().Size.X, 0);
            var end = scroll.GetChild<Control>(0).GetGlobalRect().End;
            Require(area.HasArea() && end.X <= area.End.X + 1 && end.Y <= area.End.Y + 1,
                $"最窄面板必须能滚到全部内容的右下角：end={end}，area={area}。");
        }
        finally { window.Size = size; }
    }

    [Test]
    public async Task Workspace_mask_leaves_sidebar_mouse_actions_available_and_ignores_background_clicks()
    {
        await Run(async (ui, shell) =>
        {
            var window = TestScene.GetTree().Root;
            var size = window.Size;
            try
            {
                window.Size = new(1280, 720);
                window.ContentScaleFactor = 2;
                await _input.Activate(Find<Button>(ui, "EconomyReports"));
                var workspace = shell.WorkspaceContent;
                await _input.Click(Find<Button>(ui, "Fleet").GetGlobalRect().GetCenter());
                Require(shell.ListContent?.Name == "FleetList" && shell.WorkspaceContent == workspace,
                    "工作区打开时鼠标仍须能切换侧栏列表，保留当前工作区。");
                var list = Find<Tree>(shell.ListContent!, "Objects");
                var item = TreeItems(list)[FirstSelectableItem(list)];
                list.ScrollToItem(item);
                await _input.Frames(3);
                var row = list.GetItemAreaRect(item);
                row.Position += list.GlobalPosition;
                await _input.Click(row.Intersection(shell.ListContent!.GetGlobalRect()).GetCenter());
                Require(shell.SelectedObject == new WorldObjectId.Train(new(7)), "工作区不能挡住列表行的鼠标选择。");
                await _input.Frames(3);
                ShellGeometry.Check(ui);
                var mask = Find<Control>(shell, "WorkspaceMask");
                await _input.Click(mask.GlobalPosition + new Vector2(2, 2));
                Require(shell.WorkspaceContent == workspace, "点击工作区四周的遮罩不能关闭工作区。");
                var edit = Find<Button>(shell.InspectorContent!, "EditPlan");
                Require(edit.IsVisibleInTree() && shell.InspectorContent!.GetGlobalRect().Encloses(edit.GetGlobalRect()),
                    "检查器固定底栏中的计划入口必须完整可见。");
                await _input.Click(edit.GetGlobalRect().GetCenter());
                Require(shell.WorkspaceContent is TrainPlanWorkspace, "检查器按钮必须响应鼠标并替换工作区。");
            }
            finally { window.Size = size; }
        });
    }

    [Test]
    public async Task Workspaces_leave_navigation_list_and_inspector_available_at_narrow_and_wide_sizes()
    {
        var view = EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.Owned);
        view.FormationPreviews = TrainFormationTestScenario.Vehicle9;
        var draft = EditorPreviewSession.Draft with { Display = new("青川长途货运车辆", "布局检查设计") };
        var blueprint = new BlueprintRecord(new(BlueprintLibrary.Local, new("layout-design")), draft, [], EditorPreviewSession.Evaluate(draft));
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content(vehicleBlueprint: blueprint, mapPresentation: true));
        await Run(async (ui, shell) =>
        {
            var window = TestScene.GetTree().Root;
            var size = window.Size;
            try
            {
                window.Size = new(1920, 1080);
                await _input.Activate(Find<Button>(ui, "Nodes"));
                await _input.ClickItem(Find<Tree>(shell.ListContent!, "Objects"), FirstSelectableItem(Find<Tree>(shell.ListContent!, "Objects")));
                var cases = new[] { (new Vector2I(1920, 1080), 1f), (new Vector2I(1280, 720), 2f) };
                if (DisplayServer.GetName() != "headless" &&
                    !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("TRAIN_UI_CAPTURE_DIR")))
                    cases = new[] { new Vector2I(1280, 720), new Vector2I(1920, 1080), new Vector2I(2560, 1440) }
                        .SelectMany(dimensions => new[] { (dimensions, 1f), (dimensions, 2f) }).ToArray();
                foreach (var (dimensions, scale) in cases)
                {
                    window.Size = dimensions;
                    window.ContentScaleFactor = scale;
                    await _input.Frames(6);
                    foreach (var entry in new[] { "EconomyReports", "BlueprintLibrary", "Formation", "Plan" })
                    {
                        if (entry == "Formation")
                        {
                            await _input.ChooseTab(Find<TabContainer>(shell.InspectorContent!, "Pages"), 2);
                            var selected = Find<CheckBox>(shell.InspectorContent!, "ParkedVehicle9");
                            if (!selected.ButtonPressed) await _input.Activate(selected);
                            await _input.Activate(Find<Button>(shell.InspectorContent!, "FormParked"));
                            Require(Find<CheckBox>(shell.WorkspaceContent!, "Vehicle9").ButtonPressed,
                                "车辆页的勾选应预选进入编组工作区。");
                        }
                        else if (entry == "Plan")
                        {
                            await _input.Activate(Find<Button>(ui, "Fleet"));
                            await _input.ClickItem(Find<Tree>(shell.ListContent!, "Objects"), 0);
                            await _input.Activate(Find<Button>(shell.InspectorContent!, "EditPlan"));
                        }
                        else await _input.Activate(Find<Button>(ui, entry));
                        await _input.Frames(6);
                        ShellGeometry.Check(ui);
                        await UiCapture.Capture(TestScene, $"workspace-{entry}-{dimensions.X}x{dimensions.Y}-scale-{scale:0}", scale, dimensions);
                        if (scale == 2 && entry != "EconomyReports")
                        {
                            var scroll = Find<ScrollContainer>(shell.WorkspaceContent!, "ColumnsScroll");
                            var bar = scroll.GetHScrollBar();
                            Require(bar.IsVisibleInTree(), $"{entry} 的窄工作区必须能横向滚动。");
                            await _input.Click(new(bar.GetGlobalRect().End.X - 2, bar.GetGlobalRect().GetCenter().Y));
                            Require(scroll.ScrollHorizontal > 0, $"{entry} 的横向滚动条必须响应鼠标。");
                            scroll.ScrollHorizontal = (int)bar.MaxValue;
                            await _input.Frames(3);
                            var right = scroll.GetChild<Control>(0).GetChildren().OfType<Control>().Last().GetGlobalRect();
                            Require(right.End.X <= scroll.GetGlobalRect().End.X && right.End.X > scroll.GlobalPosition.X,
                                $"{entry} 必须能滚到最右列：right={right}，scroll={scroll.GetGlobalRect()}。");
                        }
                        await _input.PressKey(Key.Escape);
                    }
                    await _input.Activate(Find<Button>(ui, "Nodes"));
                    await _input.ClickItem(Find<Tree>(shell.ListContent!, "Objects"), FirstSelectableItem(Find<Tree>(shell.ListContent!, "Objects")));
                }
            }
            finally { window.Size = size; }
        }, setup);
    }
}
