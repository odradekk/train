using System.Runtime.CompilerServices;
using Godot;
using Train.App;
using Train.App.Assets;
using Train.App.Authoring;
using Train.App.Editing;
using Train.App.Logging;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

internal sealed class RealAuthoringFixture : IDisposable
{
    private readonly Node testScene;
    private readonly TrainLogger logger;
    internal string UserData { get; } = Path.Combine(OS.GetUserDataDir(), "authoring-real-" + Guid.NewGuid().ToString("N"));
    internal string Mods => AuthoringPaths.ModsDirectory(UserData);
    internal PackageId PackageId { get; } = new("38d8c972-4414-4917-923f-6537bf007b24");
    internal string ModDirectory => Path.Combine(Mods, PackageId.Value);
    internal GodotMainThreadDispatcher Dispatcher { get; } = new();
    internal IContentFiles Files { get; }
    internal IAssetInspector Assets { get; }
    internal ContentInstallation Installation { get; }
    internal HostCoordinator Host { get; }
    internal EditorService Editor { get; }
    internal AuthoringService Service { get; }
    internal IScenesFactory Scenes { get; } = Train.Scenes.ScenesFactory.Create();
    internal HostOperationResult? LastOperation { get; private set; }

    internal RealAuthoringFixture(Node testScene, string? writableCore = null)
    {
        this.testScene = testScene;
        ContentFixture.WriteMod(ModDirectory, Guid.Parse(PackageId.Value));
        var blueprints = Path.Combine(ModDirectory, "resources", "blueprints");
        Directory.CreateDirectory(blueprints);
        File.WriteAllText(Path.Combine(blueprints, "engine-model.blueprint.json"), """
            {"schema_version":3,"id":"engine-model","category":"scene_model",
             "layout":{"palette":[{"voxel":"core:tree-trunk"}],"cells":[[0,0,0,0]]},
             "display":{"name":"真实创作模型","description":"隔离本地 Mod"}}
            """);
        Files = new ContentFiles(Dispatcher, new Dictionary<ContentRoot, ContentRootLocation>
        {
            [new("builtin")] = new ContentRootLocation.Disk(writableCore ?? ProjectSettings.GlobalizePath("res://builtin/core")),
            [new("mods")] = new ContentRootLocation.Disk(Mods)
        });
        Assets = new GodotAssetInspector(Dispatcher);
        Installation = new(new("builtin"), new("mods"), null, "0.2.0", [PackageId]);
        logger = new(Path.Combine(UserData, "logs"), LogLevel.Info, new GodotLogOutput());
        Host = new(Train.Core.CoreFactory.Create(), Train.Content.ContentFactory.Create(), Files, Assets,
            Installation, logger, sessionOptions: new(new("authoring-real", "core-session-v1", "Windows", "x64", "10"), true, 1));
        Host.OperationEnded += result => LastOperation = result;
        Editor = new(Host, Scenes, new FileSaveStore(UserData), logger);
        Service = new(Host, Editor, Scenes, true, Mods, writableCore);
    }

    internal async Task Start()
    {
        Require(Host.BeginReload(CommandSource.AutomationTest).Problem is null, "初始加载请求被拒绝。");
        await Pump(() => !Host.IsBusy);
        Require(LastOperation?.Succeeded == true && Host.Content is not null, "真实初始内容加载失败：" + LastOperation);
        Require(Host.BeginNewGame(new("core:basic-through-world"), 1, "stage1-world-v1", Host.SessionOptions!).Problem is null, "新局请求被拒绝。");
        await Pump(() => !Host.IsBusy);
        Require(Host.GetStatus() is not null, "真实核心未创建普通会话。");
    }

    internal async Task Open()
    {
        Service.Open(ModDirectory, PackageId);
        await Pump(() => !Service.IsBusy);
        Require(Service.Package is not null, "真实本地包未打开：" + Service.Message);
    }

    internal async Task Pump(Func<bool> done, TimeSpan? timeout = null, [CallerArgumentExpression(nameof(done))] string? description = null)
    {
        try
        {
            await FramesUntil(testScene, () =>
            {
                Dispatcher.Pump(); Host.ProcessFrame(0); Editor.Process(); Service.Process();
                return done();
            }, Dispatcher, timeout, description);
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith("真实 Godot 帧等待超时：", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{error.Message}\nHost: busy={Host.IsBusy}, generation={Host.Content?.Generation}, lastOperation={LastOperation}\nEditor: busy={Editor.IsBusy}, open={Editor.IsOpen}, dirty={Editor.IsDirty}, leaving={Editor.Leaving}, error={Editor.HasError}, message={Editor.Message}\nService: busy={Service.IsBusy}, leaving={Service.Leaving}, writeSuccess={Service.LastWrite?.Success}, writtenFiles={Service.LastWrite?.Completed.Length}, message={Service.Message}", error);
        }
    }

    public void Dispose()
    {
        Service.Dispose(); Editor.Dispose(); Host.Dispose(); logger.Dispose(); Dispatcher.Dispose();
        Directory.Delete(UserData, true);
    }
}
