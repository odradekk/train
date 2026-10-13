using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class UiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Map_input_hits_objects_and_panels_block_camera_input()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        var view = WorldView();
        var setup = new HostSimulationTests.HostSetup(view);
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
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            Require(ReferenceEquals(map.View, view) && ReferenceEquals(ui.View, view), "地图和宿主未读取同一固定快照。");
            WorldObjectId? clicked = null;
            map.ObjectClicked += value => clicked = value;
            await _input.Click(map.GlobalPosition + map.MapToScreen(view.World.Nodes[1].Position));
            Require(clicked == new WorldObjectId.Node(view.World.Nodes[1].Id), "实际左键没有命中地图节点。");

            var free = map.GlobalPosition + map.Size * new Vector2(.75f, .8f);
            var before = map.Offset;
            await _input.Drag(free, new Vector2(37, -19));
            Require(map.Offset.DistanceTo(before + new Vector2(37, -19)) < .1f, "实际中键拖动没有平移地图。");
            var local = free - map.GlobalPosition;
            var anchor = (local - map.Offset) / map.Zoom;
            var zoom = map.Zoom;
            await _input.MouseButton(free, MouseButton.WheelUp, true);
            await _input.MouseButton(free, MouseButton.WheelUp, false);
            Require(map.Zoom > zoom && ((local - map.Offset) / map.Zoom).DistanceTo(anchor) < .01f,
                "滚轮没有以实际鼠标位置缩放。");
            await UiCapture.Capture(TestScene, "macro-map");

            var fleet = ui.GetNode<Button>("Root/World/Bar/TopBar/Fleet");
            fleet.GrabFocus();
            await _input.PressKey(Key.Space);
            var list = ui.GetNode<ItemList>("Root/World/WorldPanels/FleetList/Layout/Body/Objects");
            Require(list.IsVisibleInTree(), "顶栏实际按键没有打开车队列表。");
            await _input.Frames(2);
            var covered = list.GlobalPosition + new Vector2(30, 30);
            var nodePosition = map.GlobalPosition + map.MapToScreen(view.World.Nodes[1].Position);
            await _input.Drag(free, covered - nodePosition);
            Require(map.HitTest(covered - map.GlobalPosition) == new WorldObjectId.Node(view.World.Nodes[1].Id),
                "遮挡情景下方没有预期地图节点。");
            clicked = null;
            await _input.Click(covered);
            Require(clicked is null, "上层列表的左键输入穿透并选中了地图对象。");
            before = map.Offset;
            zoom = map.Zoom;
            await _input.Drag(covered, new Vector2(20, 12));
            await _input.MouseButton(covered, MouseButton.WheelUp, true);
            await _input.MouseButton(covered, MouseButton.WheelUp, false);
            Require(map.Offset == before && map.Zoom == zoom, "上层列表的实际输入穿透到了地图。");
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

    [Test]
    public async Task Speed_buttons_preserve_focus_pause_and_select_only_the_current_control()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var setup = new HostSimulationTests.HostSetup(WorldView());
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            var host = app.Host!;
            host.AutoPauseOnFocusLoss = true;
            host.SetFocused(false);
            var bar = app.GetNode<GameUi>("GameUi").GetNode<Control>("Root/World/Bar/TopBar");
            var controls = new[] { "Pause", "Speed1", "Speed2", "Speed4", "Speed8" }
                .Select(name => bar.GetNode<Button>(name)).ToArray();
            controls[2].GrabFocus();
            await _input.PressKey(Key.Space);
            var status = host.GetStatus()!;
            Require(status.Speed == SimulationSpeed.Two && !status.PlayerPaused && status.FocusPaused &&
                controls[2].ButtonPressed && controls.Count(button => button.ButtonPressed) == 1,
                "真实倍速输入未解除玩家暂停、保留失焦暂停并单选当前速度。");
            controls[0].GrabFocus();
            await _input.PressKey(Key.Space);
            status = host.GetStatus()!;
            Require(status.PlayerPaused && status.FocusPaused && controls[0].ButtonPressed &&
                controls.Count(button => button.ButtonPressed) == 1 && setup.Session.AdvanceCount == 0,
                "真实暂停输入未保留失焦暂停，或保护期间仍推进模拟。");
            setup.Session.AssertComplete();
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Train_list_rejection_is_visible_and_old_session_details_cannot_submit()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        var view = WorldView();
        var setup = new HostSimulationTests.HostSetup(view);
        var rejectedView = WorldView(new(0, 1));
        setup.Session.QueueSubmit(new CoreCommand.StopTrain(new(1)), CommandSource.UserInterface,
            new SubmissionResult.Completed(new(1, view.Position, new(0, 1),
                new CommandDecision.Rejected([new("no_route", "train.plan", new WorldObjectId.Train(new(1)), [])])), [], null),
            rejectedView);
        var replacement = new ScriptedCoreSession(WorldView());
        setup.ReplacementCandidate = new(replacement, HostCoreScenario.References);
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            await _input.Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            ui.GetNode<Button>("Root/World/Bar/TopBar/Fleet").GrabFocus();
            await _input.PressKey(Key.Space);
            var list = ui.GetNode<ItemList>("Root/World/WorldPanels/FleetList/Layout/Body/Objects");
            await _input.ClickItem(list, 0);
            var panels = ui.GetNode<WorldPanels>("Root/World/WorldPanels");
            var stop = panels.GetNodeOrNull<Button>("Details/Layout/Body/Stop");
            Require(stop is not null && stop.IsVisibleInTree(),
                $"实际列表点击没有打开列车详情：rect={list.GetGlobalRect()}，item={list.GetItemRect(0)}，selected={string.Join(',', list.GetSelectedItems())}，hover={tree.Root.GuiGetHoveredControl()?.GetPath()}。");
            stop!.GrabFocus();
            await _input.PressKey(Key.Space);
            Require(setup.Session.SubmitCount == 1 && ui.ErrorText.Contains("no_route/train.plan") &&
                ui.GetNode<Control>("Root/ErrorPanel").IsVisibleInTree(), "真实停车命令的拒绝没有显示在界面。");
            setup.Session.AssertComplete();

            var disband = panels.GetNode<Button>("Details/Layout/Body/Disband");
            disband.GrabFocus();
            await _input.PressKey(Key.Space);
            var confirmation = panels.GetNode<ConfirmationDialog>("DisbandConfirmation");
            Require(confirmation.Visible, "真实解编输入没有打开原生确认窗口。");
            confirmation.GetCancelButton().GrabFocus();
            await _input.PressKey(Key.Space);
            Require(!confirmation.Visible && setup.Session.SubmitCount == 1,
                "取消原生解编确认提交了命令或未关闭窗口。");
            disband.GrabFocus();
            await _input.PressKey(Key.Space);
            Require(confirmation.Visible, "再次解编没有打开确认窗口。");
            var binding = app.Host!.GetStatus()!.Binding;
            var result = app.Host.BeginRestore("opaque core", "opaque content",
                new(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1));
            Require(result.Problem is null, "替换对局的有限情景未被接受。");
            await _input.WaitUntil(() => app.Host.GetStatus()!.Binding != binding);
            Require(confirmation.GetOkButton().Disabled && replacement.SubmitCount == 0 && setup.Session.SubmitCount == 1,
                "替换对局后旧解编目标仍可确认，或产生额外提交。");
            confirmation.GetCancelButton().GrabFocus();
            await _input.PressKey(Key.Space);
            Require(!confirmation.Visible, "旧解编确认窗口没有经真实取消输入关闭。");
            var text = panels.FindChildren("Description", "Label", true, false).Cast<Label>().Single();
            Require(stop.Disabled && text.Text.Contains("已不存在"), "旧对局详情没有明确失效。");
            await _input.Click(stop.GetGlobalRect().GetCenter());
            Require(replacement.SubmitCount == 0 && setup.Session.SubmitCount == 1,
                "旧对局按钮仍向当前或已退出会话提交。");
            replacement.AssertComplete();
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

    private static FixedCoreReadView WorldView(SimulationPosition tick = default)
    {
        var terrain = new TerrainView(new("core:plains"), 0, []);
        var first = new NodeMapItem(new(1), new(250, 300), terrain, new NodeOccupant.Empty(), [], 0, 0, null);
        var second = new NodeMapItem(new(2), new(750, 300), terrain, new NodeOccupant.Empty(), [], 0, 0, null);
        var position = new TrainPosition.Node(first.Id);
        var train = new TrainView(new(1), position, TrainActivity.Stopped, [], new(1),
            0, 20, 10, 100, 0, 30, null, null, 0,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, .2), [], null, [], null,
            TrainOrderFacing.FirstEndLeading);
        return new(tick, HostCoreScenario.Input)
        {
            World = new(1, new("fixture:railway"), 1000, 600, [first, second], [],
                [new(train.Id, position, train.Activity)], [], []),
            Trains = new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = new Lookup<TrainView>.Found(train) }
        };
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
            Require(ui.GetNode<SettingsPanel>("Root/SettingsPanel").Visible, "实际按键没有打开设置。");
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


}

