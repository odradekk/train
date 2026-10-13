using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;

namespace Train.Core.Tests.Sessions;

[TestClass]
[TestCategory("integration")]
public sealed class CoreProcessTests
{
    [TestMethod]
    public async Task Independent_processes_generate_identical_complete_world_states()
    {
        var directory = CreateDirectory();
        var first = await Run(directory, "first", "world-complete");
        var second = await Run(directory, "second", "world-complete");
        AssertIndependent(first, second);
        AssertSamples(first.Samples, second.Samples);
        CollectionAssert.AreEqual(first.Receipts, second.Receipts);
        using var payload = JsonDocument.Parse(first.Samples[0].Payload);
        Assert.AreEqual(42UL, payload.RootElement.GetProperty("world").GetProperty("source").GetProperty("world_seed").GetUInt64());
        Assert.AreEqual(1, payload.RootElement.GetProperty("world").GetProperty("nodes").GetArrayLength());
        Directory.Delete(directory, true);
    }

    [TestMethod]
    public async Task World_save_in_one_process_restores_the_same_execution_in_another()
    {
        var directory = CreateDirectory();
        var continuous = await Run(directory, "continuous", "world-complete");
        var prefix = await Run(directory, "prefix", "world-save");
        var restored = await Run(directory, "restored", "world-resume");
        AssertIndependent(continuous, prefix);
        AssertIndependent(prefix, restored);
        Assert.AreEqual(prefix.Samples[^1].Payload, await File.ReadAllTextAsync(Path.Combine(directory, "state.json")));
        AssertSamples(continuous.Samples[..5], prefix.Samples);
        AssertSamples(continuous.Samples[4..], restored.Samples);
        CollectionAssert.AreEqual(continuous.Receipts[..3], prefix.Receipts);
        CollectionAssert.AreEqual(continuous.Receipts[3..], restored.Receipts);
        Assert.AreEqual(new SimulationPosition(1, 2), restored.Samples[0].Position);
        Directory.Delete(directory, true);
    }

    [TestMethod]
    public async Task Independent_processes_repeat_the_same_session_boundaries()
    {
        var directory = CreateDirectory();
        var first = await Run(directory, "first", "run");
        var second = await Run(directory, "second", "run");
        AssertIndependent(first, second);
        AssertRun(first);
        AssertRun(second);
        AssertSamples(first.Samples, second.Samples);
        CollectionAssert.AreEqual(first.Receipts, second.Receipts);
        Directory.Delete(directory, true);
    }

    [TestMethod]
    public async Task Save_exit_and_public_restore_preserve_the_boundary_and_remaining_execution()
    {
        var directory = CreateDirectory();
        var continuous = await Run(directory, "continuous", "run");
        var prefix = await Run(directory, "prefix", "save");
        Assert.IsTrue(File.Exists(Path.Combine(directory, "state.json")));
        Assert.AreEqual(prefix.Samples[^1].Payload, await File.ReadAllTextAsync(Path.Combine(directory, "state.json")));
        var restored = await Run(directory, "restored", "resume");
        AssertIndependent(continuous, prefix);
        AssertIndependent(prefix, restored);
        AssertRun(continuous);
        AssertSamples(continuous.Samples[..5], prefix.Samples);
        AssertSamples(continuous.Samples[4..], restored.Samples);
        CollectionAssert.AreEqual(continuous.Receipts[..3], prefix.Receipts);
        CollectionAssert.AreEqual(continuous.Receipts[3..], restored.Receipts);
        Assert.AreEqual(new SimulationPosition(8, 2), restored.Samples[0].Position,
            "第一份恢复采样必须是恢复边界本身。");
        Assert.AreEqual(new CoreRevision(24), restored.Samples[0].Revision);
        CollectionAssert.AreEqual(new ulong[] { 13, 14 }, restored.Receipts.Select(item => item.Number).ToArray());
        Directory.Delete(directory, true);
    }

    private static void AssertRun(CoreProcessResult result)
    {
        CollectionAssert.AreEqual(new[]
        {
            new SimulationPosition(7, 2), new(7, 3), new(8, 0), new(8, 1),
            new(8, 2), new(8, 3), new(8, 4), new(10, 0)
        }, result.Samples.Select(item => item.Position).ToArray());
        CollectionAssert.AreEqual(new ulong[] { 20, 21, 22, 23, 24, 25, 26, 28 },
            result.Samples.Select(item => item.Revision.Value).ToArray());
        CollectionAssert.AreEqual(new[] { 'A', 'B', 'B', 'A', 'B', 'C', 'A', 'A' }
            .Select(character => new string(character, 64)).ToArray(),
            result.Samples.Select(item => item.InputIdentity).ToArray());
        CollectionAssert.AreEqual(new[]
        {
            new CoreProcessReceipt(10, new(7, 2), new(7, 3)),
            new(11, new(8, 0), new(8, 1)), new(12, new(8, 1), new(8, 2)),
            new(13, new(8, 2), new(8, 3)), new(14, new(8, 3), new(8, 4))
        }, result.Receipts);
    }

    private static void AssertSamples(CoreProcessSample[] expected, CoreProcessSample[] actual)
    {
        Assert.AreEqual(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(expected[index], actual[index], $"采样 {index} 不同。");
            Assert.AreEqual(CoreProcessDriver.Sha256(actual[index].CanonicalText), actual[index].Sha256);
        }
    }

    private static void AssertIndependent(CoreProcessResult first, CoreProcessResult second)
    {
        Assert.AreNotEqual(first.ProcessId, second.ProcessId);
        Assert.AreNotEqual(Environment.ProcessId, first.ProcessId);
        Assert.AreNotEqual(Environment.ProcessId, second.ProcessId);
        Assert.AreEqual(CoreProcessDriver.Identity(), first.Identity);
        Assert.AreEqual(first.Identity, second.Identity);
    }

    private static async Task<CoreProcessResult> Run(string directory, string name, string mode)
    {
        var output = Path.Combine(directory, name + ".json");
        var stdoutPath = Path.Combine(directory, name + ".stdout.log");
        var stderrPath = Path.Combine(directory, name + ".stderr.log");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = directory
        };
        foreach (var argument in new[] { typeof(CoreProcessTests).Assembly.Location, "--core-process",
                     mode, output, Path.Combine(directory, "state.json") })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new AssertFailedException("无法启动核心子进程。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await File.WriteAllTextAsync(stdoutPath, await stdout, new UTF8Encoding(false));
            await File.WriteAllTextAsync(stderrPath, await stderr, new UTF8Encoding(false));
            throw new AssertFailedException($"核心子进程超过 30 秒：{directory}");
        }
        await File.WriteAllTextAsync(stdoutPath, await stdout, new UTF8Encoding(false));
        await File.WriteAllTextAsync(stderrPath, await stderr, new UTF8Encoding(false));
        Assert.AreEqual(0, process.ExitCode, $"子进程失败：{stdoutPath}；{stderrPath}");
        Assert.IsTrue(File.Exists(output), $"缺少子进程结果：{output}");
        var result = JsonSerializer.Deserialize<CoreProcessResult>(await File.ReadAllTextAsync(output))
            ?? throw new AssertFailedException($"无法读取子进程结果：{output}");
        Assert.AreEqual(process.Id, result.ProcessId);
        return result;
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "core-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
