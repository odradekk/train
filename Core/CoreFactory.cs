using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.World;

namespace Train.Core;

public static class CoreFactory
{
    public static ICoreFactory Create() => new CoreSessionFactory();
}

internal sealed class CoreSessionFactory : ICoreFactory
{
    public CoreResult<ICoreSessionCandidate> PrepareNew(WorldGenerationInput input,
        CoreContentProjection content, CoreSessionOptions options)
        => WorldGenerator.Generate(input, content, options) switch
        {
            CoreResult<GeneratedWorld>.Success success => new CoreResult<ICoreSessionCandidate>.Success(
                new CoreSessionCandidate(new(new(0, 0), 1, new(0), success.Value.Content, World: success.Value.State))),
            CoreResult<GeneratedWorld>.Failure failure => new CoreResult<ICoreSessionCandidate>.Failure(failure.Problems),
            CoreResult<GeneratedWorld>.Faulted fault => new CoreResult<ICoreSessionCandidate>.Faulted(fault.Fault, fault.Diagnostic),
            _ => throw new InvalidOperationException("世界生成结果无效。")
        };

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
