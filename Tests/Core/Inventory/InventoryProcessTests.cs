using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.Sessions;

namespace Train.Core.Tests.Inventory;

[TestClass]
[TestCategory("integration")]
public sealed class InventoryProcessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Saved_inventory_and_lease_restore_in_another_process_with_identical_execution()
    {
        var root = TestContext.TestRunResultsDirectory ?? throw new AssertFailedException("缺少当前测试结果目录。");
        var directory = Path.Combine(root, "inventory-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var continuous = await Run(directory, "continuous", "complete");
        var saved = await Run(directory, "saved", "save");
        var resumed = await Run(directory, "resumed", "resume");
        Assert.AreEqual(3, new[] { continuous.ProcessId, saved.ProcessId, resumed.ProcessId }.Distinct().Count());
        foreach (var result in new[] { continuous, saved, resumed }) Assert.AreNotEqual(Environment.ProcessId, result.ProcessId);
        Assert.AreEqual(continuous.Samples[0], saved.Samples.Single());
        Assert.AreEqual(saved.Samples[0].Payload, await File.ReadAllTextAsync(Path.Combine(directory, "state.json")));
        CollectionAssert.AreEqual(continuous.PrefixReceipts, saved.PrefixReceipts);
        CollectionAssert.AreEqual(continuous.PrefixEvents, saved.PrefixEvents);
        CollectionAssert.AreEqual(continuous.Samples, resumed.Samples);
        CollectionAssert.AreEqual(continuous.Receipts, resumed.Receipts);
        CollectionAssert.AreEqual(continuous.Events, resumed.Events);
        foreach (var sample in resumed.Samples) Assert.AreEqual(CoreProcessDriver.Sha256(sample.CanonicalText), sample.Sha256);

        using var prefix = JsonDocument.Parse(saved.Samples[0].Payload);
        Assert.AreEqual(999L, prefix.RootElement.GetProperty("world").GetProperty("money").GetInt64());
        using var final = JsonDocument.Parse(resumed.Samples[^1].Payload);
        Assert.AreEqual(998L, final.RootElement.GetProperty("world").GetProperty("money").GetInt64());
        Assert.AreEqual(2L, final.RootElement.GetProperty("world").GetProperty("leases")[0].GetProperty("paid").GetInt64());
        AssertAccess(resumed.Samples[0], InventoryAccess.ReadWrite);
        AssertAccess(resumed.Samples[1], InventoryAccess.WithdrawOnly);
        AssertAccess(resumed.Samples[5], InventoryAccess.ReadWrite);
        AssertAccess(resumed.Samples[^1], InventoryAccess.WithdrawOnly);
        Assert.AreEqual(5, resumed.Receipts.Count(receipt => receipt.Contains("\"Kind\":\"rejected\"", StringComparison.Ordinal)));
        Assert.AreEqual(2, resumed.Events.Count(value => value.Contains("\"Kind\":\"LeaseExpired\"", StringComparison.Ordinal)));
        Assert.AreEqual(2, resumed.Events.Count(value => value.Contains("\"Kind\":\"GoodsTransferred\"", StringComparison.Ordinal)));
        Assert.AreEqual(1, resumed.Events.Count(value => value.Contains("\"Kind\":\"LeaseAcquired\"", StringComparison.Ordinal)));
        Directory.Delete(directory, recursive: true);
    }

    private static void AssertAccess(InventoryProcessSample sample, InventoryAccess access)
    {
        using var query = JsonDocument.Parse(sample.Query);
        var inventory = query.RootElement.GetProperty("Inventories")[1];
        Assert.AreEqual((int)access, inventory.GetProperty("Access").GetInt32());
        if (access == InventoryAccess.WithdrawOnly) Assert.AreEqual(0L, inventory.GetProperty("Available").GetInt64());
    }

    [TestMethod]
    public async Task Goods_and_original_vehicle_trades_restore_across_real_processes()
    {
        var directory = Path.Combine(TestContext.TestRunResultsDirectory!, "trade-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var complete = await Run(directory, "complete", "trade-complete");
        var saved = await Run(directory, "save", "trade-save");
        var resumed = await Run(directory, "resume", "trade-resume");
        Assert.AreEqual(3, new[] { complete.ProcessId, saved.ProcessId, resumed.ProcessId }.Distinct().Count());
        Assert.AreEqual(complete.Samples[0], saved.Samples.Single());
        CollectionAssert.AreEqual(complete.PrefixReceipts, saved.PrefixReceipts);
        CollectionAssert.AreEqual(complete.Samples, resumed.Samples);
        CollectionAssert.AreEqual(complete.Receipts, resumed.Receipts);
        CollectionAssert.AreEqual(complete.Events, resumed.Events);
        using var final = JsonDocument.Parse(resumed.Samples[^1].Payload);
        Assert.AreEqual(9999L, final.RootElement.GetProperty("world").GetProperty("money").GetInt64());
        Assert.AreEqual(0, final.RootElement.GetProperty("world").GetProperty("trains").GetArrayLength());
        Assert.AreEqual(2, resumed.Events.Count(e => e.Contains("\"Kind\":\"VehicleTraded\"", StringComparison.Ordinal)));
        Directory.Delete(directory, recursive: true);
    }

    private async Task<InventoryProcessResult> Run(string directory, string name, string mode)
    {
        var output = Path.Combine(directory, name + ".json");
        var stdoutPath = Path.Combine(directory, name + ".stdout.log");
        var stderrPath = Path.Combine(directory, name + ".stderr.log");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (var argument in new[] { typeof(InventoryProcessTests).Assembly.Location, "--inventory-process", mode,
                     output, Path.Combine(directory, "state.json") }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new AssertFailedException("无法启动库存子进程。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(TestContext.CancellationToken); }
        catch (OperationCanceledException)
        {
            throw new AssertFailedException($"测试运行已取消；库存子进程 {name}：{directory}");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(stdoutPath, await stdout, new UTF8Encoding(false));
            await File.WriteAllTextAsync(stderrPath, await stderr, new UTF8Encoding(false));
        }
        Assert.AreEqual(0, process.ExitCode, $"子进程失败：{await File.ReadAllTextAsync(stderrPath)}");
        var result = JsonSerializer.Deserialize<InventoryProcessResult>(await File.ReadAllTextAsync(output))
            ?? throw new AssertFailedException("无法读取库存子进程结果。");
        Assert.AreEqual(process.Id, result.ProcessId);
        return result;
    }
}
