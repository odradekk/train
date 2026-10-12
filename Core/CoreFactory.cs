using System.Collections.Immutable;
using Train.Core.Contracts;

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
        => throw new NotImplementedException("DecodeState 尚未实现。");

    public CoreResult<ICoreSessionCandidate> PrepareRestore(ICoreCapture saved,
        CoreContentProjection content, CoreSessionOptions options)
        => throw new NotImplementedException("PrepareRestore 尚未实现。");

    public CoreResult<ICoreReplay> PrepareReplay(CoreReplayInput input, RuntimeIdentity environment,
        ImmutableArray<CoreContentProjection> contentHistory)
        => throw new NotImplementedException("PrepareReplay 尚未实现。");

    public CoreResult<CheckpointComparison> CompareCheckpoints(CoreCheckpoint expected, CoreCheckpoint actual)
        => throw new NotImplementedException("CompareCheckpoints 尚未实现。");
}
