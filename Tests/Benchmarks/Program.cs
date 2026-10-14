using System.Text.Json;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Train.Benchmarks;

if (args.Length != 2 || args[0] != "--artifacts")
{
    Console.Error.WriteLine("Usage: dotnet run -c Release --project Tests/Benchmarks/Train.Benchmarks.csproj -- --artifacts <directory>");
    return 1;
}

try
{
    var artifacts = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(artifacts);
    var resultPath = Path.Combine(artifacts, "throughput.json");
    // A failed rerun must not leave the preceding successful measurement available to the gate.
    File.Delete(resultPath);
    var config = ManualConfig.Create(DefaultConfig.Instance)
        .WithArtifactsPath(artifacts)
        .AddJob(Job.Default.WithLaunchCount(1).WithWarmupCount(3).WithIterationCount(15)
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
        workload = "core-train-loop-v1",
        measuredAtUtc = DateTimeOffset.UtcNow,
        trainCount = TrainLoopBenchmark.TrainCount,
        stepsPerBatch = TrainLoopBenchmark.StepsPerBatch,
        meanNanosecondsPerStep = statistics.Mean,
        standardDeviationNanosecondsPerStep = statistics.StandardDeviation,
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
