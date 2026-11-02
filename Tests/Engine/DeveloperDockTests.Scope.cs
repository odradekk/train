using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class DeveloperDockTests
{
    [Test]
    public async Task Dock_opens_on_the_game_map_and_closes_when_returning_to_the_main_menu()
    {
        await Run(async (app, console) =>
        {
            Require(app.GetNode<GameUi>("GameUi").World.IsVisibleInTree(), "前置条件必须处于游戏地图。");
            await _input.PressKey(Key.Quoteleft);
            Require(console.IsOpen, "游戏地图必须能打开底栏。");

            Require(app.Host!.LockLeaving() is null, "返回主菜单前必须取得离开保护锁。");
            app.Host.LeaveSession();
            app.Host.UnlockLeaving();
            await _input.WaitUntil(() => app.GetNode<MainMenu>("MainMenu").IsVisibleInTree());
            await _input.Frames(2);
            Require(!console.IsOpen, "返回主菜单必须关闭底栏。");
            await _input.PressKey(Key.Quoteleft);
            Require(!console.IsOpen, "主菜单不能用反引号打开底栏。");
        });
    }

    [Test]
    public async Task Authoring_mode_closes_the_dock_before_a_definition_form_is_opened()
    {
        await Run(async (app, console) =>
        {
            await _input.PressKey(Key.Quoteleft);
            Require(console.IsOpen, "进入创作模式前必须实际打开底栏。");
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "创作模式");
            Require(ui.GetNode<Control>("Root/Authoring").IsVisibleInTree() && app.Authoring!.Form is null,
                "前置条件必须是真实创作面板已打开、尚未选择定义。");
            await _input.Frames(2);
            Require(!console.IsOpen, "打开创作模式必须关闭底栏。");
            await _input.PressKey(Key.Quoteleft);
            Require(!console.IsOpen, "创作模式尚未选择定义时也不能重开底栏。");
        });
    }

    [Test]
    public async Task Opening_a_design_edit_session_closes_the_dock_and_blocks_reopening()
    {
        var fixture = new EditorPreview { Name = "DeveloperDockEditorScope" };
        TestScene.AddChild(fixture);
        try
        {
            await _input.WaitUntil(() => fixture.Editor.Session is not null);
            Require(fixture.Editor.RequestClose(() => { }) is null && !fixture.Editor.IsOpen,
                "未修改的初始编辑会话必须能正常关闭。");
            await _input.Frames(3);
            var console = fixture.App.GetChildren().OfType<GameConsole>().Single();
            await _input.PressKey(Key.Quoteleft);
            Require(console.IsOpen, "重新进入编辑器前必须在地图实际打开底栏。");

            fixture.Editor.Open(EditorPreviewSession.Draft, new DraftTarget.LocalBlueprint(null));
            await _input.WaitUntil(() => fixture.Editor.Session is not null);
            await _input.Frames(2);
            Require(fixture.App.GetNode<DesignEditorPanel>("GameUi/Root/DesignEditor").IsVisibleInTree(),
                "前置条件必须是真实设计编辑面板与编辑会话均已打开。");
            Require(!console.IsOpen, "设计编辑器打开后必须关闭底栏。");
            await _input.PressKey(Key.Quoteleft);
            Require(!console.IsOpen, "设计编辑器中不能用反引号重开底栏。");
        }
        finally { fixture.QueueFree(); await _input.Frames(3); }
    }

    [Test]
    public async Task Node_scene_keeps_the_developer_dock_available()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(), EconomyFixture.Content(mapPresentation: true));
        var target = new WorldSceneTarget.Location(new WorldLocation.Node(new(1)));
        var scenes = new FixedDisplayFactory
        {
            OpenWorldCall = (binding, _) =>
            {
                var id = new SceneSessionId(Guid.NewGuid());
                return new SceneResult<IWorldSceneSession>.Success(new FixedDisplaySession
                {
                    Id = id, Binding = binding,
                    RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty
                        .Add(target, new SceneResult<SceneRequestId>.Success(new(1))),
                    PresentResults = [new SceneResult<ISceneFrame>.Success(new FixedSceneFrame
                    {
                        Update = new(null, new(id, new(1), new(1), 1), default, [], [], new([], []), []),
                        TerrainHeightResult = new Lookup<double>.Missing()
                    })]
                });
            }
        };
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            app.CreateScenes = () => scenes;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            app.WorldScenes!.Open(target);
            await _input.WaitUntil(() => app.WorldScenes.View.Interactive);
            Require(!app.GetNode<MacroMap>("GameUi/Root/World/MacroMap").IsVisibleInTree(),
                "前置条件必须已进入节点场景，而非仍停留在地图。");
            await _input.PressKey(Key.Quoteleft);
            var console = app.GetChildren().OfType<GameConsole>().Single();
            Require(console.IsOpen, "节点场景必须能打开底栏。");
        }
        finally { await fixture.Cleanup(); }
    }
}
