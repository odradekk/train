using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;
using NVector3 = System.Numerics.Vector3;

namespace Train.Engine.Tests;

public partial class SettlementSceneOpeningTests(Node testScene) : TestClass(testScene)
{
    private static readonly WorldObjectId Node = new WorldObjectId.Node(new(1));
    private static readonly WorldObjectId Settlement = new WorldObjectId.Settlement(new(1));
    private static readonly WorldObjectId Train = new WorldObjectId.Train(new(7));
    private static readonly WorldSceneTarget NodeTarget = new WorldSceneTarget.Location(new WorldLocation.Node(new(1)));
    private static readonly WorldSceneTarget TrainTarget = new WorldSceneTarget.Follow(new(7), new(10));
    private static readonly SceneSessionId SceneId = new(Guid.Parse("00000000-0000-0000-0000-000000000954"));

    [Test]
    public async Task Settlement_details_replace_the_waiting_message_after_the_ready_frame_is_drawn()
    {
        await Run([Frame(2, SceneReadiness.Loading), Frame(2, SceneReadiness.Partial, 2), Frame(2, SceneReadiness.Ready, 3)],
            (app, ui, shell) =>
            {
                ui.GetNode<WorldPanels>("Root/World/WorldPanels").OpenObject(Train);
                var previous = shell.InspectorContent!;
                OpenNode(ui);
                Waiting(shell);
                Require(!previous.IsVisibleInTree(), "等待聚居地详情时不能显示上一个对象。");
                app.WorldScenes!.ProcessFrame(0, false); ui._Process(0);
                Waiting(shell);
                app.WorldScenes.ProcessFrame(0, false); ui._Process(0);
                Draw(); ui._Process(0);
                Waiting(shell);
                app.WorldScenes.ProcessFrame(0, false); ui._Process(0);
                Waiting(shell);
                Draw(); ui._Process(0);
                Details(shell);
                return Task.CompletedTask;
            });
    }

