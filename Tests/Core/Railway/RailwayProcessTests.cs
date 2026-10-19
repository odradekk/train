using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.Sessions;

namespace Train.Core.Tests.Railway;

[TestClass]
[TestCategory("integration")]
public sealed class RailwayProcessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task An_active_expansion_restores_in_another_process_and_keeps_the_checkpoint_suffix()
    {
        var root = TestContext.TestRunResultsDirectory ?? throw new AssertFailedException("缺少测试结果目录。");
        var directory = Path.Combine(root, "railway-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var complete = await Run(directory, "complete");
            var saved = await Run(directory, "save");
            var restored = await Run(directory, "resume");
            Assert.AreEqual(3, new[] { complete.ProcessId, saved.ProcessId, restored.ProcessId }.Distinct().Count());
            foreach (var result in new[] { complete, saved, restored })
            {
                Assert.AreNotEqual(Environment.ProcessId, result.ProcessId);
                Assert.AreEqual(CoreProcessDriver.Identity(), result.Runtime);
            }
            Assert.AreEqual(saved.Samples.Single(), complete.Samples[0]);
            using (var savedState = JsonDocument.Parse(saved.Samples.Single().Payload))
            {
                var jobs = savedState.RootElement.GetProperty("world").GetProperty("railway_jobs");
                Assert.AreEqual(2, jobs.GetArrayLength());
                var expansion = jobs.EnumerateArray().Single(job => job.GetProperty("kind").GetInt32() == (int)RailwayWorkKind.AddTrack);
                Assert.AreEqual((ulong)RailwayProcessDriver.PrefixSteps, expansion.GetProperty("elapsed_ticks").GetUInt64());
                Assert.IsTrue(jobs.EnumerateArray().Any(job => job.GetProperty("kind").GetInt32() == (int)RailwayWorkKind.RepairBreak
                    && job.GetProperty("blockers").GetArrayLength() > 0));
                Assert.AreEqual(1, savedState.RootElement.GetProperty("world").GetProperty("automatic_rail_repairs").GetArrayLength());
            }
            Assert.AreEqual(saved.Samples[0].Payload, await File.ReadAllTextAsync(Path.Combine(directory, "state.json")));
            CollectionAssert.AreEqual(complete.Samples, restored.Samples);
            CollectionAssert.AreEqual(complete.Events, restored.Events);
            Assert.IsTrue(restored.Events.Contains("RailExpanded"));
            Assert.IsTrue(restored.Samples.Length >= 2);
            foreach (var sample in restored.Samples)
                Assert.AreEqual(CoreProcessDriver.Sha256(sample.CanonicalText), sample.Sha256);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<RailwayProcessResult> Run(string directory, string mode)
    {
        var output = Path.Combine(directory, mode + ".json");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (var argument in new[] { typeof(RailwayProcessTests).Assembly.Location, "--railway-process", mode,
            output, Path.Combine(directory, "state.json") }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new AssertFailedException("无法启动铁路子进程。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { throw new AssertFailedException($"铁路子进程超过 60 秒：{directory}"); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(directory, mode + ".stdout.log"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(directory, mode + ".stderr.log"), await stderr);
        }
        Assert.AreEqual(0, process.ExitCode, await File.ReadAllTextAsync(Path.Combine(directory, mode + ".stderr.log")));
        var result = JsonSerializer.Deserialize<RailwayProcessResult>(await File.ReadAllTextAsync(output))
            ?? throw new AssertFailedException("无法读取铁路子进程结果。");
        Assert.AreEqual(process.Id, result.ProcessId);
        return result;
    }
}
