using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class MacroMapLayerTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Independent_layers_map_resources_and_platform_queues_and_refresh_only_on_demand()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var input = new UiTestInput(TestScene);
        var scale = tree.Root.ContentScaleFactor;
        using var dispatcher = new GodotMainThreadDispatcher();
        var mods = ProjectSettings.GlobalizePath("user://map-layer-mods");
        Directory.CreateDirectory(mods);
        var files = new ContentFiles(dispatcher, new Dictionary<ContentRoot, ContentRootLocation>
        {
            [new("builtin")] = new ContentRootLocation.Disk(ProjectSettings.GlobalizePath("res://builtin/core")),
            [new("mods")] = new ContentRootLocation.Disk(mods)
        });
        var loaded = Train.Content.ContentFactory.Create().PrepareLoad(new(new(new("builtin"), new("mods"), null,
            "0.2.0", []), new ContentLoadPurpose.Initial()), files, new GodotAssetInspector(dispatcher));
        Require(loaded is ContentResult<IContentCandidate>.Success, $"信息层测试内容加载失败：{loaded}。");
        using var content = ((ContentResult<IContentCandidate>.Success)loaded).Value;
        try
        {
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            var map = fixture.AutoFree(new MacroMap { Theme = GD.Load<Theme>("res://Ui/Theme.tres") });
            map.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            map.DisplayFailed += message => throw new InvalidOperationException(message);
            await fixture.AddToRoot(map);
            map.PositionLayerPanel(52);
            var view = UiWorldFixture.InformationLayers();
            var unconfigured = new FixedCoreReadView(default, HostCoreScenario.Input) { World = view.World };
            map.SetView(unconfigured, content.View);
            map.RefreshPlatformQueues(1);
            var resources = map.GetNode<CheckBox>("InformationLayers/Body/Resources");
            var platforms = map.GetNode<CheckBox>("InformationLayers/Body/Platforms");
            Require(!resources.ButtonPressed && !platforms.ButtonPressed &&
                map.GetNode<CheckBox>("InformationLayers/Body/Creatures").Disabled,
                "两层必须默认关闭，世界生物层必须保持禁用。");
            await UiCapture.Capture(TestScene, "layers-off");

            await input.Activate(resources);
            map.RefreshPlatformQueues(1);
            Require(resources.ButtonPressed && !platforms.ButtonPressed, "资源开关改变了站台开关。");
            var ore = view.World.Nodes[0].Resources[0];
            var timber = view.World.Nodes[0].Resources[1];
            Require(map.ResourceDisplay(ore).Text == "铁矿石：100" && map.ResourceDisplay(timber).Text == "原木：0" &&
                map.ResourceDisplay(ore).Icon == ((ContentLookup<GoodPresentation>.Found)content.View.FindGood(ore.Good)).Value.Display.Icon,
                "资源标注没有读取公开的货物名称、图标或储量，或丢失了零储量再生资源。");
            await UiCapture.Capture(TestScene, "layers-resources");

            map.SetView(view, content.View);
            await input.Activate(platforms);
            map.RefreshPlatformQueues(0);
            Require(resources.ButtonPressed && platforms.ButtonPressed &&
                map.PlatformText(view.World.Nodes[0]) == "站台 1/2 · 排队 1" &&
                map.PlatformText(view.World.Nodes[1]) == "站台 0/0 · 排队 1",
                "站台标注未消费核心占用数，或错误统计了普通停车、已获站台及线路上的列车。");
            await UiCapture.Capture(TestScene, "layers-both");
            await input.Activate(resources);
            Require(!resources.ButtonPressed && platforms.ButtonPressed, "关闭资源改变了站台开关。");
            await UiCapture.Capture(TestScene, "layers-platforms");

            var changed = UiWorldFixture.InformationLayers(changed: true);
            map.SetView(changed, content.View);
            Require(map.ResourceDisplay(changed.World.Nodes[0].Resources[0]).Text == "铁矿石：60" &&
                map.ResourceDisplay(changed.World.Nodes[0].Resources[1]).Text == "原木：12" &&
                map.PlatformText(changed.World.Nodes[0]) == "站台 2/2 · 排队 1",
                "摘要更新没有立即更新储量与占用，或 SetView 提前执行了排队查询。");
            map.RefreshPlatformQueues(.49);
            Require(map.PlatformText(changed.World.Nodes[0]).EndsWith("排队 1"), "排队查询没有遵守 0.5 秒节流。");
            map.RefreshPlatformQueues(.02);
            Require(map.PlatformText(changed.World.Nodes[0]) == "站台 2/2 · 排队 0", "新快照的排队队龄未刷新。");
            map.RefreshPlatformQueues(1);
            await input.Activate(resources);
            await UiCapture.Capture(TestScene, "layers-changed");

            map.Hide();
            map.SetView(unconfigured, content.View);
            map.RefreshPlatformQueues(1);
            map.Show();
            await input.Activate(platforms);
            map.RefreshPlatformQueues(1);
            Require(resources.ButtonPressed && !platforms.ButtonPressed, "隐藏地图重置了图层开关。");
            await input.Activate(resources);
            map.SetView(view, content.View, resetCamera: true);
            await UiCapture.Capture(TestScene, "layers-off-again");
        }
        finally
        {
            await fixture.Cleanup();
            tree.Root.ContentScaleFactor = scale;
            Directory.Delete(mods);
        }
    }
}
