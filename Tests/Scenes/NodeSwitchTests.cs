using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeSwitchTests
{
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.None);

    [TestMethod]
    public async Task FrontNodePrefetchUsesSpeedAndDirectionWithTheExistingSegmentCap()
    {
        var content = NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 });
        var snapshot = RailwayFixtures.Snapshot(800, 800, 0, content: content);
        await using var scene = RailwayFixtures.Controlled(snapshot);
        await RailwayFixtures.Settled(scene, snapshot, new WorldSceneTarget.Location(RailwayFixtures.Location), Request,
            new(new(Guid.NewGuid()), new(1), new(1), 1));
        Assert.IsNull(scene.FrontNode(snapshot, RailwayFixtures.Train(735, speed: 10)));
        Assert.AreEqual(new WorldLocation.Node(new(2)), scene.FrontNode(snapshot, RailwayFixtures.Train(736, speed: 10)));
        Assert.IsNull(scene.FrontNode(snapshot, RailwayFixtures.Train(65, direction: TravelDirection.Reverse, speed: 10)));
        Assert.AreEqual(new WorldLocation.Node(new(1)), scene.FrontNode(snapshot, RailwayFixtures.Train(64, direction: TravelDirection.Reverse, speed: 10)));
        Assert.AreEqual(3, RailwayScene.PrefetchSegments(10, 4));
        Assert.AreEqual(4, RailwayScene.PrefetchSegments(100, 8));
        Assert.AreEqual(0, RailwayScene.PrefetchSegments(0, 8));
    }

    [TestMethod]
    [DataRow(false, TrainActivity.Servicing)]
    [DataRow(false, TrainActivity.Stopped)]
    [DataRow(true, TrainActivity.Stopped)]
    public async Task SwitchFramesKeepBoundaryDisplayPositionTangentAndTerrain(bool reverse, TrainActivity activity)
    {
        var endpoint = reverse ? new NodeId(1) : new NodeId(2);
        var content = NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 },
            terrain: RailwayFixtures.TerrainAppearance with { LocalHeightNoise = [new(8, 1)] });
        var onEdge = RailwayFixtures.Train(reverse ? 0 : 4, track: 2,
            direction: reverse ? TravelDirection.Reverse : TravelDirection.Forward, speed: 0);
        SceneWorldSnapshot Snapshot(TrainView train, ulong steps)
        {
            var value = RailwayFixtures.Snapshot(4, 4, 0, tracks: 2, trackLimit: 3,
                steps: steps, content: content, trains: [train], instances: [RailwayFixtures.Instance() with
                {
                    Location = train.Position is TrainPosition.Node atNode ? new WorldLocation.Node(atNode.Id) :
                        new WorldLocation.Edge(((TrainPosition.Edge)train.Position).Id, ((TrainPosition.Edge)train.Position).DistanceMeters)
                }]);
            foreach (var map in value.Core.World.Nodes)
                value = RailwayFixtures.ReplaceCore(value, node: map with
                {
                    Terrain = map.Terrain with { Relief = 3 },
                    Platforms = map.Id == endpoint && activity == TrainActivity.Servicing ? 2 : 0,
                    Occupant = map.Id == endpoint && activity == TrainActivity.Servicing
                        ? new NodeOccupant.Settlement(new(1), SettlementRelation.NonHostile) : map.Occupant
                });
            return value;
        }
        var initial = Snapshot(onEdge, 0);
        await using var session = Open(initial, Boundary);
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        var line = await NodeSceneTests.Settled(session);
        var oldPath = line.Railway!;
        var arc = reverse ? 0 : oldPath.LengthCells;
        var physical = oldPath.Sample(arc, 17).Position;
        var display = RailwayCoordinates.Relative(physical, line.Origin);
        var hit = (ScenePick.Hit)line.Pick(new(display + new Vector3(0.5f, 20, 0.5f), -Vector3.UnitY, 50));
        var atNode = onEdge with { Position = new TrainPosition.Node(endpoint), Activity = activity,
            NodeTransit = new(RailwayFixtures.Edge, RailwayFixtures.Edge) };
        session.Observe(Snapshot(atNode, 1), []);
        var node = await NodeSceneTests.Settled(session, line);
        Assert.AreEqual(line.Update.Frame.Request, node.Update.Frame.Request);
        Assert.AreEqual(line.Update.Frame.Space.Value + 1, node.Update.Frame.Space.Value);
        Assert.IsNull(node.Update.Base);
        Assert.AreEqual("stale_hit", ((SceneResult<PickSubject>.Failure)session.Resolve(hit.Value)).Problems.Single().Code);
        var entry = node.Node!.EntryFor(RailwayFixtures.Edge);
        var inwards = -entry.Outward;
        var nodeRight = Vector3.Cross(Vector3.UnitY, inwards);
        var offset = reverse ? -17 : 17;
        var point = RailwayCoordinates.Add(entry.Point, nodeRight * offset);
        Assert.IsTrue(Vector3.Distance(display, RailwayCoordinates.Relative(point, node.Origin)) < 0.001);
        Assert.IsTrue(Vector3.Distance(oldPath.Sample(arc).Forward, reverse ? -inwards : inwards) < 0.001);
        AssertHeight(line, node, display + new Vector3(0.5f, 0, 0.5f));
        AssertHeight(line, node, display + new Vector3(25.5f, 0, 0.5f));
        session.Observe(Snapshot(onEdge, 2), []);
        var back = await NodeSceneTests.Settled(session, node);
        var returned = back.Railway!.Sample(reverse ? 0 : back.Railway.LengthCells, 17).Position;
        Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(returned, back.Origin), display) < 0.001);
        AssertHeight(node, back, display + new Vector3(0.5f, 0, 0.5f));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExpandedLineChordKeepsNoisyTerrainAtTheSameDisplayPointAcrossSpaces(bool reverse)
    {
        var snapshot = RailwayFixtures.Snapshot(600, 28.224, 0, content: NodeFixtures.Content(
            terrain: RailwayFixtures.TerrainAppearance with { LocalHeightNoise = [new(4, 10)] }));
        foreach (var nodeMap in snapshot.Core.World.Nodes)
            snapshot = RailwayFixtures.ReplaceCore(snapshot, node: nodeMap with { Terrain = nodeMap.Terrain with { Relief = 1 } });
        var path = new RailwayPath(snapshot, new(1));
        Assert.IsTrue(path.Sample(path.LengthCells).Position.Z > 28.224 * 2);
        var index = reverse ? 0 : path.SegmentCount - 1;
        var segment = RailwayTerrain.Build(snapshot, path, index, default);
        var id = reverse ? path.Start.Id : path.End.Id;
        var layout = reverse ? path.StartNode : path.EndNode;
        var boundary = RailwayTerrain.NodeBoundary(snapshot, path, id, layout.EntryFor(path.Edge.Id), default);
        var stamp = new SceneFrameStamp(new(Guid.NewGuid()), new(1), new(1), 1);
        var line = new SceneFrame(stamp, segment.Origin, 0, SceneReadiness.Ready, [], [], [], [], railway: path, segments: [segment]);
        var origin = SceneFrame.SwitchOrigin(line, layout, null)!.Value;
        var empty = WorldSceneSession.BuildGeometry(_ => new([], [], []));
        var node = new SceneFrame(stamp with { Space = new(2) }, origin, 0, SceneReadiness.Ready, [], [], [], [],
            node: layout, nodeParts: [new("", empty, boundary.Heights, boundary.Origin)]);
        var endpoint = path.Sample(reverse ? 0 : path.LengthCells).Position;
        var point = RailwayCoordinates.Relative(new(Math.Floor(endpoint.X) + 40.5, 0, Math.Floor(endpoint.Z) + .5), line.Origin);
        AssertHeight(line, node, point);
        Assert.AreNotEqual(-RailwayCoordinates.TerrainBaselineOffset + 1,
            ((Lookup<double>.Found)line.TerrainHeight(point.X, point.Z)).Value + line.Origin.Y);
    }

    [TestMethod]
    public async Task CandidateUsesPrefetchRangeAndUnknownNextEdgeDoesNotGuessARoute()
    {
        var content = NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 });
        SceneWorldSnapshot Snapshot(double distance, double speed, ulong step) => RailwayFixtures.Snapshot(4, 4, 0,
            content: content, steps: step, trains: [RailwayFixtures.Train(distance, speed: speed)], instances: [RailwayFixtures.Instance()]);
        await using var session = Open(Snapshot(0, 0, 0), Empty);
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        await NodeSceneTests.Settled(session);
        Assert.IsNull(session.PrebuiltLocation);
        session.Observe(Snapshot(3, 10, 1), []);
        await NodeSceneTests.Settled(session);
        Assert.AreEqual(new WorldLocation.Node(new(2)), session.PrebuiltLocation);
        await Task.WhenAll(session.PendingPrebuilds);
        var atNode = RailwayFixtures.Train() with { Position = new TrainPosition.Node(new(2)), NodeTransit = new(RailwayFixtures.Edge, null) };
        session.Observe(RailwayFixtures.Snapshot(4, 4, 0, content: content, steps: 2, trains: [atNode], instances: [RailwayFixtures.Instance()]), []);
        var node = await NodeSceneTests.Settled(session);
        Assert.AreEqual(new NodeId(2), node.Node!.Node);
        Assert.IsNull(session.PrebuiltLocation);
        session.Observe(RailwayFixtures.Snapshot(4, 4, 0, content: content, steps: 3,
            trains: [atNode with { NodeTransit = new(RailwayFixtures.Edge, RailwayFixtures.Edge) }], instances: [RailwayFixtures.Instance()]), []);
        session.Present(Request, node);
        Assert.IsInstanceOfType<WorldLocation.Edge>(session.PrebuiltLocation);
    }

    [TestMethod]
    public async Task PendingNodeHasStablePanBoundsAndFogUntilItsBuildCompletes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [], content: NodeFixtures.Content(
            rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }));
        await using var session = Open(snapshot, (input, id, previous, token, ready) =>
        {
            var result = Empty(input, id, previous, token, ready);
            started.SetResult(); release.Task.GetAwaiter().GetResult(); return result;
        });
        try
        {
            session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
            await started.Task;
            var loading = (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(Request, null)).Value;
            Assert.AreEqual(SceneReadiness.Loading, loading.Readiness);
            Assert.IsNotNull(loading.NodePanBounds);
            Assert.IsFalse(loading.Update.Environment.FoggedRegions.IsEmpty);
            Assert.IsInstanceOfType<ScenePick.NotReady>(loading.Pick(new(new(0, 10, 0), -Vector3.UnitY, 30)));
            release.SetResult();
            var ready = await NodeSceneTests.Settled(session, loading);
            Assert.AreEqual(SceneReadiness.Ready, ready.Readiness);
            Assert.AreEqual(loading.NodePanBounds, ready.NodePanBounds);
            Assert.IsTrue(ready.Update.Environment.FoggedRegions.IsEmpty);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task CancelledCandidateCannotPublishLateAndCurrentFrameDoesNotWaitForIt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 });
        var initial = RailwayFixtures.Snapshot(4, 4, 0, content: content,
            trains: [RailwayFixtures.Train(3, speed: 10)], instances: [RailwayFixtures.Instance()]);
        var cache = new NodeScene.Cache();
        await using var session = Open(initial, (snapshot, id, previous, token, ready) =>
        {
            started.SetResult(); release.Task.GetAwaiter().GetResult();
            return Empty(snapshot, id, previous, CancellationToken.None, ready);
        }, cache);
        try
        {
            session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
            var current = await NodeSceneTests.Settled(session);
            await started.Task;
            Assert.AreEqual(SceneReadiness.Ready, current.Readiness);
            session.RequestView(new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, 0)));
            var replacement = await NodeSceneTests.Settled(session);
            Assert.IsNull(session.PrebuiltLocation);
            Assert.AreEqual(SceneReadiness.Ready, replacement.Readiness);
            release.SetResult();
            await Task.WhenAll(session.PendingPrebuilds);
            Assert.IsNull(cache.Read(new(2)));
        }
        finally { release.TrySetResult(); }
    }

    private static WorldSceneSession Open(SceneWorldSnapshot snapshot,
        Func<SceneWorldSnapshot, NodeId, NodeScene.Built?, CancellationToken, Action<NodeLayout>?, NodeScene.Built> generate,
        NodeScene.Cache? cache = null) => new(new(Guid.NewGuid()), snapshot, WorldSceneSession.Generate, new(), cache ?? new(), generate);
    private static NodeScene.Built Empty(SceneWorldSnapshot snapshot, NodeId node, NodeScene.Built? previous,
        CancellationToken token, Action<NodeLayout>? ready)
    {
        var layout = NodeLayout.Build(snapshot, node, token); ready?.Invoke(layout);
        return new(layout, ImmutableDictionary<string, NodeScene.Part>.Empty);
    }
    private static NodeScene.Built Boundary(SceneWorldSnapshot snapshot, NodeId node, NodeScene.Built? previous,
        CancellationToken token, Action<NodeLayout>? ready)
    {
        var layout = NodeLayout.Build(snapshot, node, token); ready?.Invoke(layout);
        var entry = layout.Entries.Single();
        var boundary = RailwayTerrain.NodeBoundary(snapshot, new(snapshot, entry.Edge), node, entry, token);
        var geometry = WorldSceneSession.BuildGeometry(id =>
        {
            var grid = new VoxelGrid(boundary.Cells); var root = id("");
            var pose = new ScenePose(RailwayCoordinates.Relative(boundary.Origin, default), Quaternion.Identity);
            return new([new(root, null, pose, null, DesignGeometry.Meshes(grid, false), [], [])], [new(root, pose, grid)], boundary.Problems);
        });
        return new(layout, ImmutableDictionary<string, NodeScene.Part>.Empty.Add("boundary", new("", geometry, boundary.Heights, boundary.Origin)));
    }
    private static void AssertHeight(ISceneFrame before, ISceneFrame after, Vector3 point)
    {
        Assert.IsInstanceOfType<Lookup<double>.Found>(before.TerrainHeight(point.X, point.Z));
        Assert.AreEqual(((Lookup<double>.Found)before.TerrainHeight(point.X, point.Z)).Value,
            ((Lookup<double>.Found)after.TerrainHeight(point.X, point.Z)).Value);
    }
}
