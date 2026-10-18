using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Construction.ConstructionFixture;

namespace Train.Core.Tests.Construction;

[TestClass]
[TestCategory("integration")]
public sealed class CommissionProcessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Queued_and_active_commissions_survive_process_exit_content_update_and_strict_replay()
    {
        var directory = Path.Combine(TestContext.TestRunResultsDirectory ?? throw new AssertFailedException("缺少测试结果目录。"),
            "commission-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var continuous = await Run("complete");
        var saved = await Run("save");
        var resumed = await Run("resume");
        var replayed = await Run("replay");
        Assert.AreEqual(4, new[] { continuous, saved, resumed, replayed }.Select(r => r.ProcessId).Distinct().Count());
        foreach (var result in new[] { continuous, saved, resumed, replayed }) Assert.AreNotEqual(Environment.ProcessId, result.ProcessId);
        Assert.AreEqual(continuous.Samples[0], saved.Samples.Single());
        CollectionAssert.AreEqual(continuous.Samples, resumed.Samples);
        CollectionAssert.AreEqual(continuous.Events, resumed.Events);
        CollectionAssert.AreEqual(continuous.Samples, replayed.Samples);
        CollectionAssert.AreEqual(continuous.Events, replayed.Events);
        foreach (var sample in replayed.Samples) Assert.AreEqual(CoreProcessDriver.Sha256(sample.CanonicalText), sample.Sha256);
        Assert.AreEqual(2, continuous.Events.Count(e => e.Contains("\"Kind\":\"ConstructionCompleted\"", StringComparison.Ordinal)));
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(
            WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Samples[0].Payload)), CommissionProcessDriver.Updated, WorldFixture.Options));
        using var session = candidate.Activate();
        Assert.AreEqual(.5, Job(session.Read(), new(1)).ProgressWork, 1e-12);
        Assert.AreEqual(2L, Job(session.Read(), new(1)).FrozenTotalWork);
        Assert.AreEqual(ConstructionState.Queued, Job(session.Read(), new(2)).State);
        Assert.AreEqual(0d, Job(session.Read(), new(2)).ProgressWork);
        Assert.AreEqual(12L, session.Read().Economy.UnassignedServicePayments.Value);
        Directory.Delete(directory, recursive: true);

        async Task<ConstructionProcessResult> Run(string mode)
        {
            var output = Path.Combine(directory, mode + ".json");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = directory };
            foreach (var argument in new[] { typeof(CommissionProcessTests).Assembly.Location, "--commission-process", mode, output, directory })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new AssertFailedException("无法启动委托子进程。");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(TestContext.CancellationToken); }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                await File.WriteAllTextAsync(Path.Combine(directory, mode + ".stdout.log"), await stdout);
                await File.WriteAllTextAsync(Path.Combine(directory, mode + ".stderr.log"), await stderr);
            }
            Assert.AreEqual(0, process.ExitCode, await stderr);
            var result = JsonSerializer.Deserialize<ConstructionProcessResult>(await File.ReadAllTextAsync(output))!;
            Assert.AreEqual(process.Id, result.ProcessId);
            return result;
        }
    }
}
