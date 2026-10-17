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
    public async Task WindowsMainMenuMatchesBaseline()
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
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            Require(app.GetNode<MainMenu>("MainMenu").IsVisibleInTree(), "Main menu must be visible before capture.");
            // 等待尺寸变更和场景首次绘制完成后，读取已经提交的 viewport。
            await TestScene.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await TestScene.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            // 仅归位鼠标坐标仍可能残留 GUI 悬停状态，布局完成后显式发送移动事件。
            Input.WarpMouse(Vector2.Zero);
            using (var motion = new InputEventMouseMotion { Position = Vector2.Zero, GlobalPosition = Vector2.Zero })
                window.PushInput(motion, true);
            await TestScene.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var actual = window.GetTexture().GetImage();
            Require(actual.GetWidth() == 1920 && actual.GetHeight() == 1080, "Screenshot viewport must be 1920x1080.");
            Require(actual.SavePng(Path.Combine(output!, "actual.png")) == Error.Ok, "Could not save screenshot candidate.");
            var baselinePath = ProjectSettings.GlobalizePath("res://Tests/Engine/Baselines/windows-main-menu.png");
            var tolerancePath = ProjectSettings.GlobalizePath("res://Tests/Engine/Baselines/windows-main-menu.json");
            Require(File.Exists(baselinePath) && File.Exists(tolerancePath),
                "Windows baseline PNG and tolerance JSON are required.");
            var tolerance = JsonSerializer.Deserialize<ScreenshotTolerance>(File.ReadAllText(tolerancePath))
                ?? throw new InvalidOperationException("Screenshot tolerance JSON is empty.");
            using var baseline = Image.LoadFromFile(baselinePath);
            Require(baseline is not null && !baseline.IsEmpty(), "Baseline PNG could not be loaded.");
            var comparison = ScreenshotComparison.Compare(actual, baseline!, tolerance.MaxChannelDelta, out var difference);
            using (difference)
                Require(difference.SavePng(Path.Combine(output!, "difference.png")) == Error.Ok, "Could not save screenshot difference.");
            Require(double.IsFinite(tolerance.MaxDifferentPixelFraction) && tolerance.MaxDifferentPixelFraction is >= 0 and <= 1,
                "MaxDifferentPixelFraction must be between 0 and 1.");
            File.WriteAllText(Path.Combine(output!, "comparison.json"), JsonSerializer.Serialize(new
            {
                comparison.DifferentPixelCount, comparison.ExceedingPixelCount,
                comparison.ExceedingPixelFraction, comparison.MaximumChannelDelta,
                tolerance.MaxChannelDelta, tolerance.MaxDifferentPixelFraction
            }, new JsonSerializerOptions { WriteIndented = true }));
            Require(comparison.ExceedingPixelFraction <= tolerance.MaxDifferentPixelFraction,
                $"Main menu screenshot differs: {comparison.ExceedingPixelFraction:P6} pixels exceed channel delta {tolerance.MaxChannelDelta}; allowed {tolerance.MaxDifferentPixelFraction:P6}.");
        }
        finally { await fixture.Cleanup(); }
    }
}

internal sealed record ScreenshotTolerance(
    [property: JsonRequired] int MaxChannelDelta,
    [property: JsonRequired] double MaxDifferentPixelFraction);

internal sealed record ScreenshotComparisonResult(
    int DifferentPixelCount, int ExceedingPixelCount, double ExceedingPixelFraction, int MaximumChannelDelta);

internal static class ScreenshotComparison
{
    internal static ScreenshotComparisonResult Compare(Image actual, Image baseline, int maxChannelDelta, out Image difference)
    {
        Require(maxChannelDelta is >= 0 and <= 255, "MaxChannelDelta must be between 0 and 255.");
        Require(actual.GetWidth() == baseline.GetWidth() && actual.GetHeight() == baseline.GetHeight(),
            "Screenshot and baseline dimensions differ.");
        actual.Convert(Image.Format.Rgba8);
        baseline.Convert(Image.Format.Rgba8);
        var pixels = actual.GetData();
        var expected = baseline.GetData();
        var differencePixels = new byte[pixels.Length];
        var different = 0;
        var exceeding = 0;
        var maximumChannelDelta = 0;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var delta = 0;
            for (var channel = 0; channel < 4; channel++)
                delta = Math.Max(delta, Math.Abs(pixels[offset + channel] - expected[offset + channel]));
            maximumChannelDelta = Math.Max(maximumChannelDelta, delta);
            if (delta > 0)
            {
                different++;
                differencePixels[offset] = 255;
                if (delta > maxChannelDelta)
                    exceeding++;
                else
                    differencePixels[offset + 1] = 255;
            }
            differencePixels[offset + 3] = 255;
        }
        difference = Image.CreateFromData(actual.GetWidth(), actual.GetHeight(), false, Image.Format.Rgba8, differencePixels);
        return new ScreenshotComparisonResult(different, exceeding,
            (double)exceeding / (actual.GetWidth() * actual.GetHeight()), maximumChannelDelta);
    }
}

public class ScreenshotComparisonTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public void LocalizedChangeExceedsToleranceWhileSmallChannelNoiseDoesNot()
    {
        {
            using var baseline = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
            using var actual = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
            baseline.Fill(Colors.Black);
            actual.Fill(new Color(32f / 255, 0, 0));
            var comparison = ScreenshotComparison.Compare(actual, baseline, 32, out var difference);
            using (difference)
            {
                Require(comparison.DifferentPixelCount == 4 && comparison.MaximumChannelDelta == 32,
                    "Channel differences at tolerance must still be recorded.");
                Require(comparison.ExceedingPixelCount == 0 && comparison.ExceedingPixelFraction <= 0.00002,
                    "Small channel differences must pass the screenshot tolerance.");
                Require(difference.GetPixel(0, 0) == Colors.Yellow,
                    "Small differences must be marked yellow.");
            }
        }

        {
            using var baseline = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
            using var actual = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
            baseline.Fill(Colors.Black);
            actual.Fill(Colors.Black);
            actual.SetPixel(0, 0, new Color(1f / 255, 0, 0));
            actual.SetPixel(1, 1, Colors.White);
            var comparison = ScreenshotComparison.Compare(actual, baseline, 32, out var difference);
            using (difference)
            {
                Require(comparison.DifferentPixelCount == 2 && comparison.MaximumChannelDelta == 255,
                    "Statistics must include both small and obvious differences.");
                Require(comparison.ExceedingPixelCount == 1 && comparison.ExceedingPixelFraction > 0.00002,
                    "A localized obvious defect must exceed the screenshot tolerance.");
                Require(difference.GetPixel(1, 1) == Colors.Red && difference.GetPixel(0, 0) == Colors.Yellow
                    && difference.GetPixel(1, 0) == Colors.Black,
                    "Difference image must distinguish obvious, small and unchanged pixels.");
            }
        }
    }
}
