using System.Text.Json;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Logging;
using Train.Content.Contracts;
using System.Text.Json.Nodes;
using Train.Ui;
using Train.App.Saves;
using Train.Core.Contracts;
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
            foreach (var name in new[] { "%NewGame", "%Example", "%Saves", "%Settings", "%QuitGame" })
            {
                var button = menu.GetNode<Button>(name);
                Require(button.IsVisibleInTree() && !button.Disabled,
                    $"生产菜单须提供可用的 {name} 按钮。");
            }
            Require(quit.Text == "退出", "Main menu must expose the quit button.");
            quit.EmitSignal(BaseButton.SignalName.Pressed);
            Require(requests == 1, "Quit button signal did not reach App's quit request.");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Example_menu_input_restores_an_unsaved_paused_world()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var input = new UiTestInput(TestScene);
        var source = Godot.FileAccess.GetFileAsString(Train.App.App.ExampleSavePath);
        using var json = JsonDocument.Parse(source);
        var header = json.RootElement.GetProperty("Header");
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            await FramesUntil(TestScene, () => !app.Host!.IsBusy);
            await input.Frames();
            var menu = app.GetNode<MainMenu>("MainMenu");
            await input.Activate(menu.GetNode<Button>("%Example"));
            await FramesUntil(TestScene, () => app.Saves!.Status is not null && !app.Saves.IsBusy);
            await FramesUntil(TestScene, () => app.GetNode<MacroMap>("GameUi/Root/World/MacroMap").IsVisibleInTree());
            var status = app.Host!.GetStatus()!;
            var save = app.Saves!.Status!;
            Require(save.Identity.Id != header.GetProperty("Id").GetGuid() &&
                save.Identity.Name == header.GetProperty("Name").GetString() && save.Unsaved,
                "示例启动未生成新 UUID、保留原名称或标记未保存。");
            Require(status.View.Position == new SimulationPosition(200, 0) && status.IsPaused &&
                !menu.IsVisibleInTree() && app.GetNode<MacroMap>("GameUi/Root/World/MacroMap").IsVisibleInTree(),
                "示例菜单输入未恢复到 200:0 的暂停宏观地图。");
            Require(Godot.FileAccess.GetFileAsString(Train.App.App.ExampleSavePath) == source,
                "示例启动修改了原存档。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Example_read_and_decode_failures_are_visible()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var path = Path.Combine(OS.GetUserDataDir(), "invalid-example-" + Guid.NewGuid() + ".json");
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            await FramesUntil(TestScene, () => !app.Host!.IsBusy);
            var panel = app.GetNode<MainMenu>("MainMenu").GetChildren().OfType<SavePanel>().Single();
            foreach (var invalidJson in new[] { false, true })
            {
                if (invalidJson) File.WriteAllText(path, "not JSON");
                var failure = app.StartExample(path);
                Require(failure is not null && panel.Visible && panel.FindChildren("*", "Label", true, false)
                    .Cast<Label>().Any(label => label.Text == failure.Message),
                    "示例读取或解码失败未通过存档面板显示。");
                Require(app.Host!.GetStatus() is null, "损坏示例创建了活动会话。");
                panel.Hide();
            }
        }
        finally
        {
            try { await fixture.Cleanup(); }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
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
            definition["simulation"]!["value"] = definition["simulation"]!["value"]!.GetValue<int>() + 1;
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
