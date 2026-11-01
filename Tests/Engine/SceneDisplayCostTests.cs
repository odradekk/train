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
    public async Task Candidate_transfers_need_no_geometry_generation_allowance()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        using var burner = Attach(cache, new FixedContentView());
        var first = Frame(1, []);
        await Commit(display, first);
        await Commit(burner, first);
        var a = Object(1, Peak(1));
        var b = Object(2, Peak(2));
        var candidate = Frame(2, [a, b], space: 2);
        display.ReceiveCandidate(candidate.Update);
        await CompleteCandidate(display);
        var promoted = Frame(3, [a], space: 2);
        await Commit(display, promoted);
        Require(display.CandidateProgress(candidate.Update.Frame) is (1, 2), "晋升A后，候选仍持有已完成的B。");
        await new UiTestInput(TestScene).Frames(1);
        SpendPreparationTime(burner, first, 2);
        display.Timestamp = () => 0;
        var transfer = Frame(4, [b], promoted.Update.Frame, space: 2);
        using (var prepared = display.Prepare(transfer))
        {
            Require(prepared is not null, "已有候选资源必须能在剩余预算中取用。");
            prepared!.Commit();
        }
        Require(display.CandidateProgress(candidate.Update.Frame) is (0, 2) && display.Objects.ContainsKey(b.Id),
            "取用必须转交B的现有视觉，不能另建一个却留下候选租约。");
    }

    [Test]
    public async Task Synchronous_new_space_preparation_is_atomic_and_trains_the_stage_estimates()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        using var burner = Attach(cache, new FixedContentView());
        var first = Frame(1, []);
        await Commit(display, first);
        await Commit(burner, first);
        await new UiTestInput(TestScene).Frames(1);
        long now = 0;
        display.Timestamp = () => now += Stopwatch.Frequency / 100;
        using (var prepared = display.Prepare(Frame(3, [Object(2, Peak(2))], space: 3)))
        {
            Require(prepared is not null, "新空间同步首帧仍不受预算限制。");
            prepared!.Commit();
        }
        var later = Frame(4, [Object(3, Peak(3)), Object(4, Peak(4))], space: 4);
        display.ReceiveCandidate(later.Update);
        await ScenePackingTestDriver.StartCandidate(display, TestScene);
        SpendPreparationTime(burner, first, 1);
        display.Timestamp = () => 0;
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        Require(display.CandidateProgress(later.Update.Frame) is (0, 2),
            "自然首帧已测的昂贵阶段必须保留，剩余3ms时不得把它当廉价项开始。");
        await new UiTestInput(TestScene).Frames(1);
        await CompleteCandidate(display);
        Require(display.PreparedCandidate == later.Update.Frame, "已测大项必须仍可在新帧首项推进。");
    }

    private static void SpendPreparationTime(SceneDisplay display, ISceneFrame baseline, int milliseconds)
    {
        var reads = 0;
        display.Timestamp = () => reads++ == 0 ? 0 : Stopwatch.Frequency * milliseconds / 1000;
        using var prepared = display.Prepare(Frame(100, [], baseline.Update.Frame));
        Require(prepared is not null, "空更新必须在给定耗时内完成并占用共享额度。");
    }
}
