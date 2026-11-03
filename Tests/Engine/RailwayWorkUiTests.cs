using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Assets;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class RailwayWorkUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Map_marks_use_warning_and_severe_only_for_breaks_and_started_work()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        var view = RailwayWorkUiFixture.Create();
        var content = RailwayWorkUiFixture.Content();
        try
        {
            tree.Root.ContentScaleFactor = 1;
            var map = fixture.AutoFree(new MacroMap { Name = "RailwayMap" });
            map.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            string? displayFailure = null;
            map.DisplayFailed += message => displayFailure = message;
            await fixture.AddToRoot(map);
            await _input.Frames(2);
            if (map.Size.X < 100)
            {
                map.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
                map.Size = tree.Root.GetVisibleRect().Size;
            }
            map.SetView(view, content);
            await _input.Frames(2);
            Require(displayFailure is null, "地图符号绘制失败：" + displayFailure);
            Require(map.HitTest(Midpoint(map, view, RailwayWorkUiFixture.ZeroTrackEdge)) == new WorldObjectId.Edge(new(RailwayWorkUiFixture.ZeroTrackEdge))
                && map.HitTest(Midpoint(map, view, RailwayWorkUiFixture.BreakEdge)) == new WorldObjectId.Edge(new(RailwayWorkUiFixture.BreakEdge))
                && map.HitTest(Midpoint(map, view, RailwayWorkUiFixture.ActiveEdge)) == new WorldObjectId.Edge(new(RailwayWorkUiFixture.ActiveEdge)),
                "线路中点没有命中对应线路。");
            if (DisplayServer.GetName() == "headless") return;
            RenderingServer.ForceDraw();
            using var image = map.GetViewport().GetTexture().GetImage()
                ?? throw new InvalidOperationException("窗口模式无法读取地图像素。");
            var zero = Midpoint(map, view, RailwayWorkUiFixture.ZeroTrackEdge);
            var broken = Marks(map, view, RailwayWorkUiFixture.BreakEdge);
            var active = Marks(map, view, RailwayWorkUiFixture.ActiveEdge);
            Require(!RegionHas(image, map, zero, UiStyle.Warning, 18) && !RegionHas(image, map, zero, UiStyle.Error, 18),
                "零轨线路中点附近出现了施工或断点颜色。");
            Require(RegionHas(image, map, broken.Break, UiStyle.Error, 3),
                "断点标记不是严重色。" + Describe(image, map, broken.Break));
            Require(!RegionHas(image, map, broken.Construction, UiStyle.Warning, 8),
                "只有断点的线路画了施工标记。");
            Require(RegionHas(image, map, active.Construction, UiStyle.Warning, 3),
                "已开工线路的施工标记不是警告色。" + Describe(image, map, active.Construction));
            Require(RegionHas(image, map, active.Break, UiStyle.Error, 3),
                "已开工线路的断点标记不是严重色。" + Describe(image, map, active.Break));
        }
        finally
        {
            await fixture.Cleanup();
            tree.Root.ContentScaleFactor = scale;
        }
    }

    [Test]
    public async Task Selecting_a_line_shows_materials_duration_and_direct_repair()
    {
        var initial = RailwayWorkUiFixture.ReadyToRepair();
        var repaired = RailwayWorkUiFixture.WithAcceptedRepair(2);
        Require(RailwayWorkUiFixture.AcceptedUnits != 11,
            "已投入材料夹具必须与修复预览的 11 件不同。");
        var setup = new HostSimulationTests.HostSetup(initial, RailwayWorkUiFixture.Content());
        await Run(setup, async ui =>
        {
            var panel = await OpenFromList(ui, (int)RailwayWorkUiFixture.ActiveEdge - 1);
            Require(panel.IsVisibleInTree(), "选择线路没有打开检查器。");
            var rows = Find<VBoxContainer>(panel, "Breaks");
            var text = VisibleText(rows);
            Require(text.Contains("1 号轨") && text.Contains("80") && text.Contains("夹具钢轨") && text.Contains("11")
                && text.Contains("秒") && !text.Contains("tick") && !text.Contains("fixture:"), "断点没有可读位置、材料和工期：" + text);
            var repair = RepairButtons(panel).Single();
            Require(!repair.Disabled && repair.Text.Contains("11"), "修复按钮必须显示材料量并可用。");
            Require(Find<OptionButton>(panel, "MaterialSource").ItemCount == 1, "施工来源只能包含自有聚居地。");
            setup.Session.QueueSubmit(new CoreCommand.RepairRail(new(RailwayWorkUiFixture.ActiveEdge), new(2), new(1)),
                CommandSource.UserInterface, Completed(1, initial, repaired, new CommandDecision.Accepted([], [])), repaired);
            await _input.Activate(repair);
            await _input.Frames(2);
            var works = Find<Label>(panel, "Works").Text;
            Require(works.Contains($"夹具钢轨 × {RailwayWorkUiFixture.AcceptedUnits}") && !works.Contains("夹具钢轨 × 11"),
                "受理后的已投入材料不是核心查询值，或误用了预览：" + works);
            await UiCapture.CaptureSizes(TestScene, "line-inspector-normal");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Missing_materials_show_the_deficit_and_disable_repair()
    {
        var setup = new HostSimulationTests.HostSetup(RailwayWorkUiFixture.WithStock(3), RailwayWorkUiFixture.Content());
        await Run(setup, async ui =>
        {
            var panel = await OpenFromList(ui, 2);
            var text = VisibleText(Find<VBoxContainer>(panel, "Breaks"));
            Require(text.Contains("还差 8 件夹具钢轨") && text.Contains("来源库存：夹具钢轨 × 3"), "库存 3、需求 11 时必须说明缺少 8：" + text);
            Require(RepairButtons(panel).Single().Disabled, "缺料时修复应禁用。");
            await UiCapture.CaptureSizes(TestScene, "line-inspector-shortage");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task No_owned_source_disables_construction_and_explains_why()
    {
        var setup = new HostSimulationTests.HostSetup(RailwayWorkUiFixture.WithoutSource(), RailwayWorkUiFixture.Content());
        await Run(setup, async ui =>
        {
            var panel = await OpenFromList(ui, 0);
            Require(Find<Button>(panel, "AddTrack").Disabled && VisibleText(panel).Contains("自有聚居地"), "没有自有库存来源时必须解释并禁用建设。");
            await UiCapture.CaptureSizes(TestScene, "line-inspector-no-source");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Construction_names_and_limits_include_the_track_under_construction()
    {
        string? occupiedDescription = null;
        var setup = new HostSimulationTests.HostSetup(RailwayWorkUiFixture.Create(), RailwayWorkUiFixture.Content());
        await Run(setup, async ui =>
        {
            var empty = await OpenFromList(ui, 0);
            Require(Find<Button>(empty, "AddTrack").Text.Contains("建设线路"), "零轨线路应显示建设线路。");
            Require(empty.FindChild("AddTrackHeading", true, false) is Label { Text: "建设线路" }, "零轨线路缺少建设线路标题。");
            var existing = await OpenFromList(ui, 1);
            Require(Find<Button>(existing, "AddTrack").Text.Contains("加轨"), "已有轨线路应显示加轨。");
            Require(existing.FindChild("AddTrackHeading", true, false) is Label { Text: "加轨" }, "已有轨线路缺少加轨标题。");
            var busy = await OpenFromList(ui, 2);
            occupiedDescription = Find<VBoxContainer>(busy, "AddTrackRow").GetNode<Label>("Description").Text;
            Require(!Find<Button>(busy, "AddTrack").Visible && occupiedDescription.Contains("施工中")
                && occupiedDescription.Contains("占用剩余名额") && !occupiedDescription.Contains("已到上限"),
                "在建轨道占满名额时，加轨行必须说明施工占额：" + occupiedDescription);
            await UiCapture.CaptureSizes(TestScene, "line-inspector-construction");
        });
        setup.Session.AssertComplete();
        var full = new HostSimulationTests.HostSetup(RailwayWorkUiFixture.AtTrackLimit(), RailwayWorkUiFixture.Content());
        await Run(full, async ui =>
        {
            var panel = await OpenFromList(ui, 2);
            var description = Find<VBoxContainer>(panel, "AddTrackRow").GetNode<Label>("Description").Text;
            Require(!Find<Button>(panel, "AddTrack").Visible && description.Contains("已到上限")
                && !description.Contains("施工中") && description != occupiedDescription,
                "实际轨道到上限的加轨说明必须区别于在建占额：" + description);
            await UiCapture.CaptureSizes(TestScene, "line-inspector-track-limit");
        });
        full.Session.AssertComplete();
    }

    [Test]
    public async Task Automatic_repair_waits_for_a_source_and_can_be_enabled_without_materials()
    {
        var initial = RailwayWorkUiFixture.WithStock(0, automaticRepair: false);
        var enabled = RailwayWorkUiFixture.WithStock(0);
        var setup = new HostSimulationTests.HostSetup(initial, RailwayWorkUiFixture.Content());
        await Run(setup, async ui =>
        {
            var panel = await OpenFromList(ui, 2);
            var toggle = Find<CheckButton>(panel, "AutomaticRepair");
            await _input.Activate(toggle);
            Require(setup.Session.SubmitCount == 0, "仅打开自动修复开关就提交了默认来源。");
            var source = Find<OptionButton>(panel, "AutomaticRepairSource");
            Require(source.Visible && source.ItemCount == 2 && source.Selected == 0, "自动修复必须先显示来源占位项。");
            setup.Session.QueueSubmit(new CoreCommand.SetAutomaticRailRepair(new(3), new(1)), CommandSource.UserInterface,
                Completed(1, initial, enabled, new CommandDecision.Accepted([], [])), enabled);
            await _input.Choose(source, 1);
            await _input.Frames(2);
            setup.Session.QueueSubmit(new CoreCommand.SetAutomaticRailRepair(new(3), null), CommandSource.UserInterface,
                Completed(2, enabled, initial, new CommandDecision.Accepted([], [])), initial);
            await _input.Activate(Find<CheckButton>(panel, "AutomaticRepair"));
            await _input.Frames(2);
            Require(setup.Session.SubmitCount == 2, "来源选择与关闭没有各提交一次命令。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Long_break_list_scrolls_and_remains_keyboard_accessible()
    {
        var setup = new HostSimulationTests.HostSetup(RailwayWorkUiFixture.WithManyBreaks(), RailwayWorkUiFixture.Content());
        await Run(setup, async ui =>
        {
            var panel = await OpenFromList(ui, 2);
            var buttons = RepairButtons(panel);
            Require(buttons.Length == 60, "长列表没有显示全部断点。");
            var scroll = panel.FindChildren("*", "ScrollContainer", true, false).Cast<ScrollContainer>().First();
            var point = scroll.GetGlobalRect().GetCenter();
            for (var notch = 0; notch < 3; notch++)
            {
                await _input.MouseButton(point, MouseButton.WheelDown, true);
                await _input.MouseButton(point, MouseButton.WheelDown, false);
            }
            await _input.WaitUntil(() => scroll.ScrollVertical > 0);
            buttons[^1].GrabFocus();
            await _input.Frames(2);
            Require(buttons[^1].HasFocus(), "末行修复按钮无法获得键盘焦点。");
            Require(scroll.GetGlobalRect().Intersects(buttons[^1].GetGlobalRect()), "键盘焦点没有滚动到可见的修复按钮。");
            var window = TestScene.GetTree().Root;
            var oldScale = window.ContentScaleFactor;
            var oldSize = window.Size;
            var settingsPath = ProjectSettings.GlobalizePath("user://settings.cfg");
            var previousSettings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
            try
            {
                await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "设置");
                var settings = Find<SettingsOverlay>(ui, "SettingsOverlay");
                var scaleInput = Find<SpinBox>(settings, "UiScale");
                await _input.Enter(scaleInput.GetLineEdit(), "2");
                await _input.PressKey(Key.Enter);
                Require(Mathf.IsEqualApprox(window.ContentScaleFactor, 2), "设置中的界面缩放没有应用 200%。");
                await _input.Activate(Find<Button>(settings, "Close"));
                window.Size = new(1920, 1080);
                await _input.Frames(2);
                await UiCapture.Capture(TestScene, "line-inspector-long-200pct", 2);
            }
            finally
            {
                window.ContentScaleFactor = oldScale;
                window.Size = oldSize;
                if (previousSettings is null) File.Delete(settingsPath); else File.WriteAllBytes(settingsPath, previousSettings);
            }
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Missing_content_uses_chinese_without_internal_identifiers()
    {
        var content = RailwayWorkUiFixture.Content(missingGood: true);
        var setup = new HostSimulationTests.HostSetup(RailwayWorkUiFixture.ReadyToRepair(), content);
        await Run(setup, async ui =>
        {
            var panel = await OpenFromList(ui, 2);
            var text = VisibleText(panel);
            Require(RepairButtons(panel).Single().Text.Contains("材料 × 11") &&
                !RepairButtons(panel).Single().Text.Contains("铁轨"), "缺少名称时应使用中性的材料称呼。");
            Require(!text.Contains("fixture:") && !text.Contains("insufficient_goods") && !text.Contains("voxel:"), "缺少展示内容时泄漏了内部标识：" + text);
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Line_trains_show_activity_track_and_position_and_select_the_train()
    {
        var setup = new HostSimulationTests.HostSetup(RailwayWorkUiFixture.Create(), RailwayWorkUiFixture.Content());
        await Run(setup, async ui =>
        {
            var panel = await OpenFromList(ui, 2);
            var trains = Find<VBoxContainer>(panel, "Trains");
            var button = trains.FindChildren("*", "Button", true, false).Cast<Button>().Single();
            Require(button.Text.Contains("运行") && button.Text.Contains("1 号轨") && button.Text.Contains("35")
                && !button.Text.Contains("987654") && !button.Text.Contains("Running"), "列车行没有中文活动、轨号、位置或泄漏内部编号：" + button.Text);
            await _input.Activate(button);
            await _input.Frames(2);
            Require(Find<WorldPanels>(ui, "WorldPanels").IsObjectOpen(new WorldObjectId.Train(new(987654))), "线路内列车按钮没有选中列车。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Map_click_selects_the_line_and_opens_its_inspector()
    {
        var setup = new HostSimulationTests.HostSetup(RailwayWorkUiFixture.Create(), RailwayWorkUiFixture.Content());
        LineScene? opened = null;
        var scenes = new FixedDisplayFactory
        {
            OpenWorldCall = (binding, _) => new SceneResult<IWorldSceneSession>.Success(opened = new(binding))
        };
        await Run(setup, async ui =>
        {
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            await _input.Click(map.GlobalPosition + Midpoint(map, ui.View!, RailwayWorkUiFixture.ActiveEdge));
            await _input.Frames(2);
            Require(Find<PanelContainer>(ui, "LineInspector").IsVisibleInTree(), "宏观地图点选线路没有直接打开检查器。");
            Require(opened?.Target is WorldSceneTarget.Location
            {
                Value: WorldLocation.Edge { Id.Value: RailwayWorkUiFixture.ActiveEdge, DistanceMeters: 200 }
            }, "地图选中线路没有进入线路中点场景。");
        }, scenes);
        setup.Session.AssertComplete();
    }

}
