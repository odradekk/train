using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.ContractSupport.Tests.Core;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:core-contract-support")]
public sealed class CaptureScenarioTests
{
    [TestMethod]
    public void FiniteDoubleSatisfiesCaptureContract()
        => CaptureScenario.AssertContract(new CaptureSessionDouble(),
            CaptureSessionDouble.Input, CaptureSessionDouble.InitialReferences,
            CaptureSessionDouble.AdvancedReferences);

    [TestMethod]
    public void UnchangedReferencesRemainValidAcrossAdvance()
        => CaptureScenario.AssertContract(
            new CaptureSessionDouble(startBatchOnAdvance: false),
            CaptureSessionDouble.Input, CaptureSessionDouble.InitialReferences,
            CaptureSessionDouble.InitialReferences);

    [TestMethod]
    public void ScenarioRejectsCaptureThatReadsLiveReferences()
    {
        using var session = new LiveReferencesSession();
        Assert.ThrowsException<AssertFailedException>(() =>
            CaptureScenario.AssertContract(session,
                CaptureSessionDouble.Input, CaptureSessionDouble.InitialReferences,
                CaptureSessionDouble.AdvancedReferences));
    }

    [TestMethod]
    public void FiniteDoubleRejectsUnsupportedInputAndSequence()
    {
        using var session = new CaptureSessionDouble();
        AssertUnconfigured(() => session.Capture(CapturePurpose.Diagnostic));
        AssertUnconfigured(() => session.Advance(2));
        session.Advance(1);
        AssertUnconfigured(() => session.Advance(1));
        AssertUnconfigured(() => session.ReadRecording());
    }

    private static void AssertUnconfigured(Action operation)
    {
        var error = Assert.ThrowsException<InvalidOperationException>(operation);
        StringAssert.StartsWith(error.Message, "UNCONFIGURED:");
    }

    private sealed class LiveReferencesSession : ICoreSession
    {
        private readonly CaptureSessionDouble inner = new();
        private CoreReferenceManifest references = CaptureSessionDouble.InitialReferences;

        public CoreResult<ICoreCapture> Capture(CapturePurpose purpose)
        {
            var capture = (CoreResult<ICoreCapture>.Success)inner.Capture(purpose);
            return new CoreResult<ICoreCapture>.Success(
                new LiveReferencesCapture(capture.Value, () => references));
        }

        public AdvanceResult Advance(uint steps)
        {
            var result = inner.Advance(steps);
            references = CaptureSessionDouble.AdvancedReferences;
            return result;
        }

        public ICoreReadView Read() => inner.Read();
        public SubmissionResult Submit(CoreCommand command, CommandSource source)
            => inner.Submit(command, source);
        public CoreRecordingWindow ReadRecording() => inner.ReadRecording();
        public CoreResult<RecordingStart> CaptureRecordingStart() => inner.CaptureRecordingStart();
        public RecordingMaintenance ConfirmRecordingStart(RecordingPointId point, RecordingWriteOutcome outcome)
            => inner.ConfirmRecordingStart(point, outcome);
        public void Dispose() => inner.Dispose();
    }

    private sealed class LiveReferencesCapture(ICoreCapture inner,
        Func<CoreReferenceManifest> currentReferences) : ICoreCapture
    {
        public SimulationPosition Position => inner.Position;
        public CoreRevision Revision => inner.Revision;
        public CoreInputId InputIdentity => inner.InputIdentity;
        public CoreReferenceManifest References => currentReferences();
        public CoreCheckpoint Checkpoint => inner.Checkpoint;
        public string Encode() => inner.Encode();
    }
}
