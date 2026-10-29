using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class WorldSceneFactoryTests(Node testScene) : TestClass(testScene)
{
    private static readonly WorldSceneTarget Target = new WorldSceneTarget.Location(new WorldLocation.Node(new(1)));

    [Test]
    public async Task Restoring_a_run_creates_a_separate_world_factory()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup();
        var factories = new FactorySource();
        try
        {
            var app = await Ready(fixture, setup, factories);
            Require(factories.Created.Count == 1, "启动只应创建供编辑器、作者工具和图标使用的工厂。");
            app.WorldScenes!.Open(Target);
            Require(factories.Created.Count == 2 && OpenCount(factories.Created[0]) == 0 &&
                OpenCount(factories.Created[1]) == 1, "首局世界必须使用独立工厂，不能借用预览工厂。");
            var firstWorld = (FixedDisplayFactory)factories.Created[1].Target!;
            var previous = app.Host!.GetStatus()!.Binding;
            Restore(app.Host);
            await Pump(app.Host);
            Require(app.Host.GetStatus()!.Binding != previous, "恢复必须建立新运行会话。");
            app.WorldScenes.Open(Target);
            Require(factories.Created.Count == 3 && OpenCount(factories.Created[2]) == 1 &&
                firstWorld.OpenCalls.Count == 1, "新局必须创建新世界工厂，不能复用上一局工厂。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Content_generation_change_keeps_the_world_factory()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new Setup();
        var factories = new FactorySource();
        try
        {
            var app = await Ready(fixture, setup, factories);
            app.WorldScenes!.Open(Target);
            Require(factories.Created.Count == 2, "首局应有一个预览工厂和一个世界工厂。");
            var previous = app.Host!.GetStatus()!.Binding;
            setup.Reload = new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(
                Content(2), changes: ContentChange.Presentation));
            app.Host.BeginReload(CommandSource.Console);
            await Pump(app.Host);
            Require(app.Host.GetStatus()!.Binding == previous && app.Host.Content!.Generation == new ContentGeneration(2),
                "本例必须只更换同一局的内容代次。");
            Require(factories.Created.Count == 2 && OpenCount(factories.Created[1]) == 2,
                "内容代次改变应重新绑定场景，但不得调用创建函数或更换世界工厂。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Closed_run_factory_can_be_collected_while_app_remains_alive()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var factories = new FactorySource();
        try
        {
            var app = await Ready(fixture, new Setup(), factories);
            var previous = OpenRunAndRestore(app, factories);
            await Pump(app.Host!);
            app.WorldScenes!.ProcessFrame(0, false);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            Require(!previous.IsAlive, "旧局关闭后，世界控制器、预览消费者或创建函数仍保留旧世界工厂。");
            GC.KeepAlive(app);
            GC.KeepAlive(factories);
        }
        finally { await fixture.Cleanup(); }
    }

    // 避免 Debug JIT 将持有工厂的局部变量保留在执行回收的测试栈中。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenRunAndRestore(Train.App.App app, FactorySource factories)
    {
        app.WorldScenes!.Open(Target);
        var previous = factories.Created[^1];
        Restore(app.Host!);
        return previous;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int OpenCount(WeakReference reference) => ((FixedDisplayFactory)reference.Target!).OpenCalls.Count;

    private static void Restore(HostCoordinator host) => host.BeginRestore("opaque", "opaque",
        new(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1));

    private async Task<Train.App.App> Ready(Fixture fixture, Setup setup, FactorySource factories)
    {
        var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
        app.CreateHost = setup.Create;
        app.CreateScenes = factories.Create;
        await fixture.AddToRoot(app);
        await Until(() => app.Host!.GetStatus() is not null);
        app.SetProcess(false);
        return app;
    }

    private Task Pump(HostCoordinator host) => Until(() => { host.ProcessFrame(0); return !host.IsBusy; });

    private async Task Until(Func<bool> complete)
    {
        var limit = System.Diagnostics.Stopwatch.StartNew();
        while (!complete() && limit.Elapsed < TimeSpan.FromSeconds(30))
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
        Require(complete(), "挂起保护超时：宿主准备未完成。");
    }

    private static FixedContentView Content(ulong generation) =>
        new(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(generation));

    private sealed class Setup
    {
        private readonly ScriptedCoreSession session = new(WorldSceneFixture.EmptyWorld(new(0, 0)));
        internal ContentResult<IContentCandidate>? Reload;
        internal HostCoordinator Create(TrainLogger logger) => WorldSceneFixture.CreateHost(session, Content(1), logger,
            () => Reload ?? throw new InvalidOperationException("UNCONFIGURED: 重载结果。"),
            RestoredSession);

        private static ScriptedCoreSession RestoredSession()
        {
            var restored = new ScriptedCoreSession(WorldSceneFixture.EmptyWorld(new(0, 0)));
            restored.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(new FixedCoreCapture(
                new(new(0, 0), "fixed", "fixed"), HostCoreScenario.References, "opaque", HostCoreScenario.Input, new(1))));
            return restored;
        }
    }

    private sealed class FactorySource
    {
        // 创建源只保存弱引用，不能替宿主保留上一局工厂。
        internal List<WeakReference> Created { get; } = [];

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal IScenesFactory Create()
        {
            var factory = new FixedDisplayFactory { OpenWorldCall = Open };
            Created.Add(new WeakReference(factory));
            return factory;
        }

        private static SceneResult<IWorldSceneSession> Open(SceneBinding binding, SceneWorldSnapshot _) =>
            new SceneResult<IWorldSceneSession>.Success(new FixedDisplaySession
            {
                Id = new(Guid.NewGuid()), Binding = binding,
                RequestResults = ImmutableDictionary<WorldSceneTarget, SceneResult<SceneRequestId>>.Empty.Add(Target,
                    new SceneResult<SceneRequestId>.Success(new(1)))
            });
    }
}
