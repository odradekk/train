using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Development;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Godot.Tests.Host;

namespace Train.Godot.Tests.Development;

[TestClass]
public sealed class DeveloperToolsTests
{
    private HostFixture? _fixture;
    public TestContext TestContext { get; set; } = null!;

    private Run CreateRun(Func<CoreEvent, NotificationAction>? notification = null, Func<double>? seconds = null)
    {
        _fixture = new(notification);
        return new(_fixture, seconds);
    }

    [TestCleanup]
    public void VerifyHostLogs()
    {
        if (TestContext.CurrentTestOutcome == UnitTestOutcome.Passed) _fixture?.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    public void AtStepStopsOnExactBoundaryWithoutAdvancingAgain()
    {
        using var run = CreateRun();
        run.Queue(View(1));
        run.Queue(View(2));
        Assert.IsNull(run.Tools.StartCondition(new DeveloperCondition.AtStep(2)));
        run.Tools.Process();
        Assert.IsFalse(run.Tools.Running);
        Assert.AreEqual(new SimulationPosition(2, 0), run.Tools.Status!.View.Position);
        Assert.AreEqual(2, run.Session.AdvanceCount);
        CollectionAssert.AreEqual(new[] { "已到达目标步" }, run.Tools.StopReasons.ToArray());
        run.Tools.Process();
        Assert.AreEqual(2, run.Session.AdvanceCount);
        run.Session.AssertComplete();
    }

    [TestMethod]
    public void EventConditionIgnoresEarlierEventAndCollectsConcurrentLimitAndPauseReasons()
    {
        using var run = CreateRun(_ => NotificationAction.Pause);
        run.Queue(View(1), [new(new(1, 0), 0, new CoreEvent.TrainArrived(new(1), new(7)))]);
        Assert.AreEqual(1ul, run.Fixture.Host.Step(1).Completed);
        Assert.IsNull(run.Tools.StartCondition(new DeveloperCondition.Event(nameof(CoreEvent.TrainArrived)), 2));
        run.Queue(View(2));
        run.Queue(View(3), [new(new(3, 0), 0, new CoreEvent.TrainArrived(new(1), new(7)))]);
        run.Tools.Process();
        Assert.AreEqual(3, run.Session.AdvanceCount);
        Assert.IsFalse(run.Tools.Running);
        CollectionAssert.AreEquivalent(new[] { "已发生领域事件：TrainArrived", "已达到最大步数", "通知要求暂停" },
            run.Tools.StopReasons.ToArray());
        Assert.IsTrue(run.Tools.Status!.PlayerPaused);
        run.Session.AssertComplete();
    }

    [TestMethod]
    public void RejectedConditionFiltersKindStableSubjectAndOnlyNewReceipts()
    {
        foreach (var subject in new WorldObjectId?[] { null, Target })
        {
            using var run = CreateRun(seconds: OneStepPerFrame());
            var records = ImmutableArray.Create(Rejected("wanted", Target, new(0, 0)));
            run.Session.ReadRecordingCall = () => new(run.Session.CurrentView.Position, [], records, [], [], [], HostCoreScenario.References);
            Assert.IsNull(run.Tools.StartCondition(new DeveloperCondition.Rejected("wanted", subject)));
            run.Queue(View(1));
            run.Tools.Process();
            Assert.IsTrue(run.Tools.Running, "启用前的拒绝不能触发条件。");
            records = records.Add(Rejected("other", Target, new(1, 1)));
            if (subject is not null) records = records.Add(Rejected("wanted", new WorldObjectId.Node(new(8)), new(1, 2)));
            run.Queue(View(2));
            run.Tools.Process();
            Assert.IsTrue(run.Tools.Running, "其他kind或其他对象不能触发条件。");
            records = records.Add(Rejected("wanted", Target, new(2, 1)));
            run.Queue(View(3));
            run.Tools.Process();
            Assert.IsFalse(run.Tools.Running);
            CollectionAssert.AreEqual(new[] { "匹配命令被拒绝" }, run.Tools.StopReasons.ToArray());
            run.Session.AssertComplete();
            run.Fixture.AssertNoUnexpectedFailures();
        }
    }

    [TestMethod]
    public void ObjectFaultMatchesSubjectAndFreezeTakesDisplayPriority()
    {
        foreach (var subject in new WorldObjectId[] { Target, new WorldObjectId.Node(new(8)) })
        {
            using var run = CreateRun();
            Assert.IsNull(run.Tools.StartCondition(new DeveloperCondition.ObjectFault(Target)));
            var fault = new CoreFault("fixture:fault", new(0, 0), "Advance", "field", subject);
            run.Queue(View(0, fault: fault), completed: 0);
            run.Tools.Process();
            Assert.IsFalse(run.Tools.Running);
            Assert.IsTrue(run.Tools.Status!.IsFrozen);
            Assert.AreEqual(new SimulationPosition(0, 0), run.Tools.Status.View.Position);
            Assert.AreEqual(1, run.Session.AdvanceCount);
            StringAssert.StartsWith(run.Tools.LastMessage!, "核心已冻结");
            Assert.AreEqual(subject == Target, run.Tools.StopReasons.Contains("目标对象发生内部故障"));
            run.Session.AssertComplete();
            run.Fixture.AssertNoUnexpectedFailures();
        }
    }

    [TestMethod]
    public void MissingTargetStopsWithoutChangingItsStableIdentity()
    {
        using var run = CreateRun();
        Assert.IsNull(run.Tools.StartCondition(new DeveloperCondition.ObjectFault(Target)));
        run.Queue(View(1, missing: true));
        run.Tools.Process();
        Assert.IsFalse(run.Tools.Running);
        Assert.AreEqual(1, run.Session.AdvanceCount);
        var inspection = run.Tools.Inspect(Target)!;
        Assert.AreEqual(Target, inspection.Id);
        Assert.AreEqual("missing", inspection.Status);
        StringAssert.StartsWith(run.Tools.StopReasons.Single(), "目标失效：");
        run.Session.AssertComplete();
    }

    [TestMethod]
    public void CancelDoesNotAdvanceAndReplacementClearsConditionButKeepsPointsAndMarks()
    {
        using var run = CreateRun();
        run.SetCapture();
        Assert.IsNull(run.Tools.CaptureCheckpoint("retained"));
        Assert.IsNull(run.Tools.AddMark("retained mark"));
        Assert.IsNull(run.Tools.StartSteps(5));
        run.Tools.Cancel();
        run.Tools.Process();
        Assert.AreEqual(0, run.Session.AdvanceCount);
        Assert.IsFalse(run.Tools.Running);
        Assert.IsNull(run.Tools.StartCondition(new DeveloperCondition.AtStep(5)));
        var binding = run.Tools.Status!.Binding;
        var previous = run.Session;
        run.Replace(View(0));
        run.Tools.Process();
        Assert.AreNotEqual(binding, run.Tools.Status!.Binding);
        Assert.IsFalse(run.Tools.Running);
        Assert.AreEqual(0, run.Session.AdvanceCount);
        Assert.AreEqual(0, previous.AdvanceCount);
        Assert.AreEqual(1, previous.DisposeCount);
        Assert.AreEqual("retained", run.Tools.Checkpoints.Single().Label);
        Assert.AreEqual(binding, run.Tools.Marks.Single().Binding);
        StringAssert.Contains(run.Tools.LastMessage!, "会话已切换");
    }

    [TestMethod]
    public void SpecifiedStepsFinishAcrossFramesWithinFiveMillisecondBudget()
    {
        using var run = CreateRun(seconds: OneStepPerFrame());
        run.Queue(View(1));
        run.Queue(View(2));
        run.Queue(View(3));
        Assert.IsNull(run.Tools.StartSteps(3));
        for (var frame = 1; frame <= 3; frame++)
        {
            run.Tools.Process();
            Assert.AreEqual(frame, run.Session.AdvanceCount);
            Assert.AreEqual(frame < 3, run.Tools.Running);
        }
        CollectionAssert.AreEqual(new[] { "已完成指定步数" }, run.Tools.StopReasons.ToArray());
        run.Session.AssertComplete();
    }

    [TestMethod]
    public void StepRunYieldsAtFiveMillisecondsAndStopsImmediatelyForNotification()
    {
        using var run = CreateRun(_ => NotificationAction.Pause, OneStepPerFrame());
        run.Queue(View(1));
        run.Queue(View(2), [new(new(2, 0), 0, new CoreEvent.TrainArrived(new(1), new(7)))]);
        Assert.IsNull(run.Tools.StartSteps(3));
        run.Tools.Process();
        Assert.IsTrue(run.Tools.Running);
        Assert.AreEqual(1, run.Session.AdvanceCount);
        run.Tools.Process();
        Assert.IsFalse(run.Tools.Running);
        Assert.AreEqual(2, run.Session.AdvanceCount);
        CollectionAssert.AreEqual(new[] { "通知要求暂停" }, run.Tools.StopReasons.ToArray());
        run.Session.AssertComplete();
    }

    [TestMethod]
    public void CheckpointComparabilityChecksMetadataInOrderBeforeCallingCore()
    {
        using var run = CreateRun();
        run.SetCapture();
        Assert.IsNull(run.Tools.CaptureCheckpoint("baseline"));
        var otherRuntime = HostFixture.Options.Runtime with { GameBuild = "other-build" };
        var otherInput = new CoreInputId("other-input");
        var cases = new[]
        {
            (View: View(1, seed: 2, input: otherInput), Runtime: otherRuntime, Message: "不可比：模拟位置不同。"),
            (View: View(0, seed: 2, input: otherInput), Runtime: otherRuntime, Message: "不可比：世界来源不同。"),
            (View: View(0, input: otherInput), Runtime: otherRuntime, Message: "不可比：运行环境不同。"),
            (View: View(0, input: otherInput), Runtime: HostFixture.Options.Runtime, Message: "内容适配：核心输入不同。")
        };
        foreach (var item in cases)
        {
            run.Replace(item.View, item.Runtime);
            run.SetCapture();
            Assert.IsNull(run.Tools.CaptureCheckpoint(item.Message));
            Assert.AreEqual(item.Message, run.Tools.Compare(0, run.Tools.Checkpoints.Count - 1));
        }
        Assert.AreEqual(0, run.Fixture.Core.CompareCheckpointsCount);
        run.Replace(View(0));
        run.SetCapture();
        Assert.IsNull(run.Tools.CaptureCheckpoint("comparable"));
        run.Fixture.Core.CompareCheckpointsCall = (receivedLeft, receivedRight) =>
        {
            Assert.AreSame(run.Tools.Checkpoints[0].Checkpoint, receivedLeft);
            Assert.AreSame(run.Tools.Checkpoints[^1].Checkpoint, receivedRight);
            return new CoreResult<CheckpointComparison>.Success(new CheckpointComparison.Different(
                new(new(0, 0), "first-field", "expected-value", "actual-value")));
        };
        var message = run.Tools.Compare(0, run.Tools.Checkpoints.Count - 1);
        StringAssert.Contains(message, "首个差异");
        StringAssert.Contains(message, "first-field");
        StringAssert.Contains(message, "expected-value → actual-value");
        Assert.AreEqual(1, run.Fixture.Core.CompareCheckpointsCount);
    }

    private static WorldObjectId.Node Target => new(new(7));
    private static Func<double> OneStepPerFrame()
    {
        var reads = 0;
        return () => reads++ * 0.006;
    }
    private static FixedCoreReadView View(ulong step, ulong seed = 1, CoreInputId? input = null,
        CoreFault? fault = null, bool missing = false)
    {
        var node = new NodeView(new(new(7), new(1, 2), new(new("fixture:terrain"), 0, []),
            new NodeOccupant.Empty(), [], 0, 1, null), []);
        return new(new(step, 0), input ?? HostCoreScenario.Input, fault)
        {
            World = new(seed, new("fixture:railway"), 100, 100, [], [], [], [], []),
            Nodes = new Dictionary<NodeId, Lookup<NodeView>>
            { [Target.Id] = missing ? new Lookup<NodeView>.Missing() : new Lookup<NodeView>.Found(node) }
        };
    }
    private static RecordedCommand Rejected(string kind, WorldObjectId subject, SimulationPosition after)
        => new(CommandSource.Console, "{\"kind\":\"" + kind + "\"}",
            new(1, new(0, 0), after, new CommandDecision.Rejected([new("rejected", "field", subject, [])])),
            HostCoreScenario.Input, HostCoreScenario.Input);

    private sealed class Run : IDisposable
    {
        internal HostFixture Fixture { get; }
        internal ScriptedCoreSession Session { get; private set; } = null!;
        internal DeveloperTools Tools { get; }
        private readonly SaveService _saves;
        internal Run(HostFixture fixture, Func<double>? seconds)
        {
            Fixture = fixture;
            Replace(View(0));
            var store = new FileSaveStore(Path.GetTempPath());
            _saves = new(Fixture.Host, store, () => 0);
            Tools = new(Fixture.Host, _saves, store, seconds ?? (() => 0));
        }
        internal void Replace(FixedCoreReadView view, RuntimeIdentity? runtime = null)
        {
            Session = new(view);
            var session = Session;
            Fixture.Core.PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(
                new ScriptedCoreCandidate(session, HostCoreScenario.References));
            Fixture.Factory.PrepareLoadCall = (_, _, _) => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(Fixture.Content));
            var operation = Fixture.Host.BeginRestore("core-state", "content-state",
                HostFixture.Options with { Runtime = runtime ?? HostFixture.Options.Runtime });
            Assert.IsNull(operation.Problem);
            Fixture.PumpUntil(() => !Fixture.Host.IsBusy);
            Assert.IsNotNull(Fixture.Host.GetStatus());
        }
        internal void Queue(FixedCoreReadView after, ImmutableArray<EventEnvelope> events = default, uint completed = 1)
            => Session.QueueAdvance(new(completed, after.Position, events.IsDefault ? [] : events, after.Fault, null), after);
        internal void SetCapture()
        {
            var view = Session.CurrentView;
            Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(new FixedCoreCapture(
                new(view.Position, "canonical", "hash"), HostCoreScenario.References, "opaque state", view.InputIdentity, view.Revision)));
        }
        public void Dispose() { Tools.Dispose(); _saves.Dispose(); Fixture.Dispose(); }
    }
}
