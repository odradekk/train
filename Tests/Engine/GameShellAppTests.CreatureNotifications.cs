using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Notifications;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class CreatureNotificationTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Existing_group_toast_selects_its_current_node_or_edge()
    {
        await Run(true, async (app, ui, shell) =>
        {
            var notices = Find<NotificationPanel>(ui, "NotificationPanel");
            notices.Add(new(NotificationCategory.RetreatStarted, NotificationLevel.Warning, "开始撤退。",
                new WorldLocation.Edge(new(1), 100), new WorldObjectId.CreatureGroup(new(1))),
                app.Host!.GetStatus()!.View, app.Host.Content!);
            await _input.Frames(2);
            await _input.Activate(Find<Button>(Find<Control>(ui, "Toasts"), "Notice"));
            Require(ui.ErrorText.Length == 0, $"仍存在的生物群通知不应报错：{ui.ErrorText}");
            Require(shell.SelectedObject == new WorldObjectId.Node(new(1)) &&
                shell.InspectorContent is { } nodeInspector && nodeInspector.IsVisibleInTree() &&
                Find<Label>(nodeInspector, "Description").Text.Contains("节点资源"),
                "提示条必须打开生物群当前节点的检查器，不能选中通知里记录的旧线路。");

            notices.Clear();
            notices.Add(new(NotificationCategory.RetreatStarted, NotificationLevel.Warning, "开始撤退。",
                new WorldLocation.Node(new(1)), new WorldObjectId.CreatureGroup(new(2))),
                app.Host.GetStatus()!.View, app.Host.Content!);
            await _input.Frames(2);
            await _input.Activate(Find<Button>(Find<Control>(ui, "Toasts"), "Notice"));
            Require(ui.ErrorText.Length == 0 && shell.SelectedObject == new WorldObjectId.Edge(new(1)) &&
                shell.InspectorContent is { } edgeInspector && edgeInspector.IsVisibleInTree() &&
                Find<Label>(edgeInspector, "Description").Text.Contains("线路长度"),
                "提示条必须打开生物群当前线路的检查器，不能保留通知里记录的旧节点。");
            Require(!app.WorldScenes!.View.Visible, "点击生物群通知不能隐式进入局部场景。");
        });
    }

    [Test]
    public async Task Missing_group_log_row_reports_missing_and_keeps_the_selection()
    {
        await Run(false, async (app, ui, shell) =>
        {
            var notices = Find<NotificationPanel>(ui, "NotificationPanel");
            notices.Add(new(NotificationCategory.Construction, NotificationLevel.Information, "地点通知。",
                new WorldLocation.Node(new(1)), null), app.Host!.GetStatus()!.View, app.Host.Content!);
            await _input.Frames(2);
            await _input.Activate(Find<Button>(Find<Control>(ui, "Toasts"), "Notice"));
            Require(shell.SelectedObject == new WorldObjectId.Node(new(1)) && shell.InspectorContent is not null,
                "点击失效通知前应先选中一个仍存在的节点。");
            var inspector = shell.InspectorContent;
            notices.Clear();
            notices.Add(new(NotificationCategory.RetreatStarted, NotificationLevel.Warning, "开始撤退。",
                new WorldLocation.Edge(new(1), 100), new WorldObjectId.CreatureGroup(new(1))),
                app.Host.GetStatus()!.View, app.Host.Content!);
            await _input.Activate(Find<Button>(ui, "Notices"));
            await _input.Activate(Find<Button>(Find<Control>(notices, "NoticeLog"), "Notice"));
            Require(ui.ErrorText == "已不存在", "生物群已消失时，通知记录必须提示已不存在。");
            Require(shell.SelectedObject == new WorldObjectId.Node(new(1)) && shell.InspectorContent == inspector &&
                inspector!.IsVisibleInTree(), "失效生物群通知必须保留原检查器，不能回退到通知中的旧地点。");
            Require(!app.WorldScenes!.View.Visible, "失效通知不能进入局部场景。");
        });
    }

    private async Task Run(bool groupsPresent, Func<Train.App.App, GameUi, GameShell, Task> check)
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        var setup = new HostSimulationTests.HostSetup(View(groupsPresent), EconomyFixture.Content(mapPresentation: true));
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            await _input.Frames(3);
            var ui = app.GetNode<GameUi>("GameUi");
            await check(app, ui, ui.GetNode<GameShell>("Root/World"));
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

    private static FixedCoreReadView View(bool groupsPresent)
    {
        var basis = UiWorldFixture.Map();
        var first = basis.World.Nodes[0];
        var second = basis.World.Nodes[1];
        var edge = new EdgeMapItem(new(1), first.Id, second.Id, 500, first.Terrain, 1, 1, [], []);
        var atNode = new CreatureGroupView(new(1), null, new WorldLocation.Node(first.Id), null,
            CreatureActivity.Retreating, 1, []);
        var atEdge = atNode with { Id = new(2), Location = new WorldLocation.Edge(edge.Id, 250), Direction = TravelDirection.Forward };
        return new(default, HostCoreScenario.Input, revision: new(1))
        {
            Economy = UiWorldFixture.Economy,
            World = basis.World with
            {
                Nodes = [first, second], Edges = [edge], Trains = [],
                CreatureGroups = groupsPresent ? [new(atNode.Id, atNode.Location, atNode.Activity),
                    new(atEdge.Id, atEdge.Location, atEdge.Activity)] : []
            },
            Nodes = new Dictionary<NodeId, Lookup<NodeView>>
            {
                [first.Id] = new Lookup<NodeView>.Found(new(first, [edge.Id], 80, 0, 80))
            },
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>>
            {
                [edge.Id] = new Lookup<EdgeView>.Found(new(edge, [new(1, null, [])], null))
            },
            Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
            {
                [atNode.Location] = new Lookup<LocationView>.Found(new(atNode.Location, [], [],
                    groupsPresent ? [atNode.Id] : [], [], first.Occupant))
            },
            CreatureGroups = new Dictionary<CreatureGroupId, Lookup<CreatureGroupView>>
            {
                [atNode.Id] = groupsPresent ? new Lookup<CreatureGroupView>.Found(atNode) : new Lookup<CreatureGroupView>.Missing(),
                [atEdge.Id] = groupsPresent ? new Lookup<CreatureGroupView>.Found(atEdge) : new Lookup<CreatureGroupView>.Missing()
            }
        };
    }
}
