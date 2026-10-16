using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodePrebuildTests
{
    [TestMethod]
    public async Task Current_scene_builds_prevent_starting_a_follow_candidate()
    {
        using var content = new SceneFixtures.BlockedAppearanceView(NodeFixtures.Content(
            rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }));
        var snapshot = RailwayFixtures.Snapshot(4, 4, 0, content: content,
            trains: [RailwayFixtures.Train(0, direction: TravelDirection.Reverse, speed: 10)],
            instances: [RailwayFixtures.Instance()]);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), snapshot,
            WorldSceneSession.Generate, new(), new(), RailwayFixtures.EmptyNode);
        var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
        try
        {
            session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
            session.Present(request, null);
            await content.Entered.Task;
            Assert.IsNull(session.PrebuiltLocation);
            Assert.AreEqual(0, session.PendingPrebuilds.Length);
            content.Release.Set();
            await RailwayFixtures.Settled(session, request);
            Assert.AreEqual(new WorldLocation.Node(new(1)), session.PrebuiltLocation);
        }
        finally { content.Release.Set(); }
    }
}
