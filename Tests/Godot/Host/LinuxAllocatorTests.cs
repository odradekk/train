using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.Scenes.Contracts;

namespace Train.Godot.Tests.Host;

[TestClass]
[DoNotParallelize]
public sealed class LinuxAllocatorTests
{
    private static readonly SceneFrameStamp Opening = new(new(Guid.Parse("00000000-0000-0000-0000-000000000863")),
        new(1), new(1), 1);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [TestMethod]
    [DataRow(SceneReadiness.Loading, true)]
    [DataRow(SceneReadiness.Partial, true)]
    [DataRow(SceneReadiness.Failed, true)]
    [DataRow(SceneReadiness.Ready, false)]
    public async Task Waits_for_ready_view_and_completed_host_packing(SceneReadiness readiness, bool packingDone)
    {
        var calls = 0;
        var allocator = new LinuxAllocator(error => Assert.Fail(error.ToString()), () => Interlocked.Increment(ref calls));
        Assert.IsNull(allocator.TryTrim(Opening, readiness, packingDone));
        Assert.AreEqual(0, calls);
        await allocator.TryTrim(Opening, SceneReadiness.Ready, true)!.WaitAsync(Limit);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task Trims_once_per_session_and_request_without_repeating_for_frame_changes()
    {
        var calls = 0;
        var allocator = new LinuxAllocator(error => Assert.Fail(error.ToString()), () => Interlocked.Increment(ref calls));
        await allocator.TryTrim(Opening, SceneReadiness.Ready, true)!.WaitAsync(Limit);
        Assert.IsNull(allocator.TryTrim(Opening, SceneReadiness.Ready, true));
        Assert.IsNull(allocator.TryTrim(Opening with { Revision = 2, Space = new(2) }, SceneReadiness.Ready, true));
        await allocator.TryTrim(Opening with { Request = new(2) }, SceneReadiness.Ready, true)!.WaitAsync(Limit);
        await allocator.TryTrim(Opening with { Session = new(Guid.Parse("00000000-0000-0000-0000-000000000864")), Request = new(2) },
            SceneReadiness.Ready, true)!.WaitAsync(Limit);
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public async Task Runs_in_background_and_retries_another_opening_after_the_process_wide_trim_finishes()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerThread = Environment.CurrentManagedThreadId;
        var first = new LinuxAllocator(error => Assert.Fail(error.ToString()), () =>
        {
            entered.SetResult(Environment.CurrentManagedThreadId);
            if (!release.Wait(Limit)) throw new TimeoutException();
        });
        var calls = 0;
        var second = new LinuxAllocator(error => Assert.Fail(error.ToString()), () => Interlocked.Increment(ref calls));
        var next = Opening with { Request = new(2) };
        Task? competing = null;
        var running = first.TryTrim(Opening, SceneReadiness.Ready, true)!;
        try
        {
            Assert.AreNotEqual(callerThread, await entered.Task.WaitAsync(Limit));
            competing = second.TryTrim(next, SceneReadiness.Ready, true);
            Assert.IsNull(competing);
            Assert.AreEqual(0, calls);
        }
        finally
        {
            release.Set();
            await running.WaitAsync(Limit);
            if (competing is not null) await competing.WaitAsync(Limit);
        }
        await second.TryTrim(next, SceneReadiness.Ready, true)!.WaitAsync(Limit);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task Reports_a_native_failure_once_and_allows_the_next_opening()
    {
        var calls = 0;
        var failures = new List<Exception>();
        var failure = new InvalidOperationException();
        var allocator = new LinuxAllocator(failures.Add, () =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw failure;
        });
        await allocator.TryTrim(Opening, SceneReadiness.Ready, true)!.WaitAsync(Limit);
        CollectionAssert.AreEqual(new[] { failure }, failures);
        Assert.IsNull(allocator.TryTrim(Opening, SceneReadiness.Ready, true));
        await allocator.TryTrim(Opening with { Request = new(2) }, SceneReadiness.Ready, true)!.WaitAsync(Limit);
        Assert.AreEqual(2, calls);
        Assert.AreEqual(1, failures.Count);
    }
}
