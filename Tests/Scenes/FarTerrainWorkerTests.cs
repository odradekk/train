using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class FarTerrainWorkerTests
{
    [TestMethod]
    public async Task A_single_running_L0_build_prevents_far_work()
    {
        var calls = 0;
        await using var worker = new FarTerrainWorker((_, _) => { Interlocked.Increment(ref calls); return true; });
        worker.Pump(1, true);
        await worker.PendingWork.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, calls, "A free build slot must not allow far work alongside L0.");
        worker.Pump(0, true);
        await worker.PendingWork.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task Session_permission_stops_each_worker_after_its_current_unit_and_resumes_without_another_pump()
    {
        var permission = new FarTerrainPermission();
        var firstEntered = Signal();
        var secondEntered = Signal();
        var release = Signal();
        var firstCalls = 0;
        var secondCalls = 0;
        await using var first = new FarTerrainWorker((budget, _) =>
        {
            Assert.AreEqual(128, budget);
            if (Interlocked.Increment(ref firstCalls) == 1)
            { firstEntered.SetResult(); release.Task.GetAwaiter().GetResult(); }
            return firstCalls == 3;
        }, permission: permission);
        await using var second = new FarTerrainWorker((_, _) =>
        {
            if (Interlocked.Increment(ref secondCalls) == 1)
            { secondEntered.SetResult(); release.Task.GetAwaiter().GetResult(); }
            return secondCalls == 3;
        }, permission: permission);
        try
        {
            first.Pump(0, true);
            second.Pump(0, true);
            Assert.AreEqual(0, first.RunningCount);
            Assert.AreEqual(0, second.RunningCount);
            permission.SetAllowed(true);
            await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(TimeSpan.FromSeconds(10));
            permission.SetAllowed(false);
            var stopped = Task.WhenAll(first.PendingWork, second.PendingWork);
            release.SetResult();
            await stopped.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, firstCalls);
            Assert.AreEqual(1, secondCalls);
            Assert.AreEqual(0, first.RunningCount);
            Assert.AreEqual(0, second.RunningCount);
            permission.SetAllowed(true);
            await Task.WhenAll(first.PendingWork, second.PendingWork).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(3, firstCalls);
            Assert.AreEqual(3, secondCalls);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task Session_permission_does_not_revive_disposed_or_locally_paused_workers()
    {
        var permission = new FarTerrainPermission();
        var calls = 0;
        var old = new FarTerrainWorker((_, _) => { Interlocked.Increment(ref calls); return true; }, permission: permission);
        old.Pump(0, true);
        await old.DisposeAsync();
        await using var current = new FarTerrainWorker((_, _) => { Interlocked.Increment(ref calls); return true; }, permission: permission);
        current.Pump(0, true);
        current.Pause();
        permission.SetAllowed(true);
        Assert.AreEqual(0, old.RunningCount);
        Assert.AreEqual(0, current.RunningCount);
        Assert.AreEqual(0, calls);
        current.Pump(0, true);
        await current.PendingWork.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task RequiredL0SlotsAndScenePermissionPreventStartingFarWork()
    {
        var calls = 0;
        await using var worker = new FarTerrainWorker((_, _) => { Interlocked.Increment(ref calls); return true; });
        worker.Pump(RailwayCoordinates.MaxConcurrentBuilds, true);
        Assert.AreEqual(0, worker.RunningCount);
        worker.SetEnabled(false);
        worker.Pump(0, true);
        Assert.AreEqual(0, worker.RunningCount);
        worker.SetEnabled(true);
        worker.Pump(0, true);
        await worker.PendingWork.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task UnitsContinueWithBudget128WithoutAnotherPump()
    {
        var budgets = new List<int>();
        await using var worker = new FarTerrainWorker((budget, _) =>
        {
            budgets.Add(budget);
            return budgets.Count == 3;
        });
        worker.Pump(0, true);
        await worker.PendingWork.WaitAsync(TimeSpan.FromSeconds(10));
        CollectionAssert.AreEqual(new[] { 128, 128, 128 }, budgets);
        Assert.AreEqual(0, worker.RunningCount);
        Assert.IsNull(worker.Failure);
    }

    [TestMethod]
    public async Task PauseFinishesOnlyCurrentUnitAndNotifiesOwnerBeforeResuming()
    {
        var entered = Signal();
        var release = Signal();
        var notified = Signal();
        var finishNotification = Signal();
        var calls = 0;
        FarTerrainWorker? worker = null;
        worker = new FarTerrainWorker((_, _) =>
        {
            if (Interlocked.Increment(ref calls) != 1) return true;
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
            return false;
        }, () =>
        {
            Assert.AreEqual(0, worker!.RunningCount);
            notified.TrySetResult();
            finishNotification.Task.GetAwaiter().GetResult();
        });
        await using var ownedWorker = worker;
        try
        {
            worker.Pump(0, false);
            Assert.AreEqual(0, worker.RunningCount);
            worker.Pump(0, true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            worker.Pause();
            var work = worker.PendingWork;
            release.SetResult();
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, Volatile.Read(ref calls));
            Assert.IsFalse(work.IsCompleted, "The slot notification is part of pending work.");
            // The owner takes the slot for L0 before it permits another far unit.
            worker.Pump(0, false);
            Assert.AreEqual(1, Volatile.Read(ref calls));
            finishNotification.SetResult();
            await work.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, Volatile.Read(ref calls));
            worker.Pump(0, true);
            await worker.PendingWork.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(2, Volatile.Read(ref calls));
            Assert.IsNull(worker.Failure);
        }
        finally
        {
            release.TrySetResult();
            finishNotification.TrySetResult();
        }
    }

    [TestMethod]
    public async Task DisposeCancelsActiveUnitAndWaitsForItsSlotNotification()
    {
        var entered = Signal();
        var release = Signal();
        var notifying = Signal();
        var releaseNotification = Signal();
        CancellationToken observedToken = default;
        var calls = 0;
        var worker = new FarTerrainWorker((_, token) =>
        {
            Interlocked.Increment(ref calls);
            observedToken = token;
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            return false;
        }, () =>
        {
            notifying.SetResult();
            releaseNotification.Task.GetAwaiter().GetResult();
        });
        try
        {
            worker.Pump(0, true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stop = worker.DisposeAsync().AsTask();
            Assert.IsTrue(observedToken.IsCancellationRequested);
            Assert.IsFalse(stop.IsCompleted);
            release.SetResult();
            await notifying.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(stop.IsCompleted, "Disposal must also wait for the owner notification.");
            releaseNotification.SetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, Volatile.Read(ref calls));
            Assert.IsTrue(worker.PendingWork.IsCompleted);
            Assert.AreEqual(0, worker.RunningCount);
            Assert.IsNull(worker.Failure);
            Assert.ThrowsExactly<ObjectDisposedException>(() => worker.Pump(0, true));
        }
        finally
        {
            release.TrySetResult();
            releaseNotification.TrySetResult();
            await worker.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task DisposeWaitsForNotificationAlreadyInProgress()
    {
        var entered = Signal();
        var release = Signal();
        var worker = new FarTerrainWorker((_, _) => true, () =>
        {
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
        });
        try
        {
            worker.Pump(0, true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stop = worker.DisposeAsync().AsTask();
            Assert.IsFalse(stop.IsCompleted);
            release.SetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(worker.PendingWork.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await worker.DisposeAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnitOrNotificationFailureIsReportedAndPreventsRestart(bool notificationFails)
    {
        var error = new InvalidOperationException("far terrain failed");
        var calls = 0;
        await using var worker = new FarTerrainWorker((_, _) =>
        {
            Interlocked.Increment(ref calls);
            if (!notificationFails) throw error;
            return true;
        }, () => { if (notificationFails) throw error; });
        worker.Pump(0, true);
        await worker.PendingWork.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(error, worker.Failure);
        worker.Pump(0, true);
        await worker.PendingWork.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, Volatile.Read(ref calls));
        Assert.AreEqual(0, worker.RunningCount);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