internal static class UiCapture
{
    private static string? OutputDirectory => DisplayServer.GetName() == "headless" ? null :
        System.Environment.GetEnvironmentVariable("TRAIN_UI_CAPTURE_DIR");

    internal static void ConfigureWindowIfNeeded(Window window)
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory)) return;
        window.Mode = Window.ModeEnum.Windowed;
        window.Size = new Vector2I(1920, 1080);
    }

    internal static async Task Capture(Node scene, string name)
    {
        var output = OutputDirectory;
        if (string.IsNullOrWhiteSpace(output)) return;
        var window = scene.GetTree().Root;
        Require(window.Size == new Vector2I(1920, 1080) && Mathf.IsEqualApprox(window.ContentScaleFactor, 1),
            "UI 原型截图需要 1920×1080 窗口和 1 倍界面缩放。");
        await scene.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await scene.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "rendering.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            operatingSystem = OS.GetName(), godot = Godot.Engine.GetVersionInfo()["string"].AsString(),
            displayServer = DisplayServer.GetName(), renderingMethod = RenderingServer.GetCurrentRenderingMethod(),
            driver = RenderingServer.GetCurrentRenderingDriverName(), adapter = RenderingServer.GetVideoAdapterName(),
            vendor = RenderingServer.GetVideoAdapterVendor(), adapterType = RenderingServer.GetVideoAdapterType().ToString(),
            apiVersion = RenderingServer.GetVideoAdapterApiVersion(), viewportWidth = 1920, viewportHeight = 1080
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        using var image = window.GetTexture().GetImage();
        Require(image.GetWidth() == 1920 && image.GetHeight() == 1080, "UI 截图实际 viewport 尺寸不符。");
        Require(image.SavePng(Path.Combine(output, name + ".png")) == Error.Ok, "UI 截图保存失败。");
    }
}
