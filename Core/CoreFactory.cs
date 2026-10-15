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
    {
        ValidateOptions(options);
        return WorldGenerator.Generate(input, content) switch
        {
            CoreResult<GeneratedWorld>.Success success => new CoreResult<ICoreSessionCandidate>.Success(
                new CoreSessionCandidate(new(new(0, 0), 1, new(0), success.Value.Content, World: success.Value.State),
                    options.RetainedRecordingStarts)),
            CoreResult<GeneratedWorld>.Failure failure => new CoreResult<ICoreSessionCandidate>.Failure(failure.Problems),
            CoreResult<GeneratedWorld>.Faulted fault => new CoreResult<ICoreSessionCandidate>.Faulted(fault.Fault, fault.Diagnostic),
            _ => throw new InvalidOperationException("世界生成结果无效。")
        };
    }

    public CoreResult<ICoreCapture> DecodeState(string canonicalPayload)
        => CoreCapture.Decode(canonicalPayload);

    public CoreResult<ICoreSessionCandidate> PrepareRestore(ICoreCapture saved,
        CoreContentProjection content, CoreSessionOptions options)
    {
        if (saved is not CoreCapture capture)
            throw new ArgumentException("捕获必须由核心工厂或会话生成。", nameof(saved));
        ValidateOptions(options);
        WorldInputs.ValidateContent(content);
        if (capture.Fault is not null)
            return new CoreResult<ICoreSessionCandidate>.Failure([new("frozen", "Fault", null, [])]);
        ImmutableArray<CoreProblem> report = capture.InputIdentity == content.Identity
            ? [] : [new("content_changed", "InputIdentity", null, [capture.InputIdentity.Value, content.Identity.Value])];
        var world = WorldReferences.RestorePopulationDependencies(capture.World, content);
        return new CoreResult<ICoreSessionCandidate>.Success(new CoreSessionCandidate(
            new(capture.Position, capture.NextCommandNumber, capture.Revision, content,
                World: Railway.TrainOperations.AdaptNodes(world, content, capture.Position).World),
            options.RetainedRecordingStarts, report));
    }

    public CoreResult<ICoreReplay> PrepareReplay(CoreReplayInput input, RuntimeIdentity environment,
        ImmutableArray<CoreContentProjection> contentHistory)
        => CoreReplay.Prepare(input, environment, contentHistory);

    public CoreResult<CheckpointComparison> CompareCheckpoints(CoreCheckpoint expected, CoreCheckpoint actual)
        => CoreCheckpoints.Compare(expected, actual);

    private static void ValidateOptions(CoreSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RetainedRecordingStarts, 1);
        ArgumentNullException.ThrowIfNull(options.Runtime);
        foreach (var value in new[] { options.Runtime.GameBuild, options.Runtime.SimulationVersion,
            options.Runtime.OperatingSystem, options.Runtime.Architecture, options.Runtime.DotNetRuntime })
            ArgumentNullException.ThrowIfNull(value);
    }
}
