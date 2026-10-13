using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Assets;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class ContentTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Production_new_game_save_restore_and_railway_scene_use_real_modules()
    {
        var fixture = new Fixture(TestScene.GetTree());
        Guid? saveId = null;
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            await FramesUntil(TestScene, () => !app.Host!.IsBusy);
            var host = app.Host!;
            Require(host.Content is not null && host.GetStatus() is null, "启动必须先准备内容并留在菜单。");
            Require(host.BeginNewGame(new("core:basic-through-world"), 1, "stage1-world-v1", host.SessionOptions!).Problem is null,
                "真实新局请求被拒绝。");
            await FramesUntil(TestScene, () => !host.IsBusy);
            var created = host.GetStatus();
            Require(created is { PlayerPaused: true } && created.View.World.Nodes.Length > 0 && created.View.World.Trains.Length == 3,
                "正式内容没有生成暂停的小世界与三列初始列车。");
            Require(created!.View.InputIdentity == host.Content!.Core.Identity, "新局的核心与内容没有配对。");
            var runtime = host.Runtime!;
            Require(runtime.OperatingSystem == (OperatingSystem.IsWindows() ? "Windows" : RuntimeInformation.OSDescription),
                "真实宿主的操作系统身份没有按兼容边界填写。");
            Require(runtime.Architecture == RuntimeInformation.ProcessArchitecture.ToString(),
                "真实宿主的架构身份与当前进程不符。");
            Require(runtime.DotNetRuntime == RuntimeInformation.FrameworkDescription,
                "真实宿主的 .NET 身份没有保留完整运行时版本。");
            Require(host.Step(2).Completed == 2, "真实会话没有完成两个模拟步。");
            var checkpoint = Checkpoint(host);
            var saves = app.Saves!;
            saveId = saves.Status!.Identity.Id;
            Require(saves.Save() is null, "真实保存请求失败。");
            await FramesUntil(TestScene, () => !saves.HasPendingWork);
            Require(!saves.Status!.Unsaved, "文件发布后仍标记为未保存。");
            var entry = saves.List().Single(entry => entry.Header.Id == saveId && !entry.Automatic);
            Require(File.Exists(entry.Path), "保存没有生成真实文件。");
            Require(saves.Load(entry) is null, "真实读档请求失败。");
            await FramesUntil(TestScene, () => !saves.HasPendingWork && !host.IsBusy);
            var restored = host.GetStatus()!;
            Require(restored.Binding != created.Binding && restored.PlayerPaused && saves.Status!.Identity.Id == saveId,
                "读档没有更换运行绑定、保留存档身份并暂停。");
            Require(Checkpoint(host).Sha256 == checkpoint.Sha256 && restored.View.InputIdentity == host.Content!.Core.Identity,
                "真实文件恢复后检查点或核心／内容配对改变。");
            var edge = restored.View.World.Edges.First();
            app.WorldScenes!.Open(new WorldSceneTarget.Location(new WorldLocation.Edge(edge.Id, 1)));
            await FramesUntil(TestScene, () => app.WorldScenes.Display?.DisplayedFrame is not null || !app.WorldScenes.View.Visible);
            Require(app.WorldScenes.Display?.DisplayedFrame is { Readiness: SceneReadiness.Ready or SceneReadiness.Partial },
                "真实体素工厂没有把线路帧接入 Godot。");
        }
        finally
        {
            await fixture.Cleanup();
            if (saveId is { } id)
                Directory.Delete(new FileSaveStore(OS.GetUserDataDir()).DirectoryFor(id), true);
        }
    }

    [Test]
    public async Task Console_and_input_reload_pair_business_updates_and_preserve_presentation_boundaries()
    {
        var directory = ContentFixture.CopyBuiltIn();
        var fixture = new Fixture(TestScene.GetTree());
        using var dispatcher = new GodotMainThreadDispatcher();
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => ContentFixture.CreateHost(logger, directory, dispatcher);
            await fixture.AddToRoot(app);
            var host = app.Host!;
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher);
            host.BeginNewGame(new("core:basic-through-world"), 1, "stage1-world-v1", host.SessionOptions!);
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher);
            Require(host.GetStatus() is not null, "真实内容夹具没有生成世界。");
            var original = host.Content!;
            var before = Checkpoint(host);
            var file = Path.Combine(directory, "resources/voxels/staple-food.voxel.json");
            var definition = JsonNode.Parse(File.ReadAllText(file))!;
            definition["simulation"]!["value"] = 13;
            File.WriteAllText(file, definition.ToJsonString());
            var console = app.GetChildren().OfType<GameConsole>().Single();
            console.SubmitLine("reload");
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher);
            var business = host.Content!;
            Require(business.Generation.Value == original.Generation.Value + 1 && business.Core.Identity != original.Core.Identity &&
                host.GetStatus()!.View.InputIdentity == business.Core.Identity &&
                host.GetStatus()!.View.Position.Sequence == before.Position.Sequence + 1,
                "控制台重载没有在同一边界发布业务内容与核心输入。");
            Require(original.Core.Identity != host.GetStatus()!.View.InputIdentity, "旧内容视图被原地修改。");
            var published = Checkpoint(host);
            definition["display"]!["name"] = "引擎重载主食";
            File.WriteAllText(file, definition.ToJsonString());
            using (var input = new InputEventAction { Action = Train.App.App.ReloadContentAction, Pressed = true })
                Input.ParseInputEvent(input);
            Input.FlushBufferedEvents();
            await FramesUntil(TestScene, () => !host.IsBusy && host.Content!.Generation.Value > business.Generation.Value, dispatcher);
            var presentation = host.Content!;
            Require(presentation.Core.Identity == business.Core.Identity && Checkpoint(host).Sha256 == published.Sha256,
                "纯表现重载改变了核心输入或检查点。");
            Require(presentation.FindVoxelDisplay(new("core:staple-food")) is ContentLookup<VoxelDisplayProjection>.Found
                { Value.Display.Name: "引擎重载主食" }, "新显示资料没有生效。");
            console.SubmitLine("reload");
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher);
            Require(ReferenceEquals(presentation, host.Content) && Checkpoint(host).Sha256 == published.Sha256,
                "无变化重载仍发布了内容或提交核心命令。");
        }
        finally
        {
            using var release = new InputEventAction { Action = Train.App.App.ReloadContentAction, Pressed = false };
            Input.ParseInputEvent(release);
            Input.FlushBufferedEvents();
            await fixture.Cleanup();
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task Invalid_required_content_reload_keeps_the_real_active_group()
    {
        var directory = ContentFixture.CopyBuiltIn();
        var fixture = new Fixture(TestScene.GetTree());
        using var dispatcher = new GodotMainThreadDispatcher();
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => ContentFixture.CreateHost(logger, directory, dispatcher);
            await fixture.AddToRoot(app);
            var host = app.Host!;
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher);
            host.BeginNewGame(new("core:basic-through-world"), 1, "stage1-world-v1", host.SessionOptions!);
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher);
            var old = host.Content!;
            var status = host.GetStatus()!;
            var checkpoint = Checkpoint(host);
            File.WriteAllText(Path.Combine(directory, "resources/voxels/staple-food.voxel.json"), "{ invalid JSON");
            var console = app.GetChildren().OfType<GameConsole>().Single();
            console.SubmitLine("reload");
            await FramesUntil(TestScene, () => !host.IsBusy, dispatcher);
            Require(ReferenceEquals(old, host.Content) && host.GetStatus()!.Binding == status.Binding &&
                host.GetStatus()!.View.InputIdentity == old.Core.Identity && Checkpoint(host).Sha256 == checkpoint.Sha256,
                "真实内容准备失败仍改变了活动核心、内容或配对。");
            Require(console.LogEvents.Any(log => log.EventId == "content_reload_failed") &&
                console.LogEvents.Any(log => log.EventId == "content_invalid" && Equals(log.Attributes["code"], "invalid_json")),
                "真实加载失败没有保留可定位的内容诊断。");
        }
        finally { await fixture.Cleanup(); Directory.Delete(directory, true); }
    }


    private static CoreCheckpoint Checkpoint(HostCoordinator host) =>
        ((CoreResult<ICoreCapture>.Success)host.CaptureCheckpoint()).Value.Checkpoint;
}
