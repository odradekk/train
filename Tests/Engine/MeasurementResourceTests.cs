using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Assets;
using Train.App.Measurement;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Rendering;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class MeasurementResourceTests(Node testScene) : TestClass(testScene)
{
    private static readonly WorldSceneTarget Target = new WorldSceneTarget.Location(new WorldLocation.Node(new(1)));

    [Test]
    public async Task Switching_displays_detaches_the_previous_resource_callback()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await Ready(fixture);
            using var measurement = Measure(app);
            using var dispatcher = new GodotMainThreadDispatcher();
            using var cache = new AssetResourceCache(dispatcher);
            using var first = new SceneDisplay(cache, new FixedContentView());
            using var second = new SceneDisplay(cache, new FixedContentView());
            measurement.ObserveSceneResources(first);
            Require(first.Measure is not null, "活动显示器未接入资源测量。");
            measurement.ObserveSceneResources(second);
            Require(first.Measure is null && second.Measure is not null,
                "切换显示器后，应解绑旧资源回调并测量新显示器。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Returning_to_map_detaches_the_closed_world_display()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await Ready(fixture);
            using var measurement = Measure(app);
            app.WorldScenes!.Open(Target);
            var display = app.WorldScenes.Display!;
            measurement.ObserveSceneResources(display);
            app.WorldScenes.ReturnToMap();
            Require(app.WorldScenes.Display is null, "本例必须先关闭世界显示器。");
            DrawCompleted();
            Require(display.Measure is null, "返回地图后，测量器仍订阅已关闭的世界显示器。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Disposing_measurement_detaches_the_active_resource_callback()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await Ready(fixture);
            using var measurement = Measure(app);
            using var dispatcher = new GodotMainThreadDispatcher();
            using var cache = new AssetResourceCache(dispatcher);
            using var display = new SceneDisplay(cache, new FixedContentView());
            measurement.ObserveSceneResources(display);
            measurement.Dispose();
            Require(display.Measure is null, "结束测量后应解绑活动资源回调。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Closed_display_and_content_can_be_collected_while_measurement_continues()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await Ready(fixture);
            using var measurement = Measure(app);
            using var dispatcher = new GodotMainThreadDispatcher();
            using var cache = new AssetResourceCache(dispatcher);
            var old = ObserveAndClose(measurement, cache);
            DrawCompleted();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            Require(!old.Display.IsAlive && !old.Content.IsAlive,
                "测量仍运行时，帧末观察者不应保留已关闭的显示器或它的内容视图。");
            GC.KeepAlive(measurement);
        }
        finally { await fixture.Cleanup(); }
    }

    // headless 不绘制像素；经同一个真实 Godot 信号验证帧末观察者，不声明窗口测量通过。
    private static void DrawCompleted() => RenderingServer.Singleton.EmitSignal(RenderingServer.SignalName.FramePostDraw);

    // 避免 Debug JIT 将关闭前的局部强引用留在执行 GC 的测试栈上。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Display, WeakReference Content) ObserveAndClose(MeasurementRun measurement, AssetResourceCache cache)
    {
        var content = new FixedContentView();
        using var display = new SceneDisplay(cache, content);
        measurement.ObserveSceneResources(display);
        return (new(display), new(content));
    }

    private static MeasurementRun Measure(Train.App.App app)
    {
        var output = Path.Combine(OS.GetUserDataDir(), $"measurement-resources-{Guid.NewGuid():N}.json");
        var identity = output + ".identity";
        File.WriteAllText(identity, "{}");
        try
        {
            return new(app, ["--measure=scene-soak", "--measure-input",
                ProjectSettings.GlobalizePath("res://examples/standard-world/save.json"),
                "--measure-output", output, "--measure-identity", identity]);
        }
        finally { File.Delete(identity); }
    }

    private async Task<Train.App.App> Ready(Fixture fixture)
    {
        var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
        app.CreateHost = logger => WorldSceneFixture.CreateHost(
            new(WorldSceneFixture.EmptyWorld(default)),
            new(HostContentScenario.CreateProjection(HostCoreScenario.Input)), logger);
        app.CreateScenes = () => new FixedDisplayFactory
        {
            OpenWorldCall = (binding, _) => new SceneResult<IWorldSceneSession>.Success(new FixedDisplaySession
            {
                Id = new(Guid.NewGuid()), Binding = binding,
                RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty.Add(Target,
                    new SceneResult<SceneRequestId>.Success(new(1)))
            })
        };
        await fixture.AddToRoot(app);
        await FramesUntil(TestScene, () => app.Host!.GetStatus() is not null);
        app.SetProcess(false);
        return app;
    }
}
