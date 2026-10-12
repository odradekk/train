using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.ContractSupport;

public static class WorldRequestScenario
{
    public static WorldSceneTarget InitialTarget { get; } =
        new WorldSceneTarget.Location(new WorldLocation.Node(new NodeId(1)));
    public static WorldSceneTarget NextTarget { get; } =
        new WorldSceneTarget.Location(new WorldLocation.Node(new NodeId(2)));
    public static SceneHit Hit { get; } = new(
        new SceneFrameStamp(new SceneSessionId(Guid.Parse("00000000-0000-0000-0000-000000000302")),
            new SceneRequestId(1), new SceneSpaceId(1), 1),
        null, new PickSubject.WorldObject(new WorldObjectId.Node(new NodeId(1)), null, null),
        new GridPosition(0, 0, 0), GridFace.PositiveZ, Vector3.Zero, Vector3.UnitZ, 1,
        ImmutableArray<int>.Empty);

    public static void AssertOldHitExpires(IWorldSceneSession session, SceneHit oldHit,
        WorldSceneTarget nextTarget)
    {
        Assert.IsInstanceOfType<SceneResult<PickSubject>.Success>(session.Resolve(oldHit));
        var next = session.RequestView(nextTarget);
        Assert.IsInstanceOfType<SceneResult<SceneRequestId>.Success>(next);
        Assert.AreNotEqual(oldHit.Frame.Request, ((SceneResult<SceneRequestId>.Success)next).Value);
        var resolved = session.Resolve(oldHit);
        Assert.IsInstanceOfType<SceneResult<PickSubject>.Failure>(resolved);
        Assert.IsTrue(((SceneResult<PickSubject>.Failure)resolved).Problems
            .Any(problem => problem.Code == "stale_request"));
    }
}
