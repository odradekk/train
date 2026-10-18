using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.Sessions;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Settlements;

[TestClass]
[TestCategory("integration")]
public sealed class FoundingProcessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Active_founding_survives_process_exit_and_strict_replay()
    {
        var root = TestContext.TestRunResultsDirectory ?? throw new AssertFailedException("缺少测试结果目录。");
        var directory = Path.Combine(root, "founding-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var complete = await Run(directory, "complete");
        var save = await Run(directory, "save");
        var resume = await Run(directory, "resume");
        var replay = await Run(directory, "replay");
        var results = new[] { complete, save, resume, replay };
        Assert.AreEqual(4, results.Select(r => r.ProcessId).Distinct().Count());
        Assert.IsTrue(results.All(r => r.ProcessId != Environment.ProcessId));
        Assert.AreEqual(complete.Samples[0], save.Samples.Single());
        Assert.AreEqual(save.Samples[0].Payload, await File.ReadAllTextAsync(Path.Combine(directory, "state.json")));
        CollectionAssert.AreEqual(new[] { new SimulationPosition(1, 0), new(2, 0), new(3, 0), new(4, 0) },
            complete.Samples.Select(s => s.Position).ToArray());
        CollectionAssert.AreEqual(complete.Samples, resume.Samples);
        CollectionAssert.AreEqual(complete.Events, resume.Events);
        CollectionAssert.AreEqual(complete.Samples, replay.Samples);
        CollectionAssert.AreEqual(complete.Events, replay.Events);
        foreach (var sample in replay.Samples) Assert.AreEqual(CoreProcessDriver.Sha256(sample.CanonicalText), sample.Sha256);
        Assert.AreEqual(1, complete.Events.Count(e => e.Contains("\"Kind\":\"SettlementFounded\"", StringComparison.Ordinal)));
        var factory = CoreFactory.Create();
        using var candidate = WorldFixture.Success(factory.PrepareRestore(
            WorldFixture.Success(factory.DecodeState(save.Samples[0].Payload)), FoundingFixture.Content, WorldFixture.Options));
        using var session = candidate.Activate();
        var job = FoundingFixture.Job(session.Read(), new(1));
        Assert.AreEqual(1UL, job.ElapsedTicks);
        Assert.AreEqual(3UL, job.FrozenDurationTicks);
        Directory.Delete(directory, recursive: true);
    }

    private async Task<FoundingProcessResult> Run(string directory, string mode)
    {
        var output = Path.Combine(directory, mode + ".json");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (var argument in new[] { typeof(FoundingProcessTests).Assembly.Location, "--founding-process", mode, output, directory })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new AssertFailedException("无法启动建城子进程。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(directory, mode + ".stdout.log"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(directory, mode + ".stderr.log"), await stderr);
        }
        Assert.AreEqual(0, process.ExitCode, await File.ReadAllTextAsync(Path.Combine(directory, mode + ".stderr.log")));
        var result = JsonSerializer.Deserialize<FoundingProcessResult>(await File.ReadAllTextAsync(output))
            ?? throw new AssertFailedException("无法读取建城子进程结果。");
        Assert.AreEqual(process.Id, result.ProcessId);
        return result;
    }
}
