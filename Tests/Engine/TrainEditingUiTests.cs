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
            await FramesUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            await Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            await Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Nodes"));
            var nodes = ui.GetNode<ItemList>("Root/World/WorldPanels/节点List/Layout/Body/Objects");
            await ClickItem(nodes, 0);
            var panels = ui.GetNode<WorldPanels>("Root/World/WorldPanels");
            await Activate(Find<Button>(panels, "Form"));
            var formation = ui.GetNode<TrainFormationPanel>("Root/World/TrainFormationPanel");
            var available = Find<ItemList>(formation, "AvailableVehicles");
            Require(available.ItemCount == 2, "有限车辆情景未在编组列表显示。");
            await ClickItem(available, 0);
            await ClickItem(available, 1, true);
            Require(available.GetSelectedItems().SequenceEqual(new[] { 0, 1 }), "真实 Ctrl 点击未形成多选。");
            await Activate(Find<Button>(formation, "AddSelected"));
            var ordered = Find<ItemList>(formation, "OrderedVehicles");
            await ClickItem(ordered, 1);
            Require(ordered.GetSelectedItems().SequenceEqual(new[] { 1 }),
                $"编组顺序列表未选中第二行：rect={ordered.GetGlobalRect()}，item={ordered.GetItemRect(1)}，selected={string.Join(',', ordered.GetSelectedItems())}，hover={tree.Root.GuiGetHoveredControl()?.GetPath()}。");
            await Activate(Find<Button>(formation, "MoveUp"));
            Require(ordered.GetItemText(0).Contains("车辆 2") && ordered.GetItemText(1).Contains("车辆 1"),
                "真实上移按钮没有调整编组顺序。");
            await UiCapture.Capture(TestScene, "formation");
            await Activate(Find<Button>(formation, "Form"));
            Require(setup.Session.SubmitCount == 1 && !formation.Visible &&
                panels.FindChildren("Description", "Label", true, false).Cast<Label>()
                    .Any(label => label.IsVisibleInTree() && label.Text.Contains("车辆 2 → 车辆 1")),
                "编组接受后没有关闭表单并打开新列车详情。");

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
            await FramesUntil(() => panels.FindChildren("Description", "Label", true, false).Cast<Label>()
                .Any(label => label.Text.Contains("循环") && label.Text.Contains("节点 2 · 途经") && label.Text.Contains("停靠 2 秒")));
            Require(setup.Session.SubmitCount == 3 && !editor.Visible &&
                panels.FindChildren("Description", "Label", true, false).Cast<Label>()
                    .Any(label => label.Text.Contains("循环") && label.Text.Contains("节点 2 · 途经") && label.Text.Contains("停靠 2 秒")),
                "再次应用没有提交保留的计划或更新列车详情。");
            await UiCapture.Capture(TestScene, "train-details");
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

    private async Task Activate(BaseButton button) { button.GrabFocus(); await PressKey(Key.Space); }

    private async Task SelectSecond(OptionButton button)
    {
        button.GrabFocus(); await PressKey(Key.Space); await PressKey(Key.Down); await PressKey(Key.Enter);
    }

    private async Task EnterSeconds(LineEdit edit, char character, Key key)
    {
        edit.GrabFocus(); await Frames(1); await PressKey(Key.A, true);
        await PressKey(key, unicode: character);
    }

    private async Task ClickItem(ItemList list, int index, bool control = false)
    {
        // ItemList 的行矩形由绘制更新；列表刚建立或填充时不能立即拿缓存坐标点击。
        await FramesUntil(() => list.IsVisibleInTree() && list.Size.X > 0 && list.Size.Y > 0 &&
            list.GetItemRect(index).Size.Y > 0 &&
            list.GetItemAtPosition(list.GetItemRect(index).GetCenter(), true) == index);
        var position = list.GlobalPosition + list.GetItemRect(index).GetCenter();
        position = TestScene.GetViewport().GetScreenTransform() * position;
        using var press = new InputEventMouseButton { Position = position, GlobalPosition = position,
            ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left, CtrlPressed = control };
        Input.ParseInputEvent(press); await Frames(1);
        using var release = new InputEventMouseButton { Position = position, GlobalPosition = position,
            ButtonIndex = MouseButton.Left, Pressed = false, CtrlPressed = control };
        Input.ParseInputEvent(release); await Frames(1);
    }

    private async Task PressKey(Key key, bool control = false, uint unicode = 0)
    {
        using var press = new InputEventKey { Keycode = key, Pressed = true, CtrlPressed = control, Unicode = unicode };
        Input.ParseInputEvent(press); await Frames(1);
        using var release = new InputEventKey { Keycode = key, Pressed = false, CtrlPressed = control, Unicode = unicode };
        Input.ParseInputEvent(release); await Frames(1);
    }

    private async Task Frames(int count)
    {
        for (var frame = 0; frame < count; frame++) await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task FramesUntil(Func<bool> completed)
    {
        for (var frame = 0; frame < 180 && !completed(); frame++) await Frames(1);
        Require(completed(), "真实帧没有发布编组测试会话。");
    }
}
