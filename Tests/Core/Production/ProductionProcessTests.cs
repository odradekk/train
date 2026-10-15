using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.Sessions;

namespace Train.Core.Tests.Production;

[TestClass]
[TestCategory("integration")]
public sealed class ProductionProcessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Frozen_batch_and_blocked_output_survive_process_exits_and_strict_replay()
    {
        var root = TestContext.TestRunResultsDirectory ?? throw new AssertFailedException("缺少当前测试结果目录。");
        var directory = Path.Combine(root, "production-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var continuous = await Run(directory, "complete");
        var saved = await Run(directory, "save");
        var resumed = await Run(directory, "resume");
        var bufferResumed = await Run(directory, "resume-buffer");
        var replayed = await Run(directory, "replay");
        var results = new[] { continuous, saved, resumed, bufferResumed, replayed };
        Assert.AreEqual(5, results.Select(r => r.ProcessId).Distinct().Count());
        foreach (var result in results) Assert.AreNotEqual(Environment.ProcessId, result.ProcessId);
        Assert.AreEqual(continuous.Samples[0], saved.Samples.Single());
        Assert.AreEqual(saved.Samples[0].Payload, await File.ReadAllTextAsync(Path.Combine(directory, "state.json")));
        CollectionAssert.AreEqual(continuous.Samples, resumed.Samples);
        CollectionAssert.AreEqual(continuous.Events, resumed.Events);
        CollectionAssert.AreEqual(continuous.Samples[3..], bufferResumed.Samples);
        CollectionAssert.AreEqual(continuous.Events.Where(AfterBuffer).ToArray(), bufferResumed.Events);
        CollectionAssert.AreEqual(continuous.Samples, replayed.Samples);
        CollectionAssert.AreEqual(continuous.Events, replayed.Events);
        foreach (var sample in replayed.Samples) Assert.AreEqual(CoreProcessDriver.Sha256(sample.CanonicalText), sample.Sha256);

        using var frozen = JsonDocument.Parse(saved.Samples[0].Query);
        var batch = frozen.RootElement.GetProperty("Facility").GetProperty("Batch");
        Assert.AreEqual(.5, batch.GetProperty("ProgressWork").GetDouble(), 1e-12);
        Assert.IsTrue(batch.GetProperty("CurrentSpecificationDiffers").GetBoolean());
        Assert.AreEqual(30d, frozen.RootElement.GetProperty("Facility").GetProperty("ActualWorkPerSecond").GetDouble());
        using var blocked = JsonDocument.Parse(bufferResumed.Samples[0].Query);
        var facility = blocked.RootElement.GetProperty("Facility");
        Assert.AreEqual((int)ProductionState.OutputBlocked, facility.GetProperty("State").GetInt32());
        Assert.AreEqual(1L, facility.GetProperty("OutputBuffer")[0].GetProperty("Quantity").GetInt64());
        Assert.AreEqual(10L, blocked.RootElement.GetProperty("Used").GetInt64());
        using var final = JsonDocument.Parse(replayed.Samples[^1].Query);
        Assert.AreEqual(15L, final.RootElement.GetProperty("Used").GetInt64());
        CollectionAssert.AreEquivalent(new long[] { 5, 10 }, final.RootElement.GetProperty("Stacks").EnumerateArray()
            .Select(s => s.GetProperty("Quantity").GetInt64()).ToArray());
        Assert.AreEqual(2, continuous.Events.Count(e => e.Contains("\"Kind\":\"ProductionCompleted\"", StringComparison.Ordinal)));
        Directory.Delete(directory, recursive: true);

        bool AfterBuffer(string value)
        {
            using var document = JsonDocument.Parse(value);
            var position = JsonSerializer.Deserialize<SimulationPosition>(document.RootElement.GetProperty("Position"));
            return position.CompletedSteps > continuous.Samples[3].Position.CompletedSteps
                || position.CompletedSteps == continuous.Samples[3].Position.CompletedSteps && position.Sequence > continuous.Samples[3].Position.Sequence;
        }
    }

    private async Task<ProductionProcessResult> Run(string directory, string mode)
    {
        var output = Path.Combine(directory, mode + ".json");
        var stdoutPath = Path.Combine(directory, mode + ".stdout.log");
        var stderrPath = Path.Combine(directory, mode + ".stderr.log");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (var argument in new[] { typeof(ProductionProcessTests).Assembly.Location, "--production-process", mode, output, directory })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new AssertFailedException("无法启动生产子进程。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(TestContext.CancellationToken); }
        catch (OperationCanceledException) { throw new AssertFailedException($"测试运行已取消；生产子进程 {mode}：{directory}"); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(stdoutPath, await stdout, new UTF8Encoding(false));
            await File.WriteAllTextAsync(stderrPath, await stderr, new UTF8Encoding(false));
        }
        Assert.AreEqual(0, process.ExitCode, $"子进程失败：{await File.ReadAllTextAsync(stderrPath)}");
        var result = JsonSerializer.Deserialize<ProductionProcessResult>(await File.ReadAllTextAsync(output))
            ?? throw new AssertFailedException("无法读取生产子进程结果。");
        Assert.AreEqual(process.Id, result.ProcessId);
        return result;
    }
}
