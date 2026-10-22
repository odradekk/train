using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.Sessions;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Construction;

[TestClass]
[TestCategory("integration")]
public sealed class ConstructionProcessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Active_construction_survives_process_exit_and_strict_replay()
    {
        var root = TestContext.TestRunResultsDirectory ?? throw new AssertFailedException("缺少当前测试结果目录。");
        var directory = Path.Combine(root, "construction-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var continuous = await Run(directory, "complete");
        var saved = await Run(directory, "save");
        var resumed = await Run(directory, "resume");
        var replayed = await Run(directory, "replay");
        Assert.AreEqual(4, new[] { continuous, saved, resumed, replayed }.Select(r => r.ProcessId).Distinct().Count());
        foreach (var result in new[] { continuous, saved, resumed, replayed }) Assert.AreNotEqual(Environment.ProcessId, result.ProcessId);
        Assert.AreEqual(continuous.Samples[0], saved.Samples.Single());
        Assert.AreEqual(saved.Samples[0].Payload, await File.ReadAllTextAsync(Path.Combine(directory, "state.json")));
        CollectionAssert.AreEqual(continuous.Samples, resumed.Samples);
        CollectionAssert.AreEqual(continuous.Events, resumed.Events);
        CollectionAssert.AreEqual(continuous.Samples, replayed.Samples);
        CollectionAssert.AreEqual(continuous.Events, replayed.Events);
        foreach (var sample in replayed.Samples) Assert.AreEqual(CoreProcessDriver.Sha256(sample.CanonicalText), sample.Sha256);
        Assert.AreEqual(1, continuous.Events.Count(e => e.Contains("\"Kind\":\"ConstructionCompleted\"", StringComparison.Ordinal)));
        var factory = CoreFactory.Create();
        using var candidate = WorldFixture.Success(factory.PrepareRestore(WorldFixture.Success(factory.DecodeState(saved.Samples[0].Payload)),
            ConstructionProcessDriver.Updated, WorldFixture.Options));
        using var session = candidate.Activate();
        Assert.AreEqual(.5, ConstructionFixture.Job(session.Read(), new(1)).ProgressWork, 1e-12);
        Assert.AreEqual(2L, ConstructionFixture.Job(session.Read(), new(1)).FrozenTotalWork);
        Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public async Task Building_construction_survives_process_exit_and_strict_replay()
    {
        var root = TestContext.TestRunResultsDirectory ?? throw new AssertFailedException("缺少当前测试结果目录。");
        var directory = Path.Combine(root, "building-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var continuous = await Run(directory, "complete", "building");
        var saved = await Run(directory, "save", "building");
        var resumed = await Run(directory, "resume", "building");
        var replayed = await Run(directory, "replay", "building");
        Assert.AreEqual(4, new[] { continuous, saved, resumed, replayed }.Select(r => r.ProcessId).Distinct().Count());
        foreach (var result in new[] { continuous, saved, resumed, replayed }) Assert.AreNotEqual(Environment.ProcessId, result.ProcessId);
        Assert.AreEqual(continuous.Samples[0], saved.Samples.Single());
        CollectionAssert.AreEqual(continuous.Samples, resumed.Samples);
        CollectionAssert.AreEqual(continuous.Events, resumed.Events);
        CollectionAssert.AreEqual(continuous.Samples, replayed.Samples);
        CollectionAssert.AreEqual(continuous.Events, replayed.Events);
        Assert.AreEqual(3, continuous.Events.Count(e => e.Contains("\"Kind\":\"ConstructionCompleted\"", StringComparison.Ordinal)));
        var factory = CoreFactory.Create();
        using var candidate = WorldFixture.Success(factory.PrepareRestore(WorldFixture.Success(factory.DecodeState(saved.Samples[0].Payload)),
            ConstructionProcessDriver.BuildingOriginal, WorldFixture.Options));
        using var session = candidate.Activate();
        Assert.AreEqual(1d, ConstructionFixture.Job(session.Read(), new(1)).ProgressWork, 1e-12);
        Assert.AreEqual(2L, ConstructionFixture.Job(session.Read(), new(1)).FrozenTotalWork);
        Assert.IsInstanceOfType<ConstructionTarget.NewAssembly>(ConstructionFixture.Job(session.Read(), new(1)).Target);
        Assert.AreEqual(8L, ConstructionFixture.Stock(session, ConstructionFixture.Local));
        Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public async Task Construction_failing_after_the_save_boundary_matches_the_uninterrupted_run()
    {
        var root = TestContext.TestRunResultsDirectory ?? throw new AssertFailedException("缺少当前测试结果目录。");
        var directory = Path.Combine(root, "construction-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var continuous = await Run(directory, "complete", "failure");
        var saved = await Run(directory, "save", "failure");
        var resumed = await Run(directory, "resume", "failure");
        Assert.AreEqual(3, new[] { continuous, saved, resumed }.Select(r => r.ProcessId).Distinct().Count());
        Assert.AreEqual(continuous.Samples[0], saved.Samples.Single());
        CollectionAssert.AreEqual(continuous.Samples, resumed.Samples);
        CollectionAssert.AreEqual(continuous.Events, resumed.Events);
        Assert.AreEqual(1, continuous.Events.Select(e => JsonDocument.Parse(e).RootElement).Count(e =>
            e.GetProperty("Kind").GetString() == "ConstructionStateChanged" && JsonDocument.Parse(e.GetProperty("Value").GetString()!)
                .RootElement.GetProperty("After").GetInt32() == (int)ConstructionState.Failed));
        Directory.Delete(directory, recursive: true);
    }

    private async Task<ConstructionProcessResult> Run(string directory, string mode, string? scenario = null)
    {
        var output = Path.Combine(directory, mode + ".json");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (var argument in new[] { typeof(ConstructionProcessTests).Assembly.Location, "--construction-process", mode, output, directory })
            start.ArgumentList.Add(argument);
        if (scenario is not null) start.ArgumentList.Add(scenario);
        using var process = Process.Start(start) ?? throw new AssertFailedException("无法启动施工子进程。");
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
        var result = JsonSerializer.Deserialize<ConstructionProcessResult>(await File.ReadAllTextAsync(output))!;
        Assert.AreEqual(process.Id, result.ProcessId);
        return result;
    }
}
