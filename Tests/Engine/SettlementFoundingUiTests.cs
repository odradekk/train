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

namespace Train.Engine.Tests;

public class SettlementFoundingUiTests(Node testScene) : TestClass(testScene)
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
            await OpenFounding(ui, 0);
            preview = Find<Label>(Founding(ui), "FoundingPreview");
            Require(preview.Text.Contains("初始人口：40") && preview.Text.Contains("主食 1.5/秒") &&
                preview.Text.Contains("主食 × 12") && !preview.Text.Contains("建城进度"), "确认前没有显示核心预览。");
            await _input.Choose(Find<OptionButton>(Founding(ui), "MaterialSource"), 1);
            setup.Session.QueueSubmit(new CoreCommand.FoundSettlement(new(1), new(7)), CommandSource.UserInterface,
                Completed(1, before, after, new CommandDecision.Accepted([new WorldObjectId.Job(new(4))],
                    [new OwnedSettlementTemplateReference(new(91), new("fixture:template"), [])])), after);
            await _input.Activate(Find<Button>(Founding(ui), "ConfirmFounding"));
            await _input.Frames(4);
            var detail = Find<Label>(ui.GetNode<WorldPanels>("Root/World/WorldPanels"), "Description");
            var host = ui.GetParent<Train.App.App>().Host!;
            var job = host.GetStatus()!.View.World.Nodes.First(node => node.Id == new NodeId(1)).FoundingJob;
            Require(during is not null && !during.Contains("建城进度"), "界面在副本配对前刷新了建城进度。");
            Require(preview.Text.Contains("建城进度") && preview.Text.Contains("已用 0 / 180 步") &&
                preview.Text.Contains("预留聚居地 91") && ReferenceEquals(host.Content, paired),
                $"配对后没有显示建城进度或没有装回配对内容。text={preview.Text} job={job} same={ReferenceEquals(host.Content, paired)} during={during}");
            Require(detail.Text.Contains("建城进行中") && !detail.Text.Contains("已用") && !detail.Text.Contains("预留聚居地"),
                "节点详情重复了建城进度。");
            Require(!Find<Button>(Founding(ui), "ConfirmFounding").Visible, "受理后仍显示确认建立。");
            Require(setup.Session.SubmitCount == 1, "建城确认没有经宿主提交一次。");
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
            Require(scenes.OpenCalls.Count == 1 && !panels.IsObjectOpen(new WorldObjectId.Node(new(4))), "生物聚落单击打开了节点详情或没有进入场景。");
            await DismissError(ui);
            await ClickNode(map, view.World.Nodes[4]);
            Require(scenes.OpenCalls.Count == 2 && !panels.IsObjectOpen(new WorldObjectId.Node(new(5))), "非自有聚居地单击打开了节点详情或没有进入场景。");
            await DismissError(ui);
            await ClickNode(map, view.World.Nodes[0]);
            Require(scenes.OpenCalls.Count == 3 && panels.IsObjectOpen(new WorldObjectId.Node(new(1))), "空节点单击没有同时进入场景并打开详情。");
            await DismissError(ui);
            await _input.Activate(Find<Button>(LatestDetail(panels), "FoundSettlement"));
            Require(Find<Label>(Founding(ui), "FoundingPreview").Text.Contains("初始人口：40"), "空节点没有打开建城预览。");
            await _input.Activate(Find<Button>(Founding(ui), "Close"));
            await CloseDetail(panels);
            Require(setup.Session.SubmitCount == 0, "关闭建城面板提交了命令。");
            await OpenFounding(ui, 2);
            var blocked = Find<Label>(Founding(ui), "FoundingPreview").Text;
            var blockedTitle = LatestDetail(panels).FindChildren("*", "Label", true, false).Cast<Label>().First().Text;
            Require(blocked.Contains("节点已被占据") && blocked.Contains("node_occupied"),
                $"有阻止原因时没有显示核心原因。title={blockedTitle} text={blocked}");
            await _input.Choose(Find<OptionButton>(Founding(ui), "MaterialSource"), 1);
            Require(Find<Button>(Founding(ui), "ConfirmFounding").Disabled, "预览存在阻止原因时仍可确认。");
            await _input.Activate(Find<Button>(Founding(ui), "Close"));
            await CloseDetail(panels);
            await OpenFounding(ui, 1);
            var unavailable = Find<Label>(Founding(ui), "FoundingPreview").Text;
            Require(unavailable.Contains("content_missing") && unavailable.Contains("Population.Rule") && !unavailable.Contains("短缺"),
                "人口规则缺失被写成物资短缺或没有显示核心原因。");
            await _input.Choose(Find<OptionButton>(Founding(ui), "MaterialSource"), 1);
            Require(!Find<Button>(Founding(ui), "ConfirmFounding").Disabled, "人口规则缺失且已选列车时不能确认。");
            await _input.Activate(Find<Button>(Founding(ui), "Close"));
            await CloseDetail(panels);
            await OpenDetail(ui, 4);
            var settlement = LatestDetail(panels);
            Require(settlement.FindChildren("Economy", "Button", true, false).Any() &&
                !settlement.FindChildren("FoundSettlement", "Button", true, false).Any(), "非自有聚居地提供了建城。");
            await CloseDetail(panels);
            await OpenDetail(ui, 3);
            var lair = LatestDetail(panels);
            Require(!lair.FindChildren("FoundSettlement", "Button", true, false).Any() &&
                !lair.FindChildren("Economy", "Button", true, false).Any(), "生物聚落提供了建城或聚居地服务。");
            await CloseDetail(panels);
            await OpenFounding(ui, 5);
            var trains = Find<OptionButton>(Founding(ui), "MaterialSource");
            Require(trains.ItemCount == 1 && trains.GetItemText(0) == "该节点没有停靠的列车" &&
                Find<Button>(Founding(ui), "ConfirmFounding").Disabled, "没有停靠列车时下拉框为空白或仍可确认。");
            await _input.Activate(Find<Button>(Founding(ui), "Close"));
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
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher);
            host.BeginNewGame(new("core:basic-through-world"), 1, "stage1-world-v1", host.SessionOptions!);
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher);
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
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Nodes"));
            var list = ui.GetNode<ItemList>("Root/World/WorldPanels/NodesList/Layout/Content/Body/Objects");
            list.Select(index);
            list.EnsureCurrentIsVisible();
            await _input.Frames(2);
            await _input.ClickItem(list, index);
            await _input.Activate(Find<Button>(ui.GetNode<WorldPanels>("Root/World/WorldPanels"), "FoundSettlement"));
            var text = Find<Label>(Founding(ui), "FoundingPreview").Text;
            Require(text.Contains($"初始人口：{shown.InitialResidents}"), "建城面板的初始人口不是核心预览。");
            foreach (var rate in shown.ExpectedConsumption)
                Require(text.Contains($"{WorldPanels.GoodName(rate.Good, content)} {rate.UnitsPerSecond.ToString("0.###", CultureInfo.InvariantCulture)}/秒"),
                    "建城面板的生活物资用量不是核心预览。");
            foreach (var material in shown.RequiredMaterials)
                Require(text.Contains($"{WorldPanels.GoodName(material.Good, content)} × {material.Quantity}"),
                    "建城面板的材料不是核心预览。");
            Require(text.Contains($"固定工期 {shown.DurationTicks} 步"), "建城面板的工期不是核心预览。");
        }
        finally
        {
            await fixture.Cleanup(); TestScene.GetTree().Root.ContentScaleFactor = scale;
            Directory.Delete(directory, true);
        }
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

    private async Task OpenFounding(GameUi ui, int nodeIndex)
    {
        await OpenDetail(ui, nodeIndex);
        await _input.Activate(Find<Button>(LatestDetail(ui.GetNode<WorldPanels>("Root/World/WorldPanels")), "FoundSettlement"));
    }

    private async Task CloseDetail(WorldPanels panels)
    {
        await _input.Activate(Find<Button>(LatestDetail(panels), "Close"));
        await _input.Frames(2);
    }

    private async Task OpenDetail(GameUi ui, int nodeIndex)
    {
        await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Nodes"));
        await _input.ClickItem(ui.GetNode<ItemList>("Root/World/WorldPanels/NodesList/Layout/Content/Body/Objects"), nodeIndex);
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

    private static SettlementFoundingPanel Founding(GameUi ui) => ui.GetNode<SettlementFoundingPanel>("Root/World/SettlementFounding");

    private static PanelContainer LatestDetail(WorldPanels panels) =>
        panels.FindChildren("Details", "PanelContainer", true, false).Cast<PanelContainer>().Last();

    private static FixedCoreReadView World(ulong revision, JobId? foundingJob)
    {
        var terrain = new TerrainView(new("fixture:plain"), 0, []);
        NodeMapItem Node(ulong id, double x, double y, NodeOccupant occupant) =>
            new(new(id), new(x, y), terrain, occupant, [], 0, 1, id == 1 ? foundingJob : null);
        var nodes = new[]
        {
            Node(1, 500, 900, new NodeOccupant.Empty()),
            Node(2, 900, 900, new NodeOccupant.Empty()),
            Node(3, 1300, 900, new NodeOccupant.Empty()),
            Node(4, 500, 1400, new NodeOccupant.Lair(new(1))),
            Node(5, 900, 1400, new NodeOccupant.Settlement(new(1), SettlementRelation.NonHostile)),
            Node(6, 1700, 900, new NodeOccupant.Empty())
        };
        TrainView Train(ulong id, ulong node) => new(new(id),
            new TrainPosition.Node(new(node), null, null, null, null, NodeTravelPhase.Waiting, 80, null),
            TrainActivity.Stopped, [new(id)], new(id), 0, 20, 10, 100, 0, 30, null, null, 0,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, .2), [], null, [],
            TrainOrderFacing.FirstEndLeading, null);
        var trains = new[] { Train(7, 1), Train(8, 2), Train(9, 3) };
        CoreResult<SettlementFoundingPreview> Success(ulong node, ImmutableArray<CoreProblem> blockers) =>
            new CoreResult<SettlementFoundingPreview>.Success(new(new(node), 40, [new(EconomyFixture.Food, 1.5)],
                [new(EconomyFixture.Food, 12)], 180, blockers));
        return new(new(0, revision), HostCoreScenario.Input, revision: new(revision))
        {
            World = new(1, new("fixture:railway"), 4000, 2400, nodes.ToImmutableArray(), [],
                trains.Select(train => new TrainMapItem(train.Id, train.Position, train.Activity)).ToImmutableArray(), [], []),
            Nodes = nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(new(node, [], 80, 0, 80))),
            Locations = nodes.ToDictionary(node => (WorldLocation)new WorldLocation.Node(node.Id), node =>
                (Lookup<LocationView>)new Lookup<LocationView>.Found(new(new WorldLocation.Node(node.Id), [], [], [], [], node.Occupant))),
            Trains = trains.ToDictionary(train => train.Id, train => (Lookup<TrainView>)new Lookup<TrainView>.Found(train)),
            FoundingPreviews = new Dictionary<NodeId, CoreResult<SettlementFoundingPreview>>
            {
                [new(1)] = Success(1, []),
                [new(2)] = new CoreResult<SettlementFoundingPreview>.Failure([new("content_missing", "Population.Rule", null, [])]),
                [new(3)] = Success(3, [new("node_occupied", "Node", new WorldObjectId.Node(new(3)), [])]),
                [new(6)] = Success(6, [])
            },
            Jobs = foundingJob is { } job ? new Dictionary<JobId, Lookup<JobView>>
            {
                [job] = new Lookup<JobView>.Found(new JobView.Founding(new(job, new(1), 180, 0, new(91),
                    new("fixture:template"), [new(EconomyFixture.Food, 12)])))
            } : new Dictionary<JobId, Lookup<JobView>>()
        };
    }
}
