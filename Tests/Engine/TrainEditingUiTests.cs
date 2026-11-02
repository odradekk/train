using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class TrainEditingUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Formation_and_plan_real_input_preserve_order_and_rejected_draft()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        var initial = Formation(0, false);
        var formed = Formation(1, true);
        var plan = new RunPlan(true, [new PlanStop.Waypoint(new(2)), new PlanStop.Waypoint(new(1))]);
        var rejected = Formation(2, true);
        var applied = Formation(3, true, plan);
        var content = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
        {
            DefinitionDisplays = new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
            { [new CoreDefinitionReference.Terrain(new("fixture:plain"))] = new ContentLookup<ContentDisplay>.NotFound() },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [new("vehicle-design")] = new ContentLookup<DesignEvaluation>.NotFound() }
        };
        var setup = new HostSimulationTests.HostSetup(initial, content);
        setup.Session.QueueSubmit(new CoreCommand.FormTrain([new(2), new(1)]), CommandSource.UserInterface,
            Completed(1, initial, formed, new CommandDecision.Accepted([new WorldObjectId.Train(new(7))], [])), formed);
        setup.Session.QueueSubmit(new CoreCommand.ReplaceRunPlan(new(7), plan), CommandSource.UserInterface,
            Completed(2, formed, rejected, new CommandDecision.Rejected(
                [new("no_route", "train.plan", new WorldObjectId.Train(new(7)), [])])), rejected);
        setup.Session.QueueSubmit(new CoreCommand.ReplaceRunPlan(new(7), plan), CommandSource.UserInterface,
            Completed(3, rejected, applied, new CommandDecision.Accepted([], [])), applied);
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            await _input.Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.Activate(Find<Button>(ui, "Nodes"));
            var nodes = Find<ItemList>(Find<PanelContainer>(ui, "NodesList"), "Objects");
            await _input.ClickItem(nodes, 0);
            await _input.Activate(Find<Button>(((GameShell)ui.World).InspectorContent!, "Form"));
            var formation = Find<TrainFormationPanel>(ui, "TrainFormationPanel");
            var available = Find<ItemList>(formation, "AvailableVehicles");
            Require(available.ItemCount == 2, "有限车辆情景未在编组列表显示。");
            await _input.ClickItem(available, 0);
            await _input.ClickItem(available, 1, true);
            Require(available.GetSelectedItems().SequenceEqual(new[] { 0, 1 }), "真实 Ctrl 点击未形成多选。");
            await _input.Activate(Find<Button>(formation, "AddSelected"));
            var ordered = Find<ItemList>(formation, "OrderedVehicles");
            await _input.ClickItem(ordered, 1);
            Require(ordered.GetSelectedItems().SequenceEqual(new[] { 1 }),
                $"编组顺序列表未选中第二行：rect={ordered.GetGlobalRect()}，item={ordered.GetItemRect(1)}，selected={string.Join(',', ordered.GetSelectedItems())}，hover={tree.Root.GuiGetHoveredControl()?.GetPath()}。");
            await _input.Activate(Find<Button>(formation, "MoveUp"));
            await UiCapture.Capture(TestScene, "formation");
            await _input.Activate(Find<Button>(formation, "Form"));
            Require(setup.Session.SubmitCount == 1 && !formation.Visible &&
                ReferenceEquals(ui.View, formed), "编组接受后没有发布新列车视图并关闭表单。");

            await _input.Activate(Find<Button>(((GameShell)ui.World).InspectorContent!, "EditPlan"));
            var editor = Find<TrainPlanWorkspace>(ui, "TrainPlanWorkspace");
            var rows = Find<VBoxContainer>(editor, "Stops");
            var first = rows.GetChild<Control>(0);
            var search = Find<LineEdit>(editor, "Search");
            search.GrabFocus();
            foreach (var character in "不存在的地点") await _input.PressKey(Key.None, unicode: character);
            Require(!Find<VBoxContainer>(editor, "SearchResults").GetChildren().OfType<Button>().Any(),
                "搜索结果包含不匹配的地点。");
            await _input.PressKey(Key.A, true);
            foreach (var character in "空节点") await _input.PressKey(Key.None, unicode: character);
            await _input.Activate(Find<Button>(Find<VBoxContainer>(editor, "SearchResults"), "Add2"));
            var waypoint = rows.GetChild<Control>(1);
            Require(Find<OptionButton>(Find<VBoxContainer>(editor, "StopEditor"), "Kind").Selected == 1,
                "通过搜索加入的空节点没有成为途经点。");
            await _input.Activate(Find<Button>(waypoint, "MoveUp"));
            search.GrabFocus();
            foreach (var character in "空节点") await _input.PressKey(Key.None, unicode: character);
            await _input.Activate(Find<Button>(Find<VBoxContainer>(editor, "SearchResults"), "Add1"));
            await _input.Activate(Find<Button>(rows.GetChild(2), "Remove"));
            await _input.Activate(Find<CheckBox>(editor, "Loop"));
            Require(rows.GetChildCount() == 2 && rows.GetChild(0) == waypoint && rows.GetChild(1) == first,
                "计划站序增删或重排未保留。");
            var apply = Find<Button>(editor, "SaveOnly");
            await _input.Activate(apply);
            var reason = Find<Label>(editor, "Reason").Text;
            Require(setup.Session.SubmitCount == 2 && editor.Visible && reason.Contains("没有可达路径。") &&
                !reason.Contains("no_route") && rows.GetChild(0) == waypoint && rows.GetChild(1) == first,
                "计划拒绝没有显示原因或保留原草稿顺序和输入。");
            await UiCapture.Capture(TestScene, "plan-rejected");
            await _input.Activate(apply);
            await _input.WaitUntil(() => ReferenceEquals(ui.View, applied));
            Require(setup.Session.SubmitCount == 3 && !editor.Visible,
                "再次应用没有提交保留的计划或更新列车视图。");
            await UiCapture.Capture(TestScene, "train-details");
            if (!nodes.IsVisibleInTree()) await _input.Activate(Find<Button>(ui, "Nodes"));
            await _input.ClickItem(nodes, 0);
            await _input.Activate(Find<Button>(((GameShell)ui.World).InspectorContent!, "Form"));
            Require(formation.IsVisibleInTree() && available.ItemCount == 0 &&
                Find<Button>(formation, "Form").Disabled && Find<Label>(formation, "Reason").Text.Length > 0 &&
                setup.Session.SubmitCount == 3,
                "已编组车辆仍被列为候选，或空候选表单没有说明原因并禁用提交。");
            await _input.Activate(Find<Button>(formation, "Cancel"));
            Require(!formation.Visible && setup.Session.SubmitCount == 3, "取消空候选编组提交了命令或未关闭表单。");
            setup.Session.AssertComplete();
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

    private static FixedCoreReadView Formation(ulong sequence, bool formed, RunPlan? plan = null)
    {
        var basis = UiWorldFixture.Formation(sequence, formed, plan);
        var nodes = basis.World.Nodes;
        var edge = new EdgeMapItem(new(1), nodes[0].Id, nodes[1].Id, 600, nodes[0].Terrain, 1, 1, [], []);
        return new(basis.Position, basis.InputIdentity, basis.Fault, basis.Revision)
        {
            Economy = basis.Economy,
            World = basis.World with { Edges = [edge] },
            Nodes = nodes.ToDictionary(node => node.Id, node =>
                (Lookup<NodeView>)new Lookup<NodeView>.Found(
                    ((Lookup<NodeView>.Found)basis.FindNode(node.Id)).Value with { AdjacentEdges = [edge.Id] })),
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>>
            { [edge.Id] = new Lookup<EdgeView>.Found(new(edge, [new(1, null, [])], null)) },
            Locations = nodes.ToDictionary(node => (WorldLocation)new WorldLocation.Node(node.Id),
                node => basis.FindLocation(new WorldLocation.Node(node.Id))),
            Instances = new InstanceId[] { new InstanceId.Vehicle(new(1)), new InstanceId.Vehicle(new(2)) }
                .ToDictionary(id => id, basis.FindInstance),
            Trains = formed ? new Dictionary<TrainId, Lookup<TrainView>> { [new(7)] = basis.FindTrain(new(7)) }
                : new Dictionary<TrainId, Lookup<TrainView>>()
        };
    }

}
