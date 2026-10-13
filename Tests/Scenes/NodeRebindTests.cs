using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeRebindTests
{
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.None);

    [TestMethod]
    public async Task AddingAndRemovingDesignDiagnosticsAcrossBindingsRebuildsOnlyTheAffectedMember()
    {
        var otherHash = new DesignHash("second-node-vehicle");
        var firstDesign = RailwayFixtures.Vehicle();
        var secondDesign = RailwayFixtures.Vehicle(design: otherHash);
        var diagnostic = new ContentDiagnostic("node_vehicle_warning", ContentDiagnosticSeverity.Warning, "evaluate",
            new ContentReference.Design(otherHash), new(new PackageId("vehicles"), "vehicles.json"), "design", null, "Vehicle warning.");
        var nodeCache = new NodeScene.Cache();
        var railwayCache = new RailwayCache();
        var frames = new List<SceneFrame>();
        var firstBodies = new List<BuiltObject>();
        var secondBodies = new List<BuiltObject>();
        for (ulong generation = 1; generation <= 3; generation++)
        {
            var evaluation = secondDesign with { Diagnostics = generation == 2 ? [diagnostic] : [] };
            var content = RailwayFixtures.Content(generation: generation,
                rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }, design: firstDesign,
                designs: new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
                { [otherHash] = new ContentLookup<DesignEvaluation>.Found(evaluation) });
            var train = NodeFixtures.Train(vehicles: [new(1), new(2)], enteredFrom: new(2), activity: TrainActivity.Servicing);
            var snapshot = NodeFixtures.Snapshot(platforms: 1, trains: [train], content: content,
                instances:
                [
                    RailwayFixtures.Instance(train: train.Id) with { Location = NodeFixtures.Location },
                    RailwayFixtures.Instance(2, design: otherHash, train: train.Id) with { Location = NodeFixtures.Location }
                ]);
            await using var session = Open(snapshot, railwayCache, nodeCache);
            session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
            var frame = await Settled(session);
            Assert.AreEqual(generation == 2, frame.Diagnostics.Any(problem => problem.Code == diagnostic.Code));
            if (generation == 2)
            {
                var problem = frame.Diagnostics.Single(item => item.Code == diagnostic.Code);
                Assert.AreEqual(new WorldObjectId.Instance(new InstanceId.Vehicle(new(2))), problem.Subject);
                Assert.AreEqual(diagnostic.Subject, problem.Content);
                Assert.AreEqual(diagnostic.Source!.Package, problem.Package);
            }
            frames.Add(frame);
            firstBodies.Add(nodeCache.ReadVehicle(NodeFixtures.NodeId, new InstanceId.Vehicle(new(1)))!.Body);
            secondBodies.Add(nodeCache.ReadVehicle(NodeFixtures.NodeId, new InstanceId.Vehicle(new(2)))!.Body);
        }
        Assert.AreSame(firstBodies[0], firstBodies[1]);
        Assert.AreSame(firstBodies[1], firstBodies[2]);
        Assert.AreNotSame(secondBodies[0], secondBodies[1]);
        Assert.AreNotSame(secondBodies[1], secondBodies[2]);
        Assert.IsFalse(frames[0].Diagnostics.Any(problem => problem.Code == diagnostic.Code));
        Assert.IsTrue(frames[1].Diagnostics.Any(problem => problem.Code == diagnostic.Code));
        Assert.IsFalse(frames[2].Diagnostics.Any(problem => problem.Code == diagnostic.Code));
    }

    [TestMethod]
    public async Task FollowingAcrossTwoEdgesRetainsOnlyCurrentAndCandidateLocationsAcrossBothCaches()
    {
        var content = NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 });
        SceneWorldSnapshot Snapshot(TrainView train, ulong steps)
        {
            var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [train], content: content, steps: steps);
            snapshot = RailwayFixtures.ReplaceCore(snapshot, node: snapshot.Core.World.Nodes[1] with { Position = new(4, 0) });
            snapshot = RailwayFixtures.ReplaceCore(snapshot, node: snapshot.Core.World.Nodes[2] with { Position = new(0, -4) });
            snapshot = RailwayFixtures.ReplaceCore(snapshot, edge: snapshot.Core.World.Edges[0] with { LengthMeters = 4 });
            return RailwayFixtures.ReplaceCore(snapshot, edge: snapshot.Core.World.Edges[1] with { LengthMeters = 4 });
        }
        var onFirstEdge = RailwayFixtures.Train(1, direction: TravelDirection.Reverse);
        var initial = Snapshot(onFirstEdge, 0);
        var railwayCache = new RailwayCache();
        var nodeCache = new NodeScene.Cache();
        await using var session = Open(initial, railwayCache, nodeCache);
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        var first = await Settled(session);
        Assert.IsNull(session.PrebuiltLocation);
        Assert.IsNotNull(railwayCache.Find(initial, new(initial, new(1)), 0));

        var atNode = NodeFixtures.Train(enteredFrom: new(1), nextEdge: new(2));
        var middle = Snapshot(atNode, 1);
        session.Observe(middle, []);
        var node = await Settled(session, first);
        Assert.AreEqual(new WorldLocation.Edge(new(2), 4), session.PrebuiltLocation);
        Assert.IsNotNull(nodeCache.Read(new(1)));
        Assert.IsNull(railwayCache.Find(middle, new(middle, new(1)), 0));
        Assert.IsNotNull(railwayCache.Find(middle, new(middle, new(2)), 0));

        var onSecondEdge = RailwayFixtures.Train(.5, direction: TravelDirection.Reverse, speed: 10) with
        { Position = new TrainPosition.Edge(new(2), 1, TravelDirection.Reverse, .5) };
        var last = Snapshot(onSecondEdge, 2);
        session.Observe(last, []);
        var second = await Settled(session, node);
        Assert.AreEqual(new WorldLocation.Node(new(3)), session.PrebuiltLocation);
        Assert.IsNull(nodeCache.Read(new(1)), "The departed node cannot remain beside the current edge and candidate node.");
        Assert.IsNotNull(nodeCache.Read(new(3)));
        Assert.IsNull(railwayCache.Find(last, new(last, new(1)), 0));
        Assert.IsNotNull(railwayCache.Find(last, new(last, new(2)), 0));

        var stopped = Snapshot(onSecondEdge with { SpeedMetersPerSecond = 0 }, 3);
        session.Observe(stopped, []);
        await Settled(session, second);
        Assert.IsNull(session.PrebuiltLocation);
        Assert.IsNull(nodeCache.Read(new(3)), "Canceling the candidate also releases its immutable cache.");
        Assert.IsNotNull(railwayCache.Find(stopped, new(stopped, new(2)), 0));
    }

    private static WorldSceneSession Open(SceneWorldSnapshot snapshot, RailwayCache railways, NodeScene.Cache nodes) =>
        new(new(Guid.NewGuid()), snapshot, WorldSceneSession.Generate, railways, nodes,
            (input, node, _, token, readyLayout) =>
            {
                var layout = NodeLayout.Build(input, node, token);
                readyLayout?.Invoke(layout);
                return new(layout, ImmutableDictionary<string, NodeScene.Part>.Empty);
            });

    private static async Task<SceneFrame> Settled(WorldSceneSession session, ISceneFrame? displayed = null)
    {
        SceneFrame Present() => (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(Request, displayed)).Value;
        var frame = Present();
        while (frame.Readiness != SceneReadiness.Ready || Work().Length > 0)
        {
            await Task.WhenAll(Work());
            frame = Present();
        }
        frame = Present();
        Assert.AreEqual(SceneReadiness.Ready, frame.Readiness);
        return frame;

        Task[] Work() => session.PendingNodeBuilds.Concat(session.PendingRailwayBuilds).Concat(session.PendingPrebuilds).ToArray();
    }
}
