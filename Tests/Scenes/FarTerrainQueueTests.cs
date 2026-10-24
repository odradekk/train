using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class FarTerrainQueueTests
{
    [TestMethod]
    public async Task WorkersContinueAcrossUnitsAndKeysWithoutAnotherPump()
    {
        var entered = Signal();
        var release = Signal();
        await using var queue = new FarTerrainQueue(128);
        var budgets = new List<int>();
        var tileCalls = 0;
        queue.Enqueue("cap", (budget, _) =>
        {
            budgets.Add(budget);
            if (budgets.Count == 1)
            {
                entered.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
            return budgets.Count == 3;
        });
        queue.Enqueue("tile", (_, _) => { tileCalls++; return true; });
        try
        {
            queue.Pump(1, false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var work = Task.WhenAll(queue.PendingWork);
            release.SetResult();
            await work.WaitAsync(TimeSpan.FromSeconds(10));
            CollectionAssert.AreEqual(new[] { 128, 128, 128 }, budgets);
            Assert.AreEqual(1, tileCalls);
            Assert.IsFalse(queue.HasPendingWork);
            Assert.AreEqual(0, queue.RunningCount);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task PumpLimitsConcurrentWorkersAndCompletedKeysReleaseTheirSlots()
    {
        var entered = new CountdownEvent(2);
        var release = Signal();
        await using var queue = new FarTerrainQueue(7);
        var calls = 0;
        var active = 0;
        for (var i = 0; i < 3; i++)
            queue.Enqueue($"tile-{i}", (budget, _) =>
            {
                Assert.AreEqual(7, budget);
                Assert.IsTrue(Interlocked.Increment(ref active) <= 2);
                var call = Interlocked.Increment(ref calls);
                if (call <= 2) entered.Signal();
                release.Task.GetAwaiter().GetResult();
                Interlocked.Decrement(ref active);
                return true;
            });
        try
        {
            queue.Pump(2, false);
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(2, queue.RunningCount);
            Assert.AreEqual(2, calls);
            var work = Task.WhenAll(queue.PendingWork);
            release.SetResult();
            await work.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(3, calls);
            Assert.IsFalse(queue.HasPendingWork);
            Assert.IsNull(queue.Failure);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task WaitingL0PreventsFarWorkEvenWithFreeSlots()
    {
        await using var queue = new FarTerrainQueue(17);
        var calls = 0;
        queue.Enqueue("cap", (_, _) => { calls++; return true; });
        queue.Pump(2, true);
        Assert.AreEqual(0, calls);
        Assert.AreEqual(0, queue.RunningCount);
        Assert.IsTrue(queue.HasPendingWork);
    }

    [TestMethod]
    public async Task L0ArrivalLetsOnlyCurrentUnitFinishAndNotifiesBeforeResumingFar()
    {
        var entered = Signal();
        var release = Signal();
        var notified = Signal();
        var finishNotification = Signal();
        var calls = 0;
        FarTerrainQueue? queue = null;
        queue = new FarTerrainQueue(17, () =>
        {
            Assert.AreEqual(0, queue!.RunningCount);
            notified.TrySetResult();
            finishNotification.Task.GetAwaiter().GetResult();
        });
        await using var ownedQueue = queue;
        queue.Enqueue("cap", (_, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                entered.SetResult();
                release.Task.GetAwaiter().GetResult();
                return false;
            }
            return true;
        });
        queue.Enqueue("tile", (_, _) => { Interlocked.Increment(ref calls); return true; });
        try
        {
            queue.Pump(1, false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            queue.PauseForL0(true);
            var work = Task.WhenAll(queue.PendingWork);
            release.SetResult();
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, calls);
            Assert.IsFalse(work.IsCompleted);
            Assert.IsTrue(queue.HasPendingWork);
            finishNotification.SetResult();
            await work.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, calls);
            // The scene takes the slot for L0 before unpausing far work.
            queue.Pump(0, false);
            Assert.AreEqual(1, calls);
            queue.Pump(1, false);
            await Task.WhenAll(queue.PendingWork).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(3, calls);
            Assert.IsFalse(queue.HasPendingWork);
            Assert.IsNull(queue.Failure);
        }
        finally
        {
            release.TrySetResult();
            finishNotification.TrySetResult();
        }
    }

    [TestMethod]
    public async Task DuplicateKeyIsRejectedUntilItsContinuousWorkCompletes()
    {
        var entered = Signal();
        var release = Signal();
        await using var queue = new FarTerrainQueue(23);
        var calls = 0;
        queue.Enqueue("tile", (budget, _) =>
        {
            Assert.AreEqual(23, budget);
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                release.Task.GetAwaiter().GetResult();
                return false;
            }
            return true;
        });
        try
        {
            Assert.IsFalse(queue.Enqueue("tile", (_, _) => throw new AssertFailedException()));
            queue.Pump(3, false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(queue.Enqueue("tile", (_, _) => throw new AssertFailedException()));
            var work = Task.WhenAll(queue.PendingWork);
            release.SetResult();
            await work.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(2, calls);
            Assert.IsFalse(queue.HasPendingWork);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task DisposeCancelsAndWaitsForActiveUnitWithoutRequeueing()
    {
        var entered = Signal();
        var release = Signal();
        var queue = new FarTerrainQueue(11);
        CancellationToken observedToken = default;
        queue.Enqueue("cap", (_, token) =>
        {
            observedToken = token;
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            return false;
        });
        try
        {
            queue.Pump(1, false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stop = queue.DisposeAsync().AsTask();
            Assert.IsTrue(observedToken.IsCancellationRequested);
            Assert.IsFalse(stop.IsCompleted);
            release.SetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(queue.HasPendingWork);
            Assert.AreEqual(0, queue.PendingWork.Count);
            Assert.IsNull(queue.Failure);
            Assert.ThrowsExactly<ObjectDisposedException>(() => queue.Pump(1, false));
            Assert.ThrowsExactly<ObjectDisposedException>(() => queue.Enqueue("late", (_, _) => true));
        }
        finally
        {
            release.TrySetResult();
            await queue.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task DisposeWaitsForSlotNotificationAlreadyInProgress()
    {
        var entered = Signal();
        var release = Signal();
        var queue = new FarTerrainQueue(11, () =>
        {
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
        });
        queue.Enqueue("cap", (_, _) => true);
        try
        {
            queue.Pump(1, false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, queue.RunningCount);
            Assert.AreEqual(1, queue.PendingWork.Count);
            var stop = queue.DisposeAsync().AsTask();
            Assert.IsFalse(stop.IsCompleted);
            release.SetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, queue.PendingWork.Count);
        }
        finally
        {
            release.TrySetResult();
            await queue.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task FailedUnitIsRetainedForOwnerAndStopsNewWork()
    {
        await using var queue = new FarTerrainQueue(11);
        var error = new InvalidOperationException("cap failed");
        queue.Enqueue("cap", (_, _) => throw error);
        queue.Enqueue("tile", (_, _) => throw new AssertFailedException());
        queue.Pump(1, false);
        await Task.WhenAll(queue.PendingWork).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(error, queue.Failure);
        Assert.AreEqual(0, queue.RunningCount);
        Assert.IsTrue(queue.HasPendingWork);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
