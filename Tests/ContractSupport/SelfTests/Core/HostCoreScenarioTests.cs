using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.ContractSupport.Tests.Core;

[TestClass]
public sealed class HostCoreScenarioTests
{
    [TestMethod]
    public void ScriptRejectsUnknownInputAndOrderWithoutConsumingExpectedSteps()
    {
        var initial = new FixedCoreReadView(new(0, 0), HostCoreScenario.Input);
        var after = new FixedCoreReadView(new(1, 0), HostCoreScenario.Input);
        using var session = new ScriptedCoreSession(initial);
        CoreCommand command = new CoreCommand.DisbandTrain(new(1));
        SubmissionResult submitted = new SubmissionResult.NotAccepted(new("fixture", "", null, []));
        var advanced = new AdvanceResult(1, after.Position, [], null, null);
        session.QueueSubmit(command, CommandSource.Console, submitted, initial);
        session.QueueAdvance(2, advanced, after);

        AssertUnconfigured(() => session.Advance(2));
        AssertUnconfigured(() => session.Submit(new CoreCommand.DisbandTrain(new(2)), CommandSource.Console));
        AssertUnconfigured(() => session.Submit(command, CommandSource.UserInterface));
        AssertUnconfigured(session.AssertComplete);
        Assert.AreSame(submitted, session.Submit(command, CommandSource.Console));
        AssertUnconfigured(() => session.Advance(1));
        Assert.AreSame(advanced, session.Advance(2));
        session.AssertComplete();
        AssertUnconfigured(() => session.Advance(2));
        AssertUnconfigured(() => session.ReadRecording());
        AssertUnconfigured(() => session.CaptureRecordingStart());
        AssertUnconfigured(() => session.ConfirmRecordingStart(new(1), RecordingWriteOutcome.Failed));
        AssertUnconfigured(() => initial.FindTrain(new(1)));
    }

    [TestMethod]
    public void SingleStepRequestCanReturnPreconfiguredFaultWithoutCompletingStep()
    {
        var fault = new CoreFault("fixture:fault", new(0, 0), "Advance", "fixture", null);
        var initial = new FixedCoreReadView(new(0, 0), HostCoreScenario.Input);
        var faulted = new FixedCoreReadView(new(0, 0), HostCoreScenario.Input, fault);
        using var session = new ScriptedCoreSession(initial);
        var result = new AdvanceResult(0, faulted.Position, [], fault, null);
        session.QueueAdvance(result, faulted);
        AssertUnconfigured(() => session.Advance(0));
        Assert.AreSame(result, session.Advance(1));
        HostCoreScenario.AssertView(session.Read(), initial.Position, HostCoreScenario.Input, fault);
        session.AssertComplete();
    }

    [TestMethod]
    public void RetainedViewAndCaptureRemainFixedAfterAdvanceAndDisposal()
    {
        var initial = new FixedCoreReadView(new(0, 0), HostCoreScenario.Input, revision: new(1));
        var after = new FixedCoreReadView(new(1, 0), HostCoreScenario.Input, revision: new(2));
        var checkpoint = new CoreCheckpoint(initial.Position, "fixed checkpoint", new string('B', 64));
        var capture = new FixedCoreCapture(checkpoint, HostCoreScenario.References,
            "opaque state", HostCoreScenario.Input, initial.Revision);
        var session = new ScriptedCoreSession(initial);
        var result = new CoreResult<ICoreCapture>.Success(capture);
        session.SetCapture(CapturePurpose.Save, result);
        session.QueueAdvance(new(1, after.Position, [], null, null), after);
        var retained = session.Read();
        Assert.AreSame(retained, session.Read());
        AssertUnconfigured(() => session.Capture(CapturePurpose.Diagnostic));
        Assert.AreSame(result, session.Capture(CapturePurpose.Save));
        session.Advance(1);
        Assert.AreSame(after, session.Read());
        session.AssertComplete();
        session.Dispose();
        HostCoreScenario.AssertView(retained, new(0, 0), HostCoreScenario.Input);
        Assert.AreEqual(new CoreRevision(1), retained.Revision);
        Assert.AreSame(checkpoint, capture.Checkpoint);
        Assert.AreEqual("opaque state", capture.Encode());
        Assert.ThrowsExactly<ObjectDisposedException>(() => session.Read());
    }

