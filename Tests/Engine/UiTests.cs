using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class UiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Lair_node_draws_a_hostile_marker_without_a_content_error()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var original = UiWorldFixture.Map();
        var lair = original.World.Nodes[1] with { Occupant = new NodeOccupant.Lair(new(1)) };
        var view = new FixedCoreReadView(default, HostCoreScenario.Input)
        { Economy = UiWorldFixture.Economy, World = original.World with { Nodes = original.World.Nodes.SetItem(1, lair) },
            Trains = original.World.Trains.ToDictionary(train => train.Id, train => original.FindTrain(train.Id)) };
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => new HostSimulationTests.HostSetup(view).Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            var ui = app.GetNode<GameUi>("GameUi");
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            var drawn = false;
            map.Draw += () => drawn = true;
            map.QueueRedraw();
            await _input.WaitUntil(() => drawn);
            Require(ui.ErrorText.Length == 0 && !ui.GetNode<Control>("Root/ErrorPanel").Visible &&
                map.HitTest(map.MapToScreen(lair.Position) + new Vector2(0, 15)) == new WorldObjectId.Node(lair.Id) &&
                map.HitTest(map.MapToScreen(lair.Position) + new Vector2(0, 17)) is null,
                "生物聚落标记导致内容错误，或命中区域未匹配聚居地标记半径。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Notifications_stay_above_docked_lists_and_below_settings()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new HostSimulationTests.HostSetup(UiWorldFixture.Map());
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.Activate(Find<Button>(ui, "Notices"));
            await _input.Activate(Find<Button>(ui, "Fleet"));
            await _input.Frames(2);
            var notices = (Control)ui.FindChild("NoticeLog", true, false);
            var fleet = (Control)ui.FindChild("FleetList", true, false);
            var toasts = (Control)ui.FindChild("Toasts", true, false);
            Require(Above(notices, fleet) && Above(toasts, fleet), "停靠列表盖住了通知记录或提示。");
            await _input.Activate(Find<Button>(ui, "Notices"));
            Require(notices.IsVisibleInTree() && fleet.IsVisibleInTree(), "打开通知记录关闭了停靠列表。");
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "设置");
            var settings = (Control)ui.GetNode("Root/SettingsOverlay").FindChild("Settings", true, false);
            var minimap = ui.World.GetChildren().OfType<NodeMinimap>().Single();
            Require(Above(settings, notices) && Above(settings, fleet) && Above(settings, toasts) &&
                Above(ui.GetNode("Root/ErrorPanel"), toasts), "业务面板或通知提示盖过了设置或错误界面。");
            Require(Above(notices, minimap) && !fleet.GetGlobalRect().Intersects(minimap.GetGlobalRect()),
                "通知被小地图盖住，或停靠列表覆盖了小地图。");
            var markers = ui.World.GetChildren().OfType<SceneStatusOverlay>().Single();
            Require(Above(minimap, markers) && !fleet.GetGlobalRect().Intersects(markers.GetGlobalRect()) && Above(toasts, markers),
                "场景状态覆盖层超出中间区域，或盖住了小地图、通知提示。");
            setup.Session.AssertComplete();
        }
        finally { await fixture.Cleanup(); }
    }

    // 同一 CanvasLayer 内按场景树顺序绘制和接收输入：在最近公共祖先下排在后面的在上面。
    private static bool Above(Node upper, Node lower)
    {
        var lowerPath = new List<Node>();
        for (var node = lower; node is not null; node = node.GetParent()) lowerPath.Add(node);
        for (Node? node = upper, child = null; node is not null; child = node, node = node.GetParent())
        {
            var index = lowerPath.IndexOf(node);
            if (index > 0 && child is not null) return child.GetIndex() > lowerPath[index - 1].GetIndex();
        }
        return false;
    }

    [Test]
    public async Task Settings_open_through_input_and_persist_scale_with_bundled_fonts()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var path = ProjectSettings.GlobalizePath("user://settings.cfg");
        var original = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var scale = tree.Root.ContentScaleFactor;
        var mode = tree.Root.Mode;
        var fps = Godot.Engine.MaxFps;
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            var ui = app.GetNode<GameUi>("GameUi");
            var settings = app.GetNode<MainMenu>("MainMenu").GetNode<Button>("%Settings");
            settings.GrabFocus();
            await _input.PressKey(Key.Space);
            Require(ui.GetNode<SettingsOverlay>("Root/SettingsOverlay").Visible, "按键未打开设置。");
            var font = settings.GetThemeFont("font");
            Require(font.ResourcePath == "res://Ui/Fonts/NotoSansSC-Regular.otf" && font.GetFontName() == "Noto Sans SC",
                "正文未使用随包的官方 SC 地区子集字体。");
            var bold = settings.GetThemeFont("font", "Title");
            Require(bold.ResourcePath == "res://Ui/Fonts/NotoSansSC-Bold.otf" && bold.GetFontName() == "Noto Sans SC",
                "标题未使用随包的官方 SC 粗体。");
            Require(Mathf.IsEqualApprox(font.GetStringSize("1111").X, font.GetStringSize("8888").X),
                "字体实际整形后的数字不等宽。");
            Require(Mathf.IsEqualApprox(bold.GetStringSize("1111").X, bold.GetStringSize("8888").X),
                "粗体实际整形后的数字不等宽。");
            var input = ui.FindChildren("UiScale", "SpinBox", true, false).Cast<SpinBox>().Single();
            var edit = input.GetLineEdit();
            edit.GrabFocus();
            await _input.Frames();
            Require(edit.HasFocus() && edit.IsEditing(), "缩放输入未进入编辑模式。");
            edit.Text = "1.25";
            await _input.PressKey(Key.Enter);
            Require(Mathf.IsEqualApprox(tree.Root.ContentScaleFactor, 1.25f),
                $"设置输入没有改变界面缩放：text={edit.Text}，value={input.Value}，scale={tree.Root.ContentScaleFactor}。");
            using var saved = new ConfigFile();
            Require(saved.Load("user://settings.cfg") == Error.Ok &&
                Mathf.IsEqualApprox((float)saved.GetValue("display", "scale").AsDouble(), 1.25f),
                "设置没有通过真实 ConfigFile 保存。");
        }
        finally
        {
            await fixture.Cleanup();
            tree.Root.ContentScaleFactor = scale; tree.Root.Mode = mode; Godot.Engine.MaxFps = fps;
            if (original is null) File.Delete(path); else File.WriteAllBytes(path, original);
        }
    }

    [Test]
    public async Task Restore_adaptation_notice_states_the_content_change_in_info_color()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var setup = new HostSimulationTests.HostSetup(UiWorldFixture.Map());
        var previous = new string('a', 64);
        var current = new string('b', 64);
        var problem = new CoreProblem("content_changed", "InputIdentity", null, [previous, current]);
        var replacement = UiWorldFixture.Session(UiWorldFixture.Map());
        setup.ReplacementCandidate = new(replacement, HostCoreScenario.References, adaptation: [problem]);
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            var ui = app.GetNode<GameUi>("GameUi");
            var binding = app.Host!.GetStatus()!.Binding;
            var started = app.Host.BeginRestore("opaque core", "opaque content",
                new(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1));
            Require(started.Problem is null, "内容适配读档未被接受。");
            var panel = ui.GetNode<Control>("Root/ErrorPanel");
            await _input.WaitUntil(() => app.Host.GetStatus()!.Binding != binding && panel.Visible);
            var body = panel.GetNode<Label>("Layout/Content/Body/Error");
            var title = panel.GetNode<Label>("Layout/Heading/Title");
            var commandText = CommandDiagnostics.Describe(problem);
            Require(panel.IsVisibleInTree() &&
                title.Text == "读档完成，内容已适配" &&
                ui.ErrorText.Length > 0 &&
                !ui.ErrorText.Contains(commandText) &&
                !ui.ErrorText.Contains(problem.Code) &&
                !ui.ErrorText.Contains(problem.Field) &&
                !ui.ErrorText.Contains(previous) &&
                !ui.ErrorText.Contains(current) &&
                body.GetThemeColor("font_color").IsEqualApprox(UiStyle.Info),
                $"读档适配说明不正确：title={title.Text}，text={ui.ErrorText}，color={body.GetThemeColor("font_color")}。");
            ui.ShowError("颜色探针");
            Require(ui.ErrorText == "颜色探针" && body.GetThemeColor("font_color").IsEqualApprox(UiStyle.Error),
                $"随后的错误没有回到错误色：text={ui.ErrorText}，color={body.GetThemeColor("font_color")}。");
            replacement.AssertComplete();
        }
        finally { await fixture.Cleanup(); }
    }

}
