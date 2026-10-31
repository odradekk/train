using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;

namespace Train.Godot.Tests.Measurement;

[TestClass]
[TestCategory("integration")]
public sealed class ArrayPoolDiagnosisTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Diagnostic_child_records_large_pool_calls_with_safe_synchronous_stacks(bool enabled)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"train-pool-diagnosis-{Guid.NewGuid():N}");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { typeof(ArrayPoolDiagnosisTests).Assembly.Location,
            "--array-pool-diagnosis", directory, enabled.ToString() }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(0, process.ExitCode, await error);
            var file = Path.Combine(directory, "array-pool.jsonl");
            if (!enabled) { Assert.IsFalse(File.Exists(file)); return; }
            var text = File.ReadAllText(file);
            StringAssert.DoesNotMatch(text, new Regex(@"[A-Za-z]:[\\/]|/(?:home|Users|tmp)/"));
            var rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.AreEqual(1, rows.Count(row => row.RootElement.GetProperty("event").GetString() == "listener-started"));
                var events = rows.Select(row => row.RootElement).Where(row => row.GetProperty("event").GetString()!.StartsWith("Buffer")).ToArray();
                foreach (var name in new[] { "BufferRented", "BufferAllocated", "BufferReturned", "BufferDropped" })
                    Assert.IsTrue(events.Any(row => row.GetProperty("event").GetString() == name), name);
                foreach (var row in events)
                {
                    var size = row.GetProperty("size_bytes");
                    Assert.IsTrue(size.ValueKind == JsonValueKind.Null
                        ? row.GetProperty("buffer_length").GetInt32() >= 1024 * 1024 : size.GetInt64() >= 1024 * 1024);
                    var stack = row.GetProperty("stack").EnumerateArray().Select(frame => frame.GetString()!).ToArray();
                    Assert.IsTrue(stack.Length <= 20);
                    Assert.IsFalse(stack.Any(frame => frame.StartsWith("System.Buffers.") || frame.StartsWith("System.Diagnostics.Tracing.")));
                }
                var rents = events.Where(row => row.GetProperty("event").GetString() == "BufferRented").ToArray();
                foreach (var type in new[] { "System.Byte", "System.Char" })
                    Assert.IsTrue(rents.Any(row => row.GetProperty("element_type").GetString() == type
                        && row.GetProperty("size_bytes").GetInt64() == 1024 * 1024
                        && row.GetProperty("stack").EnumerateArray().Any(frame => frame.GetString()!.EndsWith(".RentLargePayload"))));
                Assert.IsTrue(rents.Any(row => row.GetProperty("thread_pool").GetBoolean()));
                Assert.IsTrue(rents.Any(row => !row.GetProperty("thread_pool").GetBoolean()));
                Assert.IsTrue(rents.Select(row => row.GetProperty("thread_id").GetInt32()).Distinct().Count() >= 2);
                var cross = rents.Single(row => row.GetProperty("stack").EnumerateArray()
                    .Any(frame => frame.GetString()!.EndsWith(".RentForCrossThread")));
                Assert.IsTrue(events.Any(row => row.GetProperty("event").GetString() == "BufferReturned"
                    && row.GetProperty("buffer_id").GetInt32() == cross.GetProperty("buffer_id").GetInt32()
                    && row.GetProperty("pool_id").GetInt32() == cross.GetProperty("pool_id").GetInt32()
                    && row.GetProperty("timestamp").GetInt64() > cross.GetProperty("timestamp").GetInt64()
                    && row.GetProperty("thread_id").GetInt32() != cross.GetProperty("thread_id").GetInt32()
                    && row.GetProperty("thread_pool").GetBoolean()));
                Assert.IsFalse(rows.Any(row => row.RootElement.GetProperty("event").GetString() == "listener-error"));
            }
            finally { foreach (var row in rows) row.Dispose(); }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    internal static int RunChild(string directory, bool enabled)
    {
        Environment.SetEnvironmentVariable("TRAIN_HEAP_DIAGNOSIS", enabled ? directory : null);
        using var listener = ArrayPoolDiagnosis.StartIfEnabled();
        if ((listener is not null) != enabled) throw new InvalidOperationException("Diagnosis activation mismatch.");
        RentLargePayload();
        Task.Run(RentLargePayload).GetAwaiter().GetResult();
        var cross = RentForCrossThread();
        Task.Run(() => ArrayPool<byte>.Shared.Return(cross)).GetAwaiter().GetResult();
        var pool = ArrayPool<byte>.Create(16, 1);
        var oversized = pool.Rent(1024 * 1024);
        pool.Return(oversized);
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] RentForCrossThread() => ArrayPool<byte>.Shared.Rent(1024 * 1024);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RentLargePayload()
    {
        var small = ArrayPool<byte>.Shared.Rent(512 * 1024);
        ArrayPool<byte>.Shared.Return(small);
        var bytes = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        var chars = ArrayPool<char>.Shared.Rent(512 * 1024);
        ArrayPool<byte>.Shared.Return(bytes);
        ArrayPool<char>.Shared.Return(chars);
    }
}
