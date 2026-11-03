using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Logging;
using Train.App.Notifications;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class CombatUiTests
{
    [Test]
    public async Task Combat_marker_inspects_before_double_click_opens_the_combat_edge_location()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var host = new CombatHost(rich: true);
        var scale = TestScene.GetTree().Root.ContentScaleFactor;
        FixedDisplaySession? scene = null;
        var target = new WorldSceneTarget.Location(new WorldLocation.Edge(new(1), 100));
        Train.App.App? app = null;
        try
        {
            UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
            TestScene.GetTree().Root.ContentScaleFactor = 1;
            app = await Boot(fixture, (_, logger) => host.Create(logger), () => new FixedDisplayFactory
            {
                OpenWorldCall = (binding, _) =>
                {
                    scene = new FixedDisplaySession
                    {
                        Id = new(Guid.Parse("00000000-0000-0000-0000-000000320354")),
                        Binding = binding,
                        RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty.Add(
                            target, new SceneResult<SceneRequestId>.Success(new(1))),
                        PresentResults = [new SceneResult<ISceneFrame>.Failure([
                            new("combat_probe", SceneStage.Present, null, null, null, null, "只记录战斗地点。")])]
                    };
                    return new SceneResult<IWorldSceneSession>.Success(scene);
                }
            });
            var map = app.GetNode<MacroMap>("GameUi/Root/World/MacroMap");
            await _input.WaitUntil(() =>
            {
                var start = map.MapToScreen(new(250, 300));
                var end = map.MapToScreen(new(750, 300));
                return map.IsVisibleInTree() && map.Size.X > 100 && start.DistanceTo(end) > 80;
            });
            var start = map.MapToScreen(new(250, 300));
            var end = map.MapToScreen(new(750, 300));
            var point = map.GlobalPosition + start.Lerp(end, 100f / 600f);
            await _input.Click(point);
            var shell = (GameShell)app.GetNode<GameUi>("GameUi").World;
            Require(scene is null && shell.SelectedObject == new WorldObjectId.Combat(new(5)) &&
                shell.InspectorContent is CombatPanel, "单击战斗标记必须只显示现有战斗检查器。");
            await _input.DoubleClick(point);
            Require(scene?.RequestCalls.Count == 1 && scene.RequestCalls[0] == target,
                $"战斗标记没有打开线路 100 m：count={scene?.RequestCalls.Count}，point={point}，start={start}，end={end}。");
        }
        finally
        {
            TestScene.GetTree().Root.ContentScaleFactor = scale;
            await Cleanup(fixture, app);
        }
    }

}
