using System.Text.Json;
using System.Text.Json.Serialization;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class ScreenshotTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task WindowsMainMenuMatchesApprovedBaseline()
    {
        Require(OS.GetName() == "Windows" && DisplayServer.GetName() != "headless",
            "Screenshot comparison requires windowed Windows Godot.");
        var output = System.Environment.GetEnvironmentVariable("TRAIN_SCREENSHOT_OUTPUT");
        Require(!string.IsNullOrWhiteSpace(output), "TRAIN_SCREENSHOT_OUTPUT must name this run's artifact directory.");
        Directory.CreateDirectory(output!);
        var adapterType = RenderingServer.GetVideoAdapterType();
        File.WriteAllText(Path.Combine(output!, "rendering.json"), JsonSerializer.Serialize(new
        {
            operatingSystem = OS.GetName(), godot = Godot.Engine.GetVersionInfo()["string"].AsString(),
            displayServer = DisplayServer.GetName(), renderingMethod = RenderingServer.GetCurrentRenderingMethod(),
            driver = RenderingServer.GetCurrentRenderingDriverName(), adapter = RenderingServer.GetVideoAdapterName(),
            vendor = RenderingServer.GetVideoAdapterVendor(), adapterType = adapterType.ToString(),
            apiVersion = RenderingServer.GetVideoAdapterApiVersion(), viewportWidth = 1920, viewportHeight = 1080
        }, new JsonSerializerOptions { WriteIndented = true }));
        Require(RenderingServer.GetCurrentRenderingMethod() == "forward_plus", "Screenshot requires Forward+ rendering.");
        Require(adapterType is RenderingDevice.DeviceType.DiscreteGpu or RenderingDevice.DeviceType.IntegratedGpu,
            "Screenshot requires a real hardware GPU; see rendering.json for the actual adapter.");
        var window = TestScene.GetTree().Root;
        window.Mode = Window.ModeEnum.Windowed;
        window.Size = new Vector2I(1920, 1080);
        Input.WarpMouse(Vector2.Zero);
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            Require(app.GetNode<MainMenu>("MainMenu").IsVisibleInTree(), "Main menu must be visible before capture.");
            // 等待尺寸变更和场景首次绘制完成后，读取已经提交的 viewport。
            await TestScene.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await TestScene.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var actual = window.GetTexture().GetImage();
            Require(actual.GetWidth() == 1920 && actual.GetHeight() == 1080, "Screenshot viewport must be 1920x1080.");
            Require(actual.SavePng(Path.Combine(output!, "actual.png")) == Error.Ok, "Could not save screenshot candidate.");
            var baselinePath = ProjectSettings.GlobalizePath("res://Tests/Engine/Baselines/windows-main-menu.png");
            var tolerancePath = ProjectSettings.GlobalizePath("res://Tests/Engine/Baselines/windows-main-menu.json");
            Require(File.Exists(baselinePath) && File.Exists(tolerancePath),
                "Approved Windows baseline PNG and tolerance JSON are required. actual.png is a candidate, not an accepted baseline.");
            var tolerance = JsonSerializer.Deserialize<ScreenshotTolerance>(File.ReadAllText(tolerancePath))
                ?? throw new InvalidOperationException("Screenshot tolerance JSON is empty.");
            using var baseline = Image.LoadFromFile(baselinePath);
            Require(baseline is not null && !baseline.IsEmpty(), "Approved baseline PNG could not be loaded.");
            var fraction = ScreenshotComparison.DifferentPixelFraction(actual, baseline!, tolerance.MaxChannelDelta);
            Require(double.IsFinite(tolerance.MaxDifferentPixelFraction) && tolerance.MaxDifferentPixelFraction is >= 0 and <= 1,
                "MaxDifferentPixelFraction must be between 0 and 1.");
            File.WriteAllText(Path.Combine(output!, "comparison.json"), JsonSerializer.Serialize(new
            {
                differentPixelFraction = fraction, tolerance.MaxChannelDelta, tolerance.MaxDifferentPixelFraction
            }, new JsonSerializerOptions { WriteIndented = true }));
            Require(fraction <= tolerance.MaxDifferentPixelFraction,
                $"Main menu screenshot differs: {fraction:P6} pixels exceed channel delta {tolerance.MaxChannelDelta}; allowed {tolerance.MaxDifferentPixelFraction:P6}.");
        }
        finally { await fixture.Cleanup(); }
    }
}

internal sealed record ScreenshotTolerance(
    [property: JsonRequired] int MaxChannelDelta,
    [property: JsonRequired] double MaxDifferentPixelFraction);

internal static class ScreenshotComparison
{
    internal static double DifferentPixelFraction(Image actual, Image baseline, int maxChannelDelta)
    {
        Require(maxChannelDelta is >= 0 and <= 255, "MaxChannelDelta must be between 0 and 255.");
        Require(actual.GetWidth() == baseline.GetWidth() && actual.GetHeight() == baseline.GetHeight(),
            "Screenshot and baseline dimensions differ.");
        actual.Convert(Image.Format.Rgba8);
        baseline.Convert(Image.Format.Rgba8);
        var pixels = actual.GetData();
        var expected = baseline.GetData();
        var different = 0;
        for (var offset = 0; offset < pixels.Length; offset += 4)
            for (var channel = 0; channel < 4; channel++)
                if (Math.Abs(pixels[offset + channel] - expected[offset + channel]) > maxChannelDelta)
                {
                    different++;
                    break;
                }
        return (double)different / (actual.GetWidth() * actual.GetHeight());
    }
}

public class ScreenshotComparisonTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public void LocalizedChangeExceedsToleranceWhileSmallChannelNoiseDoesNot()
    {
        using var baseline = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
        using var actual = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
        baseline.Fill(Colors.Black);
        actual.Fill(new Color(1f / 255, 0, 0));
        Require(ScreenshotComparison.DifferentPixelFraction(actual, baseline, 1) == 0,
            "One-byte channel noise at tolerance must be accepted.");
        actual.SetPixel(1, 1, Colors.White);
        var fraction = ScreenshotComparison.DifferentPixelFraction(actual, baseline, 1);
        Require(fraction == 0.25,
            "A localized one-pixel defect must count as one changed pixel out of four.");
    }
}
