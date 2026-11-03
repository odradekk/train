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
    public async Task Double_click_keeps_the_first_train_when_it_moves_off_the_second_press()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var input = new UiTestInput(TestScene);
        using var dispatcher = new Train.App.Assets.GodotMainThreadDispatcher();
        var mods = ProjectSettings.GlobalizePath("user://moving-double-click-mods");
        using var content = LoadContent(dispatcher, mods);
        try
        {
            var map = fixture.AutoFree(new MacroMap { Theme = GD.Load<Theme>("res://Ui/Theme.tres") });
            map.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            await fixture.AddToRoot(map);
            var original = UiWorldFixture.InformationLayers();
            var edge = original.World.Edges[0];
            var train = new TrainMapItem(new(10), new TrainPosition.Edge(edge.Id, 0, TravelDirection.Forward, 100), TrainActivity.Running);
            map.SetView(new FixedCoreReadView(default, HostCoreScenario.Input)
            { World = original.World with { Trains = [train] } }, content.View);
            WorldObjectId? selected = null, activated = null;
            map.ObjectClicked += id => selected = id;
            map.ObjectActivated += id => activated = id;
            var id = new WorldObjectId.Train(train.Id);
            var point = map.GlobalPosition + map.ObjectPoint(id)!.Value;
            await input.Click(point);
            Require(selected == id && activated is null, "第一次按下必须只选中正在运行的列车。");
            map.SetView(new FixedCoreReadView(default, HostCoreScenario.Input)
            { World = original.World with { Trains = [train with { Position = new TrainPosition.Edge(edge.Id, 0, TravelDirection.Forward, 170) }] } }, content.View);
            Require(map.HitTest(point - map.GlobalPosition) == new WorldObjectId.Edge(edge.Id),
                "列车移走后，第二次按下的位置必须实际命中下面的线路。");
            await input.DoubleClick(point);
            Require(activated == id && selected == id,
                "双击必须进入第一次选中的列车，不能进入第二次按下时露出的线路。");
        }
        finally { await fixture.Cleanup(); Directory.Delete(mods); }
    }

    [Test]
    public async Task Unmatched_double_click_press_only_selects_the_new_hit()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var input = new UiTestInput(TestScene);
        using var dispatcher = new Train.App.Assets.GodotMainThreadDispatcher();
        var mods = ProjectSettings.GlobalizePath("user://unmatched-double-click-mods");
        using var content = LoadContent(dispatcher, mods);
        try
        {
            var map = fixture.AutoFree(new MacroMap { Theme = GD.Load<Theme>("res://Ui/Theme.tres") });
            map.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            await fixture.AddToRoot(map);
            var original = UiWorldFixture.InformationLayers();
            map.SetView(new FixedCoreReadView(default, HostCoreScenario.Input)
            { World = original.World with { Trains = [] } }, content.View);
            var id = new WorldObjectId.Edge(original.World.Edges[0].Id);
            var point = map.GlobalPosition + map.ObjectPoint(id)!.Value;
            map.SetView(new FixedCoreReadView(default, HostCoreScenario.Input)
            { World = original.World with { Trains = [], Edges = [] } }, content.View);
            Require(map.HitTest(point - map.GlobalPosition) is null, "第一次按下的位置必须是空白。");
            WorldObjectId? selected = null, activated = null;
            map.ObjectClicked += value => selected = value;
            map.ObjectActivated += value => activated = value;
            await input.Click(point);
            map.SetView(new FixedCoreReadView(default, HostCoreScenario.Input)
            { World = original.World with { Trains = [] } }, content.View);
            Require(map.HitTest(point - map.GlobalPosition) == id, "第二次按下的位置必须已有线路。");
            await input.DoubleClick(point);
            Require(selected == id && activated is null, "没有前一次地图选中的双击按下必须只选中，不能直接进入场景。");
        }
        finally { await fixture.Cleanup(); Directory.Delete(mods); }
    }
}
