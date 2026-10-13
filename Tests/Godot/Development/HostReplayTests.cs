using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Development;
using Train.App.Recording;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Godot.Tests.Host;

namespace Train.Godot.Tests.Development;

[TestClass]
public sealed class HostReplayTests
{
    [TestMethod]
    public void InputSelectionPrefersFaultAndOtherwiseUsesEarliestContinuousStart()
    {
        using var run = new ReplayFixture();
        var early = new RecordingStart(new(1), run.Host.Capture(new(0, 0)));
        var late = new RecordingStart(new(2), new FixedCoreCapture(new(new(3, 0), "late checkpoint", "late digest"),
            HostCoreScenario.References, "late state", HostCoreScenario.Input, new(2)));
        var window = ReplayFixture.Window(early) with { Starts = [late, early], EndPosition = new(5, 0),
            Gaps = [new(new(1, 0), new(3, 0), "write_failed")] };
        var package = run.PackageFor(window);
        Assert.AreEqual(late.State.Encode(), HostCoordinator.SelectReplayInput(package).InitialStatePayload);
        Assert.AreEqual(early.State.Encode(), HostCoordinator.SelectReplayInput(run.PackageFor(window with { Gaps = [] })).InitialStatePayload);
        Assert.ThrowsExactly<InvalidDataException>(() => HostCoordinator.SelectReplayInput(run.PackageFor(window with
            { Gaps = [new(new(1, 0), null, "write_failed")] })));
        var fault = new CoreFault("fault", new(2, 4), "Submit", "Target", null);
        var faultCommand = ReplayFixture.Command(new(2, 3), fault.Position);
        var faultPackage = run.PackageFor(window with { FaultSegments = [new(early, [faultCommand], [], fault, HostCoreScenario.References)] });
        var input = HostCoordinator.SelectReplayInput(faultPackage);
        var recordedFault = input.ExpectedFault;
        Assert.AreEqual(fault, recordedFault);
        Assert.AreEqual(fault.Position, input.EndPosition);
        Assert.AreSame(faultCommand, input.Commands[0]);
    }

    [TestMethod]
    public void OpenRetiresSessionKeepsSameInputHistoryAndRejectsMutablePorts()
    {
        using var run = new ReplayFixture();
        var second = new FixedContentView(run.Host.Content.Core, new(2), new(2));
        run.History = [run.Host.Content, second];
        run.Open();
        Assert.AreEqual(1, run.Host.Session.DisposeCount);
        Assert.AreEqual(2, run.PreparedHistory.Length);
        Assert.AreSame(run.Host.Content.Core, run.PreparedHistory[0]);
        Assert.AreSame(second.Core, run.PreparedHistory[1]);
        Assert.AreSame(second, run.Host.Host.Content);
        var status = run.Host.Host.GetStatus()!;
        Assert.IsTrue(status.IsReplay);
        Assert.AreEqual("replay_read_only", ((SubmissionResult.NotAccepted)run.Host.Host.Submit(status.Binding,
            new CoreCommand.StartTrain(new(1)), CommandSource.UserInterface)).Reason.Code);
        Assert.AreEqual("replay_read_only", run.Host.Host.BeginReload(CommandSource.Console).Problem?.Code);
        Assert.AreEqual("replay_read_only", run.Host.Host.CaptureSave().Failure?.Code);
        using var saves = new SaveService(run.Host.Host, new FileSaveStore(Path.GetTempPath()));
        Assert.AreEqual("replay_read_only", saves.Save()?.Code);
        Assert.IsNull(run.Host.Host.ReadRecording(status.Binding));
        Assert.IsInstanceOfType<CoreResult<RecordingStart>.Failure>(run.Host.Host.CaptureRecordingStart(status.Binding));
        run.Host.Host.ProcessFrame(10);
        Assert.AreEqual(0, run.Replay.AdvanceCalls.Count);
        Assert.AreEqual(0, run.Host.Session.SubmitCount);
    }

    [TestMethod]
    public void ReaderAndMissingDependencyFailuresPreserveOriginalBinding()
    {
        using var run = new ReplayFixture();
        var original = run.Host.Host.GetStatus()!.Binding;
        Assert.IsNull(run.Host.Host.BeginReplay("memory.zip", _ => throw new InvalidDataException("bad package")).Problem);
        run.Host.PumpUntil(() => !run.Host.Host.IsBusy);
        Assert.AreEqual(original, run.Host.Host.GetStatus()!.Binding);
        StringAssert.Contains(run.Host.Host.ReplayMessage!, "bad package");
        run.Host.Core.PrepareReplayCall = (_, _, _) => new CoreResult<ICoreReplay>.Failure(
            [new("content_missing", "ContentHistory", null, [])]);
        Assert.IsNull(run.Host.Host.BeginReplay("memory.zip", _ => run.Package).Problem);
        run.Host.PumpUntil(() => !run.Host.Host.IsBusy);
        Assert.AreEqual(original, run.Host.Host.GetStatus()!.Binding);
        StringAssert.Contains(run.Host.Host.ReplayMessage!, "ContentHistory");
        Assert.AreEqual(0, run.Host.Session.DisposeCount);
    }

