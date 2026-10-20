using System.Collections.Immutable;
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
        using var content = LoadContent(dispatcher, mods);
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
            var creatures = map.GetNode<CheckBox>("InformationLayers/Body/Creatures");
            var legends = map.GetNode<VBoxContainer>("InformationLayers/Body").GetChildren().OfType<Label>()
                .Select(label => label.Text).ToArray();
            Require(!resources.ButtonPressed && !platforms.ButtonPressed && !creatures.ButtonPressed && !creatures.Disabled,
                "三个信息层必须默认关闭，世界生物层必须可以打开。");
            Require(legends is ["地图信息层", "货物图标 · 名称与剩余储量", "站台 占用/总数 · 排队列车数", "群活动 · 聚落类型与群数"],
                "信息层图例被改动。");
            await UiCapture.Capture(TestScene, "layers-off");

            await input.Activate(resources);
            map.RefreshPlatformQueues(1);
            Require(resources.ButtonPressed && !platforms.ButtonPressed, "资源开关改变了站台开关。");
            var ore = view.World.Nodes[0].Resources[0];
            var timber = view.World.Nodes[0].Resources[1];
            Require(map.ResourceText(ore) == "铁矿石：100" && map.ResourceText(timber) == "原木：0" &&
                map.ResourceIcon(ore) == ((ContentLookup<GoodPresentation>.Found)content.View.FindGood(ore.Good)).Value.Display.Icon,
                "资源标注没有读取公开的货物名称、图标或储量，或丢失了零储量再生资源。");
            await UiCapture.Capture(TestScene, "layers-resources");

            map.SetView(view, content.View);
            await input.Activate(platforms);
            map.RefreshPlatformQueues(0);
            Require(resources.ButtonPressed && platforms.ButtonPressed &&
                map.PlatformText(view.World.Nodes[0]) == "站台 1/2 · 排队 1" &&
                map.PlatformText(view.World.Nodes[1]) == "站台 0/0 · 排队 1",
                "站台标注未消费核心占用数，或错误统计了普通停车、已获站台及线路上的列车。");
            Require(map.HitTest(map.MapToScreen(new(250, 330))) == new WorldObjectId.Train(new(10)),
                "用于检查标注叠放的线路列车没有位于预期位置。");
            await UiCapture.Capture(TestScene, "layers-both");
            await input.Activate(resources);
            Require(!resources.ButtonPressed && platforms.ButtonPressed, "关闭资源改变了站台开关。");
            await UiCapture.Capture(TestScene, "layers-platforms");

            var changed = UiWorldFixture.InformationLayers(changed: true);
            map.SetView(changed, content.View);
            Require(map.ResourceText(changed.World.Nodes[0].Resources[0]) == "铁矿石：60" &&
                map.ResourceText(changed.World.Nodes[0].Resources[1]) == "原木：12" &&
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

    [Test]
    public async Task Creature_layer_maps_activity_and_lair_groups_without_changing_hits_or_camera()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var input = new UiTestInput(TestScene);
        var scale = tree.Root.ContentScaleFactor;
        using var dispatcher = new GodotMainThreadDispatcher();
        var mods = ProjectSettings.GlobalizePath("user://creature-layer-mods");
        using var content = LoadContent(dispatcher, mods);
        try
        {
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            var map = fixture.AutoFree(new MacroMap { Theme = GD.Load<Theme>("res://Ui/Theme.tres") });
            map.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            var failures = new List<string>();
            map.DisplayFailed += failures.Add;
            await fixture.AddToRoot(map);
            map.PositionLayerPanel(52);
            var scenario = UiWorldFixture.CreatureLayer();
            map.SetView(scenario.View, content.View);
            await input.Frames(2);
            Require(failures.Count == 0, failures.FirstOrDefault() ?? "");
            var offset = map.Offset;
            var zoom = map.Zoom;
            var creatures = map.GetNode<CheckBox>("InformationLayers/Body/Creatures");
            var resources = map.GetNode<CheckBox>("InformationLayers/Body/Resources");
            var platforms = map.GetNode<CheckBox>("InformationLayers/Body/Platforms");
            Vector2[] samples =
            [
                map.MapToScreen(scenario.Nest.Position),
                map.MapToScreen(scenario.Nest.Position) + new Vector2(0, 15),
                (map.MapToScreen(scenario.Nest.Position) + map.MapToScreen(scenario.Hive.Position)) / 2,
                new(4, 4)
            ];
            var before = samples.Select(map.HitTest).ToArray();
            await UiCapture.Capture(TestScene, "creatures-off");
            await input.Activate(creatures);
            Require(creatures.ButtonPressed && !resources.ButtonPressed && !platforms.ButtonPressed &&
                map.Offset == offset && map.Zoom == zoom, "打开世界生物层改变了其他开关或相机。");
            var after = samples.Select(map.HitTest).ToArray();
            for (var index = 0; index < before.Length; index++)
                Require(before[index] == after[index], "世界生物层改变了拾取。");
            Require(before[0] == new WorldObjectId.Node(scenario.Nest.Id) && before[2] == new WorldObjectId.Edge(scenario.Edge.Id) &&
                before[3] is null, "采样点没有落在预期的节点、线路中点和空白处。");
            foreach (var (activity, text) in new (CreatureActivity Activity, string Text)[]
            {
                (CreatureActivity.Guarding, "驻守"), (CreatureActivity.Wandering, "游荡"),
                (CreatureActivity.RaidingSettlement, "袭击聚居地"), (CreatureActivity.RaidingRailway, "袭击线路"),
                (CreatureActivity.Combat, "战斗"), (CreatureActivity.Retreating, "撤退")
            })
                Require(map.GroupText(scenario.Groups.Single(group => group.Activity == activity)) == text,
                    $"活动 {activity} 的标注不是「{text}」。");
            var nestName = ((ContentLookup<LairAppearanceProjection>.Found)content.View.FindLair(new("core:small-nest"))).Value.Display.Text.Name;
            var hiveName = ((ContentLookup<LairAppearanceProjection>.Found)content.View.FindLair(new("core:large-hive"))).Value.Display.Text.Name;
            Require(nestName == "巢穴" && hiveName == "大型巢群" &&
                map.LairText(scenario.Nest) == "巢穴 · 3 群" && map.LairText(scenario.Hive) == "大型巢群 · 2 群",
                "聚落标注没有使用内容显示名和 FindLair 的群数。");
            await UiCapture.Capture(TestScene, "creatures-on");
            await input.Activate(resources);
            await input.Activate(platforms);
            Require(creatures.ButtonPressed && resources.ButtonPressed && platforms.ButtonPressed &&
                map.Offset == offset && map.Zoom == zoom, "另外两层改变了世界生物开关或相机。");
            await UiCapture.Capture(TestScene, "creatures-all");

            var emptied = scenario.Nest with { Occupant = new NodeOccupant.Empty() };
            var remaining = scenario.Groups.Where(group => group.Location is not WorldLocation.Node node || node.Id != scenario.Nest.Id)
                .ToImmutableArray();
            map.SetView(new FixedCoreReadView(default, HostCoreScenario.Input)
            {
                World = scenario.View.World with
                {
                    Nodes = [emptied, scenario.Hive], CreatureGroups = remaining
                },
                Lairs = new Dictionary<LairId, Lookup<LairView>> { [new(2)] = scenario.View.Lairs[new(2)] }
            }, content.View);
            await input.Frames(2);
            Require(failures.Count == 0 && map.LairText(emptied) is null && map.LairText(scenario.Hive) == "大型巢群 · 2 群" &&
                map.Offset == offset && map.Zoom == zoom,
                "清剿后的摘要绘制失败，或聚落标注和相机没有跟着更新。");
            await UiCapture.Capture(TestScene, "creatures-cleared");

            var unnamed = scenario.Hive with { Occupant = new NodeOccupant.Lair(new(9)) };
            map.SetView(new FixedCoreReadView(default, HostCoreScenario.Input)
            {
                World = scenario.View.World with { Nodes = [scenario.Nest, unnamed], CreatureGroups = scenario.Groups },
                Lairs = new Dictionary<LairId, Lookup<LairView>>
                {
                    [new(1)] = scenario.View.Lairs[new(1)],
                    [new(9)] = new Lookup<LairView>.Missing()
                }
            }, content.View);
            Require(map.LairText(unnamed) == "?" && failures.Count == 1 && failures[0] == "聚落 9 缺少可显示的类型名称。",
                "缺少聚落资料时没有显示问号或没有报告一次失败。");
            await input.Frames(2);
            Require(failures.Count == 1, "绘制重复报告了同一次缺失。");
        }
        finally
        {
            await fixture.Cleanup();
            tree.Root.ContentScaleFactor = scale;
            Directory.Delete(mods);
        }
    }

    private static IContentCandidate LoadContent(GodotMainThreadDispatcher dispatcher, string mods)
    {
        Directory.CreateDirectory(mods);
        var files = new ContentFiles(dispatcher, new Dictionary<ContentRoot, ContentRootLocation>
        {
            [new("builtin")] = new ContentRootLocation.Disk(ProjectSettings.GlobalizePath("res://builtin/core")),
            [new("mods")] = new ContentRootLocation.Disk(mods)
        });
        var loaded = Train.Content.ContentFactory.Create().PrepareLoad(new(new(new("builtin"), new("mods"), null,
            "0.2.0", []), new ContentLoadPurpose.Initial()), files, new GodotAssetInspector(dispatcher));
        Require(loaded is ContentResult<IContentCandidate>.Success, $"信息层测试内容加载失败：{loaded}。");
        return ((ContentResult<IContentCandidate>.Success)loaded).Value;
    }
}
