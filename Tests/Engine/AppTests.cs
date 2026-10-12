using System.Text.Json;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Logging;
using Train.Content.Voxels;
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
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            var console = app.GetChildren().OfType<GameConsole>().Single();
            Require(console.LogEvents.Any(e => e.EventId == TrainLogger.ApplicationReadyEventId), "App startup event missing.");
            app.SetProcess(false);
            var logger = app.Logger;
            path = logger.JsonlPath;
            await Task.Run(() => logger.Info("engine_test", "worker_event", "Queued from a worker thread."));
            Require(logger.PendingGodotEventCount == 1, "Worker event must wait for App's main-thread drain.");
            Require(console.LogEvents.Any(e => e.EventId == "worker_event"), "Worker event did not reach the console buffer.");
            app.SetProcess(true);
            for (var frame = 0; frame < 3 && logger.PendingGodotEventCount != 0; frame++)
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            Require(logger.PendingGodotEventCount == 0, "App frame did not drain the queued worker event.");
        }
        finally { await fixture.Cleanup(); }
        Require(path is not null, "JSONL logging was not started.");
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
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            var menu = app.GetNode<MainMenu>("MainMenu");
            Require(menu.IsNodeReady() && menu.IsVisibleInTree(), "Main menu is not ready and visible.");
            var buttons = menu.FindChildren("*", "BaseButton", true, false).Cast<BaseButton>().Where(b => b.IsVisibleInTree()).ToArray();
            var quit = menu.GetNode<Button>("%QuitGame");
            Require(buttons.Length == 1 && buttons[0] == quit && !quit.Disabled && quit.Text == "退出", "Main menu must expose only the enabled quit button.");
            var requests = 0;
            app.QuitAction = () => requests++;
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
            Require(app.CurrentContent is { Generation: > 0 }, "App did not publish initial content.");
            var grain = app.CurrentContent!.Voxels[new VoxelDefinitionId("core", "grain")];
            const string imagePath = "voxels/grain/placeholder.png";
            var presentation = grain.Definition.Presentation;
            Require(presentation.IconPath == imagePath && presentation.TopPath == imagePath && presentation.SidePath == imagePath && presentation.BottomPath == imagePath,
                "Grain presentation paths were not loaded.");
            Require(grain.PresentationImages.Count == 1 && grain.PresentationImages[imagePath] is { Width: > 0, Height: > 0 }, "Godot PNG decoder did not produce the grain image.");
            foreach (var path in new[] { "res://builtin/core/resources/voxels/grain.voxels.json", "res://builtin/core/resources/" + imagePath })
            {
                using var resource = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
                Require(resource is not null && resource.GetLength() > 0 && (ulong)resource.GetBuffer((long)resource.GetLength()).Length == resource.GetLength(), "Godot VFS could not read " + path);
            }
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task ReloadContentInputPublishesOneNewGeneration()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var directory = Path.Combine(OS.GetUserDataDir(), "mods", "engine-input-" + Guid.NewGuid().ToString("N"));
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            var before = app.CurrentContent!;
            ContentFixture.WriteMod(directory);
            Require(InputMap.HasAction(Train.App.App.ReloadContentAction), "reload_content input action is missing.");
            using var input = new InputEventAction { Action = Train.App.App.ReloadContentAction, Pressed = true };
            Input.ParseInputEvent(input);
            Input.FlushBufferedEvents();
            for (var frame = 0; frame < 3 && app.CurrentContent!.Generation == before.Generation; frame++)
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            Require(app.CurrentContent!.Generation == before.Generation + 1, "reload_content input did not publish one new generation.");
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
