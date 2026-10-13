using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;

namespace Train.Core.ContractSupport;

public static class HostCoreScenario
{
    public static CoreInputId Input => new(new string('A', 64));
    public static CoreReferenceManifest References => new([], [], [], [Input]);
    public static WorldGenerationInput World { get; } = new(
        1, "fixture-generator", new(new string('B', 64)),
        new(100, 100, 10, 1,
            [new(10, 1)], [new(10, 1)], [new(10, 1)], -1, 0.01,
            [new(new("fixture:terrain"), -1, 1, -1, 1, 1, 1, 1)],
            1, 0.5, 0, 0, [], [], new(1, 1, 1, 0),
            new(1, 1, 100, 100), new(false, 0, 0, 0, []),
            new(1, 1, true, true, 1, new(new string('C', 64))),
            new(new("fixture:population"), new("fixture:market"),
                new("fixture:construction"), new("fixture:railway"), new("fixture:combat"))),
        new(new(1000), [new([new(new string('C', 64))], [])]));

    public static void AssertView(ICoreReadView view, SimulationPosition position,
        CoreInputId input, CoreFault? fault = null)
    {
        Assert.AreEqual(position, view.Position);
        Assert.AreEqual(input, view.InputIdentity);
        Assert.AreEqual(fault, view.Fault);
    }

    internal static InvalidOperationException Unconfigured(string operation)
        => new($"UNCONFIGURED: host scenario does not support {operation} input or sequence.");
}

public sealed class ScriptedCoreFactory : ICoreFactory
{
    public Func<WorldGenerationInput, CoreContentProjection, CoreSessionOptions, CoreResult<ICoreSessionCandidate>>? PrepareNewCall { get; set; }
    public Func<string, CoreResult<ICoreCapture>>? DecodeStateCall { get; set; }
    public Func<ICoreCapture, CoreContentProjection, CoreSessionOptions, CoreResult<ICoreSessionCandidate>>? PrepareRestoreCall { get; set; }
    public int PrepareNewCount { get; private set; }
    public int DecodeStateCount { get; private set; }
    public int PrepareRestoreCount { get; private set; }

    public CoreResult<ICoreSessionCandidate> PrepareNew(WorldGenerationInput input,
        CoreContentProjection content, CoreSessionOptions options)
    {
        if (PrepareNewCall is null)
            throw HostCoreScenario.Unconfigured(nameof(PrepareNew));
        var result = PrepareNewCall(input, content, options);
        PrepareNewCount++;
        return result;
    }

    public CoreResult<ICoreCapture> DecodeState(string canonicalPayload)
    {
        if (DecodeStateCall is null)
            throw HostCoreScenario.Unconfigured(nameof(DecodeState));
        var result = DecodeStateCall(canonicalPayload);
        DecodeStateCount++;
        return result;
    }

    public CoreResult<ICoreSessionCandidate> PrepareRestore(ICoreCapture saved,
        CoreContentProjection content, CoreSessionOptions options)
    {
        if (PrepareRestoreCall is null)
            throw HostCoreScenario.Unconfigured(nameof(PrepareRestore));
        var result = PrepareRestoreCall(saved, content, options);
        PrepareRestoreCount++;
        return result;
    }

    public void AssertComplete()
    {
        if ((PrepareNewCall is not null && PrepareNewCount == 0)
            || (DecodeStateCall is not null && DecodeStateCount == 0)
            || (PrepareRestoreCall is not null && PrepareRestoreCount == 0))
            throw HostCoreScenario.Unconfigured(nameof(AssertComplete));
    }

    public CoreResult<ICoreReplay> PrepareReplay(CoreReplayInput input, RuntimeIdentity environment,
        ImmutableArray<CoreContentProjection> contentHistory)
        => throw HostCoreScenario.Unconfigured(nameof(PrepareReplay));
    public CoreResult<CheckpointComparison> CompareCheckpoints(CoreCheckpoint expected, CoreCheckpoint actual)
        => throw HostCoreScenario.Unconfigured(nameof(CompareCheckpoints));
}

public sealed class ScriptedCoreCandidate(ICoreSession session, CoreReferenceManifest references,
    ImmutableArray<MissingCoreReference> missing = default,
    ImmutableArray<CoreProblem> adaptation = default) : ICoreSessionCandidate
{
    private bool disposed;
    public int ActivationCount { get; private set; }
    public int? ActivationThread { get; private set; }
    public int DisposeCount { get; private set; }
    public ICoreReadView View => session.Read();
    public CoreReferenceManifest References { get; } = references;
    public ImmutableArray<MissingCoreReference> MissingReferences { get; } = missing.IsDefault ? [] : missing;
    public ImmutableArray<CoreProblem> AdaptationReport { get; } = adaptation.IsDefault ? [] : adaptation;

    public ICoreSession Activate()
    {
        if (disposed || ActivationCount != 0)
            throw HostCoreScenario.Unconfigured(nameof(Activate));
        ActivationCount++;
        ActivationThread = Environment.CurrentManagedThreadId;
        return session;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        DisposeCount++;
        if (ActivationCount == 0) session.Dispose();
    }
}

