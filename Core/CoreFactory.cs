using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Sessions;

namespace Train.Core;

public static class CoreFactory
{
    public static ICoreFactory Create() => new CoreSessionFactory();
}

internal sealed class CoreSessionFactory : ICoreFactory
{
    public CoreResult<ICoreSessionCandidate> PrepareNew(WorldGenerationInput input,
        CoreContentProjection content, CoreSessionOptions options)
        => throw new NotImplementedException("PrepareNew 尚未实现。");

    public CoreResult<ICoreCapture> DecodeState(string canonicalPayload)
        => CoreCapture.Decode(canonicalPayload);

    public CoreResult<ICoreSessionCandidate> PrepareRestore(ICoreCapture saved,
        CoreContentProjection content, CoreSessionOptions options)
    {
        if (saved is not CoreCapture capture)
            throw new ArgumentException("捕获必须由核心工厂或会话生成。", nameof(saved));
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RetainedRecordingStarts, 1);
        if (capture.Fault is not null)
            return new CoreResult<ICoreSessionCandidate>.Failure([new("frozen", "Fault", null, [])]);
        ImmutableArray<CoreProblem> report = capture.InputIdentity == content.Identity
            ? [] : [new("content_changed", "InputIdentity", null, [capture.InputIdentity.Value, content.Identity.Value])];
        return new CoreResult<ICoreSessionCandidate>.Success(new CoreSessionCandidate(
            new(capture.Position, capture.NextCommandNumber, capture.Revision, content),
            options.RetainedRecordingStarts, report));
    }

    public CoreResult<ICoreReplay> PrepareReplay(CoreReplayInput input, RuntimeIdentity environment,
        ImmutableArray<CoreContentProjection> contentHistory)
        => CoreReplay.Prepare(input, environment, contentHistory);

    public CoreResult<CheckpointComparison> CompareCheckpoints(CoreCheckpoint expected, CoreCheckpoint actual)
        => CoreCheckpoints.Compare(expected, actual);
}
