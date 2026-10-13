using System.Text.Json;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Logging;
using Train.Content.Contracts;
using System.Text.Json.Nodes;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class AppTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task WorkerLoggingDrainsOnAppFrameAndClosesOnExit()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        string? path = null;
        using var output = new GodotOutputCapture();
        OS.AddLogger(output);
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            await FramesUntil(TestScene, () => !app.Host!.IsBusy);
            var console = app.GetChildren().OfType<GameConsole>().Single();
            Require(console.LogEvents.Any(e => e.EventId == TrainLogger.ApplicationReadyEventId), "App startup event missing.");
            Require(output.Contains("[application][application_ready]"), "App startup event did not reach Godot output.");
            app.SetProcess(false);
            var logger = app.Logger;
            path = logger.JsonlPath;
            await Task.Run(() => logger.Info("engine_test", "worker_event", "Queued from a worker thread."));
            Require(logger.PendingGodotEventCount == 1, "Worker event must wait for App's main-thread drain.");
            Require(console.LogEvents.Any(e => e.EventId == "worker_event"), "Worker event did not reach the console buffer.");
            Require(!output.Contains("[engine_test][worker_event]"), "Worker event reached Godot output before App's main-thread drain.");
            app.SetProcess(true);
            for (var frame = 0; frame < 3 && !output.Contains("[engine_test][worker_event]"); frame++)
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            Require(logger.PendingGodotEventCount == 0, "App frame did not drain the queued worker event.");
            Require(output.Contains("[engine_test][worker_event]"), "App frame did not deliver the worker event to Godot output.");
        }
        finally
        {
            try { await fixture.Cleanup(); }
            finally { OS.RemoveLogger(output); }
        }
        Require(path is not null, "JSONL logging was not started.");
        // 独占打开验证 logger 已关闭写句柄；共享读取无法证明退出时释放了文件。
        using var file = new FileStream(path!, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.None);
        using var reader = new StreamReader(file);
        var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Require(lines.Any(line => { using var json = JsonDocument.Parse(line); return json.RootElement.GetProperty("event_id").GetString() == "worker_event"; }),
            "Worker event was not persisted before logger disposal.");
    }

    [Test]
    public async Task MainMenuQuitButtonRequestsAppExit()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            var requests = 0;
            app.QuitAction = () => requests++;
            await fixture.AddToRoot(app);
            await FramesUntil(TestScene, () => !app.Host!.IsBusy);
            var menu = app.GetNode<MainMenu>("MainMenu");
            Require(menu.IsNodeReady() && menu.IsVisibleInTree(), "Main menu is not ready and visible.");
            var quit = menu.GetNode<Button>("%QuitGame");
            Require(quit.IsVisibleInTree() && !quit.Disabled && quit.Text == "退出", "Main menu must expose the enabled quit button.");
            quit.EmitSignal(BaseButton.SignalName.Pressed);
            Require(requests == 1, "Quit button signal did not reach App's quit request.");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task StartupLoadsBuiltInContentThroughGodotResources()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            await FramesUntil(TestScene, () => !app.Host!.IsBusy);
            var content = app.Host!.Content;
            Require(content is { Generation.Value: > 0 }, "App did not publish initial content.");
            Require(content!.FindVoxelDisplay(new("core:staple-food")) is ContentLookup<VoxelDisplayProjection>.Found,
                "Built-in staple food was not loaded.");
            var display = ((ContentLookup<VoxelDisplayProjection>.Found)content.FindVoxelDisplay(new("core:staple-food"))).Value;
            Require(display.Icon is { } icon && content.FindAsset(icon) is ContentLookup<ContentAsset>.Found
                { Value.Metadata: AssetMetadata.Png { Width: > 0, Height: > 0 } },
                "Godot PNG decoder did not produce the built-in icon.");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task ReloadContentInputPublishesOneNewGeneration()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var directory = Path.Combine(OS.GetUserDataDir(), "mods", "engine-input-" + Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        try
        {
            ContentFixture.WriteMod(directory, id);
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            await FramesUntil(TestScene, () => !app.Host!.IsBusy);
            var before = app.Host!.Content!;
            Require(before.FindVoxelDisplay(new(id.ToString("D") + ":engine-food")) is ContentLookup<VoxelDisplayProjection>.Found,
                "Production composition did not enable the installed Mod.");
            var path = Path.Combine(directory, "resources/voxels/engine-food.voxel.json");
            var definition = JsonNode.Parse(File.ReadAllText(path))!;
            definition["simulation"]!["value"] = 13;
            File.WriteAllText(path, definition.ToJsonString());
            Require(InputMap.HasAction(Train.App.App.ReloadContentAction), "reload_content input action is missing.");
            using var input = new InputEventAction { Action = Train.App.App.ReloadContentAction, Pressed = true };
            Input.ParseInputEvent(input);
            Input.FlushBufferedEvents();
            await FramesUntil(TestScene, () => !app.Host.IsBusy && app.Host.Content!.Generation != before.Generation);
            Require(app.Host.Content!.Generation.Value == before.Generation.Value + 1 && app.Host.Content.Core.Identity != before.Core.Identity,
                "reload_content input did not publish the modified Mod exactly once.");
        }
        finally
        {
            using var release = new InputEventAction { Action = Train.App.App.ReloadContentAction, Pressed = false };
            Input.ParseInputEvent(release);
            Input.FlushBufferedEvents();
            try { await fixture.Cleanup(); }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }

}