    [Test]
    public async Task A_previous_requests_ready_picture_cannot_finish_the_new_settlement_inspector()
    {
        await Run([Frame(1, SceneReadiness.Ready), Frame(2, SceneReadiness.Ready)], (app, ui, shell) =>
        {
            ui.GetNode<MacroMap>("Root/World/MacroMap").RaiseObjectClicked(Train);
            Require(shell.SelectedObject == Train && shell.InspectorContent is not null,
                "打开列车场景仍须同步显示列车检查器。");
            app.WorldScenes!.Display!.Timestamp = () => 0;
            app.WorldScenes.ProcessFrame(0, false);
            Draw();
            OpenNode(ui);
            Draw(); ui._Process(0);
            Waiting(shell);
            app.WorldScenes.ProcessFrame(0, false);
            Draw(); ui._Process(0);
            Details(shell);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task Selecting_another_object_cancels_the_waiting_settlement_inspector()
    {
        await Run([Frame(2, SceneReadiness.Ready)], (app, ui, shell) =>
        {
            OpenNode(ui); Waiting(shell);
            ui.GetNode<WorldPanels>("Root/World/WorldPanels").OpenObject(Train);
            var trainDetails = shell.InspectorContent;
            app.WorldScenes!.ProcessFrame(0, false);
            Draw(); ui._Process(0);
            Require(shell.SelectedObject == Train && shell.InspectorContent == trainDetails && trainDetails!.IsVisibleInTree(),
                "新选择的列车检查器不能被迟到的聚居地检查器覆盖。");
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task Closing_the_waiting_inspector_prevents_it_from_reopening()
    {
        await Run([Frame(2, SceneReadiness.Ready)], async (app, ui, shell) =>
        {
            OpenNode(ui); Waiting(shell);
            await new UiTestInput(TestScene).Activate(Find<Button>(shell.InspectorContent!, "Close"));
            app.WorldScenes!.ProcessFrame(0, false);
            Draw(); ui._Process(0);
            Require(shell.SelectedObject is null && shell.InspectorContent is null,
                "已关闭的等待检查器不能在 Ready 后重新打开。");
        });
    }

    [Test]
    public async Task Returning_to_the_map_before_ready_shows_the_full_settlement_details()
    {
        await Run([Frame(2, SceneReadiness.Partial)], (app, ui, shell) =>
        {
            OpenNode(ui); app.WorldScenes!.ProcessFrame(0, false); Waiting(shell);
            app.WorldScenes.ReturnToMap();
            Details(shell);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task Regenerating_the_scene_while_waiting_shows_the_full_settlement_details()
    {
        await Run([Frame(1, SceneReadiness.Ready), Frame(2, SceneReadiness.Partial)], (app, ui, shell) =>
        {
            ui.GetNode<MacroMap>("Root/World/MacroMap").RaiseObjectClicked(Train);
            app.WorldScenes!.Display!.Timestamp = () => 0;
            app.WorldScenes.ProcessFrame(0, false);
            OpenNode(ui); app.WorldScenes!.ProcessFrame(0, false); Waiting(shell);
            Require(app.WorldScenes.Regenerate() is null && app.WorldScenes.View.Visible,
                "等待期间应能重新生成当前场景并保留场景视图。");
            ui._Process(0);
            Details(shell);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task A_scene_failure_replaces_the_waiting_message_with_settlement_details()
    {
        await Run([Frame(2, SceneReadiness.Partial), Failure()], (app, ui, shell) =>
        {
            OpenNode(ui); app.WorldScenes!.ProcessFrame(0, false); Waiting(shell);
            app.WorldScenes.ProcessFrame(0, false);
            Details(shell);
            Require(!app.WorldScenes.View.Visible && ui.ErrorText.Contains("fixture_scene_failed"),
                "场景失败应返回地图并保留失败原因。");
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task A_rejected_scene_open_still_shows_settlement_details_synchronously()
    {
        await Run([], (app, ui, shell) =>
        {
            OpenNode(ui); Details(shell);
            Require(!app.WorldScenes!.View.Visible, "打开失败应留在地图。");
            return Task.CompletedTask;
        }, rejectOpen: true);
    }

    [Test]
    public async Task Waiting_and_complete_details_fit_the_shell_at_both_ui_scales()
    {
        await Run([Frame(2, SceneReadiness.Ready)], async (app, ui, shell) =>
        {
            var input = new UiTestInput(TestScene);
            OpenNode(ui);
            await Check("waiting");
            app.WorldScenes!.ProcessFrame(0, false);
            Draw(); ui._Process(0);
            await Check("ready");

            async Task Check(string state)
            {
                foreach (var scale in new[] { 1f, 2f })
                {
                    TestScene.GetTree().Root.ContentScaleFactor = scale;
                    await input.Frames(4);
                    if (state == "waiting") Waiting(shell); else Details(shell);
                    ShellGeometry.Check(ui);
                    await UiCapture.Capture(TestScene, $"settlement-{state}-scale-{scale:0}", scale);
                }
            }
        });
    }

    private static void OpenNode(GameUi ui)
    {
        ui.GetNode<MacroMap>("Root/World/MacroMap").RaiseObjectClicked(Node);
        if (ui.GetParent<Train.App.App>().WorldScenes!.Display is { } display) display.Timestamp = () => 0;
    }

    private static void Waiting(GameShell shell)
    {
        Require(shell.SelectedObject == Settlement && shell.InspectorContent is { } panel &&
            panel.IsVisibleInTree() && Find<Label>(panel, "Title").Text == "青川" &&
            panel.FindChildren("*", "Label", true, false).Cast<Label>().Any(label => label.IsVisibleInTree() && label.Text == "正在加载详情…") &&
            !panel.FindChildren("*", "TabContainer", true, false).Any(),
            "Ready 画出前应显示当前聚居地名称和等待提示，不应提前构建完整检查器。");
    }

    private static void Details(GameShell shell)
    {
        Require(shell.SelectedObject == Settlement && shell.InspectorContent is { } panel &&
            panel.IsVisibleInTree() && Find<Label>(panel, "Title").Text == "青川" &&
            panel.FindChildren("Pages", "TabContainer", true, false).OfType<TabContainer>().SingleOrDefault()?.GetTabCount() == 5 &&
            !panel.FindChildren("*", "Label", true, false).Cast<Label>().Any(label => label.Text == "正在加载详情…"),
            "等待结束后应显示同一聚居地原有的五页检查器。");
    }

    // headless 不绘制像素；通过真实 Godot 绘制完成信号控制帧边界，窗口显示另行验证。
    private static void Draw() => RenderingServer.Singleton.EmitSignal(RenderingServer.SignalName.FramePostDraw);

    private async Task Run(ImmutableArray<SceneResult<ISceneFrame>> frames,
        Func<Train.App.App, GameUi, GameShell, Task> check,
        HostSimulationTests.HostSetup? setup = null, bool rejectOpen = false)
    {
        var fixture = new Fixture(TestScene.GetTree());
        var window = TestScene.GetTree().Root;
        var previousScale = window.ContentScaleFactor;
        var previousSize = window.Size;
        try
        {
            UiCapture.ConfigureWindowIfNeeded(window);
            window.ContentScaleFactor = 1;
            setup ??= new HostSimulationTests.HostSetup(EconomyFixture.Create(), EconomyFixture.Content(mapPresentation: true, equipmentName: "装备"));
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            var nodeRequests = new Queue<SceneRequestId>([new(2), new(1)]);
            app.CreateScenes = () => new FixedDisplayFactory
            {
                OpenWorldCall = (binding, _) => rejectOpen
                    ? new SceneResult<IWorldSceneSession>.Failure([Problem()])
                    : new SceneResult<IWorldSceneSession>.Success(new FixedDisplaySession
                {
                    Id = SceneId, Binding = binding,
                    RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty
                        .Add(NodeTarget, new SceneResult<SceneRequestId>.Success(nodeRequests.Dequeue()))
                        .Add(TrainTarget, new SceneResult<SceneRequestId>.Success(new(1))),
                    PresentResults = frames,
                    ObserveResults = Enumerable.Repeat<SceneResult<SimulationPosition>>(
                        new SceneResult<SimulationPosition>.Success(new(0, 1)), 10).ToImmutableArray()
                })
            };
            await fixture.AddToRoot(app);
            await FramesUntil(TestScene, () => app.Host!.GetStatus() is not null && app.Saves!.Status is not null);
            await new UiTestInput(TestScene).Frames(2);
            app.SetProcess(false);
            var ui = app.GetNode<GameUi>("GameUi");
            ui.SetProcess(false);
            await check(app, ui, (GameShell)ui.World);
        }
        finally { await fixture.Cleanup(); window.ContentScaleFactor = previousScale; window.Size = previousSize; }
    }

    private static SceneResult<ISceneFrame> Frame(ulong request, SceneReadiness readiness, ulong revision = 1) =>
        new SceneResult<ISceneFrame>.Success(new FixedSceneFrame
        {
            Update = new(null, new(SceneId, new(request), new(request), revision), NVector3.Zero, [], [], new([], []), []),
            Readiness = readiness, PickResult = new ScenePick.Miss(), TerrainHeightResult = new Lookup<double>.Found(0),
            Poses = ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty.Add(new WorldObjectId.Instance(new InstanceId.Vehicle(new(10))),
                new Lookup<ScenePose>.Found(new(NVector3.Zero, System.Numerics.Quaternion.Identity)))
        });

    private static SceneProblem Problem() => new("fixture_scene_failed", SceneStage.Build, Node, null, null, null, "固定场景失败。");
    private static SceneResult<ISceneFrame> Failure() => new SceneResult<ISceneFrame>.Failure([Problem()]);
}
