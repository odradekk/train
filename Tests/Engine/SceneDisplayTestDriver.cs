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

    private async Task Prime(SceneDisplay display, ISceneFrame frame)
    {
        await new UiTestInput(TestScene).Frames(1);
        var timestamp = display.Timestamp;
        try
        {
            display.Timestamp = () => 0;
            using var prepared = display.Prepare(frame, TimeSpan.Zero);
            Require(prepared is null, "零预算只能启动后台打包，不能完成有新视觉的同空间帧。");
            await display.PreparationPacking.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { display.Timestamp = timestamp; }
        await new UiTestInput(TestScene).Frames(1);
    }

    private async Task PrimeCandidate(SceneDisplay display)
    {
        await new UiTestInput(TestScene).Frames(1);
        var timestamp = display.Timestamp;
        try
        {
            display.Timestamp = () => 0;
            display.PrepareCandidate(TimeSpan.Zero);
            await display.CandidatePacking.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { display.Timestamp = timestamp; }
        await new UiTestInput(TestScene).Frames(1);
    }

    private async Task Commit(SceneDisplay display, ISceneFrame frame)
    {
        using var candidate = await Prepare(display, frame);
        candidate.Commit();
    }
}