public sealed class ScriptedCoreSession(ICoreReadView initial) : ICoreSession
{
    private abstract record Step(ICoreReadView After);
    private sealed record AdvanceStep(uint Steps, AdvanceResult Result, ICoreReadView View) : Step(View);
    private sealed record SubmitStep(CoreCommand Command, CommandSource Source,
        SubmissionResult Result, ICoreReadView View) : Step(View);
    private readonly Queue<Step> steps = new();
    private readonly Dictionary<CapturePurpose, CoreResult<ICoreCapture>> captures = new();
    private readonly HashSet<CapturePurpose> captured = [];
    public ICoreReadView CurrentView { get; private set; } = initial;
    public int AdvanceCount { get; private set; }
    public int SubmitCount { get; private set; }
    public int CaptureCount { get; private set; }
    public int? AdvanceThread { get; private set; }
    public int? SubmitThread { get; private set; }
    public int? CaptureThread { get; private set; }
    public int DisposeCount { get; private set; }

    public void QueueAdvance(AdvanceResult result, ICoreReadView after)
        => QueueAdvance(1, result, after);
    public void QueueAdvance(uint expectedSteps, AdvanceResult result, ICoreReadView after)
        => steps.Enqueue(new AdvanceStep(expectedSteps, result, after));
    public void QueueSubmit(CoreCommand expected, CommandSource source, SubmissionResult result, ICoreReadView after)
        => steps.Enqueue(new SubmitStep(expected, source, result, after));
    public void SetCapture(CapturePurpose purpose, CoreResult<ICoreCapture> result)
    {
        captures[purpose] = result;
        captured.Remove(purpose);
    }

    public ICoreReadView Read()
    {
        EnsureAlive();
        return CurrentView;
    }

    public AdvanceResult Advance(uint requestedSteps)
    {
        EnsureAlive();
        if (!steps.TryPeek(out var next) || next is not AdvanceStep advance || advance.Steps != requestedSteps)
            throw HostCoreScenario.Unconfigured(nameof(Advance));
        steps.Dequeue();
        CurrentView = advance.After;
        AdvanceCount++;
        AdvanceThread = Environment.CurrentManagedThreadId;
        return advance.Result;
    }

    public SubmissionResult Submit(CoreCommand command, CommandSource source)
    {
        EnsureAlive();
        if (!steps.TryPeek(out var next) || next is not SubmitStep submit
            || submit.Command != command || submit.Source != source)
            throw HostCoreScenario.Unconfigured(nameof(Submit));
        steps.Dequeue();
        CurrentView = submit.After;
        SubmitCount++;
        SubmitThread = Environment.CurrentManagedThreadId;
        return submit.Result;
    }

    public CoreResult<ICoreCapture> Capture(CapturePurpose purpose)
    {
        EnsureAlive();
        if (!captures.TryGetValue(purpose, out var result))
            throw HostCoreScenario.Unconfigured(nameof(Capture));
        captured.Add(purpose);
        CaptureCount++;
        CaptureThread = Environment.CurrentManagedThreadId;
        return result;
    }

    public void AssertComplete()
    {
        if (steps.Count != 0 || captures.Keys.Any(purpose => !captured.Contains(purpose)))
            throw HostCoreScenario.Unconfigured(nameof(AssertComplete));
    }

    public void Dispose() { if (DisposeCount == 0) DisposeCount++; }
    private void EnsureAlive() => ObjectDisposedException.ThrowIf(DisposeCount != 0, this);
    public CoreRecordingWindow ReadRecording() => throw HostCoreScenario.Unconfigured(nameof(ReadRecording));
    public CoreResult<RecordingStart> CaptureRecordingStart() => throw HostCoreScenario.Unconfigured(nameof(CaptureRecordingStart));
    public RecordingMaintenance ConfirmRecordingStart(RecordingPointId point, RecordingWriteOutcome outcome)
        => throw HostCoreScenario.Unconfigured(nameof(ConfirmRecordingStart));
}

public sealed class FixedCoreCapture(CoreCheckpoint checkpoint, CoreReferenceManifest references,
    string opaquePayload, CoreInputId inputIdentity, CoreRevision revision) : ICoreCapture
{
    public SimulationPosition Position => Checkpoint.Position;
    public CoreCheckpoint Checkpoint { get; } = checkpoint;
    public CoreReferenceManifest References { get; } = references;
    public CoreInputId InputIdentity { get; } = inputIdentity;
    public CoreRevision Revision { get; } = revision;
    public string Encode() => opaquePayload;
}
