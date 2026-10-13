using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Train.Core.Simulation;
using Train.Core.Tests.Simulation;

namespace Train.Benchmarks;

public class ResourceSimulationBenchmark
{
    public const int StepsPerBatch = 65_536;
    public const int EntityCount = 16;
    private SimulationSession _session = null!;

    [IterationSetup]
    public void Reset()
    {
        var ids = Enumerable.Range(1, EntityCount).Select(id => (ulong)id).ToImmutableSortedSet();
        var state = new ResourceState
        {
            Stock = 2UL * EntityCount * StepsPerBatch,
            NextEntityId = EntityCount + 1,
            EntityIds = ids,
            Demands = ids.Select(id => new ResourceDemand(id, 1, (int)(id % 4), 1)).ToImmutableArray()
        };
        _session = new SimulationSession(state, new ResourceCalculations(), new ResourceCommandCodec());
    }

    [Benchmark(OperationsPerInvoke = StepsPerBatch)]
    public ulong AdvanceSteps() => _session.Advance(StepsPerBatch).StepsAdvanced;

    [IterationCleanup]
    public void CheckCompletedWork()
    {
        var state = (ResourceState)_session.GetState();
        if (state.Fault is not null || state.CompletedSteps != StepsPerBatch ||
            state.Stock != (ulong)EntityCount * StepsPerBatch ||
            state.Holdings.Count != EntityCount || state.Holdings.Values.Any(amount => amount != StepsPerBatch))
            throw new InvalidOperationException("Benchmark 未完成指定的资源模拟负载。");
    }
}
