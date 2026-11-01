using Godot;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

internal static class UiCapture
{
    private static string? OutputDirectory => DisplayServer.GetName() == "headless" ? null :
        System.Environment.GetEnvironmentVariable("TRAIN_UI_CAPTURE_DIR");

    internal static void ConfigureWindowIfNeeded(Window window)
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory)) return;
        window.Mode = Window.ModeEnum.Windowed;
        window.Size = new Vector2I(1920, 1080);
    }

    internal static async Task Capture(Node scene, string name, float scale = 1)
    {
        var output = OutputDirectory;
        if (string.IsNullOrWhiteSpace(output)) return;
        var window = scene.GetTree().Root;
        var drawable = window.Size == new Vector2I(1920, 1080) && window.Mode != Window.ModeEnum.Minimized;
        if (drawable)
        {
            await scene.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await scene.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
        using var image = drawable ? window.GetTexture().GetImage() : null;
        var valid = window.Size == new Vector2I(1920, 1080) && window.Mode != Window.ModeEnum.Minimized &&
            Mathf.IsEqualApprox(window.ContentScaleFactor, scale) && image?.GetWidth() == 1920 && image.GetHeight() == 1080;
        Directory.CreateDirectory(output);
        var metadata = System.Text.Json.JsonSerializer.Serialize(new
        {
            operatingSystem = OS.GetName(), godot = Godot.Engine.GetVersionInfo()["string"].AsString(),
            displayServer = DisplayServer.GetName(), renderingMethod = RenderingServer.GetCurrentRenderingMethod(),
            driver = RenderingServer.GetCurrentRenderingDriverName(), adapter = RenderingServer.GetVideoAdapterName(),
            vendor = RenderingServer.GetVideoAdapterVendor(), adapterType = RenderingServer.GetVideoAdapterType().ToString(),
            apiVersion = RenderingServer.GetVideoAdapterApiVersion(), requestedScale = scale,
            windowSize = new { width = window.Size.X, height = window.Size.Y }, windowMode = window.Mode.ToString(),
            contentScaleFactor = window.ContentScaleFactor,
            imageSize = new { width = image?.GetWidth(), height = image?.GetHeight() }, valid
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, name + ".json"), metadata);
        File.WriteAllText(Path.Combine(output, "rendering.json"), metadata);
        if (image is not null) Require(image.SavePng(Path.Combine(output, name + ".png")) == Error.Ok, "UI 截图保存失败。");
        Require(valid, $"无效截图：窗口 {window.Size}，模式 {window.Mode}，缩放 {window.ContentScaleFactor}，期望 {scale}。");
    }
}
