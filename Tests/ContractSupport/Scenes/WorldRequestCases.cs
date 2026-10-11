// 体素契约的请求身份情景；只通过公开世界会话检查旧命中资格。
// 有限替身只支持此情景，真实体素会话待后续生产任务验证。
using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.ContractSupport.Tests.Scenes;

public static class WorldRequestCases
{
    public static void OldHitExpiresAfterNewRequest(IWorldSceneSession session, SceneHit oldHit,
        WorldSceneTarget nextTarget)
    {
        Assert.IsInstanceOfType<SceneResult<PickSubject>.Success>(session.Resolve(oldHit));

        var next = session.RequestView(nextTarget);
        Assert.IsInstanceOfType<SceneResult<SceneRequestId>.Success>(next);
        var request = ((SceneResult<SceneRequestId>.Success)next).Value;
        Assert.AreNotEqual(oldHit.Frame.Request, request);

        var resolved = session.Resolve(oldHit);
        Assert.IsInstanceOfType<SceneResult<PickSubject>.Failure>(resolved);
        var stale = (SceneResult<PickSubject>.Failure)resolved;
        Assert.IsTrue(stale.Problems.Any(problem => problem.Code == "stale_request"));
    }
}

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:scenes-contract-support")]
public sealed class WorldRequestCasesTests
{
    [TestMethod]
    public async Task NewRequestInvalidatesAnOldHit()
    {
        var sessionId = new SceneSessionId(Guid.Parse("00000000-0000-0000-0000-000000000302"));
        var oldHit = new SceneHit(
            new SceneFrameStamp(sessionId, new SceneRequestId(1), new SceneSpaceId(1), 1),
            null,
            new PickSubject.WorldObject(new WorldObjectId.Node(new NodeId(1)), null, null),
            new GridPosition(0, 0, 0), GridFace.PositiveZ, Vector3.Zero, Vector3.UnitZ, 1,
            ImmutableArray<int>.Empty);
        var session = new FixedWorldRequestSession(sessionId, oldHit);

        WorldRequestCases.OldHitExpiresAfterNewRequest(session, oldHit,
            new WorldSceneTarget.Location(new WorldLocation.Node(new NodeId(2))));
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task UnconfiguredTargetFailsClearly()
    {
        var sessionId = new SceneSessionId(Guid.Parse("00000000-0000-0000-0000-000000000302"));
        var oldHit = new SceneHit(
            new SceneFrameStamp(sessionId, new SceneRequestId(1), new SceneSpaceId(1), 1),
            null,
            new PickSubject.WorldObject(new WorldObjectId.Node(new NodeId(1)), null, null),
            new GridPosition(0, 0, 0), GridFace.PositiveZ, Vector3.Zero, Vector3.UnitZ, 1,
            ImmutableArray<int>.Empty);
        var session = new FixedWorldRequestSession(sessionId, oldHit);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            session.RequestView(new WorldSceneTarget.Location(new WorldLocation.Node(new NodeId(3)))));
        StringAssert.Contains(error.Message, "未配置");
        await session.DisposeAsync();
    }
}

internal sealed class FixedWorldRequestSession(SceneSessionId id, SceneHit knownHit) : IWorldSceneSession
{
    private SceneRequestId request = knownHit.Frame.Request;
    private bool disposed;

    public SceneSessionId Id => id;
    public SceneBinding Binding { get; } = new(Guid.Parse("00000000-0000-0000-0000-000000000303"));

    public SceneResult<SceneRequestId> RequestView(WorldSceneTarget target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (target is not WorldSceneTarget.Location { Value: WorldLocation.Node { Id.Value: 2 } }
            || request.Value != knownHit.Frame.Request.Value)
            throw new InvalidOperationException("未配置的观察请求");
        request = new SceneRequestId(request.Value + 1);
        return new SceneResult<SceneRequestId>.Success(request);
    }

    public SceneResult<PickSubject> Resolve(SceneHit hit)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ReferenceEquals(hit, knownHit))
            throw new InvalidOperationException("未配置的场景命中");
        return hit.Frame.Request == request
            ? new SceneResult<PickSubject>.Success(hit.Subject)
            : new SceneResult<PickSubject>.Failure([
                new SceneProblem("stale_request", SceneStage.Pick, null, null, null, null, "旧观察请求")]);
    }

    public SceneResult<SimulationPosition> Observe(SceneWorldSnapshot snapshot,
        ImmutableArray<EventEnvelope> events) => throw new InvalidOperationException("未配置的场景观察");

    public SceneResult<ISceneFrame> Present(SceneFrameRequest frame, ISceneFrame? displayedFrame)
        => throw new InvalidOperationException("未配置的帧接入");

    public ValueTask DisposeAsync()
    {
        disposed = true;
        return ValueTask.CompletedTask;
    }
}
