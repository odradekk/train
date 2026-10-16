using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class PendingWorkTests
{
    [TestMethod]
    public async Task Canceled_work_waits_for_the_remaining_work_before_completing()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        canceled.SetCanceled();

        var waiting = SceneFixtures.AwaitPendingWork([canceled.Task, remaining.Task]);
        Assert.IsFalse(waiting.IsCompleted);

        remaining.SetResult();
        await waiting;
    }

    [TestMethod]
    public async Task Faulted_work_still_fails_when_other_work_is_canceled()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new InvalidOperationException("Build failed.");
        canceled.SetCanceled();

        var waiting = SceneFixtures.AwaitPendingWork([canceled.Task, faulted.Task]);
        faulted.SetException(error);

        Assert.AreSame(error, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => waiting));
    }

    [TestMethod]
    public async Task Faulted_cancellation_exception_is_not_treated_as_canceled_work()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new OperationCanceledException("Build failed without canceling its task.");
        canceled.SetCanceled();

        var waiting = SceneFixtures.AwaitPendingWork([canceled.Task, faulted.Task]);
        faulted.SetException(error);

        Assert.AreSame(error, await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => waiting));
    }
}