    [TestMethod]
    public void CancelledPreparationDisposesLateReplayWithoutReplacingNewGroup()
    {
        using var run = new ReplayFixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var late = new ScriptedCoreReplay(ReplayFixture.View(new(0, 0)));
        run.Host.Core.PrepareReplayCall = (_, _, _) =>
        {
            entered.Set();
            HostFixture.WaitForSignal(release, "释放已取消的重放准备");
            return new CoreResult<ICoreReplay>.Success(late);
        };
        try
        {
            var first = run.Host.Host.BeginReplay("late.zip", _ => run.Package);
            HostFixture.WaitForSignal(entered, "迟到候选已进入核心准备");
            Assert.IsTrue(run.Host.Host.CancelReplay(first.Operation));
            run.ConfigureReplay();
            run.Open();
            var current = run.Host.Host.GetStatus()!.Binding;
            release.Set();
            run.Host.PumpUntil(() => late.DisposeCount == 1);
            Assert.AreEqual(current, run.Host.Host.GetStatus()!.Binding);
            Assert.AreEqual(0, run.Replay.DisposeCount);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public void DeveloperStepAndAdvanceToUseReplayAndPreserveSequence()
    {
        using var run = new ReplayFixture();
        var package = run.Package with { Commands = [ReplayFixture.Command(new(1, 0), new(1, 1)),
            ReplayFixture.Command(new(1, 1), new(1, 2)), ReplayFixture.Command(new(1, 2), new(1, 3))] };
        Assert.IsNull(run.Host.Host.BeginReplay("memory.zip", _ => package).Problem);
        run.Host.PumpUntil(() => run.Host.Host.GetStatus()?.IsReplay == true);
        var store = new FileSaveStore(Path.GetTempPath());
        using var saves = new SaveService(run.Host.Host, store, () => 0);
        using var tools = new DeveloperTools(run.Host.Host, saves, store, () => 0);
        run.Replay.QueueAdvance(1, null, new(ReplayStop.BudgetReached, new(1, 0), [], null, null), ReplayFixture.View(new(1, 0)));
        Assert.IsNull(tools.StartSteps(1));
        tools.Process();
        Assert.IsFalse(tools.Running);
        Assert.AreEqual(new SimulationPosition(1, 0), run.Host.Host.GetStatus()!.View.Position);
        var target = new SimulationPosition(1, 3);
        run.Replay.QueueAdvance(1, target, new(ReplayStop.RequestedPosition, target, [], null, null), ReplayFixture.View(target));
        Assert.IsNull(tools.AdvanceTo(target));
        tools.Process();
        Assert.IsFalse(tools.Running);
        Assert.AreEqual(target, run.Host.Host.GetStatus()!.View.Position);
        Assert.IsTrue(run.Host.Host.GetStatus()!.PlayerPaused);
        Assert.AreEqual((1U, (SimulationPosition?)target), run.Replay.AdvanceCalls[1]);
    }

    [TestMethod]
    public void NonexistentSequenceIsRejectedBeforeDrivingReplay()
    {
        using var run = new ReplayFixture();
        run.Open();
        var store = new FileSaveStore(Path.GetTempPath());
        using var saves = new SaveService(run.Host.Host, store, () => 0);
        using var tools = new DeveloperTools(run.Host.Host, saves, store);
        Assert.AreEqual("invalid-position", tools.AdvanceTo(new(1, 99))?.Code);
        Assert.IsFalse(tools.Running);
        tools.Process();
        Assert.AreEqual(0, run.Replay.AdvanceCalls.Count);
    }

    [TestMethod]
    public void ReplayCollectsEndNotificationAndStepLimitAtTheSameBoundary()
    {
        using var run = new ReplayFixture(_ => NotificationAction.Pause);
        var package = run.Package with { Header = run.Package.Header with { EndPosition = new(1, 0) } };
        Assert.IsNull(run.Host.Host.BeginReplay("memory.zip", _ => package).Problem);
        run.Host.PumpUntil(() => run.Host.Host.GetStatus()?.IsReplay == true);
        run.Replay.QueueAdvance(1, null, new(ReplayStop.EndOfRecording, new(1, 0),
            [new(new(1, 0), 0, new CoreEvent.TrainArrived(new(1), new(7)))], null, null), ReplayFixture.View(new(1, 0)));
        var store = new FileSaveStore(Path.GetTempPath());
        using var saves = new SaveService(run.Host.Host, store, () => 0);
        using var tools = new DeveloperTools(run.Host.Host, saves, store);
        Assert.IsNull(tools.StartSteps(1));
        tools.Process();
        Assert.IsFalse(tools.Running);
        Assert.AreEqual(3, tools.StopReasons.Count);
        Assert.IsTrue(tools.StopReasons.Any(reason => reason.Contains(nameof(ReplayStop.EndOfRecording), StringComparison.Ordinal)));
        CollectionAssert.Contains(tools.StopReasons.ToArray(), "通知要求暂停");
        CollectionAssert.Contains(tools.StopReasons.ToArray(), "已完成指定步数");
        Assert.AreEqual(1, run.Replay.AdvanceCalls.Count);
    }

    [TestMethod]
    public void ExplicitReplayAdvancePreservesInStepPositionAndEveryStopDetail()
    {
        foreach (var stop in Enum.GetValues<ReplayStop>())
        {
            using var run = new ReplayFixture();
            run.Open();
            var position = new SimulationPosition(0, 3);
            var difference = stop == ReplayStop.FirstDifference ? new CheckpointDifference(position, "/world/test", "a", "b") : null;
            var fault = stop is ReplayStop.RecordedFault or ReplayStop.UnexpectedFault
                ? new CoreFault("test_fault", position, "Advance", "Target", null) : null;
            var expected = new ReplayAdvanceResult(stop, position, [], difference, fault);
            run.Replay.QueueAdvance(0, position, expected, ReplayFixture.View(position, fault));
            var result = run.Host.Host.AdvanceReplay(0, position);
            Assert.AreSame(expected, result);
            Assert.AreEqual(position, run.Host.Host.GetStatus()!.View.Position);
            Assert.AreEqual((0U, (SimulationPosition?)position), run.Replay.AdvanceCalls.Single());
            StringAssert.Contains(run.Host.Host.ReplayMessage!, stop.ToString());
            if (difference is not null) StringAssert.Contains(run.Host.Host.ReplayMessage!, difference.Field);
            if (fault is not null) Assert.IsTrue(run.Host.Host.GetStatus()!.IsFrozen);
        }
    }
}

internal sealed class ReplayFixture : IDisposable
{
    internal HostFixture Host { get; }
    internal ScriptedCoreReplay Replay { get; } = new(View(new(0, 0)));
    internal ImmutableArray<IContentView> History { get; set; }
    internal ImmutableArray<CoreContentProjection> PreparedHistory { get; private set; }
    internal RecordingPackage Package { get; }

    internal ReplayFixture(Func<CoreEvent, NotificationAction>? notification = null)
    {
        Host = new(notification);
        Host.ReadyHost();
        History = [Host.Content];
        Host.Factory.DecodeReplayCall = payload => payload == "replay-content"
            ? new ContentResult<IContentReplayCapture>.Success(new FixedContentReplayCapture(History, payload))
            : throw new InvalidDataException("wrong replay content");
        Package = PackageFor(Window(new(new(1), Host.Capture(new(0, 0)))));
        ConfigureReplay();
    }

    internal void ConfigureReplay() => Host.Core.PrepareReplayCall = (input, runtime, history) =>
    {
        Assert.AreEqual(HostFixture.Options.Runtime, runtime);
        Assert.AreEqual(HostFixture.Options.Runtime, input.OriginalRuntime);
        PreparedHistory = history;
        return new CoreResult<ICoreReplay>.Success(Replay);
    };

    internal void Open(string path = "memory.zip", bool fromFile = false)
    {
        Assert.IsNull(Host.Host.BeginReplay(path, fromFile ? null : _ => Package).Problem);
        Host.PumpUntil(() => Host.Host.GetStatus()?.IsReplay == true);
    }

    internal RecordingPackage PackageFor(CoreRecordingWindow window)
        => RecordingPackage.Capture(window, new FixedContentReplayCapture(History, "replay-content"), HostFixture.Options.Runtime);
    internal static CoreRecordingWindow Window(RecordingStart start)
        => new(new(5, 0), [start], [], [], [], [], HostCoreScenario.References);
    internal static RecordedCommand Command(SimulationPosition before, SimulationPosition after)
        => new(CommandSource.Console, "{\"kind\":\"train.start\",\"train\":1}",
            new(1, before, after, new CommandDecision.Accepted([], [])), HostCoreScenario.Input, HostCoreScenario.Input);
    internal static FixedCoreReadView View(SimulationPosition position, CoreFault? fault = null)
        => new(position, HostCoreScenario.Input, fault, new(1))
        { World = new(17, new("fixture:railway"), 100, 100, [], [], [], [], []) };
    public void Dispose() => Host.Dispose();
}
