using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Tests.Sessions;

namespace Train.Core.Tests.Production;

[TestClass]
[TestCategory("integration")]
public sealed class ExtractionProcessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Extraction_and_resource_cycles_survive_process_exit_and_strict_replay()
    {
        var root = TestContext.TestRunResultsDirectory ?? throw new AssertFailedException("缺少当前测试结果目录。");
        var directory = Path.Combine(root, "extraction-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var continuous = await Run(directory, "complete");
        var resumed = await Run(directory, "resume");
        var replayed = await Run(directory, "replay");
        Assert.AreEqual(3, new[] { continuous.ProcessId, resumed.ProcessId, replayed.ProcessId }.Distinct().Count());
        Assert.IsTrue(new[] { continuous.ProcessId, resumed.ProcessId, replayed.ProcessId }.All(id => id != Environment.ProcessId));
        var expected = continuous.Runs.Single();
        StringAssert.Contains(expected.Samples[0].Query, "fixture:material");
        Assert.IsTrue(expected.Events.Any(value => value.Contains("fixture:material", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(expected.Samples, replayed.Runs.Single().Samples);
        CollectionAssert.AreEqual(expected.Events, replayed.Runs.Single().Events);
        foreach (var restored in resumed.Runs)
        {
            CollectionAssert.AreEqual(expected.Samples[(restored.SavedAt - 1)..], restored.Samples);
            CollectionAssert.AreEqual(expected.Events.Where(value => After(value, restored.SavedAt)).ToArray(), restored.Events);
            Assert.AreEqual(expected.Samples[restored.SavedAt - 1].Payload,
                await File.ReadAllTextAsync(Path.Combine(directory, $"state-{restored.SavedAt}.json")));
        }
        foreach (var sample in expected.Samples)
            Assert.AreEqual(CoreProcessDriver.Sha256(sample.CanonicalText), sample.Sha256);
        using var active = JsonDocument.Parse(expected.Samples[1].Query);
        Assert.IsTrue(active.RootElement.GetProperty("Finite").GetProperty("Batch").GetProperty("ProgressWork").GetDouble() > 0);
        Assert.IsTrue(active.RootElement.GetProperty("ResourceRecovery").EnumerateArray().Any(x =>
            x.GetProperty("Remaining").GetDouble() is > 0 and < 1));
        using var buffered = JsonDocument.Parse(expected.Samples[3].Query);
        Assert.IsTrue(buffered.RootElement.GetProperty("Finite").GetProperty("OutputBuffer").GetArrayLength() > 0);
        Assert.IsTrue(buffered.RootElement.GetProperty("ResourceRegenerations").EnumerateArray().Any(x =>
            x.GetProperty("ElapsedSteps").GetUInt64() > 0));
        Assert.IsTrue(expected.Events.Any(e => e.Contains("\"Kind\":\"ResourceRegenerated\"", StringComparison.Ordinal)));
        Assert.IsTrue(expected.Events.Any(e => e.Contains("\"Kind\":\"ResourceRecovered\"", StringComparison.Ordinal)));
        Assert.IsTrue(expected.Samples[0].Payload != expected.Samples[^1].Payload);
        Directory.Delete(directory, recursive: true);
    }

    private static bool After(string value, int savedAt)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.GetProperty("Position").GetProperty("CompletedSteps").GetUInt64() > (ulong)savedAt;
    }

    private async Task<ExtractionProcessResult> Run(string directory, string mode)
    {
        var output = Path.Combine(directory, mode + ".json");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (var argument in new[] { typeof(ExtractionProcessTests).Assembly.Location, "--extraction-process", mode, output, directory })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new AssertFailedException("无法启动开采子进程。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(TestContext.CancellationToken); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(directory, mode + ".stdout.log"), await stdout, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(directory, mode + ".stderr.log"), await stderr, new UTF8Encoding(false));
        }
        Assert.AreEqual(0, process.ExitCode, $"开采子进程失败：{await stderr}");
        var result = JsonSerializer.Deserialize<ExtractionProcessResult>(await File.ReadAllTextAsync(output))
            ?? throw new AssertFailedException("无法读取开采子进程结果。");
        Assert.AreEqual(process.Id, result.ProcessId);
        return result;
    }
}
