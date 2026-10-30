using System.Collections.Immutable;
using System.Diagnostics;
using Chickensoft.GoDotTest;
using Train.App.Assets;
using Train.Content.ContractSupport;
using Train.Rendering;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SceneDisplayTests
{
    [Test]
    public async Task Candidate_packing_collection_yields_before_starting_background_work_and_preserves_display()
    {
        await new UiTestInput(TestScene).Frames(1);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, [Object(1, Peak(1))]);
        await Commit(display, first);
        var shown = display.Objects[first.Update.Upserts[0].Id].Visuals[0];
        var objects = Enumerable.Range(2, 24).Select(id => Object((ulong)id, Peak(id))).ToImmutableArray();
        var candidate = Frame(2, objects, space: 2);
        var packed = 0;
        display.PackMesh = (mesh, cancellation) =>
        {
            Interlocked.Increment(ref packed);
            return PackedSceneMesh.Create(mesh, cancellation);
        };
        display.ReceiveCandidate(candidate.Update);
        await new UiTestInput(TestScene).Frames(1);
        long ticks = 0;
        display.Timestamp = () => Interlocked.Add(ref ticks, Stopwatch.Frequency / 250);
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        await display.CandidatePacking.WaitAsync(TimeSpan.FromSeconds(30));
        Require(Volatile.Read(ref packed) == 0,
            "候选输入收集耗尽本帧预算时，不能扫描余下输入并提前启动整批后台打包。");
        Require(display.PreparedCandidate is null && ReferenceEquals(display.DisplayedFrame, first) &&
            ReferenceEquals(display.Objects[first.Update.Upserts[0].Id].Visuals[0], shown) && shown.Node.IsInsideTree(),
            "候选收集尚未完成时不能宣布候选就绪，也不能改变当前显示。");
        for (var frame = 0; frame < 200 && display.PreparedCandidate is null; frame++)
        {
            await new UiTestInput(TestScene).Frames(1);
            display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
            await display.CandidatePacking.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Require(display.PreparedCandidate == candidate.Update.Frame && Volatile.Read(ref packed) == objects.Length &&
            ReferenceEquals(display.DisplayedFrame, first) && shown.Node.IsInsideTree(),
            "分帧收集与准备最终必须完整就绪，每份网格只打包一次且旧显示持续保留。");
    }
}
