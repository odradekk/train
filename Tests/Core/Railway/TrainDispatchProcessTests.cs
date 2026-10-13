using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Tests.Sessions;

namespace Train.Core.Tests.Railway;

[TestClass]
[TestCategory("integration")]
public sealed class TrainDispatchProcessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("queue")]
    [DataRow("handoff")]
    [DataRow("returning")]
    public async Task Dispatch_state_restores_from_file_in_an_independent_process(string boundary)
    {
        var root = TestContext.TestRunResultsDirectory ?? throw new AssertFailedException("缺少测试结果目录。");
        var directory = Path.Combine(root, "dispatch-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var continuous = await Run(directory, "complete", boundary);
            var saved = await Run(directory, "save", boundary);
            var restored = await Run(directory, "resume", boundary);
            Assert.AreEqual(3, new[] { continuous.ProcessId, saved.ProcessId, restored.ProcessId }.Distinct().Count());
            foreach (var result in new[] { continuous, saved, restored })
            {
                Assert.AreNotEqual(Environment.ProcessId, result.ProcessId);
                Assert.AreEqual(CoreProcessDriver.Identity(), result.Runtime);
            }
            Assert.AreEqual(saved.Samples.Single(), continuous.Samples[0]);
            Assert.AreEqual(saved.Samples[0].Payload, await File.ReadAllTextAsync(Path.Combine(directory, "state.json")));
            CollectionAssert.AreEqual(continuous.Samples, restored.Samples);
            CollectionAssert.AreEqual(continuous.Events, restored.Events);
            foreach (var sample in restored.Samples)
                Assert.AreEqual(CoreProcessDriver.Sha256(sample.CanonicalText), sample.Sha256);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<TrainProcessResult> Run(string directory, string mode, string boundary)
    {
        var output = Path.Combine(directory, mode + ".json");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (var argument in new[] { typeof(TrainDispatchProcessTests).Assembly.Location, "--dispatch-process", mode,
            boundary, output, Path.Combine(directory, "state.json") }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new AssertFailedException("无法启动调度子进程。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { throw new AssertFailedException($"调度子进程超过 30 秒：{directory}"); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(directory, mode + ".stdout.log"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(directory, mode + ".stderr.log"), await stderr);
        }
        Assert.AreEqual(0, process.ExitCode, await File.ReadAllTextAsync(Path.Combine(directory, mode + ".stderr.log")));
        var result = JsonSerializer.Deserialize<TrainProcessResult>(await File.ReadAllTextAsync(output))
            ?? throw new AssertFailedException("无法读取调度子进程结果。");
        Assert.AreEqual(process.Id, result.ProcessId);
        return result;
    }
}
