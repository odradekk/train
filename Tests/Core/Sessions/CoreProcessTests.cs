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
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task World_save_in_one_process_restores_the_same_execution_in_another()
    {
        var directory = CreateDirectory();
        var continuous = await Run(directory, "continuous", "complete");
        var prefix = await Run(directory, "prefix", "save");
        var restored = await Run(directory, "restored", "resume");
        AssertIndependent(continuous, prefix);
        AssertIndependent(prefix, restored);
        Assert.AreEqual(prefix.Samples[^1].Payload, await File.ReadAllTextAsync(Path.Combine(directory, "state.json")));
        AssertSamples(continuous.Samples[..5], prefix.Samples);
        AssertSamples(continuous.Samples[4..], restored.Samples);
        CollectionAssert.AreEqual(continuous.Receipts[..3], prefix.Receipts);
        CollectionAssert.AreEqual(continuous.Receipts[3..], restored.Receipts);
        Assert.AreEqual(new SimulationPosition(1, 2), restored.Samples[0].Position);
        using var payload = JsonDocument.Parse(continuous.Samples[0].Payload);
        var world = payload.RootElement.GetProperty("world");
        Assert.AreEqual(42UL, world.GetProperty("source").GetProperty("world_seed").GetUInt64());
        Assert.IsTrue(world.GetProperty("nodes").GetArrayLength() > 1);
        Assert.IsTrue(world.GetProperty("lairs").GetArrayLength() > 0);
        Directory.Delete(directory, true);
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

    private async Task<CoreProcessResult> Run(string directory, string name, string mode)
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
        try
        {
            await process.WaitForExitAsync(TestContext.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new AssertFailedException($"测试运行已取消；核心子进程 {name}：{directory}");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(stdoutPath, await stdout, new UTF8Encoding(false));
            await File.WriteAllTextAsync(stderrPath, await stderr, new UTF8Encoding(false));
        }
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
