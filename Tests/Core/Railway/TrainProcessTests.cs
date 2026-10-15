using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.Sessions;

namespace Train.Core.Tests.Railway;

[TestClass]
[TestCategory("integration")]
public sealed class TrainProcessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("approaching")]
    [DataRow("docked")]
    [DataRow("edge")]
    [DataRow("pending-stop")]
    public async Task Running_train_restores_in_a_new_process_with_identical_queries_events_and_checkpoints(string boundary)
    {
        var root = TestContext.TestRunResultsDirectory ?? throw new AssertFailedException("缺少当前测试结果目录。");
        var directory = Path.Combine(root, "train-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var continuous = await Run(directory, "continuous", "complete", boundary);
        var repeated = await Run(directory, "repeated", "complete", boundary);
        var prefix = await Run(directory, "prefix", "save", boundary);
        var resumed = await Run(directory, "resumed", "resume", boundary);
        foreach (var result in new[] { continuous, repeated, prefix, resumed })
        {
            Assert.AreNotEqual(Environment.ProcessId, result.ProcessId);
            Assert.AreEqual(CoreProcessDriver.Identity(), result.Runtime);
        }
        Assert.AreEqual(4, new[] { continuous.ProcessId, repeated.ProcessId, prefix.ProcessId, resumed.ProcessId }.Distinct().Count());
        CollectionAssert.AreEqual(continuous.Samples, repeated.Samples);
        CollectionAssert.AreEqual(continuous.Events, repeated.Events);
        CollectionAssert.AreEqual(continuous.PrefixEvents, repeated.PrefixEvents);
        Assert.AreEqual(continuous.Samples[0], prefix.Samples.Single());
        CollectionAssert.AreEqual(continuous.PrefixEvents, prefix.PrefixEvents);
        CollectionAssert.AreEqual(continuous.Samples, resumed.Samples);
        CollectionAssert.AreEqual(continuous.Events, resumed.Events);
        Assert.AreEqual(prefix.Samples[0].Payload, await File.ReadAllTextAsync(Path.Combine(directory, "state.json")));
        Assert.IsTrue(continuous.Events.Any(value => value.Contains("TrainArrived", StringComparison.Ordinal)));
        Assert.IsTrue(continuous.Events.Any(value => value.Contains("TrainActivityChanged", StringComparison.Ordinal)));
        Assert.IsTrue(continuous.PrefixEvents.Any(value => value.Contains("TrainCompositionChanged", StringComparison.Ordinal)));
        foreach (var sample in resumed.Samples)
            Assert.AreEqual(CoreProcessDriver.Sha256(sample.CanonicalText), sample.Sha256);
        using var query = JsonDocument.Parse(prefix.Samples[0].Query);
        if (boundary == "docked")
        {
            Assert.AreEqual((int)TrainActivity.Servicing, query.RootElement.GetProperty("Activity").GetInt32());
            Assert.IsTrue(query.RootElement.GetProperty("RemainingDwellSteps").GetUInt64() > 0);
        }
        else Assert.IsTrue(query.RootElement.GetProperty("Position").GetString()!.Contains("DistanceMeters", StringComparison.Ordinal));
        using var payload = JsonDocument.Parse(prefix.Samples[0].Payload);
        var savedTrain = payload.RootElement.GetProperty("world").GetProperty("trains")[0];
        if (Enum.TryParse<NodeTravelPhase>(boundary, ignoreCase: true, out var phase))
            Assert.AreEqual((int)phase, savedTrain.GetProperty("position").GetProperty("phase").GetInt32());
        if (boundary == "approaching")
        {
            Assert.IsTrue(query.RootElement.GetProperty("SpeedMetersPerSecond").GetDouble() > 0);
            Assert.IsTrue(savedTrain.GetProperty("position").GetProperty("platform_number").GetInt32() > 0);
        }
        Assert.AreEqual(boundary == "pending-stop", savedTrain.GetProperty("stop_requested").GetBoolean());
        Directory.Delete(directory, recursive: true);
    }

    private async Task<TrainProcessResult> Run(string directory, string name, string mode, string boundary)
    {
        var output = Path.Combine(directory, name + ".json");
        var stdoutPath = Path.Combine(directory, name + ".stdout.log");
        var stderrPath = Path.Combine(directory, name + ".stderr.log");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (var argument in new[] { typeof(TrainProcessTests).Assembly.Location, "--train-process", mode,
            boundary, output, Path.Combine(directory, "state.json") }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new AssertFailedException("无法启动铁路子进程。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(TestContext.CancellationToken); }
        catch (OperationCanceledException)
        {
            throw new AssertFailedException($"测试运行已取消；铁路子进程 {name}：{directory}");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(stdoutPath, await stdout, new UTF8Encoding(false));
            await File.WriteAllTextAsync(stderrPath, await stderr, new UTF8Encoding(false));
        }
        Assert.AreEqual(0, process.ExitCode, $"子进程失败：{await File.ReadAllTextAsync(stderrPath)}");
        var result = JsonSerializer.Deserialize<TrainProcessResult>(await File.ReadAllTextAsync(output))
            ?? throw new AssertFailedException("无法读取铁路子进程结果。");
        Assert.AreEqual(process.Id, result.ProcessId);
        return result;
    }
}
