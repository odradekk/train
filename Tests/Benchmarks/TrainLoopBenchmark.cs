using BenchmarkDotNet.Attributes;
using Train.Core.Contracts;
using Train.Core.Tests.Railway;

namespace Train.Benchmarks;

public class TrainLoopBenchmark
{
    public const int StepsPerBatch = 65_536;
    public const int TrainCount = 1;
    private ICoreSession _session = null!;
    private AdvanceResult _result = null!;

    [IterationSetup]
    public void Reset()
    {
        _session = TrainRecoveryFixture.New();
        TrainRecoveryFixture.Configure(_session);
        var train = TrainRecoveryFixture.Train(_session);
        TrainRecoveryFixture.Submit(_session,
            new CoreCommand.ReplaceRunPlan(train.Id, train.Plan! with { Loop = true }));
    }

    [Benchmark(OperationsPerInvoke = StepsPerBatch)]
    public AdvanceResult AdvanceSteps() => _result = _session.Advance(StepsPerBatch);

    [IterationCleanup]
    public void CheckCompletedWork()
    {
        using var session = _session;
        var train = TrainRecoveryFixture.Train(session);
        if (_result.Fault is not null || _result.CompletedSteps != StepsPerBatch ||
            session.Read().Position.CompletedSteps != StepsPerBatch || train.Plan is not { Loop: true } ||
            train.Activity is TrainActivity.Stopped or TrainActivity.NoRoute ||
            train.Plan.Stops.OfType<PlanStop.Station>().Any(stop =>
                _result.Events.Count(e => e.Event is CoreEvent.TrainArrived arrived && arrived.Node == stop.Node) < 2))
            throw new InvalidOperationException("Benchmark 未完成指定的真实列车循环运行负载。");
    }
}
