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

    private async Task<SceneDisplay.PreparedFrame> Prepare(SceneDisplay display, ISceneFrame frame)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (display.Prepare(frame) is { } prepared) return prepared;
            await display.PreparationPacking.WaitAsync(TimeSpan.FromSeconds(30));
            await new UiTestInput(TestScene).Frames(1);
        }
        throw new InvalidOperationException("已配置的可显示帧准备超时。");
    }

    private async Task CompleteCandidate(SceneDisplay display)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (display.IsPreparingCandidate && watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
            await display.CandidatePacking.WaitAsync(TimeSpan.FromSeconds(30));
            if (display.IsPreparingCandidate) await new UiTestInput(TestScene).Frames(1);
        }
        Require(!display.IsPreparingCandidate, "候选准备必须在挂起保护内完成。");
    }

    private async Task Commit(SceneDisplay display, ISceneFrame frame)
    {
        using var candidate = await Prepare(display, frame);
        candidate.Commit();
    }
}
