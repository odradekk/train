using System.Collections.Immutable;
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
    public const ulong Seed = 42;
    public const string GeneratorVersion = "stage1-world-v1";
    public const double ApproachLengthMeters = 80;
    public const double PlatformLengthMeters = 96;
    public const double DepartureLengthMeters = 80;
    public const double NodeSpeedLimitMetersPerSecond = 15;
    public const double BrakingDeceleration = 0.7;
    public const double WorldWidthMeters = 40;
    public const double WorldHeightMeters = 20;
    private ICoreSession _session = null!;
    private AdvanceResult _result = null!;

    [IterationSetup]
    public void Reset()
    {
        var input = WorldFixture.Input with { WorldSeed = Seed, GeneratorVersion = GeneratorVersion, Configuration = WorldFixture.Input.Configuration with
        {
            WidthMeters = WorldWidthMeters, HeightMeters = WorldHeightMeters,
            SettlementRatio = 1, LairRatio = 0, HostileSettlementRatio = 0
        } };
        var source = TrainRecoveryFixture.Content;
        var content = source with
        {
            RailwayRules = [source.RailwayRules[0] with
            {
                ApproachLengthMeters = ApproachLengthMeters, DepartureLengthMeters = DepartureLengthMeters,
                NodeSpeedLimitMetersPerSecond = NodeSpeedLimitMetersPerSecond, BrakingDeceleration = BrakingDeceleration
            }],
            SettlementTemplates = source.SettlementTemplates.Select(template =>
                template with { PlatformLengthMeters = PlatformLengthMeters }).ToImmutableArray(),
            Designs = source.Designs.SetItem(0, source.Designs[0] with
            { Parts = [source.Designs[0].Parts[0] with
                { Capabilities = [new PartCapability.Control(), new PartCapability.Propulsion(100, 30), new PartCapability.Storage(100)] }] })
        };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareNew(input, content,
            new(WorldFixture.Options.Runtime, false, 32)));
        _session = candidate.Activate();
        TrainRecoveryFixture.Configure(_session);
        var train = TrainRecoveryFixture.Train(_session);
        var stops = train.Plan!.Stops.Cast<PlanStop.Station>().ToArray();
        TrainRecoveryFixture.Submit(_session, new CoreCommand.ReplaceRunPlan(train.Id, new(true,
            [new PlanStop.Station(stops[0].Node, 4, []), new PlanStop.Station(stops[1].Node, 3, []),
             new PlanStop.Station(stops[2].Node, 2, [])])));
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
