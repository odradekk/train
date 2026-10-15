using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.Core.Tests.Settlements;

[TestClass]
[TestCategory("integration")]
public sealed class PopulationProcessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Independent_process_resume_matches_uninterrupted_population_events_and_checkpoints()
    {
        var root = TestContext.TestRunResultsDirectory ?? throw new AssertFailedException("缺少测试结果目录。");
        var directory = Path.Combine(root, "population-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var complete = await Run(directory, "complete");
        var save = await Run(directory, "save");
        var resume = await Run(directory, "resume");
        Assert.AreEqual(3, new[] { complete.ProcessId, save.ProcessId, resume.ProcessId }.Distinct().Count());
        Assert.IsTrue(new[] { complete, save, resume }.All(r => r.ProcessId != Environment.ProcessId));
        Assert.AreEqual(complete.Samples[0], save.Samples.Single());
        Assert.AreEqual(save.Samples.Single(), await File.ReadAllTextAsync(Path.Combine(directory, "state.json")));
        CollectionAssert.AreEqual(complete.Samples, resume.Samples);
        CollectionAssert.AreEqual(complete.Checkpoints, resume.Checkpoints);
        CollectionAssert.AreEqual(complete.Events, resume.Events);
        Assert.IsTrue(resume.Events.Any(e => e.Contains("PopulationSettled", StringComparison.Ordinal)));
        Directory.Delete(directory, recursive: true);
    }

    private static async Task<PopulationProcessResult> Run(string directory, string mode)
    {
        var output = Path.Combine(directory, mode + ".json");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (var argument in new[] { typeof(PopulationProcessTests).Assembly.Location, "--population-process", mode,
            output, Path.Combine(directory, "state.json") }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new AssertFailedException("无法启动人口子进程。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { throw new AssertFailedException($"人口子进程超过 30 秒：{directory}"); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(directory, mode + ".stdout.log"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(directory, mode + ".stderr.log"), await stderr);
        }
        Assert.AreEqual(0, process.ExitCode, await File.ReadAllTextAsync(Path.Combine(directory, mode + ".stderr.log")));
        var result = JsonSerializer.Deserialize<PopulationProcessResult>(await File.ReadAllTextAsync(output))
            ?? throw new AssertFailedException("无法读取人口子进程结果。");
        Assert.AreEqual(process.Id, result.ProcessId);
        return result;
    }
}
