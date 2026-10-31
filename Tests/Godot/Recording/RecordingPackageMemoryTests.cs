using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Recording;

namespace Train.Godot.Tests.Recording;

[TestClass]
[TestCategory("integration")]
public sealed class RecordingPackageMemoryTests
{
    [TestMethod]
    [DataRow("write", 1)]
    [DataRow("write", 2)]
    [DataRow("write", 4)]
    [DataRow("read", 1)]
    [DataRow("read", 2)]
    [DataRow("read", 4)]
    [DataRow("validate", 1)]
    [DataRow("validate", 2)]
    [DataRow("validate", 4)]
    [DataRow("write-commands", 1)]
    public async Task Operations_do_not_rent_payload_sized_shared_arrays(string operation, int workers)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { typeof(RecordingPackageMemoryTests).Assembly.Location,
            "--recording-package-memory", operation, workers.ToString() })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(0, process.ExitCode, await error);
            using var result = JsonDocument.Parse(await output);
            foreach (var item in result.RootElement.EnumerateArray())
                Assert.AreEqual(0L, item.GetProperty("count").GetInt64(), item.ToString());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    internal static int MeasureInChild(string operation, int workers)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"train-recording-pool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = RecordingPackageTests.Package();
            const string pattern = "\"\\内容🚂";
            var patternBytes = System.Text.Encoding.UTF8.GetByteCount(pattern);
            var payload = string.Concat(Enumerable.Repeat(pattern, 1024 * 1024 / patternBytes))
                + new string('x', 1024 * 1024 % patternBytes);
            var package = source with { Header = source.Header with
            {
                Starts = [source.Header.Starts[0] with { Payload = payload }]
            } };
            if (operation == "write-commands")
                package = source with { Commands = Enumerable.Repeat(source.Commands[0], 8192).ToImmutableArray() };
            var paths = Enumerable.Range(0, workers).Select(i => Path.Combine(directory, $"{i}.zip")).ToArray();
            if (operation is "read" or "validate") foreach (var path in paths) package.Write(path);
            using var listener = new PoolRents();
            PoolRents.Measuring = true;
            try
            {
                var bytes = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                ArrayPool<byte>.Shared.Return(bytes);
                var chars = ArrayPool<char>.Shared.Rent(512 * 1024);
                ArrayPool<char>.Shared.Return(chars);
            }
            finally { PoolRents.Measuring = false; }
            if (!listener.SawBothPools) throw new InvalidOperationException("ArrayPool event positive control failed.");
            listener.Clear();
            using var ready = new CountdownEvent(workers);
            using var start = new ManualResetEventSlim();
            using var done = new CountdownEvent(workers);
            using var release = new ManualResetEventSlim();
            var ids = new int[workers];
            var tasks = Enumerable.Range(0, workers).Select(i => Task.Run(() =>
            {
                ids[i] = Environment.CurrentManagedThreadId;
                ready.Signal();
                start.Wait();
                try
                {
                    PoolRents.Measuring = true;
                    switch (operation)
                    {
                        case "write": package.Write(paths[i]); break;
                        case "write-commands": package.Write(paths[i]); break;
                        case "read": GC.KeepAlive(RecordingPackage.Read(paths[i])); break;
                        case "validate": package.Validate(paths[i]); break;
                        default: throw new ArgumentException(nameof(operation));
                    }
                }
                finally
                {
                    PoolRents.Measuring = false;
                    done.Signal();
                    release.Wait();
                }
            })).ToArray();
            try
            {
                ready.Wait();
                if (ids.Distinct().Count() != workers) throw new InvalidOperationException("Workers must use distinct threads.");
                start.Set();
                done.Wait();
                if (operation == "write-commands")
                {
                    using var zip = ZipFile.OpenRead(paths[0]);
                    if (zip.GetEntry("commands.jsonl")!.Length < 1024 * 1024)
                        throw new InvalidOperationException("Commands must exceed the observed array size.");
                }
                // Workers remain alive while the main thread collects the observed rents.
                System.Console.WriteLine(JsonSerializer.Serialize(listener.Results()));
            }
            finally
            {
                start.Set();
                release.Set();
                Task.WaitAll(tasks);
            }
            return 0;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class PoolRents : EventListener
    {
        [ThreadStatic] internal static bool Measuring;
        private readonly long[,] counts = new long[2, 32];
        private readonly int bytePool = ArrayPool<byte>.Shared.GetHashCode();
        private readonly int charPool = ArrayPool<char>.Shared.GetHashCode();
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Buffers.ArrayPoolEventSource") EnableEvents(source, EventLevel.Verbose);
        }
        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            if (!Measuring || args.EventId != 1) return;
            var pool = (int)args.Payload![2]!;
            var kind = pool == bytePool ? 0 : pool == charPool ? 1 : -1;
            var size = (int)args.Payload[1]!;
            if (kind < 0 || (long)size * (kind == 0 ? 1 : 2) < 1024 * 1024) return;
            var bucket = System.Numerics.BitOperations.Log2((uint)size);
            Interlocked.Increment(ref counts[kind, bucket]);
        }
        internal bool SawBothPools => counts[0, 20] > 0 && counts[1, 19] > 0;
        internal void Clear() => Array.Clear(counts);
        internal IEnumerable<object> Results()
        {
            for (var kind = 0; kind < 2; kind++)
                for (var bucket = kind == 0 ? 20 : 19; bucket < 31; bucket++)
                    if (counts[kind, bucket] > 0)
                        yield return new { type = kind == 0 ? "byte" : "char", size = (1L << bucket) * (kind == 0 ? 1 : 2), count = counts[kind, bucket] };
        }
    }
}
