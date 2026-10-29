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
    public async Task Resource_prepared_waits_for_every_visual_of_the_object()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var original = Object(1, Mesh());
        var baseline = Frame(1, [original]);
        await Commit(display, baseline);
        var reused = display.Objects[original.Id].Visuals[0];
        var added = Object(1, Mesh()).Visuals[0];
        var source = original with { Visuals = [original.Visuals[0], added, added] };
        List<SceneDisplayEvent> events = [];
        display.Observe = events.Add;
        long timestamp = 0;
        display.Timestamp = () => timestamp += System.Diagnostics.Stopwatch.Frequency;
        var frame = Frame(2, [source], baseline.Update.Frame);
        await ScenePackingTestDriver.StartFormal(display, frame, TestScene);
        Require(display.Prepare(frame) is null && events.Count == 1 &&
            events[0].Stage == SceneDisplayStage.FramePreparationStarted && events[0].Object is null &&
            events[0].ResourceObjects is { } required && required.SequenceEqual(new[] { source.Id }),
            "复用一个、只新准备一个视觉资源不能记录整个对象资源就绪。");
        using var prepared = await Prepare(display, frame);
        Require(ReferenceEquals(prepared.Objects[source.Id].Visuals[0], reused) &&
            prepared.Objects[source.Id].Visuals.Length == 3,
            "多视觉对象必须保留已显示的视觉资源，并准备其余两个视觉资源。");
        Require(events.Count == 3 && events[2].Stage == SceneDisplayStage.FramePrepared && events[1].Object == source.Id &&
            events[1].Stage == SceneDisplayStage.ResourcesPrepared,
            "全部视觉资源准备完才记录一次对象资源就绪。");
        prepared.Commit();
        Require(events.Count == 4 && events[3].Stage == SceneDisplayStage.FrameCommitted &&
            events[3].Object is null,
            "帧提交应在对象资源就绪与整帧准备完成之后记录一次。");
    }

    [Test]
    public async Task Preparation_observation_is_inside_the_measured_span()
    {
        await new UiTestInput(TestScene).Frames(2);
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, []);
        await Commit(display, first);
        long now = 0;
        display.Timestamp = () => now;
        display.Observe = value =>
        {
            if (value.Stage == SceneDisplayStage.FramePreparationStarted)
                now += Stopwatch.Frequency * 7 / 1000;
        };
        List<TimeSpan> durations = [];
        display.Measure = (work, elapsed) =>
        {
            if (work == SceneDisplayWork.Prepare) durations.Add(elapsed);
        };
        var next = Frame(2, [], first.Update.Frame, space: 2);
        using var prepared = display.Prepare(next);
        Require(durations.Count == 1 && Math.Abs(durations[0].TotalMilliseconds - 7) < .001,
            "完整复用匹配的观测及其 GC 必须处于 Prepare 的同一计时区间内。");
        Require(ReferenceEquals(display.DisplayedFrame, first), "准备不得提前提交显示帧。");
        Require(prepared is not null, "空增量应完成准备。");
        prepared!.Commit();
        Require(ReferenceEquals(display.DisplayedFrame, next), "观测不改变正式提交的帧。");
    }
}
