using System.Text.Json;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Train.Benchmarks;
using Train.Core.Tests.Railway;

var dryRun = args.Length == 4 && args[2] == "--job" && args[3] == "Dry";
if ((args.Length != 2 && !dryRun) || args[0] != "--artifacts")
{
    Console.Error.WriteLine("Usage (from Tests/Benchmarks): dotnet run -c Release --no-build -- --artifacts <directory> [--job Dry]");
    return 1;
}

try
{
    var artifacts = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(artifacts);
    var resultPath = Path.Combine(artifacts, "throughput.json");
    // A failed rerun must not leave the preceding successful measurement available to the gate.
    File.Delete(resultPath);
    var job = dryRun ? Job.Dry : Job.Default.WithWarmupCount(3).WithIterationCount(15);
    var config = ManualConfig.Create(DefaultConfig.Instance)
        .WithArtifactsPath(artifacts)
        .AddJob(job.WithLaunchCount(1)
            .WithInvocationCount(1).WithUnrollFactor(1));
    var summary = BenchmarkRunner.Run<TrainLoopBenchmark>(config);
    var report = summary.Reports.SingleOrDefault();
    if (summary.HasCriticalValidationErrors || report is null || !report.Success ||
        report.ResultStatistics is not { } statistics || !double.IsFinite(statistics.Mean) || statistics.Mean <= 0)
    {
        Console.Error.WriteLine("Benchmark 未生成有效测量；查看 artifacts 中的原始日志。");
        return 1;
    }

    var result = new
    {
        workload = "core-train-loop-v2",
        dryRun,
        measuredAtUtc = DateTimeOffset.UtcNow,
        trainCount = TrainLoopBenchmark.TrainCount,
        seed = TrainLoopBenchmark.Seed,
        generatorVersion = TrainLoopBenchmark.GeneratorVersion,
        stepsPerBatch = TrainLoopBenchmark.StepsPerBatch,
        approachLengthMeters = TrainLoopBenchmark.ApproachLengthMeters,
        platformLengthMeters = TrainLoopBenchmark.PlatformLengthMeters,
        departureLengthMeters = TrainLoopBenchmark.DepartureLengthMeters,
        nodeSpeedLimitMetersPerSecond = TrainLoopBenchmark.NodeSpeedLimitMetersPerSecond,
        brakingDecelerationMetersPerSecondSquared = TrainLoopBenchmark.BrakingDeceleration,
        worldWidthMeters = TrainLoopBenchmark.WorldWidthMeters,
        worldHeightMeters = TrainLoopBenchmark.WorldHeightMeters,
        populationConsumptionIntervalTicks = TrainRecoveryFixture.ConsumptionIntervalTicks,
        populationStatisticsPeriodTicks = TrainRecoveryFixture.StatisticsPeriodTicks,
        meanNanosecondsPerStep = statistics.Mean,
        standardDeviationNanosecondsPerStep = double.IsFinite(statistics.StandardDeviation)
            ? (double?)statistics.StandardDeviation : null,
        sampleCount = statistics.N,
        stepsPerSecond = 1_000_000_000 / statistics.Mean
    };
    File.WriteAllText(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
