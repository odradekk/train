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
    public async Task Idle_candidate_elapsed_time_exhausts_the_budget_before_the_first_work_item()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        await new UiTestInput(TestScene).Frames(1);
        var reads = 0;
        var elapsed = Stopwatch.Frequency * 4 / 1000;
        display.Timestamp = () => reads++ == 0 ? 0 : elapsed;
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        display.Timestamp = () => elapsed;
        List<SceneDisplayEvent> events = [];
        display.Observe = events.Add;
        using var prepared = display.Prepare(Frame(1, []));
        Require(prepared is null && events.Count == 0,
            "即使尚未推进资源项，空闲调用本身已耗尽4ms时，本帧也不能再强制开始第一项。");
    }

    [Test]
    public async Task Idle_candidate_calls_are_measured_without_including_the_measure_callback()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        long now = 0;
        display.Timestamp = () => now;
        List<TimeSpan> durations = [];
        display.Measure = (work, elapsed, _) =>
        {
            if (work != SceneDisplayWork.PrepareCandidate) return;
            durations.Add(elapsed);
            now += Stopwatch.Frequency * 7 / 1000;
        };
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        Require(durations.Count == 2 && durations.All(value => value == TimeSpan.Zero),
            "每次候选调用都应计时，Measure回调自身的7ms不能计入本次或下一次准备。");
    }

    [Test]
    public async Task Loading_cancellation_includes_synchronous_packing_cleanup_in_preparation_time() =>
        await CancellationIncludesCleanup(loading: true);

    [Test]
    public async Task Full_replacement_cancellation_consumes_the_shared_budget_before_new_preparation() =>
        await CancellationIncludesCleanup(loading: false);

    private async Task CancellationIncludesCleanup(bool loading)
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        var first = Frame(1, [Object(1, Peak(1))]);
        await Commit(display, first);
        var original = display.Objects[new(1)].Visuals[0];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long now = 0;
        display.PackMesh = (mesh, token) =>
        {
            using var registration = token.Register(() => Interlocked.Add(ref now, Stopwatch.Frequency * 7 / 1000));
            entered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(30), token).GetAwaiter().GetResult();
            return PackedSceneMesh.Create(mesh, token);
        };
        Task packing = Task.CompletedTask;
        try
        {
            var pending = Frame(2, [Object(2, Peak(2))], first.Update.Frame);
            await new UiTestInput(TestScene).Frames(1);
            display.Timestamp = () => Interlocked.Read(ref now);
            using var partial = display.Prepare(pending);
            Require(partial is null, "准备必须停在受控后台打包。");
            packing = display.PreparationPacking;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await new UiTestInput(TestScene).Frames(1);
            List<TimeSpan> durations = [];
            List<SceneDisplayEvent> events = [];
            display.Measure = (work, elapsed, _) => { if (work == SceneDisplayWork.Prepare) durations.Add(elapsed); };
            display.Observe = events.Add;
            using var cancelled = display.Prepare(Frame(3, [], loading ? first.Update.Frame : null,
                readiness: loading ? SceneReadiness.Loading : SceneReadiness.Ready));
            Require(cancelled is null && durations.Count == 1 && Math.Abs(durations[0].TotalMilliseconds - 7) < .001,
                "取消引发的同步7ms打包清理必须计入本次Prepare，不能漏报为零或没有调用。");
            Require(display.IsPreparing != loading && ReferenceEquals(display.DisplayedFrame, first) &&
                ReferenceEquals(display.Objects[new(1)].Visuals[0], original) && original.Node.IsInsideTree(),
                "取消待准备工作必须保留D及其视觉资源。");
            using var followup = display.Prepare(Frame(4, [], first.Update.Frame));
            Require(followup is null && events.All(item => item.Stage != SceneDisplayStage.FramePreparationStarted),
                "取消已耗尽本帧共享预算后，不能强制开始新候选的初始化并发出匹配完成事件。");
        }
        finally
        {
            release.TrySetResult();
            await packing.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
