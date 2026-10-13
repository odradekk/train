using System.Collections.Immutable;
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
        var initial = View(0, false);
        var formed = View(1, true);
        var plan = new RunPlan(true, [new PlanStop.Waypoint(new(2)), new PlanStop.Station(new(1), 120, [])]);
        var rejected = View(2, true);
        var applied = View(3, true, plan);
        var content = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
        {
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
            await Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Nodes"));
            var nodes = ui.GetNode<ItemList>("Root/World/WorldPanels/NodesList/Layout/Body/Objects");
            await _input.ClickItem(nodes, 0);
            var panels = ui.GetNode<WorldPanels>("Root/World/WorldPanels");
            await Activate(Find<Button>(panels, "Form"));
            var formation = ui.GetNode<TrainFormationPanel>("Root/World/TrainFormationPanel");
            var available = Find<ItemList>(formation, "AvailableVehicles");
            Require(available.ItemCount == 2, "有限车辆情景未在编组列表显示。");
            await _input.ClickItem(available, 0);
            await _input.ClickItem(available, 1, true);
            Require(available.GetSelectedItems().SequenceEqual(new[] { 0, 1 }), "真实 Ctrl 点击未形成多选。");
            await Activate(Find<Button>(formation, "AddSelected"));
            var ordered = Find<ItemList>(formation, "OrderedVehicles");
            await _input.ClickItem(ordered, 1);
            Require(ordered.GetSelectedItems().SequenceEqual(new[] { 1 }),
                $"编组顺序列表未选中第二行：rect={ordered.GetGlobalRect()}，item={ordered.GetItemRect(1)}，selected={string.Join(',', ordered.GetSelectedItems())}，hover={tree.Root.GuiGetHoveredControl()?.GetPath()}。");
            await Activate(Find<Button>(formation, "MoveUp"));
            await UiCapture.Capture(TestScene, "formation");
            await Activate(Find<Button>(formation, "Form"));
            Require(setup.Session.SubmitCount == 1 && !formation.Visible &&
                ReferenceEquals(ui.View, formed), "编组接受后没有发布新列车视图并关闭表单。");

            await Activate(Find<Button>(panels, "EditPlan"));
            var editor = ui.GetNode<TrainPlanPanel>("Root/World/TrainPlanPanel");
            var rows = editor.GetNode<VBoxContainer>("Plan/Layout/Body/RowsScroll/Rows");
            var station = rows.GetNode<HBoxContainer>("Row0");
            await EnterSeconds(station.GetNode<LineEdit>("DwellSeconds"), '2', Key.Key2);
            await Activate(Find<Button>(editor, "AddStop"));
            var waypoint = rows.GetNode<HBoxContainer>("Row1");
            await SelectSecond(waypoint.GetNode<OptionButton>("Node"));
            await SelectSecond(waypoint.GetNode<OptionButton>("Kind"));
            Require(waypoint.GetNode<OptionButton>("Node").Selected == 1 &&
                waypoint.GetNode<OptionButton>("Kind").Selected == 1, "实际下拉输入没有选择节点和途经点。");
            await Activate(waypoint.GetNode<Button>("MoveUp"));
            await Activate(Find<Button>(editor, "AddStop"));
            await Activate(rows.GetNode<Button>("Row2/Remove"));
            await Activate(editor.GetNode<CheckBox>("Plan/Layout/Body/Loop"));
            Require(rows.GetChildCount() == 2 && rows.GetChild(0) == waypoint &&
                station.GetNode<LineEdit>("DwellSeconds").Text == "2", "计划行增删重排或秒输入未保留。");
            var apply = Find<Button>(editor, "Apply");
            await Activate(apply);
            Require(setup.Session.SubmitCount == 2 && editor.Visible && ui.ErrorText.Contains("no_route/train.plan") &&
                rows.GetChild(0) == waypoint && station.GetNode<LineEdit>("DwellSeconds").Text == "2",
                "计划拒绝没有显示原因或保留原草稿顺序和输入。");
            await UiCapture.Capture(TestScene, "plan-rejected");
            await Activate(apply);
            await _input.WaitUntil(() => ReferenceEquals(ui.View, applied));
            Require(setup.Session.SubmitCount == 3 && !editor.Visible,
                "再次应用没有提交保留的计划或更新列车视图。");
            await UiCapture.Capture(TestScene, "train-details");
            await Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Nodes"));
            await _input.ClickItem(nodes, 0);
            await Activate(Find<Button>(panels, "Form"));
            Require(formation.IsVisibleInTree() && available.ItemCount == 0 &&
                Find<Button>(formation, "Form").Disabled && Find<Label>(formation, "Reason").Text.Length > 0 &&
                setup.Session.SubmitCount == 3,
                "已编组车辆仍被列为候选，或空候选表单没有说明原因并禁用提交。");
            await Activate(Find<Button>(formation, "Cancel"));
            Require(!formation.Visible && setup.Session.SubmitCount == 3, "取消空候选编组提交了命令或未关闭表单。");
            setup.Session.AssertComplete();
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

    private static SubmissionResult Completed(ulong number, ICoreReadView before, ICoreReadView after, CommandDecision decision)
        => new SubmissionResult.Completed(new(number, before.Position, after.Position, decision), [], null);

    private static FixedCoreReadView View(ulong sequence, bool formed, RunPlan? plan = null)
    {
        var terrain = new TerrainView(new("fixture:plain"), 0, []);
        var first = new NodeMapItem(new(1), new(250, 300), terrain, new NodeOccupant.Empty(), [], 0, 1, null);
        var second = new NodeMapItem(new(2), new(750, 300), terrain, new NodeOccupant.Empty(), [], 0, 1, null);
        var position = new TrainPosition.Node(first.Id);
        var train = new TrainView(new(7), position, TrainActivity.Stopped, [new(2), new(1)], new(1),
            0, 20, 10, 100, 0, 30, plan, null, 0,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, .2), [], null, [], null,
            TrainOrderFacing.FirstEndLeading);
        InstanceId.Vehicle[] vehicles = [new(new(1)), new(new(2))];
        var instances = vehicles.ToDictionary(id => (InstanceId)id, id => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(
            new(id, new("vehicle-design"), new("vehicle-composition"), new WorldLocation.Node(first.Id),
                SettlementRelation.PlayerOwned, new InstanceDetails.Vehicle(SizeClass.Medium, formed ? train.Id : null),
                InstanceLock.None, new(100, 100), [], [])));
        var location = new LocationView(new WorldLocation.Node(first.Id), formed ? [train.Id] : [],
            vehicles.Cast<InstanceId>().ToImmutableArray(), [], [], new NodeOccupant.Empty());
        return new(new(0, sequence), HostCoreScenario.Input)
        {
            World = new(1, new("fixture:railway"), 1000, 600, [first, second], [],
                formed ? [new(train.Id, position, train.Activity)] : [], [], []),
            Nodes = new Dictionary<NodeId, Lookup<NodeView>>
            { [first.Id] = new Lookup<NodeView>.Found(new(first, [])), [second.Id] = new Lookup<NodeView>.Found(new(second, [])) },
            Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
            { [location.Location] = new Lookup<LocationView>.Found(location) },
            Instances = instances,
            Trains = formed ? new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = new Lookup<TrainView>.Found(train) }
                : new Dictionary<TrainId, Lookup<TrainView>>()
        };
    }

    private static T Find<T>(Node root, string name) where T : Node
        => root.FindChildren(name, typeof(T).Name, true, false).Cast<T>().Single();

    private async Task Activate(BaseButton button) { button.GrabFocus(); await _input.PressKey(Key.Space); }

    private async Task SelectSecond(OptionButton button)
    {
        button.GrabFocus(); await _input.PressKey(Key.Space); await _input.PressKey(Key.Down); await _input.PressKey(Key.Enter);
    }

    private async Task EnterSeconds(LineEdit edit, char character, Key key)
    {
        edit.GrabFocus(); await _input.Frames(1); await _input.PressKey(Key.A, true);
        await _input.PressKey(key, unicode: character);
    }

}
