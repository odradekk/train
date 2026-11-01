using System.Reflection;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class TrainPlanWorkspaceTests
{
    private async Task WithWorkspace(List<CoreCommand> submitted,
        Func<TrainPlanWorkspace, HostBinding, Control, ICoreReadView, IContentView, Task> run,
        ICoreReadView? view = null, Func<CoreCommand, Action<string>, CommandDecision.Accepted?>? decide = null, bool shell = false)
    {
        var fixture = new Fixture(TestScene.GetTree());
        view ??= World();
        var content = DispatchProxy.Create<IContentView, ContentFixture>();
        var setup = new HostSimulationTests.HostSetup(view, content);
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await FramesUntil(TestScene, () => app.Host!.GetStatus() is not null);
            var parent = fixture.AutoFree(new Control { Size = TestScene.GetViewport().GetVisibleRect().Size });
            var panel = shell ? Find<TrainPlanWorkspace>(app, "TrainPlanWorkspace") : new TrainPlanWorkspace();
            if (!shell) panel.Initialize(app.Host!, (_, command, error) =>
            {
                submitted.Add(command);
                return decide is null ? new([], []) : decide(command, error);
            });
            if (!shell)
            {
                parent.AddChild(panel); app.GetNode<GameUi>("GameUi").AddChild(parent);
                await new UiTestInput(TestScene).Frames(2);
            }
            await run(panel, app.Host!.GetStatus()!.Binding, parent, view, content);
        }
        finally { await fixture.Cleanup(); }
    }

    private static TrainView Train(ICoreReadView view, ulong id) => Value(view.FindTrain(new(id)));
    private static VBoxContainer Stops(TrainPlanWorkspace panel) => Find<VBoxContainer>(panel, "Stops");
    private static VBoxContainer Card(TrainPlanWorkspace panel, int index) => Stops(panel).GetChild<VBoxContainer>(index);
    private static VBoxContainer Rules(TrainPlanWorkspace panel) => Find<VBoxContainer>(panel, "Rules");
    private static VBoxContainer Rule(TrainPlanWorkspace panel, int index) => Rules(panel).GetChild<VBoxContainer>(index);
    private static int[] ActionIds(Node rule)
    {
        var action = Choice(rule, "Action");
        return Enumerable.Range(0, action.ItemCount).Where(index => !action.IsItemDisabled(index)).Select(action.GetItemId).ToArray();
    }
    private static void Open(TrainPlanWorkspace panel, HostBinding binding, TrainView train, ICoreReadView view, IContentView content) =>
        panel.Open(new(binding, content.Generation, new WorldObjectId.Train(train.Id)), train, view, content);

    private Task Click(Node root, string name) => new UiTestInput(TestScene).Activate(Find<BaseButton>(root, name));
    private static LineEdit Field(Node root, string name) => Find<LineEdit>(root, name);
    private static string Text(Node root, string name) => Find<Label>(root, name).Text;
    private static OptionButton Choice(Node root, string name) => Find<OptionButton>(root, name);
    private static Button Button(Node root, string name) => Find<Button>(root, name);

    private async Task EnterText(LineEdit edit, string value)
    {
        edit.GrabFocus();
        var input = new UiTestInput(TestScene);
        await input.PressKey(Key.A, true);
        foreach (var letter in value) await input.PressKey(Key.None, unicode: letter);
    }

    private async Task MapClick(TrainPlanWorkspace panel, ICoreReadView view, ulong id, bool shift = false)
    {
        var input = new UiTestInput(TestScene);
        await input.Frames(3);
        var map = Find<TrainPlanMap>(panel, "RouteMap");
        var count = Stops(panel).GetChildCount();
        var local = map.MapToScreen(view.World.Nodes.Single(node => node.Id.Value == id).Position);
        var position = TestScene.GetViewport().GetScreenTransform() * (map.GlobalPosition + local);
        foreach (var pressed in new[] { true, false })
        {
            using var mouse = new InputEventMouseButton { Position = position, GlobalPosition = position,
                ButtonIndex = MouseButton.Left, Pressed = pressed, ShiftPressed = shift,
                ButtonMask = pressed ? MouseButtonMask.Left : 0 };
            Input.ParseInputEvent(mouse);
            await input.Frames();
        }
        Require(Stops(panel).GetChildCount() == count + 1,
            $"地图点击未添加地点：map={map.GetGlobalRect()}，point={local}，hover={TestScene.GetViewport().GuiGetHoveredControl()?.GetPath()}。");
    }

    private static T Value<T>(Lookup<T> found) where T : notnull => ((Lookup<T>.Found)found).Value;

    private class ContentFixture : DispatchProxy
    {
        public ContentFixture() { }
        private readonly IContentView inner = EconomyFixture.Content(mapPresentation: true);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_Core") return inner.Core with {
                Goods = [.. inner.Core.Goods, new(EconomyFixture.Food, new("food-v1"), new(1), 1, true, null)],
                DesignGoods = [new(EconomyFixture.Equipment, new("equipment-v1"), new(1), 1, false, null)] };
            if (method.Name == nameof(IContentView.FindSettlementLayout) && args![0] is SettlementId { Value: 4 })
                return new ContentLookup<SettlementLayoutProjection>.NotFound();
            if (method.Name == nameof(IContentView.FindDefinitionDisplay) &&
                args![0] is CoreDefinitionReference.Terrain) return new ContentLookup<ContentDisplay>.NotFound();
            if (method.Name == nameof(IContentView.FindGood) && Equals(args![0], EconomyFixture.Equipment))
                return new ContentLookup<GoodPresentation>.Found(new(EconomyFixture.Equipment, new(new("装备", ""), null, null, false), null, false));
            return method.Invoke(inner, args);
        }
    }

    private static FixedCoreReadView World(ulong revision = 1, bool expired = false, bool running = false)
    {
        var original = EconomyFixture.Create(revision, vehicle: EconomyFixture.VehicleState.Formed);
        var terrain = original.World.Nodes[0].Terrain;
        NodeMapItem[] nodes = [
            new(new(2), new(500, 150), terrain, new NodeOccupant.Settlement(new(2), SettlementRelation.PlayerOwned), [], 0, 1, null),
            original.World.Nodes[0],
            new(new(3), new(750, 400), terrain, new NodeOccupant.Empty(), [], 0, 1, null),
            new(new(4), new(750, 150), terrain, new NodeOccupant.Settlement(new(4), SettlementRelation.Hostile), [], 0, 1, null),
            new(new(5), new(450, 500), terrain, new NodeOccupant.Lair(new(5)), [], 0, 1, null)];
        EdgeMapItem[] edges = [new(new(1), new(1), new(2), 300, terrain, 1, 1, [], []),
            new(new(2), new(2), new(3), 400, terrain, 1, 1, [], []),
            new(new(3), new(2), new(4), 250, terrain, 1, 1, [], []),
            new(new(4), new(2), new(5), 350, terrain, 1, 1, [], [])];
        var train = Train(original, 7);
        if (running) train = train with { Activity = TrainActivity.Running, Position = new TrainPosition.Edge(new(1), 1, TravelDirection.Forward, 30) };
        var settlement = Value(original.FindSettlement(new(1)));
        var warehouse = Value(original.FindInventory(new(11)));
        var own = Value(original.FindInventory(new(10))) with
        { Id = new(20), Owner = new InventoryOwner.Settlement(new(2)), Location = new WorldLocation.Node(new(2)) };
        return new(original.Position, original.InputIdentity, revision: original.Revision)
        {
            World = original.World with { Nodes = [.. nodes], Edges = [.. edges],
                Trains = [new(train.Id, train.Position, train.Activity), original.World.Trains[1]] },
            Economy = original.Economy,
            Nodes = nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(
                new(node, [.. edges.Where(edge => edge.Start == node.Id || edge.End == node.Id).Select(edge => edge.Id)], 80, 0, 80)))
                .Append(new(new(999), new Lookup<NodeView>.Missing())).ToDictionary(pair => pair.Key, pair => pair.Value),
            Edges = edges.ToDictionary(edge => edge.Id, edge => (Lookup<EdgeView>)new Lookup<EdgeView>.Found(new(edge, [new(1, null, [])], null))),
            Locations = nodes.ToDictionary(node => (WorldLocation)new WorldLocation.Node(node.Id), node => (Lookup<LocationView>)new Lookup<LocationView>.Found(
                new(new WorldLocation.Node(node.Id), node.Id.Value == 1 ? [new(7), new(8)] : [], [], [], [], node.Occupant))),
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>> {
                [new InstanceId.Vehicle(new(9))] = original.FindInstance(new InstanceId.Vehicle(new(9))),
                [new InstanceId.Vehicle(new(10))] = original.FindInstance(new InstanceId.Vehicle(new(10))) },
            Lairs = new Dictionary<LairId, Lookup<LairView>> { [new(5)] = new Lookup<LairView>.Found(new(new(5), new(5), new("fixture:lair"), [])) },
            Trains = new Dictionary<TrainId, Lookup<TrainView>> { [new(7)] = new Lookup<TrainView>.Found(train), [new(8)] = original.FindTrain(new(8)) },
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>> { [new(1)] = new Lookup<SettlementView>.Found(settlement),
                [new(2)] = new Lookup<SettlementView>.Found(settlement with { Id = new(2), Node = new(2), Relation = SettlementRelation.PlayerOwned, Inventory = new(20), Leases = [] }),
                [new(4)] = new Lookup<SettlementView>.Found(settlement with { Id = new(4), Node = new(4), Relation = SettlementRelation.Hostile, Inventory = new(40), Leases = [] }) },
            Inventories = new Dictionary<InventoryId, Lookup<InventoryView>> { [new(10)] = original.FindInventory(new(10)),
                [new(11)] = new Lookup<InventoryView>.Found(warehouse with { Access = expired ? InventoryAccess.WithdrawOnly : InventoryAccess.ReadWrite }),
                [new(12)] = original.FindInventory(new(12)), [new(13)] = original.FindInventory(new(13)), [new(20)] = new Lookup<InventoryView>.Found(own) },
            Leases = new Dictionary<LeaseId, Lookup<LeaseView>> { [new(1)] = new Lookup<LeaseView>.Found(
                Value(original.FindLease(new(1))) with { Expired = expired, ExpiresAtStep = expired ? 0UL : EconomyFixture.LeaseDuration }) }
        };
    }
}
