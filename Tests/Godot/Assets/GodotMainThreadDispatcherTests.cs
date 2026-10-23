using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Assets;

namespace Train.Godot.Tests.Assets;

[TestClass]
public sealed class GodotMainThreadDispatcherTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Dispose_rejects_new_calls_and_waits_for_direct_calls_even_when_they_throw(bool throws)
    {
        var dispatcher = new GodotMainThreadDispatcher();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var limit = TimeSpan.FromSeconds(30);
        var mainThread = Environment.CurrentManagedThreadId;
        var queued = dispatcher.Enqueue(() => 1);
        var direct = Task.Run(() => dispatcher.InvokeOnCallingThread(() =>
        {
            var thread = Environment.CurrentManagedThreadId;
            entered.SetResult(thread);
            if (!release.Wait(limit)) throw new TimeoutException("测试没有释放直接调用。");
            if (throws) throw new InvalidOperationException("直接调用失败。");
            return thread;
        }));
        Thread? disposing = null;
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int callingThread;
        try
        {
            callingThread = await entered.Task.WaitAsync(limit);
            Assert.AreNotEqual(mainThread, callingThread);
            disposing = new Thread(() =>
            {
                try { dispatcher.Dispose(); disposed.SetResult(); }
                catch (Exception error) { disposed.SetException(error); }
            }) { IsBackground = true };
            disposing.Start();
            // 排队请求被取消，证明 Dispose 已标记关闭；直接调用仍被 release 挡住。
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => queued.WaitAsync(limit));
            Assert.ThrowsExactly<ObjectDisposedException>(() => dispatcher.InvokeOnCallingThread(() => 1));
            Assert.IsTrue(SpinWait.SpinUntil(() => disposed.Task.IsCompleted ||
                (disposing.ThreadState & ThreadState.WaitSleepJoin) != 0, limit), "关闭线程既未阻塞也未返回。");
            Assert.IsFalse(disposed.Task.IsCompleted, "进行中的直接调用结束前 Dispose 已返回。");
        }
        finally
        {
            release.Set();
            if (disposing is not null)
            {
                await disposed.Task.WaitAsync(limit);
                Assert.IsTrue(disposing.Join(limit), "关闭线程没有结束。");
            }
        }
        if (throws)
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => direct.WaitAsync(limit));
        else
            Assert.AreEqual(callingThread, await direct.WaitAsync(limit));
    }
}
