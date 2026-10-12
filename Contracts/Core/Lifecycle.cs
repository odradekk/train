using System.Collections.Immutable;

namespace Train.Core.Contracts;

public interface ICoreFactory
{
    CoreResult<ICoreSessionCandidate> PrepareNew(WorldGenerationInput input,
        CoreContentProjection content, CoreSessionOptions options);
    CoreResult<ICoreCapture> DecodeState(string canonicalPayload);
    CoreResult<ICoreSessionCandidate> PrepareRestore(ICoreCapture saved,
        CoreContentProjection content, CoreSessionOptions options);
    CoreResult<ICoreReplay> PrepareReplay(CoreReplayInput input, RuntimeIdentity environment,
        ImmutableArray<CoreContentProjection> contentHistory);
    CoreResult<CheckpointComparison> CompareCheckpoints(CoreCheckpoint expected, CoreCheckpoint actual);
}

public sealed record CoreSessionOptions(RuntimeIdentity Runtime, bool EnableDeveloperCommands,
    int RetainedRecordingStarts);
public sealed record RuntimeIdentity(string GameBuild, string SimulationVersion,
    string OperatingSystem, string Architecture, string DotNetRuntime);

public interface ICoreSessionCandidate : IDisposable
{
    ICoreReadView View { get; }
    CoreReferenceManifest References { get; }
    ImmutableArray<CoreProblem> AdaptationReport { get; }
    ImmutableArray<MissingCoreReference> MissingReferences { get; }
    ICoreSession Activate();
}

public interface ICoreSession : IDisposable
{
    ICoreReadView Read();
    SubmissionResult Submit(CoreCommand command, CommandSource source);
    AdvanceResult Advance(uint steps);
    CoreResult<ICoreCapture> Capture(CapturePurpose purpose);
    CoreRecordingWindow ReadRecording();
    CoreResult<RecordingStart> CaptureRecordingStart();
    RecordingMaintenance ConfirmRecordingStart(RecordingPointId point, RecordingWriteOutcome outcome);
}

public sealed record AdvanceResult(uint CompletedSteps, SimulationPosition Position,
    ImmutableArray<EventEnvelope> Events, CoreFault? Fault, CoreDiagnostic? Diagnostic);
public sealed record CoreFault(string Code, SimulationPosition Position, string Operation,
    string Field, WorldObjectId? Subject);
public sealed record CoreDiagnostic(string Message, string? StackTrace);

public enum CapturePurpose { Save, Diagnostic }
public interface ICoreCapture
{
    SimulationPosition Position { get; }
    CoreRevision Revision { get; }
    CoreInputId InputIdentity { get; }
    CoreReferenceManifest References { get; }
    CoreCheckpoint Checkpoint { get; }
    string Encode();
}

public sealed record CoreReferenceManifest(ImmutableArray<DesignHash> Designs,
    ImmutableArray<OwnedSettlementTemplateReference> Templates,
    ImmutableArray<RequiredDefinition> Definitions, ImmutableArray<CoreInputId> Inputs);
public sealed record OwnedSettlementTemplateReference(SettlementId Owner,
    SettlementTemplateId SourceTemplate, ImmutableArray<TemplatePlotReference> Plots);
public sealed record TemplatePlotReference(int Number, PlotSize Size);
public sealed record RequiredDefinition(CoreDefinitionReference Definition,
    SemanticRevision? FrozenRevision, string Field, bool WasMissing);
public sealed record CoreCheckpoint(SimulationPosition Position, string CanonicalText, string Sha256);

public sealed record RecordedCommand(CommandSource Source, string CanonicalPayload, CommandReceipt Result,
    CoreInputId InputBefore, CoreInputId InputAfter);
public sealed record RecordingStart(RecordingPointId Id, ICoreCapture State);
public sealed record RecordingGap(SimulationPosition From, SimulationPosition? Until, string Code);
public sealed record FaultRecordingSegment(RecordingStart Start,
    ImmutableArray<RecordedCommand> Commands, ImmutableArray<CoreCheckpoint> Checkpoints,
    CoreFault Fault, CoreReferenceManifest RequiredReferences);
public sealed record CoreRecordingWindow(SimulationPosition EndPosition, ImmutableArray<RecordingStart> Starts,
    ImmutableArray<RecordedCommand> Commands, ImmutableArray<CoreCheckpoint> Checkpoints,
    ImmutableArray<FaultRecordingSegment> FaultSegments, ImmutableArray<RecordingGap> Gaps,
    CoreReferenceManifest RequiredReferences);
public enum RecordingWriteOutcome { PublishedWithDependencies, Failed }
public sealed record RecordingMaintenance(ImmutableArray<CoreInputId> ReleasedInputs,
    ImmutableArray<RecordingGap> Gaps);

public sealed record CoreReplayInput(int FormatVersion, RuntimeIdentity OriginalRuntime,
    string InitialStatePayload, ImmutableArray<RecordedCommand> Commands,
    ImmutableArray<CoreCheckpoint> Checkpoints, SimulationPosition EndPosition, CoreFault? ExpectedFault);
public interface ICoreReplay : IDisposable
{
    ICoreReadView Read();
    ReplayAdvanceResult Advance(uint maximumSteps, SimulationPosition? stopAt = null);
    ICoreCapture CaptureBoundary();
}
public enum ReplayStop { BudgetReached, RequestedPosition, EndOfRecording, FirstDifference, RecordedFault, UnexpectedFault }
public sealed record CheckpointDifference(SimulationPosition Position, string Field,
    string Expected, string Actual);
public abstract record CheckpointComparison
{
    private CheckpointComparison() { }
    public sealed record Equal : CheckpointComparison;
    public sealed record Different(CheckpointDifference Difference) : CheckpointComparison;
}
public sealed record ReplayAdvanceResult(ReplayStop Stop, SimulationPosition Position,
    ImmutableArray<EventEnvelope> Events, CheckpointDifference? Difference, CoreFault? Fault);
