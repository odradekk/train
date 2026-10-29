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
    public async Task Candidate_transfers_need_no_upload_allowance_and_preserve_the_upload_estimate()
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
        await ScenePackingTestDriver.StartCandidate(display, TestScene);
        display.Timestamp = () => display.CandidateProgress(candidate.Update.Frame)!.Value.Prepared * Stopwatch.Frequency * 3 / 1000;
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        Require(display.CandidateProgress(candidate.Update.Frame) is (1, 2), "先实测一次3ms的几何上传。");
        display.Timestamp = () => 0;
        var promoted = Frame(3, [a], space: 2);
        await Commit(display, promoted);
        await ScenePackingTestDriver.StartCandidate(display, TestScene);
        display.Timestamp = () => display.CandidateProgress(candidate.Update.Frame)!.Value.Prepared * Stopwatch.Frequency * 3 / 1000;
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        Require(display.CandidateProgress(candidate.Update.Frame) is (1, 2), "晋升后剩余的B也按3ms上传。");
        await new UiTestInput(TestScene).Frames(1);
        SpendPreparationTime(burner, first, 2);
        display.Timestamp = () => 0;
        var transfer = Frame(4, [b], promoted.Update.Frame, space: 2);
        using (var prepared = display.Prepare(transfer))
        {
            Require(prepared is not null, "剩余2ms不足上传，但已在候选内的视觉必须仍能取用。");
            prepared!.Commit();
        }
        var later = Frame(5, [Object(3, Peak(3)), Object(4, Peak(4))], space: 3);
        display.ReceiveCandidate(later.Update);
        await ScenePackingTestDriver.StartCandidate(display, TestScene);
        SpendPreparationTime(burner, first, 2);
        display.Timestamp = () => display.CandidateProgress(later.Update.Frame)!.Value.Prepared * Stopwatch.Frequency * 3 / 1000;
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        Require(display.CandidateProgress(later.Update.Frame) is (0, 2),
            "候选取用不能把3ms上传预估降为0；剩余2ms时不得开始真正的新上传。");
    }

    [Test]
    public async Task Synchronous_new_space_preparation_does_not_inflate_the_upload_estimate()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        using var display = Attach(cache, new FixedContentView());
        using var burner = Attach(cache, new FixedContentView());
        var first = Frame(1, []);
        await Commit(display, first);
        await Commit(burner, first);
        var training = Frame(2, [Object(1, Peak(1))], space: 2);
        display.ReceiveCandidate(training.Update);
        await ScenePackingTestDriver.StartCandidate(display, TestScene);
        display.Timestamp = () => display.CandidateProgress(training.Update.Frame)!.Value.Prepared * Stopwatch.Frequency * 3 / 1000;
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        Require(display.PreparedCandidate == training.Update.Frame, "先以3ms真实上传建立同类预估。");
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
        display.Timestamp = () => display.CandidateProgress(later.Update.Frame)!.Value.Prepared * Stopwatch.Frequency * 3 / 1000;
        display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
        Require(display.CandidateProgress(later.Update.Frame) is (1, 2),
            "同步路径包含打包的10ms不能替换3ms上传预估；剩余3ms时应准备一项。");
    }

    private static void SpendPreparationTime(SceneDisplay display, ISceneFrame baseline, int milliseconds)
    {
        var reads = 0;
        display.Timestamp = () => reads++ == 0 ? 0 : Stopwatch.Frequency * milliseconds / 1000;
        using var prepared = display.Prepare(Frame(100, [], baseline.Update.Frame));
        Require(prepared is not null, "空更新必须在给定耗时内完成并占用共享额度。");
    }
}
