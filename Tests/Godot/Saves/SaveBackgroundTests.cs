using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Godot.Tests.Host;

namespace Train.Godot.Tests.Saves;

[TestClass]
[TestCategory("integration")]
public sealed class SaveBackgroundTests
{
    [TestMethod]
    public async Task Repeated_save_and_load_keep_container_buffers_on_one_dedicated_thread()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(SaveBackgroundTests).Assembly.Location);
        start.ArgumentList.Add("--save-background");
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(0, process.ExitCode, (await error) + (await output));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    [TestMethod]
    public void Load_waits_for_the_pending_save_to_publish()
    {
        using var run = new Run(blockWrite: true);
        Assert.IsNull(run.Service.Save());
        HostFixture.WaitForSignal(run.WriterEntered, "save writer entered");
        var entry = new SaveEntry(run.Path, SaveContainerTests.Document().Header, false);
        Assert.IsNull(run.Service.Load(entry));
        Assert.IsTrue(run.Service.Leaving!.Waiting);
        Assert.AreEqual(0, run.DecodeCount);
        run.ReleaseWriter.Set();
        run.Drain();
        Assert.IsTrue(run.Published);
        Assert.AreEqual(1, run.DecodeCount);
        Assert.IsFalse(run.Service.Status!.Unsaved);
        run.Host.AssertNoUnexpectedFailures();
    }

    internal static int MeasureInChild()
    {
        using var listener = new PoolRents();
        listener.PositiveControl = true;
        var bytes = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        ArrayPool<byte>.Shared.Return(bytes);
        var chars = ArrayPool<char>.Shared.Rent(512 * 1024);
        ArrayPool<char>.Shared.Return(chars);
        listener.PositiveControl = false;
        Assert.IsTrue(listener.SawBytePool && listener.SawCharPool, "ArrayPool event positive control failed.");
        using var run = new Run();
        for (var repeat = 0; repeat < 3; repeat++)
        {
            listener.ActivePath = run.Path;
            var before = listener.Rents.Count;
            Assert.IsNull(run.Service.Save());
            run.Drain();
            Assert.IsTrue(listener.Rents.Count > before, "Real save validation did not rent a large container buffer.");
            var header = run.Store.ReadHeader(run.Path);
            before = listener.Rents.Count;
            Assert.IsNull(run.Service.Load(new(run.Path, header, false)));
            run.Drain();
            Assert.IsTrue(listener.Rents.Count > before, "Real load did not rent a large container buffer.");
            listener.ActivePath = null;
            Assert.IsFalse(run.Service.Status!.Unsaved);
        }
        run.Host.AssertNoUnexpectedFailures();
        Assert.AreEqual(3, run.DecodeCount);
        foreach (var name in new[] { "write", "publish", "DecodeState", "DecodeCapture", "PrepareLoad", "PrepareRestore" })
            Assert.AreEqual(3, run.Calls.Count(call => call.Name == name), name);
        var observations = run.Calls.Concat(listener.Rents.Select(rent => rent.Call)).ToArray();
        Assert.IsFalse(observations.Any(call => call.Pool),
            "Save/load background work used a thread-pool thread: " + string.Join<Call>(", ", observations));
        Assert.AreEqual(1, observations.Select(call => call.Thread).Distinct().Count(),
            "Save/load background work changed threads: " + string.Join<Call>(", ", observations));
        Assert.IsTrue(listener.Rents.All(rent => rent.Path == run.Path));
        return 0;
    }

    private sealed record Call(string Name, int Thread, bool Pool)
    {
        internal static Call Here(string name) => new(name, Environment.CurrentManagedThreadId, System.Threading.Thread.CurrentThread.IsThreadPoolThread);
    }

    private sealed class Run : IDisposable
    {
        internal HostFixture Host { get; } = new();
        internal FileSaveStore Store { get; }
        internal SaveService Service { get; }
        internal string Path { get; }
        internal ConcurrentQueue<Call> Calls { get; } = new();
        internal ManualResetEventSlim WriterEntered { get; } = new();
        internal ManualResetEventSlim ReleaseWriter { get; } = new();
        internal bool Published;
        internal int DecodeCount;
        private readonly string root = Directory.CreateTempSubdirectory("train-save-background-").FullName;

        internal Run(bool blockWrite = false)
        {
            Host.ReadyHost();
            var payload = new string('x', 2 * 1024 * 1024);
            FixedCoreCapture Capture() => new(new(new(0, 0), "canonical", "hash"),
                HostCoreScenario.References, payload, HostCoreScenario.Input, new(0));
            Host.Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(Capture()));
            Host.CaptureContent = (view, _) => new ContentResult<IContentCapture>.Success(new FixedContentCapture(payload, view.PersistentRevision));
            Host.Core.DecodeStateCall = value =>
            {
                Calls.Enqueue(Call.Here("DecodeState"));
                Assert.IsTrue(Published, "Load decoded before save published.");
                Assert.AreEqual(payload, value);
                Interlocked.Increment(ref DecodeCount);
                return new CoreResult<ICoreCapture>.Success(Capture());
            };
            Host.Factory.DecodeCaptureCall = value =>
            {
                Calls.Enqueue(Call.Here("DecodeCapture"));
                Assert.AreEqual(payload, value);
                return new ContentResult<IContentCapture>.Success(new FixedContentCapture(value));
            };
            Host.Core.PrepareRestoreCall = (_, _, _) =>
            {
                Calls.Enqueue(Call.Here("PrepareRestore"));
                var session = new ScriptedCoreSession(new FixedCoreReadView(new(0, 0), HostCoreScenario.Input));
                session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(Capture()));
                return new CoreResult<ICoreSessionCandidate>.Success(new ScriptedCoreCandidate(session, HostCoreScenario.References));
            };
            Host.Load = _ =>
            {
                Calls.Enqueue(Call.Here("PrepareLoad"));
                return new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(Host.Content));
            };
            Path = System.IO.Path.Combine(root, FileSaveStore.SavesFolder,
                FileSaveStore.FormatId(Host.Host.GetSaveStatus()!.Identity.Id), FileSaveStore.FileName);
            Store = new(root, (path, json) =>
            {
                Calls.Enqueue(Call.Here("write"));
                if (blockWrite)
                {
                    WriterEntered.Set();
                    HostFixture.WaitForSignal(ReleaseWriter, "release save writer");
                }
                FileSaveStore.WriteCandidate(path, json);
            }, (candidate, target, overwrite) =>
            {
                Calls.Enqueue(Call.Here("publish"));
                File.Move(candidate, target, overwrite);
                Published = true;
            });
            Service = new(Host.Host, Store, Host.CreateEditor(Store));
            Service.SetAutoSaveInterval(null);
        }

        internal void Drain() => Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            Host.Host.ProcessFrame(0);
            Service.Process();
            return !Service.HasPendingWork && Service.Leaving is null;
        }, HostFixture.HangTimeout), "Save/load did not settle.");

        public void Dispose()
        {
            ReleaseWriter.Set();
            try { Drain(); }
            finally
            {
                Service.Dispose();
                Host.Dispose();
                WriterEntered.Dispose();
                ReleaseWriter.Dispose();
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class PoolRents : EventListener
    {
        internal bool PositiveControl;
        internal bool SawBytePool;
        internal bool SawCharPool;
        internal string? ActivePath;
        internal ConcurrentQueue<(string Path, Call Call)> Rents { get; } = new();
        private readonly int bytePool = ArrayPool<byte>.Shared.GetHashCode();
        private readonly int charPool = ArrayPool<char>.Shared.GetHashCode();

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Buffers.ArrayPoolEventSource") EnableEvents(source, EventLevel.Verbose);
        }

        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            if (args.EventId != 1) return;
            var pool = (int)args.Payload![2]!;
            var size = (int)args.Payload[1]!;
            var bytes = pool == bytePool ? size : pool == charPool ? (long)size * 2 : 0;
            if (bytes < 1024 * 1024) return;
            if (PositiveControl)
            {
                SawBytePool |= pool == bytePool;
                SawCharPool |= pool == charPool;
                return;
            }
            if (ActivePath is { } path && new StackTrace().GetFrames().Any(frame =>
                frame.GetMethod()?.DeclaringType == typeof(SaveContainer)))
                Rents.Enqueue((path, Call.Here("container buffer")));
        }
    }
}