    [TestMethod]
    public void CompletionDetectsUnusedCaptureAndFactoryCall()
    {
        using var session = new ScriptedCoreSession(new FixedCoreReadView(new(0, 0), HostCoreScenario.Input));
        var failure = new CoreResult<ICoreCapture>.Failure([]);
        session.SetCapture(CapturePurpose.Save, failure);
        AssertUnconfigured(session.AssertComplete);
        session.Capture(CapturePurpose.Save);
        session.AssertComplete();

        var factory = new ScriptedCoreFactory
        {
            DecodeStateCall = payload => payload == "expected" ? failure
                : throw new InvalidOperationException("UNCONFIGURED: unexpected payload.")
        };
        AssertUnconfigured(factory.AssertComplete);
        AssertUnconfigured(() => factory.DecodeState("unknown"));
        AssertUnconfigured(factory.AssertComplete);
        Assert.AreSame(failure, factory.DecodeState("expected"));
        factory.AssertComplete();
        Assert.AreSame(failure, factory.DecodeState("expected"));
        AssertUnconfigured(() => new ScriptedCoreFactory().DecodeState("expected"));
    }

    [TestMethod]
    public void Replacing_fixed_capture_requires_using_new_configuration()
    {
        using var session = new ScriptedCoreSession(new FixedCoreReadView(new(0, 0), HostCoreScenario.Input));
        var first = new CoreResult<ICoreCapture>.Failure([]);
        var second = new CoreResult<ICoreCapture>.Failure([new("fixture:replacement", "capture", null, [])]);
        session.SetCapture(CapturePurpose.Save, first);
        Assert.AreSame(first, session.Capture(CapturePurpose.Save));
        session.AssertComplete();
        session.SetCapture(CapturePurpose.Save, second);
        AssertUnconfigured(session.AssertComplete);
        Assert.AreSame(second, session.Capture(CapturePurpose.Save));
        session.AssertComplete();
    }

    [TestMethod]
    public void CandidateOwnsSessionUntilActivationAndDisposesOnlyOnce()
    {
        var abandonedSession = new ScriptedCoreSession(new FixedCoreReadView(new(0, 0), HostCoreScenario.Input));
        var abandoned = new ScriptedCoreCandidate(abandonedSession, HostCoreScenario.References);
        abandoned.Dispose();
        abandoned.Dispose();
        Assert.AreEqual(1, abandonedSession.DisposeCount);
        Assert.AreEqual(1, abandoned.DisposeCount);
        AssertUnconfigured(() => abandoned.Activate());

        using var activeSession = new ScriptedCoreSession(new FixedCoreReadView(new(0, 0), HostCoreScenario.Input));
        var activated = new ScriptedCoreCandidate(activeSession, HostCoreScenario.References);
        Assert.AreSame(activeSession.Read(), activated.View);
        Assert.AreSame(activeSession, activated.Activate());
        AssertUnconfigured(() => activated.Activate());
        activated.Dispose();
        activated.Dispose();
        Assert.AreEqual(0, activeSession.DisposeCount);
        Assert.AreEqual(1, activated.ActivationCount);
        Assert.AreSame(activeSession.CurrentView, activeSession.Read());
    }

