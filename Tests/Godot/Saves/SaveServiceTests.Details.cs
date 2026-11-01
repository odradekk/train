using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Godot.Tests.Saves;

public sealed partial class SaveServiceTests
{
    [TestMethod]
    public void Changing_selection_discards_late_details_and_releases_both_candidates()
    {
        using var run = new Run();
        var source = run.Source();
        var first = run.ConfigureRestore(view: DetailView());
        var second = new ScriptedCoreCandidate(new ScriptedCoreSession(DetailView(54321)), HostCoreScenario.References);
        var secondContent = new ScriptedContentCandidate(run.Host.Content);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var loads = 0;
        run.Host.Load = _ => new ContentResult<IContentCandidate>.Success(Interlocked.Increment(ref loads) == 1 ? first.Content : secondContent);
        run.Host.Core.PrepareRestoreCall = (_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) != 1) return new CoreResult<ICoreSessionCandidate>.Success(second);
            entered.Set();
            Train.Godot.Tests.Host.HostFixture.WaitForSignal(release, "release first details");
            return new CoreResult<ICoreSessionCandidate>.Success(first.Core);
        };
        var entry = new SaveEntry(source, run.Store.ReadHeader(source), false);
        try
        {
            run.Service.Inspect(entry);
            Train.Godot.Tests.Host.HostFixture.WaitForSignal(entered, "first details entered");
            run.Service.Inspect(entry);
        }
        finally { release.Set(); }
        run.PumpUntil(() => !run.Service.IsReadingDetails);
        Assert.AreEqual(54321L, run.Service.Details?.Funds);
        Assert.AreEqual(1, first.Core.DisposeCount);
        Assert.AreEqual(1, first.Content.Disposals);
        Assert.AreEqual(1, second.DisposeCount);
        Assert.AreEqual(1, secondContent.Disposals);
    }

    [TestMethod]
    public void Broken_selected_file_reports_a_detail_failure_without_changing_the_session()
    {
        using var run = new Run();
        var source = run.Source();
        var entry = new SaveEntry(source, run.Store.ReadHeader(source), false);
        var before = run.Service.Status;
        File.WriteAllText(source, "broken file");
        run.Service.Inspect(entry);
        run.PumpUntil(() => !run.Service.IsReadingDetails);
        Assert.IsNull(run.Service.Details);
        StringAssert.Contains(run.Service.DetailsMessage!, "无法读取详情");
        Assert.AreEqual(before, run.Service.Status);
        Assert.IsFalse(run.Service.IsBusy);
    }

    [TestMethod]
    [DataRow("load")]
    [DataRow("rename")]
    [DataRow("delete")]
    [DataRow("export")]
    public void File_actions_cancel_pending_details_and_dispose_the_unactivated_candidates(string action)
    {
        using var run = new Run();
        var source = run.Source();
        File.Copy(source, run.Store.PathFor(run.Id));
        var entry = run.Service.List().Single();
        var candidates = run.ConfigureRestore(view: DetailView());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var restore = run.Host.Core.PrepareRestoreCall;
        run.Host.Core.PrepareRestoreCall = (saved, content, options) =>
        {
            entered.Set();
            Train.Godot.Tests.Host.HostFixture.WaitForSignal(release, "release detail preparation");
            return restore!(saved, content, options);
        };
        try
        {
            run.Service.Inspect(entry);
            Train.Godot.Tests.Host.HostFixture.WaitForSignal(entered, "detail preparation entered");
            Assert.IsNull(action switch
            {
                "load" => run.Service.Load(entry),
                "rename" => run.Service.Rename(entry, "新名称"),
                "delete" => run.Service.Delete(entry),
                _ => run.Service.Export(entry, Path.Combine(run.Root, "export.json"))
            });
            Assert.IsFalse(run.Service.IsReadingDetails);
            run.Service.ChooseLeave(LeaveChoice.Cancel);
        }
        finally
        {
            release.Set();
            run.Host.Host.BackgroundWork.Enqueue(() => true).GetAwaiter().GetResult();
        }
        run.Drain();
        Assert.IsNull(run.Service.Details);
        Assert.AreEqual(0, candidates.Core.ActivationCount);
        Assert.AreEqual(1, candidates.Core.DisposeCount);
        Assert.AreEqual(1, candidates.Content.Disposals);
    }

    [TestMethod]
    public void Selected_save_details_are_read_without_activating_or_blocking_the_current_session()
    {
        using var run = new Run();
        var source = run.Source();
        var candidates = run.ConfigureRestore(view: DetailView());
        PackageMetadata package = new(new("fixture:railway"), "山间铁路", "1.0", [], []);
        run.Host.Factory.DecodeCaptureCall = value => new ContentResult<IContentCapture>.Success(
            new FixedContentCapture(value, referencedPackages: [package]));
        var before = run.Service.Status;
        var entry = new SaveEntry(source, run.Store.ReadHeader(source), false);
        run.Service.Inspect(entry);
        Assert.IsTrue(run.Service.IsReadingDetails);
        Assert.IsFalse(run.Service.IsBusy);
        Assert.IsFalse(run.Host.Host.IsBusy);
        run.PumpUntil(() => !run.Service.IsReadingDetails);
        var details = run.Service.Details!;
        Assert.IsNotNull(details, run.Service.DetailsMessage);
        Assert.AreEqual(12345L, details.Funds);
        Assert.AreEqual(1, details.OwnedSettlements);
        Assert.AreEqual(2, details.World.Trains.Length);
        Assert.AreEqual("山间铁路", details.Packages.Single().Name);
        Assert.AreEqual(before, run.Service.Status);
        Assert.AreEqual(0, candidates.Core.ActivationCount);
        Assert.AreEqual(0, candidates.Content.Activations);
        Assert.AreEqual(1, candidates.Core.DisposeCount);
        Assert.AreEqual(1, candidates.Content.Disposals);
    }

    private static FixedCoreReadView DetailView(long funds = 12345)
    {
        var rule = new RuleStatusView(new("fixture:rule"), null, []);
        NodeMapItem Node(ulong id, SettlementRelation relation) => new(new(id), new(id * 10, 10),
            new(new("fixture:terrain"), 0, []), new NodeOccupant.Settlement(new(id), relation), [], 0, 1, null);
        TrainMapItem Train(ulong id) => new(new(id), new TrainPosition.Edge(new(1), 1, TravelDirection.Forward, 1), TrainActivity.Stopped);
        return new(new(120, 0), HostCoreScenario.Input)
        {
            World = new(40, new("fixture:railway"), 100, 100,
                [Node(1, SettlementRelation.PlayerOwned), Node(2, SettlementRelation.NonHostile), Node(3, SettlementRelation.NonHostile)],
                [], [Train(1), Train(2)], [], []),
            Economy = new(new(funds), rule, rule, rule, new(0), [])
        };
    }
}
