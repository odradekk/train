using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Saves;

namespace Train.Godot.Tests.Saves;

[TestClass]
[TestCategory("integration")]
public sealed class SaveContainerMemoryTests
{
    [TestMethod]
    [DataRow("core")]
    [DataRow("content")]
    public async Task Encoding_retains_bounded_temporary_memory_as_module_payload_grows(string module)
    {
        var small = await RetainedBytes(module, 1024);
        var large = await RetainedBytes(module, 1024 * 1024);
        Assert.IsTrue(large <= small + 1024 * 1024,
            $"{module}: small retained {small} bytes; large retained {large} bytes.");
    }

    private static async Task<long> RetainedBytes(string module, int size)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { typeof(SaveContainerMemoryTests).Assembly.Location,
            "--save-container-memory", module, size.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(0, process.ExitCode, await error);
            return long.Parse(await output, CultureInfo.InvariantCulture);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    internal static int MeasureInChild(string module, int size)
    {
        var payload = "{\"opaque\":\"" + new string('x', size) + "\"}";
        var document = SaveContainerTests.Document("{}");
        document = module == "core" ? document with { CorePayload = payload }
            : document with { ContentPayload = payload };
        using var ready = new ManualResetEventSlim();
        using var run = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                Encode(SaveContainerTests.Document("{}"));
                ready.Set();
                run.Wait();
                Encode(document);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                ready.Set();
                finished.Set();
                release.Wait();
            }
        });
        worker.Start();
        long retained;
        try
        {
            ready.Wait();
            var before = Collect();
            run.Set();
            finished.Wait();
            retained = Collect() - before;
        }
        finally
        {
            release.Set();
            worker.Join();
        }
        if (failure is not null) throw new InvalidOperationException("Encoding failed.", failure);
        System.Console.WriteLine(retained.ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Encode(SaveDocument document)
    {
        for (var repeat = 0; repeat < 8; repeat++)
            if (SaveContainer.Encode(document).Length == 0) throw new InvalidOperationException("Empty container.");
    }

    private static long Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(false);
    }
}