    [TestMethod]
    public void ReplayPreservesScriptOnWrongBoundaryAndRetainedStateAfterDisposal()
    {
        var initial = new FixedCoreReadView(new(0, 0), HostCoreScenario.Input);
        var after = new FixedCoreReadView(new(2, 1), HostCoreScenario.Input);
        var result = new ReplayAdvanceResult(ReplayStop.RequestedPosition, after.Position, [], null, null);
        var checkpoint = new CoreCheckpoint(after.Position, "checkpoint", new string('B', 64));
        var capture = new FixedCoreCapture(checkpoint, HostCoreScenario.References,
            "opaque state", HostCoreScenario.Input, after.Revision);
        var replay = new ScriptedCoreReplay(initial);
        replay.QueueAdvance(5, after.Position, result, after);
        AssertUnconfigured(() => replay.CaptureBoundary());
        AssertUnconfigured(() => replay.Advance(4, after.Position));
        AssertUnconfigured(() => replay.Advance(5));
        AssertUnconfigured(replay.AssertComplete);
        Assert.AreSame(initial, replay.Read());
        Assert.AreSame(result, replay.Advance(5, after.Position));
        Assert.AreEqual((5u, (SimulationPosition?)after.Position), replay.AdvanceCalls.Single());
        Assert.AreSame(after, replay.Read());
        replay.SetCapture(capture);
        AssertUnconfigured(replay.AssertComplete);
        Assert.AreSame(capture, replay.CaptureBoundary());
        replay.AssertComplete();
        replay.SetCapture(capture);
        AssertUnconfigured(replay.AssertComplete);
        replay.CaptureBoundary();
        replay.AssertComplete();
        replay.Dispose();
        replay.Dispose();
        Assert.AreEqual(1, replay.DisposeCount);
        Assert.AreSame(checkpoint, capture.Checkpoint);
        HostCoreScenario.AssertView(initial, new(0, 0), HostCoreScenario.Input);
        Assert.ThrowsExactly<ObjectDisposedException>(() => replay.Read());
        Assert.ThrowsExactly<ObjectDisposedException>(() => replay.Advance(5));
        Assert.ThrowsExactly<ObjectDisposedException>(() => replay.CaptureBoundary());
    }

    [TestMethod]
    public void ReplayFactoryRequiresConfigurationAndPassesInputAndComparisonThrough()
    {
        var runtime = new RuntimeIdentity("fixture", "1", "os", "architecture", "runtime");
        var input = new CoreReplayInput(1, runtime, "opaque initial", [], [], new(2, 0), null);
        var checkpoint = new CoreCheckpoint(new(2, 0), "checkpoint", new string('B', 64));
        var otherCheckpoint = checkpoint with { CanonicalText = "actual" };
        using var replay = new ScriptedCoreReplay(new FixedCoreReadView(new(0, 0), HostCoreScenario.Input));
        var prepared = new CoreResult<ICoreReplay>.Success(replay);
        var compared = new CoreResult<CheckpointComparison>.Success(new CheckpointComparison.Equal());
        var factory = new ScriptedCoreFactory();
        AssertUnconfigured(() => factory.PrepareReplay(input, runtime, []));
        AssertUnconfigured(() => factory.CompareCheckpoints(checkpoint, otherCheckpoint));
        factory.PrepareReplayCall = (receivedInput, receivedRuntime, history) =>
        {
            Assert.AreSame(input, receivedInput);
            Assert.AreSame(runtime, receivedRuntime);
            Assert.IsTrue(history.IsEmpty);
            return prepared;
        };
        factory.CompareCheckpointsCall = (receivedExpected, receivedActual) =>
        {
            Assert.AreSame(checkpoint, receivedExpected);
            Assert.AreSame(otherCheckpoint, receivedActual);
            return compared;
        };
        AssertUnconfigured(factory.AssertComplete);
        Assert.AreSame(prepared, factory.PrepareReplay(input, runtime, []));
        AssertUnconfigured(factory.AssertComplete);
        Assert.AreSame(compared, factory.CompareCheckpoints(checkpoint, otherCheckpoint));
        factory.AssertComplete();
    }

    private static void AssertUnconfigured(Action call)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(call);
        StringAssert.StartsWith(error.Message, "UNCONFIGURED:");
    }
}
