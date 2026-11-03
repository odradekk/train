using System.Collections.Immutable;
using System.Globalization;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;
using static Train.Engine.Tests.SettlementFoundingUiFixture;

namespace Train.Engine.Tests;

public partial class SettlementFoundingUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Founding_confirm_refreshes_only_after_settlement_copies_are_paired()
    {
        var content = EconomyFixture.Content();
        var before = World(1, null);
        var after = World(2, new(4));
        var setup = new HostSimulationTests.HostSetup(before, content);
        Label? preview = null;
        string? during = null;
        IContentView? paired = null;
        setup.Copies = (basis, copies) =>
        {
            during = preview!.Text;
            Require(ReferenceEquals(basis, content), "配对没有使用提交时的固定内容。");
            Require(copies.Length == 1 && copies[0].Owner == new SettlementId(91), "接受结果中的聚居地副本没有交给内容配对。");
            return paired = EconomyFixture.Content();
        };
        await Run(setup, async ui =>
        {
            await OpenDetail(ui, 0);
            preview = Find<Label>(LatestDetail(ui), "FoundingPreview");
            Require(preview.Text.Contains("初始人口：40") && preview.Text.Contains("主食 1.5/秒") &&
                Find<Label>(LatestDetail(ui), "FoundingConditions").Text.Contains("12") && !preview.Text.Contains("建立进度"), "确认前没有显示核心预览。");
            await _input.Choose(Find<OptionButton>(LatestDetail(ui), "MaterialSource"), 1);
            setup.Session.QueueSubmit(new CoreCommand.FoundSettlement(new(1), new(7)), CommandSource.UserInterface,
                Completed(1, before, after, new CommandDecision.Accepted([new WorldObjectId.Job(new(4))],
                    [new OwnedSettlementTemplateReference(new(91), new("fixture:template"), [])])), after);
            await _input.Activate(Find<Button>(LatestDetail(ui), "FoundSettlement"));
            var confirmation = Find<ConfirmationDialog>(LatestDetail(ui), "FoundingConfirmation");
            Require(confirmation.DialogText.Contains("主食 × 12") && confirmation.DialogText.Contains("列车 7"),
                "确认框没有说明材料来源和完整消耗。");
            await _input.Activate(confirmation.GetOkButton(), confirmation);
            await _input.Frames(4);
            var host = ui.GetParent<Train.App.App>().Host!;
            var job = host.GetStatus()!.View.World.Nodes.First(node => node.Id == new NodeId(1)).FoundingJob;
            Require(during is not null && !during.Contains("建立进度"), "界面在副本配对前刷新了建立进度。");
            Require(preview.Text.Contains("建立进度") && preview.Text.Contains("3 秒") &&
                !preview.Text.Contains("91") && !preview.Text.Contains("步") && ReferenceEquals(host.Content, paired),
                $"配对后没有显示建城进度或没有装回配对内容。text={preview.Text} job={job} same={ReferenceEquals(host.Content, paired)} during={during}");
            Require(!Find<Button>(LatestDetail(ui), "FoundSettlement").Visible, "受理后仍显示建立按钮。");
            Require(setup.Session.SubmitCount == 1, "建城确认没有经宿主提交一次。");
            await UiCapture.CaptureSizes(TestScene, "founding-progress");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Closed_blocked_unowned_and_lair_paths_do_not_submit()
    {
        var view = World(1, null);
        var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        var scenes = new FixedDisplayFactory
        {
            OpenWorldCall = (_, _) => new SceneResult<IWorldSceneSession>.Failure(
                [new("fixture_scene_unavailable", SceneStage.Build, null, null, null, null, "建城测试不构建世界场景。")])
        };
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            app.CreateScenes = () => scenes;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            await _input.Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            var panels = ui.GetNode<WorldPanels>("Root/World/WorldPanels");
            await ClickNode(map, view.World.Nodes[3]);
            Require(scenes.OpenCalls.Count == 1 && panels.IsObjectOpen(new WorldObjectId.Node(new(4))), "生物聚落单击没有选中节点或进入场景。");
            await DismissError(ui);
            await ClickNode(map, view.World.Nodes[4]);
            Require(scenes.OpenCalls.Count == 2 && Find<GameShell>(ui, "World").SelectedObject == new WorldObjectId.Settlement(new(1)), "非自有聚居地单击没有选中聚居地或进入场景。");
            await DismissError(ui);
            await ClickNode(map, view.World.Nodes[0]);
            Require(scenes.OpenCalls.Count == 3 && panels.IsObjectOpen(new WorldObjectId.Node(new(1))), "空节点单击没有同时进入场景并打开详情。");
            await DismissError(ui);
            Require(Find<Label>(LatestDetail(ui), "FoundingPreview").Text.Contains("初始人口：40"), "空节点没有打开建城预览。");
            await _input.Activate(Find<Button>(LatestDetail(ui), "Close"));
            Require(Find<GameShell>(ui, "World").WorkspaceContent is null, "关闭建城后仍保留工作区遮罩。");
            Require(setup.Session.SubmitCount == 0, "关闭建城面板提交了命令。");
            await OpenDetail(ui, 2);
            var blocked = Find<Label>(LatestDetail(ui), "FoundingReason").Text;
            var blockedTitle = LatestDetail(ui).FindChildren("*", "Label", true, false).Cast<Label>().First().Text;
            Require(blocked.Contains("节点已被占据") && !blocked.Contains("节点 3") && !blocked.Contains("node_occupied"),
                $"有阻止原因时没有显示中文原因。title={blockedTitle} text={blocked}");
            await _input.Choose(Find<OptionButton>(LatestDetail(ui), "MaterialSource"), 1);
            Require(Find<Button>(LatestDetail(ui), "FoundSettlement").Disabled, "预览存在阻止原因时仍可确认。");
            await CloseDetail(ui);
            await OpenDetail(ui, 1);
            var unavailable = Find<Label>(LatestDetail(ui), "FoundingPreview").Text;
            Require(unavailable.Contains("建立条件暂不可用") && unavailable.Contains("所需内容缺失") && !unavailable.Contains("content_missing") &&
                !unavailable.Contains("Population.Rule") && !unavailable.Contains("短缺"),
                "人口规则缺失被写成物资短缺或露出了内部标识。");
            await _input.Choose(Find<OptionButton>(LatestDetail(ui), "MaterialSource"), 1);
            Require(Find<Button>(LatestDetail(ui), "FoundSettlement").Disabled, "无法核对消耗时仍允许建立。");
            await CloseDetail(ui);
            await OpenDetail(ui, 4);
            var settlement = Find<PanelContainer>(ui, "Settlement");
            Require(Find<TabContainer>(settlement, "Pages").GetTabCount() == 4 &&
                !settlement.FindChildren("FoundSettlement", "Button", true, false).Any(), "非自有聚居地提供了建城。");
            await CloseDetail(ui);
            await OpenDetail(ui, 3);
            var lair = LatestDetail(ui);
            Require(!Find<Button>(lair, "FoundSettlement").Visible &&
                !lair.FindChildren("Economy", "Button", true, false).Any(), "生物聚落提供了建城或聚居地服务。");
            await CloseDetail(ui);
            await OpenDetail(ui, 5);
            var trains = Find<OptionButton>(LatestDetail(ui), "MaterialSource");
            Require(trains.ItemCount == 1 && trains.GetItemText(0) == "该节点没有停靠的列车" &&
                Find<Button>(LatestDetail(ui), "FoundSettlement").Disabled, "没有停靠列车时下拉框为空白或仍可确认。");
            await _input.Activate(Find<Button>(LatestDetail(ui), "Close"));
            Require(setup.Session.SubmitCount == 0, "未确认的建城路径提交了命令。");
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Real_new_game_preview_shows_core_residents_consumption_and_materials()
    {
        var directory = ContentFixture.CopyBuiltIn();
        var fixture = new Fixture(TestScene.GetTree());
        using var dispatcher = new GodotMainThreadDispatcher();
        var scale = TestScene.GetTree().Root.ContentScaleFactor;
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => ContentFixture.CreateHost(logger, directory, dispatcher);
            await fixture.AddToRoot(app);
            var host = app.Host!;
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher, timeout: ContentPreparationTimeout);
            host.BeginNewGame(new("core:basic-through-world"), 1, "stage1-world-v1", host.SessionOptions!);
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher, timeout: ContentPreparationTimeout);
            var status = host.GetStatus()!;
            var content = host.Content!;
            var nodes = status.View.World.Nodes;
            var index = -1;
            SettlementFoundingPreview? preview = null;
            for (var i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].Occupant is not NodeOccupant.Empty) continue;
                if (status.View.PreviewSettlement(nodes[i].Id) is not CoreResult<SettlementFoundingPreview>.Success success ||
                    !success.Value.Blockers.IsEmpty || success.Value.InitialResidents <= 0 ||
                    success.Value.ExpectedConsumption.IsDefaultOrEmpty || success.Value.RequiredMaterials.IsDefaultOrEmpty)
                    continue;
                index = i;
                preview = success.Value;
                break;
            }
            Require(preview is not null, "真实新局没有可预览人口、生活物资和材料的空节点。");
            var shown = preview!;
            TestScene.GetTree().Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
            await _input.Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.Activate(Find<Button>(ui, "Nodes"));
            var list = NodeObjects(ui);
            var rows = WorldListDrawer.Rows(WorldListKind.Nodes, status.View, content, "", 0);
            var row = Enumerable.Range(0, rows.Count).Single(i => rows[i].Object == new WorldObjectId.Node(nodes[index].Id));
            list.ScrollToItem(TreeItems(list)[row]);
            await _input.Frames(2);
            await _input.ClickItem(list, row);
            Require(Find<GameShell>(ui, "World").SelectedObject == new WorldObjectId.Node(nodes[index].Id),
                "建城预览必须选中已找到的实际空节点。");
            var text = Find<Label>(LatestDetail(ui), "FoundingPreview").Text;
            Require(text.Contains($"初始人口：{shown.InitialResidents}"), "建城面板的初始人口不是核心预览。");
            foreach (var rate in shown.ExpectedConsumption)
                Require(text.Contains($"{WorldPanels.GoodName(rate.Good, content)} {rate.UnitsPerSecond.ToString("0.###", CultureInfo.InvariantCulture)}/秒"),
                    "建城面板的生活物资用量不是核心预览。");
            foreach (var material in shown.RequiredMaterials)
                Require(Find<Label>(LatestDetail(ui), "FoundingConditions").Text.Contains($"{WorldPanels.GoodName(material.Good, content)}：0 / {material.Quantity}"),
                    "检查器的所需材料不是核心预览。");
            Require(text.Contains("工期：" + EconomyPanels.ServiceDuration(status.View, shown.DurationTicks / (double)SimulationTime.StepsPerSecond)),
                "检查器的工期不是核心预览，或没有使用秒与游戏时间。");
        }
        finally
        {
            await fixture.Cleanup(); TestScene.GetTree().Root.ContentScaleFactor = scale;
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task Open_node_detail_replaces_founding_when_a_settlement_appears()
    {
        var before = World(1, null);
        var after = World(2, null, new NodeOccupant.Settlement(new(2), SettlementRelation.PlayerOwned));
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async ui =>
        {
            await OpenDetail(ui, 0);
            var detail = LatestDetail(ui);
            Require(detail.Visible && detail.FindChildren("FoundSettlement", "Button", true, false).Cast<Button>().Any(),
                "空节点没有建城按钮。");
            var host = ui.GetParent<Train.App.App>().Host!;
            setup.Session.QueueAdvance(new(1, after.Position, [], null, null), after);
            Require(host.Step(1).Problem is null, "无法发布占用者变化。");
            await _input.Frames(2);
            var shell = Find<GameShell>(ui, "World");
            Require(shell.SelectedObject == new WorldObjectId.Settlement(new(2)) &&
                shell.InspectorContent is { Visible: true } settlement &&
                Enumerable.Range(0, Find<TabContainer>(settlement, "Pages").GetTabCount())
                    .Select(Find<TabContainer>(settlement, "Pages").GetTabTitle)
                    .SequenceEqual(new[] { "概况", "库存", "建筑", "车辆" }) &&
                !settlement.FindChildren("FoundSettlement", "Button", true, false).Any(),
                "占用者变化没有把建城入口替换为聚居地检查器。");
            var entry = Find<Button>(shell.InspectorContent!, "OpenSettlementCombat");
            await _input.Click(entry.GetGlobalRect().GetCenter());
            Require(Find<Button>(ui, "DeclareWar").IsVisibleInTree() && Find<Button>(ui, "Occupy").IsVisibleInTree(),
                "新聚居地的战斗入口没有打开宣战和占领。");
        });
        setup.Session.AssertComplete();
    }

    private async Task Run(HostSimulationTests.HostSetup setup, Func<GameUi, Task> body)
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            await _input.Frames(2);
            await body(app.GetNode<GameUi>("GameUi"));
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

    private async Task CloseDetail(GameUi ui)
    {
        await _input.Activate(Find<Button>(Find<GameShell>(ui, "World").InspectorContent!, "Close"));
        await _input.Frames(2);
    }

    private async Task OpenDetail(GameUi ui, int nodeIndex)
    {
        if (Find<GameShell>(ui, "World").ListContent?.Name != "NodesList")
            await _input.Activate(Find<Button>(ui, "Nodes"));
        var panel = Find<PanelContainer>(ui, "NodesList");
        var node = ui.View!.World.Nodes[nodeIndex];
        var rows = WorldListDrawer.Rows(WorldListKind.Nodes, ui.View!, ui.GetParent<Train.App.App>().Host!.Content!, "", 0);
        var row = Enumerable.Range(0, rows.Count).Single(i => rows[i].Object == new WorldObjectId.Node(node.Id));
        await _input.ClickItem(Find<Tree>(panel, "Objects"), row);
        var expected = node.Occupant is NodeOccupant.Settlement town
            ? (WorldObjectId)new WorldObjectId.Settlement(town.Id) : new WorldObjectId.Node(node.Id);
        Require(Find<GameShell>(ui, "World").SelectedObject == expected, "节点行点击必须选中指定的实际对象。");
    }

    private async Task ClickNode(MacroMap map, NodeMapItem node)
    {
        var point = map.GlobalPosition + map.MapToScreen(node.Position);
        var screen = TestScene.GetViewport().GetScreenTransform() * point;
        using (var motion = new InputEventMouseMotion { Position = screen, GlobalPosition = screen })
            Input.ParseInputEvent(motion);
        await _input.Frames();
        Require(TestScene.GetTree().Root.GuiGetHoveredControl() == map, $"节点 {node.Id.Value} 的点击被其他界面遮挡。");
        await _input.Click(point);
    }

    private async Task DismissError(GameUi ui)
    {
        var panel = ui.GetNode<PanelContainer>("Root/ErrorPanel");
        if (panel.Visible) await _input.Activate(Find<Button>(panel, "Close"));
    }

    private static PanelContainer LatestDetail(GameUi ui) => Find<PanelContainer>(ui, "Details");

}
