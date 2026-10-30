using System.Diagnostics;
using Chickensoft.GoDotTest;
using Train.App.Assets;
using Train.Content.ContractSupport;
using Train.Rendering;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SceneDisplayTests
{
    [Test]
    public async Task Preparation_excludes_gc_after_its_ending_timestamp() =>
        await GcAfterEndingTimestamp(candidate: false);

    [Test]
    public async Task Candidate_preparation_excludes_gc_after_its_ending_timestamp() =>
        await GcAfterEndingTimestamp(candidate: true);

    private async Task GcAfterEndingTimestamp(bool candidate)
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        await new UiTestInput(TestScene).Frames(1);
        var reads = 0;
        var samples = 0;
        TimeSpan before = default, allowedPause = default, injectedPause = default, observedPause = default;
        display.Timestamp = () =>
        {
            var timestamp = Stopwatch.GetTimestamp();
            if (reads++ == 0) before = GC.GetTotalPauseDuration();
            else
            {
                allowedPause = GC.GetTotalPauseDuration() - before;
                // 模拟终点快照之后、采样线程恢复之前发生暂停，返回的终点仍在这次 GC 之前。
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                injectedPause = GC.GetTotalPauseDuration() - before - allowedPause;
            }
            return timestamp;
        };
        display.Measure = (work, _, pause) =>
        {
            Require(work == (candidate ? SceneDisplayWork.PrepareCandidate : SceneDisplayWork.Prepare),
                "必须记录被测试的准备调用。");
            samples++;
            observedPause = pause;
        };
        if (candidate) display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        else
        {
            using var prepared = display.Prepare(Frame(1, [], readiness: SceneReadiness.Loading));
            Require(prepared is null, "Loading 调用不得准备显示资源。");
        }
        Require(reads == 2 && samples == 1 && injectedPause > TimeSpan.Zero,
            "必须在该调用的终点快照之后实际注入一次可见的 GC 暂停。");
        Require(observedPause <= allowedPause,
            "准备计时不得扣除墙钟终点之后才发生的 GC 暂停。");
    }
}
