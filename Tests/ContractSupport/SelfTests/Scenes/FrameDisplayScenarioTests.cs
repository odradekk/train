using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;

namespace Train.ContractSupport.Tests.Scenes;

[TestClass]
public sealed class FrameDisplayScenarioTests
{
    [TestMethod]
    public async Task PresentKeepsDisplayedBaseAndRetainedFrame()
    {
        var id = new SceneSessionId(Guid.NewGuid());
        var stamp = new SceneFrameStamp(id, new(1), new(1), 1);
        var first = Frame(new(null, stamp, Vector3.Zero, [], [], new([], []), []));
        var next = Frame(new(stamp, stamp with { Revision = 2 }, -Vector3.UnitX, [], [], new([], []), []));
        await using var session = new FixedDisplaySession
        {
            Id = id,
            Binding = new(Guid.NewGuid()),
            PresentResults = [new SceneResult<ISceneFrame>.Success(first), new SceneResult<ISceneFrame>.Success(next)]
        };
        var request = new SceneFrameRequest(0, null, SceneDebugLayer.None);
        var displayed = ((SceneResult<ISceneFrame>.Success)session.Present(request, null)).Value;
        FrameDisplayScenario.AssertFullReplacement(displayed);
        FrameDisplayScenario.AssertRetainedFrame(displayed, () =>
        {
            var second = ((SceneResult<ISceneFrame>.Success)session.Present(request, displayed)).Value;
            FrameDisplayScenario.AssertIncrementalBase(second, displayed);
        });
        Assert.AreSame(displayed, session.PresentCalls[1].DisplayedFrame);
        AssertUnconfigured(() => session.Present(request, next));
    }

    [TestMethod]
    public async Task UnconfiguredOperationsFailClearlyAndDisposeRevokesImmediately()
    {
        var frame = Frame(new(null, WorldRequestScenario.Hit.Frame, Vector3.Zero, [], [], new([], []), []));
        AssertUnconfigured(() => frame.Pick(new(Vector3.Zero, Vector3.UnitZ, 10)));
        AssertUnconfigured(() => frame.FindPose(new Train.Core.Contracts.WorldObjectId.Node(new(1))));
        AssertUnconfigured(() => frame.Locate(Vector3.Zero));
        AssertUnconfigured(() => frame.TerrainHeight(0, 0));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FixedDisplaySession { Id = frame.Update.Frame.Session, Binding = new(Guid.NewGuid()), DisposeGate = gate.Task };
        AssertUnconfigured(() => session.RequestView(WorldRequestScenario.InitialTarget));
        AssertUnconfigured(() => session.Observe(new(null!, null!), []));
        AssertUnconfigured(() => session.Resolve(WorldRequestScenario.Hit));
        AssertUnconfigured(() => new FixedDisplayFactory().OpenWorld(session.Binding, new(null!, null!)));
        var closing = session.DisposeAsync();
        Assert.IsFalse(closing.IsCompleted);
        Assert.IsTrue(session.IsDisposed);
        Assert.AreEqual(1, session.DisposeCalls);
        Assert.AreEqual(Environment.CurrentManagedThreadId, session.DisposeThreadId);
        Assert.ThrowsExactly<ObjectDisposedException>(() => session.Resolve(WorldRequestScenario.Hit));
        gate.SetResult();
        await closing;
    }

    private static FixedSceneFrame Frame(SceneUpdate update) => new() { Update = update };
    private static void AssertUnconfigured(Action action)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(action);
        StringAssert.StartsWith(error.Message, "UNCONFIGURED:");
    }
}
