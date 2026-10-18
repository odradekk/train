using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;

namespace Train.ContractSupport.Tests.Scenes;

[TestClass]
public sealed class FrameDisplayScenarioTests
{
    [TestMethod]
    public async Task UnconfiguredOperationsFailClearlyAndDisposeRevokesImmediately()
    {
        var overview = new SceneNodeOverview([new(7, PlotSize.Small,
            [new(-2, -3), new(2, -3), new(2, 3), new(-2, 3)])],
            [[new(-10, 0), new(10, 0)]]);
        var frame = new FixedSceneFrame
        {
            Update = new(null, WorldRequestScenario.Hit.Frame, Vector3.Zero, [], [], new([], []), []),
            NodeOverview = overview
        };
        AssertUnconfigured(() => frame.Pick(new(Vector3.Zero, Vector3.UnitZ, 10)));
        AssertUnconfigured(() => frame.FindPose(new Train.Core.Contracts.WorldObjectId.Node(new(1))));
        AssertUnconfigured(() => frame.Locate(Vector3.Zero));
        AssertUnconfigured(() => frame.TerrainHeight(0, 0));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FixedDisplaySession
        {
            Id = frame.Update.Frame.Session, Binding = new(Guid.NewGuid()), DisposeGate = gate.Task,
            PresentResults = [new SceneResult<ISceneFrame>.Success(frame)]
        };
        var retained = ((SceneResult<ISceneFrame>.Success)session.Present(new(0, null, SceneDebugLayer.None), null)).Value;
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
        Assert.AreSame(overview, retained.NodeOverview);
        Assert.IsNotNull(retained.NodeOverview);
        Assert.AreEqual(new Vector2(-2, -3), retained.NodeOverview.Plots[0].Corners[0]);
        Assert.AreEqual(new Vector2(10, 0), retained.NodeOverview.Tracks[0][1]);
    }

    [TestMethod]
    public void DesignPreviewRequiresConfigurationAndPassesExactInputs()
    {
        var content = new Train.Content.ContractSupport.FixedContentView();
        var design = new Train.Content.Contracts.DesignEvaluation.Failed([]);
        var result = new SceneResult<IDesignPreview>.Failure([]);
        var factory = new FixedDisplayFactory
        {
            PreviewDesignCall = (actualContent, actualDesign) =>
            {
                Assert.AreSame(content, actualContent);
                Assert.AreSame(design, actualDesign);
                return result;
            }
        };
        Assert.AreSame(result, factory.PreviewDesign(content, design));
        Assert.AreEqual(1, factory.PreviewCalls.Count);
        Assert.AreSame(content, factory.PreviewCalls[0].Content);
        Assert.AreSame(design, factory.PreviewCalls[0].Initial);
        AssertUnconfigured(() => new FixedDisplayFactory().PreviewDesign(content, design));
    }

    private static void AssertUnconfigured(Action action)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(action);
        StringAssert.StartsWith(error.Message, "UNCONFIGURED:");
    }
}
