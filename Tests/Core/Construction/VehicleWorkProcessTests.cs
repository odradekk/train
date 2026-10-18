using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.Sessions;

namespace Train.Core.Tests.Construction;

[TestClass]
[TestCategory("integration")]
public sealed class VehicleWorkProcessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Refit_and_paused_deconstruction_survive_process_exit_and_strict_replay()
    {
        var root = TestContext.TestRunResultsDirectory ?? throw new AssertFailedException("缺少当前测试结果目录。");
        var directory = Path.Combine(root, "vehicle-work-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var continuous = await Run(directory, "complete");
        var refitSave = await Run(directory, "save-refit");
        var refitResume = await Run(directory, "resume-refit");
        var pausedSave = await Run(directory, "save-paused");
        var pausedResume = await Run(directory, "resume-paused");
        var replay = await Run(directory, "replay");
        var results = new[] { continuous, refitSave, refitResume, pausedSave, pausedResume, replay };
        Assert.AreEqual(results.Length, results.Select(result => result.ProcessId).Distinct().Count());
        foreach (var result in results) Assert.AreNotEqual(Environment.ProcessId, result.ProcessId);
        CollectionAssert.AreEqual(continuous.Samples, refitResume.Samples);
        CollectionAssert.AreEqual(continuous.Samples, replay.Samples);
        CollectionAssert.AreEqual(continuous.Samples[..3], pausedSave.Samples);
        Assert.AreEqual(continuous.Samples[0], refitSave.Samples.Single());
        Assert.AreEqual(continuous.Samples[2] with { Events = "[]" }, pausedResume.Samples[0]);
        CollectionAssert.AreEqual(continuous.Samples[3..], pausedResume.Samples[1..]);
        Assert.AreEqual(refitSave.Samples[0].Payload, await File.ReadAllTextAsync(Path.Combine(directory, "refit.state.json")));
        Assert.AreEqual(pausedSave.Samples[^1].Payload, await File.ReadAllTextAsync(Path.Combine(directory, "paused.state.json")));
        foreach (var sample in replay.Samples) Assert.AreEqual(CoreProcessDriver.Sha256(sample.CanonicalText), sample.Sha256);
        Assert.AreEqual(2, continuous.Samples.Sum(sample => CountEvent(sample.Events, "ConstructionCompleted")));
        Assert.AreEqual(1, continuous.Samples.Sum(sample => CountEvent(sample.Events, "InstanceDesignChanged")));
        using var refitQuery = JsonDocument.Parse(continuous.Samples[0].Queries);
        Assert.AreEqual((int)InstanceLock.UnderRefit, refitQuery.RootElement.GetProperty("Lock").GetInt32());
        Assert.AreEqual(1L, refitQuery.RootElement.GetProperty("Reserved").GetInt64());
        Assert.AreEqual(1d, refitQuery.RootElement.GetProperty("ProgressWork").GetDouble());
        using var pausedQuery = JsonDocument.Parse(continuous.Samples[2].Queries);
        using var laterQuery = JsonDocument.Parse(continuous.Samples[3].Queries);
        Assert.AreEqual((int)ConstructionState.Paused, pausedQuery.RootElement.GetProperty("State").GetInt32());
        Assert.AreEqual(2L, pausedQuery.RootElement.GetProperty("Reserved").GetInt64());
        Assert.AreEqual(pausedQuery.RootElement.GetProperty("ProgressWork").GetDouble(), laterQuery.RootElement.GetProperty("ProgressWork").GetDouble());
        Directory.Delete(directory, recursive: true);
    }

    private static int CountEvent(string events, string kind)
    {
        using var document = JsonDocument.Parse(events);
        return document.RootElement.EnumerateArray().Count(item => item.GetString()!.Contains("\"Kind\":\"" + kind + "\"", StringComparison.Ordinal));
    }

    private async Task<VehicleWorkProcessResult> Run(string directory, string mode)
    {
        var output = Path.Combine(directory, mode + ".json");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (var argument in new[] { typeof(VehicleWorkProcessTests).Assembly.Location, "--vehicle-work-process", mode, output, directory })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new AssertFailedException("无法启动车辆施工子进程。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(TestContext.CancellationToken); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(directory, mode + ".stdout.log"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(directory, mode + ".stderr.log"), await stderr);
        }
        Assert.AreEqual(0, process.ExitCode, $"子进程失败：{await File.ReadAllTextAsync(Path.Combine(directory, mode + ".stderr.log"))}");
        var result = JsonSerializer.Deserialize<VehicleWorkProcessResult>(await File.ReadAllTextAsync(output))!;
        Assert.AreEqual(process.Id, result.ProcessId);
        return result;
    }
}
