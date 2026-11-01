using Train.Rendering;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SceneDisplayTests
{
    private async Task AdvancePreparation(Action step, Func<bool> reached)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!reached() && watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            await new UiTestInput(TestScene).Frames(1);
            step();
        }
        Require(reached(), "准备必须在真实后续帧内达到夹具所需进度。");
    }

    private async Task UntilResource(SceneDisplay display, ISceneFrame frame, int count)
    {
        var observe = display.Observe;
        var ready = 0;
        display.Observe = item =>
        {
            observe?.Invoke(item);
            if (item.Stage == SceneDisplayStage.ResourcesPrepared) ready++;
        };
        try
        {
            await AdvancePreparation(() =>
            {
                using var prepared = display.Prepare(frame);
                Require(prepared is null, "后续资源未就绪时不得完成正式准备。");
            }, () => ready >= count);
        }
        finally { display.Observe = observe; }
    }

    private Task UntilCandidate(SceneDisplay display, SceneFrameStamp frame, int count) =>
        AdvancePreparation(() => display.PrepareCandidate(TimeSpan.FromMilliseconds(4)),
            () => display.CandidateProgress(frame) is { } progress && progress.Prepared >= count);
}
