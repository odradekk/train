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
                Require(map.HitTest(point) == id && map.HitTest(point + new Vector2(13, 6)) == id,
                    "同节点的每列列车都必须有不被其他标记覆盖的点击区域。");
                var tooltip = map._GetTooltip(point);
                Require(tooltip.Contains("列车") && tooltip.Contains("等待") && tooltip.Contains("单击查看，双击进入场景") &&
                    !tooltip.Contains(train.Id.Value.ToString()), "悬停必须显示名称、中文状态及操作提示，不显示内部编号。");
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
