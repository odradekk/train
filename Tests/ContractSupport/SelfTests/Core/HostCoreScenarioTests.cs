using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.ContractSupport.Tests.Core;

[TestClass]
public sealed class HostCoreScenarioTests
{
    [TestMethod]
    public void TrainArrayCommandsMatchContentsButRejectWrongOrderWithoutConsumingStep()
    {
        var view = new FixedCoreReadView(new(0, 0), HostCoreScenario.Input);
        using var session = new ScriptedCoreSession(view);
        SubmissionResult result = new SubmissionResult.NotAccepted(new("fixture", "", null, []));
        session.QueueSubmit(new CoreCommand.FormTrain([new(2), new(1)]), CommandSource.UserInterface, result, view);
        AssertUnconfigured(() => session.Submit(new CoreCommand.FormTrain([new(1), new(2)]), CommandSource.UserInterface));
        Assert.AreSame(result, session.Submit(new CoreCommand.FormTrain([new(2), new(1)]), CommandSource.UserInterface));
        session.QueueSubmit(new CoreCommand.ReplaceRunPlan(new(3), new(true,
            [new PlanStop.Station(new(2), 61, []), new PlanStop.Waypoint(new(1))])), CommandSource.UserInterface, result, view);
        AssertUnconfigured(() => session.Submit(new CoreCommand.ReplaceRunPlan(new(3), new(true,
            [new PlanStop.Waypoint(new(1)), new PlanStop.Station(new(2), 61, [])])), CommandSource.UserInterface));
        Assert.AreSame(result, session.Submit(new CoreCommand.ReplaceRunPlan(new(3), new(true,
            [new PlanStop.Station(new(2), 61, []), new PlanStop.Waypoint(new(1))])), CommandSource.UserInterface));
        session.AssertComplete();
    }
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

    private static void AssertUnconfigured(Action call)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(call);
        StringAssert.StartsWith(error.Message, "UNCONFIGURED:");
    }
}
