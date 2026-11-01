using Godot;

namespace Train.Engine.Tests;

internal static class MenuCapture
{
    internal static async Task Capture(Node scene, string name)
    {
        if (DisplayServer.GetName() == "headless" ||
            string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("TRAIN_UI_CAPTURE_DIR"))) return;
        var window = scene.GetTree().Root;
        var originalSize = window.Size;
        var originalScale = window.ContentScaleFactor;
        try
        {
            foreach (var size in new[] { new Vector2I(1280, 720), new Vector2I(1920, 1080), new Vector2I(2560, 1440) })
            foreach (var scale in new[] { 1f, 2f })
            {
                window.Size = size;
                window.ContentScaleFactor = scale;
                await scene.ToSignal(scene.GetTree(), SceneTree.SignalName.ProcessFrame);
                await scene.ToSignal(scene.GetTree(), SceneTree.SignalName.ProcessFrame);
                await UiCapture.Capture(scene, $"{name}-{size.X}x{size.Y}-{scale:0}x", scale, size);
            }
        }
        finally
        {
            window.Size = originalSize;
            window.ContentScaleFactor = originalScale;
            await scene.ToSignal(scene.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }
}
