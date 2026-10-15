using BenchmarkDotNet.Attributes;
using Train.Core;
using Train.Core.Contracts;
using Train.Core.Tests.Railway;
using Train.Core.Tests.World;

namespace Train.Benchmarks;

public class TrainLoopBenchmark
{
    public const int StepsPerBatch = 65_536;
    public const int TrainCount = 1;
    internal const int ConsumptionIntervalTicks = 120;
    internal const int StatisticsPeriodTicks = 720;
    private ICoreSession _session = null!;
    private AdvanceResult _result = null!;

    [IterationSetup]
    public void Reset()
    {
        var content = TrainRecoveryFixture.Content;
        // Match the builtin population cadence without slowing the shared unit-test fixture.
        content = content with { PopulationRules = [content.PopulationRules[0] with
            { ConsumptionIntervalTicks = ConsumptionIntervalTicks, StatisticsPeriodTicks = StatisticsPeriodTicks }] };
        var input = WorldFixture.Input with { Configuration = WorldFixture.Input.Configuration with
            { WidthMeters = 40, HeightMeters = 20, SettlementRatio = 1, LairRatio = 0, HostileSettlementRatio = 0 } };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareNew(input, content, new(TrainRecoveryFixture.Runtime, false, 32)));
        _session = candidate.Activate();
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
