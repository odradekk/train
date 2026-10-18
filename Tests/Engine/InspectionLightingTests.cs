using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class InspectionLightingTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Switching_presets_restores_the_game_environment_and_sun()
    {
        var view = new WorldSceneView();
        TestScene.AddChild(view);
        try
        {
            var worldEnvironment = view.GetNode<WorldEnvironment>("WorldEnvironment");
            var game = worldEnvironment.Environment;
            var sky = game.Sky;
            var material = (ShaderMaterial)sky.SkyMaterial;
            var sun = view.GetNode<DirectionalLight3D>("Sun");
            var originalSun = (sun.Visible, sun.LightEnergy, sun.LightColor, sun.Rotation);
            var originalAmbient = (game.AmbientLightSource, game.AmbientLightColor, game.AmbientLightEnergy);
            void RequireFogDoesNotCoverSky(string mode) =>
                Require(worldEnvironment.Environment is { FogEnabled: true, FogSkyAffect: 0 },
                    $"{mode}的雾未开启或遮蔽了天空。");
            Require(view.Lighting == InspectionLighting.Game, "初始光照不是正常游戏光照。");
            RequireFogDoesNotCoverSky("正常游戏");
            for (var cycle = 0; cycle < 2; cycle++)
            {
                view.SetLighting(InspectionLighting.Overcast);
                RequireFogDoesNotCoverSky("阴天");
                Require(!sun.Visible && worldEnvironment.Environment.AmbientLightSource == Godot.Environment.AmbientSource.Color,
                    "阴天未关闭太阳或未使用颜色环境光。");
                var overcastSkyColor = ((ProceduralSkyMaterial)worldEnvironment.Environment.Sky.SkyMaterial).SkyTopColor;
                view.SetLighting(InspectionLighting.Night);
                RequireFogDoesNotCoverSky("夜间");
                var nightSky = (ProceduralSkyMaterial)worldEnvironment.Environment.Sky.SkyMaterial;
                Require(!sun.Visible && Brightness(nightSky.SkyTopColor) < Brightness(overcastSkyColor),
                    "夜间没有关闭太阳并压暗天空。");
                var lights = view.GetNode<Node3D>("InspectionWorkLights");
                Require(lights.Visible && lights.GetChildren().OfType<Light3D>().Any(light =>
                    light.Visible && light.LightEnergy > 0 && light.LightColor.IsEqualApprox(new Color("ffd9a0"))),
                    "夜间缺少启用的暖色工作灯。");
                foreach (var mode in new[] { InspectionLighting.Daylight, InspectionLighting.Game })
                {
                    view.SetLighting(mode);
                    RequireFogDoesNotCoverSky(mode == InspectionLighting.Daylight ? "日光" : "恢复游戏");
                    Require(worldEnvironment.Environment == game && game.Sky == sky && sky.SkyMaterial == material,
                        "日光或恢复替换了正常游戏的环境/天空资源。");
                    Require((game.AmbientLightSource, game.AmbientLightColor, game.AmbientLightEnergy) == originalAmbient &&
                        (sun.Visible, sun.LightEnergy, sun.LightColor, sun.Rotation) == originalSun,
                        "反复切换后没有恢复正常天空、环境光或太阳原值。");
                    Require(!lights.Visible, "离开夜间后工作灯仍启用。");
                }
            }
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        finally { view.Release(); }
    }

    [Test]
    public async Task Inactive_selection_is_retained_and_work_lights_follow_focus_instead_of_camera_orbit()
    {
        var view = new WorldSceneView();
        TestScene.AddChild(view);
        try
        {
            var environment = view.GetNode<WorldEnvironment>("WorldEnvironment");
            var game = environment.Environment;
            view.SetLighting(InspectionLighting.Overcast);
            var overcastSkyColor = ((ProceduralSkyMaterial)environment.Environment.Sky.SkyMaterial).SkyTopColor;
            view.SetActive(false);
            view.SetLighting(InspectionLighting.Night);
            Require(environment.Environment is null && view.Lighting == InspectionLighting.Night,
                "隐藏时设置光照重新接入了环境或丢失了模式。");
            view.SetActive(true);
            var night = environment.Environment ?? throw new InvalidOperationException("进入世界没有接入环境。");
            Require(night != game && view.Lighting == InspectionLighting.Night,
                "进入世界没有应用隐藏时选择的光照。");
            Require(night.AmbientLightSource == Godot.Environment.AmbientSource.Color &&
                Brightness(((ProceduralSkyMaterial)night.Sky.SkyMaterial).SkyTopColor) < Brightness(overcastSkyColor),
                "进入世界后的夜间没有使用颜色环境光和较暗天空。");
            view.ShowFrame(Frame(new(Guid.NewGuid())), null, railwayPan: true);
            view.CameraRig.Pan(new(12, 0, -8));
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
            var lights = view.GetNode<Node3D>("InspectionWorkLights");
            Require(lights.Position.IsEqualApprox(view.CameraRig.Focus), "工作灯没有随观察焦点平移。");
            var position = lights.Position;
            var cameraPosition = view.CameraRig.Camera.Position;
            view.CameraRig.Orbit(100, 30);
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(view.CameraRig.Camera.Position != cameraPosition && lights.Position.IsEqualApprox(position),
                "绕焦点旋转相机移动了工作灯。");
            view.SetActive(false);
            Require(environment.Environment is null, "离开世界仍占用环境。");
            view.SetActive(true);
            Require(view.Lighting == InspectionLighting.Night && lights.Visible && environment.Environment == night,
                "重新进入世界没有保留夜间模式。");
        }
        finally { view.Release(); }
    }

    [Test]
    public async Task Lighting_survives_return_and_reentry_without_simulation_or_save_mutation()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var core = new ScriptedCoreSession(WorldSceneFixture.EmptyWorld(new(0, 0)));
        var content = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1));
        var target = new WorldSceneTarget.Location(new WorldLocation.Node(new(1)));
        var factory = new FixedDisplayFactory
        {
            OpenWorldCall = (binding, _) =>
            {
                var id = new SceneSessionId(Guid.NewGuid());
                return new SceneResult<IWorldSceneSession>.Success(new FixedDisplaySession
                {
                    Id = id, Binding = binding,
                    RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty
                        .Add(target, new SceneResult<SceneRequestId>.Success(new(1))),
                    PresentResults = [new SceneResult<ISceneFrame>.Success(Frame(id))]
                });
            }
        };
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => WorldSceneFixture.CreateHost(core, content, logger);
            app.CreateScenes = () => factory;
            await fixture.AddToRoot(app);
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (app.Host!.GetStatus() is null && timeout.Elapsed < TimeSpan.FromSeconds(30))
                await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(app.Host.GetStatus() is not null, "宿主准备超时。");
            app.SetProcess(false);
            var world = app.WorldScenes!;
            var calls = (core.AdvanceCount, core.SubmitCount, core.CaptureCount);
            var save = app.Host.GetSaveStatus();
            var status = app.Host.GetStatus();
            var console = app.GetChildren().OfType<GameConsole>().Single();
            console.SubmitLine("lighting overcast");
            Require(world.View.Lighting == InspectionLighting.Overcast &&
                world.View.GetNode<WorldEnvironment>("WorldEnvironment").Environment is null,
                "地图状态下的控制台命令没有保留光照选择，或提前接入世界环境。");
            world.Open(target);
            world.ProcessFrame(0, false);
            var environment = world.View.GetNode<WorldEnvironment>("WorldEnvironment");
            var overcastSkyColor = ((ProceduralSkyMaterial)environment.Environment.Sky.SkyMaterial).SkyTopColor;
            console.SubmitLine("lighting daylight");
            Require(world.View.Lighting == InspectionLighting.Daylight, "控制台命令没有应用日光模式。");
            var game = environment.Environment;
            foreach (var (argument, mode) in new[] { ("overcast", InspectionLighting.Overcast),
                ("night", InspectionLighting.Night) })
            {
                console.SubmitLine($"lighting {argument}");
                Require(world.View.Lighting == mode, "控制台命令没有应用请求的光照模式。");
            }
            world.ReturnToMap();
            Require(!world.View.Visible && world.View.GetNode<WorldEnvironment>("WorldEnvironment").Environment is null,
                "返回地图没有撤下世界环境。");
            world.Open(target);
            world.ProcessFrame(0, false);
            Require(world.View.Interactive && world.View.Lighting == InspectionLighting.Night &&
                world.View.GetNode<Node3D>("InspectionWorkLights").Visible,
                "重新打开世界丢失了夜间光照。");
            var night = environment.Environment ?? throw new InvalidOperationException("重新打开世界没有接入环境。");
            Require(night != game, "重新打开世界接入了游戏环境而非检查光照环境。");
            Require(night.AmbientLightSource == Godot.Environment.AmbientSource.Color &&
                Brightness(((ProceduralSkyMaterial)night.Sky.SkyMaterial).SkyTopColor) < Brightness(overcastSkyColor),
                "重新打开世界后的夜间没有使用颜色环境光和较暗天空。");
            console.SubmitLine("lighting restore");
            Require(world.View.Lighting == InspectionLighting.Game, "控制台restore没有恢复游戏光照。");
            Require((core.AdvanceCount, core.SubmitCount, core.CaptureCount) == calls &&
                app.Host.GetSaveStatus() == save && app.Host.GetStatus() == status,
                "光照切换或重入推进了模拟、提交命令、捕获存档或改变宿主存档状态。");
            Require(!console.LogEvents.Any(log => log.EventId == "host_consumer_failed"),
                "光照用例的宿主边界未成功接入界面。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Production_app_selects_lighting_for_all_views_before_entering_the_world()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            var world = app.WorldScenes;
            Require(world is not null && !world.View.Visible && app.Host!.GetStatus() is null,
                "生产启动没有在菜单创建尚未进入场景的世界视图。");
            var console = app.GetChildren().OfType<GameConsole>().Single();
            var previous = console.LogEvents.Count;
            console.SubmitLine("lighting night");
            var feedback = console.LogEvents.Skip(previous).ToArray();
            Require(world!.View.Lighting == InspectionLighting.Night && app.Host!.GetStatus() is null &&
                feedback.Any(log => log.EventId == "lighting_mode" && Equals(log.Attributes["mode"], "night")) &&
                !feedback.Any(log => log.EventId is "lighting_unavailable" or "lighting_denied" or "unknown_command"),
                "菜单预选光照没有生效，或错误地建立了核心会话。");
            var editor = app.GetNode<DesignEditorPanel>("GameUi/Root/DesignEditor");
            Require(editor.Scene.View.Lighting == InspectionLighting.Night &&
                app.TemplateScenes!.View.Lighting == InspectionLighting.Night,
                "检查光照没有同步到设计编辑器和模板预览。");
            console.SubmitLine("lighting restore");
            Require(world.View.Lighting == InspectionLighting.Game &&
                editor.Scene.View.Lighting == InspectionLighting.Game &&
                app.TemplateScenes.View.Lighting == InspectionLighting.Game,
                "恢复游戏光照没有同步到所有视图。");
        }
        finally { await fixture.Cleanup(); }
    }

    private static float Brightness(Color color) => color.R + color.G + color.B;
    private static FixedSceneFrame Frame(SceneSessionId id) => new()
    {
        Update = new(null, new(id, new(1), new(1), 1), default, [], [], new([], []), []),
        TerrainHeightResult = new Lookup<double>.Missing()
    };
}
