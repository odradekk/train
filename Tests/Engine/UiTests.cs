using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Train.App;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class UiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Major_settlement_outer_marker_accepts_input_while_ordinary_settlement_does_not()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        using var output = new GodotOutputCapture();
        using var dispatcher = new GodotMainThreadDispatcher();
        var factory = Train.Content.ContentFactory.Create();
        var loaded = factory.PrepareLoad(new(new(new("builtin"), new("mods"), null, "0.0.56", []),
            new ContentLoadPurpose.Initial()), new MajorSettlementFiles(), new GodotAssetInspector(dispatcher));
        Require(loaded is ContentResult<IContentCandidate>.Success, $"聚居地测试内容加载失败：{loaded}。");
        using var candidate = ((ContentResult<IContentCandidate>.Success)loaded).Value;
        var templates = new[] { "core:agricultural-town", "core:ordinary-town" };
        var content = factory.WithSettlementCopies(candidate.View, templates.Select((id, index) =>
        {
            var template = candidate.View.Core.SettlementTemplates.Single(item => item.Id.Value == id);
            return new OwnedSettlementTemplateReference(new((ulong)index + 1), template.Id,
                template.Plots.Select(plot => new TemplatePlotReference(plot.Number, plot.Size)).ToImmutableArray());
        }).ToImmutableArray());
        var original = UiWorldFixture.Map();
        var nodes = original.World.Nodes.Select(node => node with
        { Occupant = new NodeOccupant.Settlement(new(node.Id.Value), SettlementRelation.Hostile) }).ToImmutableArray();
        var view = new FixedCoreReadView(default, HostCoreScenario.Input)
        {
            World = original.World with { Nodes = nodes, Trains = [] },
            Nodes = nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(new(node, []))),
            Locations = nodes.ToDictionary(node => (WorldLocation)new WorldLocation.Node(node.Id), node =>
                (Lookup<LocationView>)new Lookup<LocationView>.Found(new(new WorldLocation.Node(node.Id), [], [], [], [], node.Occupant)))
        };
        var scenes = new FixedDisplayFactory { OpenWorldResults = [new SceneResult<IWorldSceneSession>.Failure(
            [new("fixture_scene_unavailable", SceneStage.Build, null, null, null, null, "地图测试不构建世界场景。")])] };
        OS.AddLogger(output);
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => new HostSimulationTests.HostSetup(view, content).Create(logger);
            app.CreateScenes = () => scenes;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            await _input.Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            Require(ui.ErrorText.Length == 0, $"聚居地地图显示失败：{ui.ErrorText}。");
            await UiCapture.Capture(TestScene, "major-settlements");
            WorldObjectId? clicked = null;
            map.ObjectClicked += value => clicked = value;
            // 先点击普通聚居地，避免主要聚居地命中后打开的详情面板遮挡后续输入。
            var ordinaryPoint = map.GlobalPosition + map.MapToScreen(nodes[1].Position) + new Vector2(18, 0);
            Require(map.GetGlobalRect().HasPoint(ordinaryPoint), "普通聚居地测试点击位于地图范围外。");
            var screenPoint = TestScene.GetViewport().GetScreenTransform() * ordinaryPoint;
            using (var motion = new InputEventMouseMotion { Position = screenPoint, GlobalPosition = screenPoint })
                Input.ParseInputEvent(motion);
            await _input.Frames();
            Require(tree.Root.GuiGetHoveredControl() == map, "普通聚居地测试点击被其他界面遮挡。");
            await _input.Click(ordinaryPoint);
            Require(clicked is null, "普通聚居地在圆心外 18 像素处被实际左键选中。");
            await _input.Click(map.GlobalPosition + map.MapToScreen(nodes[0].Position) + new Vector2(18, 0));
            Require(clicked == new WorldObjectId.Node(nodes[0].Id), "主要聚居地在圆心外 18 像素处未被实际左键选中。");
            Require(scenes.OpenCalls.Count == 1, "地图点击没有发出唯一的场景打开请求。");
            Require(!output.Contains("scene_adapter_failed"), "地图点击打开场景记录了适配异常。");
        }
        finally { OS.RemoveLogger(output); await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

    private sealed class MajorSettlementFiles : IContentFiles
    {
        private readonly DiskContentFiles _disk = new(ProjectSettings.GlobalizePath("res://builtin/core"));

        public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory)
        {
            if (directory.Root.Key == "mods") return new ContentIoResult<ImmutableArray<ContentFileEntry>>.Success([]);
            var result = _disk.Enumerate(directory);
            var extra = directory.RelativePath switch
            {
                "resources/settlement-types" => "ordinary-town.settlement-type.json",
                "resources/settlement-templates" => "ordinary-town.settlement-template.json",
                _ => null
            };
            return extra is not null && result is ContentIoResult<ImmutableArray<ContentFileEntry>>.Success entries
                ? new ContentIoResult<ImmutableArray<ContentFileEntry>>.Success(entries.Value.Add(new(extra, ContentEntryKind.File))) : result;
        }

        public ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes)
        {
            var ordinary = file.RelativePath.Contains("ordinary-town", StringComparison.Ordinal);
            var source = ordinary ? file with { RelativePath = file.RelativePath.Replace("ordinary-town", "agricultural-town", StringComparison.Ordinal) } : file;
            var result = _disk.Read(source, maximumBytes);
            if (result is not ContentIoResult<ImmutableArray<byte>>.Success bytes ||
                source.RelativePath is not ("resources/settlement-types/agricultural-town.settlement-type.json" or
                    "resources/settlement-templates/agricultural-town.settlement-template.json")) return result;
            var json = JsonNode.Parse(bytes.Value.AsSpan())!;
            if (ordinary) json["id"] = "ordinary-town";
            if (source.RelativePath.EndsWith(".settlement-type.json", StringComparison.Ordinal)) json["display"]!["major"] = !ordinary;
            else
            {
                if (ordinary) json["settlement_type"] = "ordinary-town";
                json["display"]!["name"] = ordinary ? "普通农业镇 · major=false" : "主要农业镇 · major=true";
            }
            return new ContentIoResult<ImmutableArray<byte>>.Success(Encoding.UTF8.GetBytes(json.ToJsonString()).ToImmutableArray());
        }
    }

    [Test]
    public async Task Lair_node_draws_an_ordinary_marker_without_a_content_error()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var original = UiWorldFixture.Map();
        var lair = original.World.Nodes[1] with { Occupant = new NodeOccupant.Lair(new(1)) };
        var view = new FixedCoreReadView(default, HostCoreScenario.Input)
        { World = original.World with { Nodes = original.World.Nodes.SetItem(1, lair) } };
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
                map.HitTest(map.MapToScreen(lair.Position)) == new WorldObjectId.Node(lair.Id),
                "生物聚落标记导致内容错误或无法选中。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Map_input_hits_objects_and_panels_block_camera_input()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        using var output = new GodotOutputCapture();
        OS.AddLogger(output);
        var view = UiWorldFixture.Map();
        var setup = new HostSimulationTests.HostSetup(view);
        var scenes = new FixedDisplayFactory { OpenWorldResults = [new SceneResult<IWorldSceneSession>.Failure(
            [new("fixture_scene_unavailable", SceneStage.Build, null, null, null, null, "地图测试不构建世界场景。")])] };
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
            Require(scenes.OpenCalls.Count == 1, "地图点击没有发出唯一的场景打开请求。");
            Require(!output.Contains("scene_adapter_failed"), "地图点击打开场景记录了适配异常。");
        }
        finally { OS.RemoveLogger(output); await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

    [Test]
    public async Task Speed_buttons_preserve_focus_pause_and_select_only_the_current_control()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var setup = new HostSimulationTests.HostSetup(UiWorldFixture.Map());
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
        var view = UiWorldFixture.Map();
        var setup = new HostSimulationTests.HostSetup(view);
        var rejectedView = UiWorldFixture.Map(new(0, 1));
        setup.Session.QueueSubmit(new CoreCommand.StopTrain(new(7)), CommandSource.UserInterface,
            new SubmissionResult.Completed(new(1, view.Position, new(0, 1),
                new CommandDecision.Rejected([new("no_route", "train.plan", new WorldObjectId.Train(new(7)), [])])), [], null),
            rejectedView);
        var replacement = UiWorldFixture.Session(UiWorldFixture.Map());
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
            await _input.PressKey(Key.Space, window: confirmation);
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
            await _input.PressKey(Key.Space, window: confirmation);
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
