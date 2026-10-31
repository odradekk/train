using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;

namespace Train.Godot.Tests.Host;

[TestClass]
[TestCategory("fast")]
public sealed class HostBackgroundWorkTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task Work_reuses_one_thread_outside_the_thread_pool()
    {
        using var worker = new HostBackgroundWork();
        var first = await worker.Enqueue(() => (Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread));
        var second = await worker.Enqueue(() => (Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread));
        Assert.IsFalse(first.IsThreadPoolThread);
        Assert.IsFalse(second.IsThreadPoolThread);
        Assert.AreEqual(first.CurrentManagedThreadId, second.CurrentManagedThreadId);
    }

    [TestMethod]
    public async Task Waiting_work_runs_in_submission_order()
    {
        using var worker = new HostBackgroundWork();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var order = new List<int>();
        var first = worker.Enqueue(() =>
        {
            entered.Set();
            HostFixture.WaitForSignal(release, "放行第一个后台任务");
            lock (order) order.Add(1);
            return 1;
        });
        try
        {
            HostFixture.WaitForSignal(entered, "第一个后台任务开始");
            var second = worker.Enqueue(() => { lock (order) order.Add(2); return 2; });
            var third = worker.Enqueue(() => { lock (order) order.Add(3); return 3; });
            release.Set();
            await Task.WhenAll(first, second, third).WaitAsync(Timeout);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, order);
        }
        finally { release.Set(); await first.WaitAsync(Timeout); }
    }

    [TestMethod]
    public async Task Cancellation_before_start_skips_the_work()
    {
        using var worker = new HostBackgroundWork();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var first = worker.Enqueue(() =>
        {
            entered.Set();
            HostFixture.WaitForSignal(release, "放行正在执行的任务");
            return 1;
        });
        try
        {
            HostFixture.WaitForSignal(entered, "后台任务开始");
            var called = false;
            var queued = worker.Enqueue(() => called = true, cancellation.Token);
            cancellation.Cancel();
            release.Set();
            await first.WaitAsync(Timeout);
            await Assert.ThrowsAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
            Assert.IsFalse(called);
        }
        finally { release.Set(); await first.WaitAsync(Timeout); }
    }

    [TestMethod]
    public async Task A_failed_work_item_does_not_stop_the_queue()
    {
        using var worker = new HostBackgroundWork();
        var failure = new InvalidOperationException("fixture failure");
        var failed = worker.Enqueue<int>(() => throw failure);
        var next = worker.Enqueue(() => 7);
        Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => failed));
        Assert.AreEqual(7, await next.WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task Closing_cancels_waiting_work_and_rejects_new_work_without_waiting_for_running_work()
    {
        using var worker = new HostBackgroundWork();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = worker.Enqueue(() =>
        {
            entered.Set();
            HostFixture.WaitForSignal(release, "关闭后放行正在执行的任务");
            return 1;
        });
        try
        {
            HostFixture.WaitForSignal(entered, "后台任务开始");
            var called = false;
            var queued = worker.Enqueue(() => called = true);
            worker.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => worker.Enqueue(() => 2));
            await Assert.ThrowsAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
            Assert.IsFalse(called);
            Assert.IsFalse(first.IsCompleted);
        }
        finally { release.Set(); Assert.AreEqual(1, await first.WaitAsync(Timeout)); }
    }

    [TestMethod]
    public async Task Work_cannot_submit_to_its_own_queue()
    {
        using var worker = new HostBackgroundWork();
        var work = worker.Enqueue(() => worker.Enqueue(() => 1));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => work.WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task Caller_continuations_do_not_run_on_the_background_thread()
    {
        using var worker = new HostBackgroundWork();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var work = worker.Enqueue(() =>
        {
            entered.Set();
            HostFixture.WaitForSignal(release, "放行带有 continuation 的任务");
            return Environment.CurrentManagedThreadId;
        });
        try
        {
            HostFixture.WaitForSignal(entered, "后台任务开始");
            var continuation = work.ContinueWith(done => (done.Result, Environment.CurrentManagedThreadId),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            release.Set();
            var threads = await continuation.WaitAsync(Timeout);
            Assert.AreNotEqual(threads.Result, threads.CurrentManagedThreadId);
        }
        finally { release.Set(); await work.WaitAsync(Timeout); }
    }

    [TestMethod]
    public void Cancelling_a_waiting_restore_skips_module_preparation()
    {
        using var fixture = new HostFixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var decoded = 0;
        var decode = fixture.Core.DecodeStateCall!;
        fixture.Core.DecodeStateCall = payload => { decoded++; return decode(payload); };
        var first = fixture.Host.BackgroundWork.Enqueue(() =>
        {
            entered.Set();
            HostFixture.WaitForSignal(release, "放行读档前的后台工作");
            return 1;
        });
        try
        {
            HostFixture.WaitForSignal(entered, "读档前的工作开始");
            var operation = fixture.Host.BeginRestore("core-state", "content-state", HostFixture.Options);
            Assert.IsNull(operation.Problem);
            Assert.IsTrue(fixture.Host.Cancel(operation.Operation));
            var finished = fixture.Host.BackgroundWork.Enqueue(() => 2);
            release.Set();
            Assert.IsTrue(finished.Wait(Timeout));
            fixture.Host.ProcessFrame(0);
            Assert.AreEqual(0, decoded);
            Assert.IsNull(fixture.Host.GetStatus());
            fixture.AssertNoUnexpectedFailures();
        }
        finally { release.Set(); Assert.IsTrue(first.Wait(Timeout)); }
    }
}
