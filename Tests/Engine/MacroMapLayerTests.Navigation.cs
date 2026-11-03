using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class MacroMapLayerTests
{
    [Test]
    public async Task Node_and_combat_tooltips_show_chinese_names_states_and_actions()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var map = fixture.AutoFree(new MacroMap { Theme = GD.Load<Theme>("res://Ui/Theme.tres") });
            map.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            await fixture.AddToRoot(map);
            map.SetView(WorldListUiFixture.View(), WorldListUiFixture.Content());
            foreach (var (id, name, status) in new (WorldObjectId, string, string)[]
            {
                (new WorldObjectId.Node(new(2)), "平原", "未建立聚居地"),
                (new WorldObjectId.Node(new(3)), "青川", "自有"),
                (new WorldObjectId.Node(new(4)), "南渡", "非敌对"),
                (new WorldObjectId.Node(new(5)), "赤岭", "敌对"),
                (new WorldObjectId.Node(new(6)), "石甲兽巢穴", "生物聚落"),
                (new WorldObjectId.Combat(new(1)), "战斗", "交战中")
            })
            {
                var point = map.ObjectPoint(id)!.Value;
                Require(map.HitTest(point) == id, $"悬停样本未命中预期对象：{id}。");
                var tooltip = map._GetTooltip(point);
                var parts = tooltip.Split(['\n', '·'], StringSplitOptions.TrimEntries);
                Require(tooltip.Contains(name) && parts.Contains(status) &&
                    tooltip.Contains("单击") && tooltip.Contains("查看") &&
                    tooltip.Contains("双击") && tooltip.Contains("进入场景"),
                    $"悬停缺少中文名称、状态或操作提示：{id}，实际 {tooltip}。");
            }
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Edge_tooltip_shows_the_name_and_full_line_status_instead_of_a_midpoint_distance()
    {
        var fixture = new Fixture(TestScene.GetTree());
        using var dispatcher = new Train.App.Assets.GodotMainThreadDispatcher();
        var mods = ProjectSettings.GlobalizePath("user://edge-tooltip-mods");
        using var content = LoadContent(dispatcher, mods);
        try
        {
            var map = fixture.AutoFree(new MacroMap { Theme = GD.Load<Theme>("res://Ui/Theme.tres") });
            map.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            await fixture.AddToRoot(map);
            var view = UiWorldFixture.InformationLayers();
            var edge = view.World.Edges[0];
            foreach (var (value, status) in new (EdgeMapItem, string)[]
            {
                (edge, "铁轨 1/1 · 长度 200 米"),
                (edge with { Tracks = 0 }, "未建成 · 长度 200 米"),
                (edge with { Breaks = [new(new(1), 1, 50)] }, "铁轨 1/1 · 长度 200 米 · 1 处断点")
            })
            {
                map.SetView(new FixedCoreReadView(default, HostCoreScenario.Input)
                { World = view.World with { Edges = [value] } }, content.View);
                var tooltip = map._GetTooltip(map.ObjectPoint(new WorldObjectId.Edge(edge.Id))!.Value);
                Require(tooltip.Split('\n')[0].Contains('—') && !tooltip.Split('\n')[0].Contains("米") &&
                    tooltip.Contains(status) && !tooltip.Contains("100 米"),
                    "线路悬停必须显示线路名和整条线路的状态，不能把中点距离当作长度。");
            }
        }
        finally { await fixture.Cleanup(); Directory.Delete(mods); }
    }

    [Test]
    public async Task Parked_trains_have_independent_click_targets_and_named_status_tooltips()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var input = new UiTestInput(TestScene);
        var scale = tree.Root.ContentScaleFactor;
        using var dispatcher = new Train.App.Assets.GodotMainThreadDispatcher();
        var mods = ProjectSettings.GlobalizePath("user://train-map-mods");
        using var content = LoadContent(dispatcher, mods);
        try
        {
            tree.Root.ContentScaleFactor = 1;
            var map = fixture.AutoFree(new MacroMap { Theme = GD.Load<Theme>("res://Ui/Theme.tres") });
            map.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            await fixture.AddToRoot(map);
            var original = UiWorldFixture.InformationLayers();
            var trains = Enumerable.Range(100, 40).Select(id => new TrainMapItem(new((ulong)id),
                original.World.Trains[0].Position, TrainActivity.Waiting)).ToImmutableArray();
            var view = new FixedCoreReadView(default, HostCoreScenario.Input) { World = original.World with { Trains = trains } };
            map.SetView(view, content.View);
            var selected = new List<WorldObjectId>();
            var activated = new List<WorldObjectId>();
            map.ObjectClicked += selected.Add;
            map.ObjectActivated += activated.Add;
            foreach (var train in trains)
            {
                var id = new WorldObjectId.Train(train.Id);
                var point = map.ObjectPoint(id)!.Value;
                Require(map.HitTest(point) == id,
                    "同节点的每列列车都必须有不被其他标记覆盖的点击区域。");
                var tooltip = map._GetTooltip(point);
                Require(tooltip.StartsWith($"列车 {train.Id.Value}\n") && tooltip.Contains("等待") &&
                    tooltip.Contains("单击查看，双击进入场景"), "悬停必须显示该列车的名称、中文状态及操作提示。");
                await input.Click(map.GlobalPosition + point);
                Require(selected.LastOrDefault() == id && activated.Count == 0, "实际单击必须只选中目标列车。");
            }
            var points = trains.ToDictionary(train => train.Id, train => map.ObjectPoint(new WorldObjectId.Train(train.Id)));
            map.SetView(new FixedCoreReadView(default, HostCoreScenario.Input)
            { World = view.World with { Trains = trains.Reverse().ToImmutableArray() } }, content.View);
            Require(trains.All(train => map.ObjectPoint(new WorldObjectId.Train(train.Id)) == points[train.Id]),
                "摘要顺序变化不能改变同节点列车的散开位置。");
            Require(map._GetTooltip(new(4, 4)) == "", "地图空白处不能显示对象提示。");
        }
        finally
        {
            await fixture.Cleanup();
            tree.Root.ContentScaleFactor = scale;
            Directory.Delete(mods);
        }
    }
}
