using Godot;
using Train.App.Assets;
using Train.Content.ContractSupport;
using Train.Rendering;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SceneDisplayTests
{
    private SceneDisplay Attach(AssetResourceCache cache, FixedContentView content, bool completeInitialFrame = false)
    {
        var display = new SceneDisplay(cache, content, completeInitialFrame) { Timestamp = () => 0 };
        TestScene.AddChild(display.Root);
        return display;
    }

    private Task<SceneDisplay.PreparedFrame> Prepare(SceneDisplay display, ISceneFrame frame) =>
        SceneDisplayTestDriver.Prepare(display, frame, TestScene);

    private Task CompleteCandidate(SceneDisplay display) =>
        SceneDisplayTestDriver.AdvanceCandidate(display, () => !display.IsPreparingCandidate, TestScene);

    private async Task Commit(SceneDisplay display, ISceneFrame frame)
    {
        using var candidate = await Prepare(display, frame);
        candidate.Commit();
    }
}

internal static class SceneDisplayTestDriver
{
    internal static PreparedVisual PrepareVisual(SceneResources resources, AssetResourceCache cache,
        SceneVisual source, SceneMeshPacking? packing = null)
    {
        if (source is not SceneVisual.Geometry geometry) return resources.Prepare(source);
        using var display = new SceneDisplay(cache, new FixedContentView()) { Timestamp = () => 0 };
        SceneResources.GeometryPreparation? pending = null;
        try
        {
            return resources.TryPrepare(geometry, packing, new(display, 0, null, true), ref pending)
                ?? throw new InvalidOperationException("已就绪网格必须完成无限额准备。");
        }
        finally { pending?.Dispose(); }
    }

    internal static async Task<SceneDisplay.PreparedFrame> Prepare(SceneDisplay display, ISceneFrame frame, Node scene)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (display.Prepare(frame) is { } prepared) return prepared;
            await display.PreparationPacking.WaitAsync(TimeSpan.FromSeconds(30));
            await new UiTestInput(scene).Frames(1);
        }
        throw new InvalidOperationException("已配置的可显示帧准备超时。");
    }

    internal static async Task AdvanceCandidate(SceneDisplay display, Func<bool> reached, Node scene)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!reached() && watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
            await display.CandidatePacking.WaitAsync(TimeSpan.FromSeconds(30));
            if (!reached()) await new UiTestInput(scene).Frames(1);
        }
        Require(reached(), "候选必须在真实后续帧内达到预期进度。");
    }
}
