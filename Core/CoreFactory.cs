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
        if (capture.Fault is not null)
            return new CoreResult<ICoreSessionCandidate>.Failure([new("frozen", "Fault", null, [])]);
        ImmutableArray<CoreProblem> report = capture.InputIdentity == content.Identity
            ? [] : [new("content_changed", "InputIdentity", null, [capture.InputIdentity.Value, content.Identity.Value])];
        return new CoreResult<ICoreSessionCandidate>.Success(new CoreSessionCandidate(
            new(capture.Position, capture.NextCommandNumber, capture.Revision, content), report));
    }

    public CoreResult<ICoreReplay> PrepareReplay(CoreReplayInput input, RuntimeIdentity environment,
        ImmutableArray<CoreContentProjection> contentHistory)
        => throw new NotImplementedException("PrepareReplay 尚未实现。");

    public CoreResult<CheckpointComparison> CompareCheckpoints(CoreCheckpoint expected, CoreCheckpoint actual)
        => throw new NotImplementedException("CompareCheckpoints 尚未实现。");
}
