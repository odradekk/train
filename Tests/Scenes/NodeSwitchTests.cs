using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeSwitchTests
{
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.None);

    [TestMethod]
    public async Task Follow_candidate_prebuilds_only_handover_range()
    {
        var source = NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 });
        var layout = NodeFixtures.Layout with
        {
            Plots = [],
            SceneModel = NodeFixtures.Layout.SceneModel with
            {
                Scene = SceneFixtures.Projection([SceneFixtures.Cell(new(-96, 0, -96)), SceneFixtures.Cell(new(95, 0, 95), 2)]) with
                { OccupiedBounds = new(new(-96, 0, -96), new(95, 0, 95)) }
            }
        };
        var content = new FixedContentView
        {
            Generation = source.Generation, Core = source.Core, Voxels = source.Voxels,
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [SceneFixtures.Design] = source.FindDesign(SceneFixtures.Design) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            { [SceneFixtures.Voxel] = source.FindVoxelAppearance(SceneFixtures.Voxel) },
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [RailwayFixtures.Terrain] = new ContentLookup<TerrainAppearanceProjection>.Missing([]) },
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
            { [RailwayFixtures.Rule] = source.FindRailwayAppearance(RailwayFixtures.Rule) },
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
            { [new(1)] = new ContentLookup<SettlementLayoutProjection>.Found(layout) }
        };
        var initial = NodeFixtures.Snapshot(platforms: 0, trains: [RailwayFixtures.Train(0, direction: TravelDirection.Reverse)], content: content,
            tracks: 2, trackLimit: 2, platformLengthMeters: 0);
        var cache = new NodeScene.Cache();
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), initial, WorldSceneSession.Generate, new(), cache);
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        var displayed = await RailwayFixtures.Settled(session, Request);
        Assert.AreEqual(new WorldLocation.Node(NodeFixtures.NodeId), session.PrebuiltLocation);
        do
        {
            await Task.WhenAll(session.PendingPrebuilds);
            displayed = Present(session, displayed);
        } while (session.PendingPrebuilds.Length > 0);
        var retained = cache.Read(NodeFixtures.NodeId)!;
        var inlet = retained.Layout.EntryFor(new(1));
        var entry = RailwayCoordinates.Add(inlet.Point,
            Vector3.Cross(Vector3.UnitY, -inlet.Outward) * (float)RailwayCoordinates.TrackOffset(1, 2));
        var expected = new NodeRange(entry, NodeStreaming.PrefetchCells).Chunks(retained.Layout)
            .Select(NodeScene.ChunkKey).ToArray();
        CollectionAssert.AreEquivalent(expected, retained.Parts.Keys.Where(key => key.StartsWith("terrain:", StringComparison.Ordinal)).ToArray());
        var railSegments = NodeScene.Prepare(initial, retained.Layout).Rails;
        var entryRange = new NodeRange(entry, NodeStreaming.PrefetchCells);
        CollectionAssert.AreEquivalent(railSegments.Values.Where(segment => entryRange.Intersects(segment.Bounds)).Select(segment => segment.Key).ToArray(),
            retained.Parts.Keys.Where(railSegments.ContainsKey).ToArray());
        var wholeCount = (retained.Layout.Bounds.Maximum.X - retained.Layout.Bounds.Minimum.X) / VoxelGrid.ChunkSize *
            (retained.Layout.Bounds.Maximum.Y - retained.Layout.Bounds.Minimum.Y) / VoxelGrid.ChunkSize;
        Assert.IsTrue(expected.Length < wholeCount, "The candidate must leave distant node chunks unbuilt.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SingleTrackEntryKeepsVehiclePoseAndDoesNotInterpolateAcrossSpaces(bool reverse)
    {
        var endpoint = reverse ? new NodeId(1) : new NodeId(2);
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle(),
            rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 });
        var onEdge = RailwayFixtures.Train(reverse ? 0 : 4,
            direction: reverse ? TravelDirection.Reverse : TravelDirection.Forward);
        SceneWorldSnapshot Snapshot(TrainView train, ulong step) => RailwayFixtures.Snapshot(4, 4, 0,
            steps: step, content: content, trains: [train], instances: [RailwayFixtures.Instance() with
            {
                Location = train.Position is TrainPosition.Node ? new WorldLocation.Node(endpoint) :
                    new WorldLocation.Edge(RailwayFixtures.Edge, ((TrainPosition.Edge)train.Position).DistanceMeters)
            }]);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), Snapshot(onEdge, 0),
            WorldSceneSession.Generate, new(), new());
        session.RequestView(new WorldSceneTarget.Follow(onEdge.Id, onEdge.Vehicles[0]));
        var line = await RailwayFixtures.Settled(session, Request);
        var subject = new WorldObjectId.Instance(new InstanceId.Vehicle(onEdge.Vehicles[0]));
        var before = ((Lookup<ScenePose>.Found)line.FindPose(subject)).Value;
        var entering = onEdge with
        {
            Position = new TrainPosition.Node(endpoint, RailwayFixtures.Edge, null, NodeTravelPhase.Approaching, 0, null)
        };
        session.Observe(Snapshot(entering, 1), []);
        var first = (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(Request with { Interpolation = 0 }, line)).Value;
        var node = await RailwayFixtures.Settled(session, Request, first);
        var atStart = ((Lookup<ScenePose>.Found)first.FindPose(subject)).Value;
        var atEnd = ((Lookup<ScenePose>.Found)node.FindPose(subject)).Value;
        Assert.IsTrue(Vector3.Distance(before.Position, atStart.Position) < .001);
        Assert.IsTrue(Vector3.Distance(atStart.Position, atEnd.Position) < .001,
            "A space transition must use the current node boundary at every interpolation value.");
        var forward = Vector3.Transform(Vector3.UnitZ, before.Rotation);
        Assert.IsTrue(Vector3.Distance(forward, Vector3.Transform(Vector3.UnitZ, atStart.Rotation)) < .001);
        var hit = (ScenePick.Hit)node.Pick(new(atEnd.Position + Vector3.Transform(new Vector3(.5f, 20, .5f), atEnd.Rotation),
            -Vector3.UnitY, 50));
        Assert.AreEqual(subject, ((PickSubject.WorldObject)hit.Value.Subject).Object);
    }

    [TestMethod]
    public async Task ProductionFollowCrossesEmptyNodeBetweenTwoDifferentLines()
    {
        var content = new FixedContentView
        {
            Generation = new(1), Core = SceneFixtures.Content().Core,
            Voxels = [new(NodeFixtures.Concrete, null), new(SceneFixtures.Voxel, null)],
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [SceneFixtures.Design] = new ContentLookup<DesignEvaluation>.Found(RailwayFixtures.Vehicle()) },
            // Concrete is deliberately unconfigured: reading it would fault this build.
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube) },
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [RailwayFixtures.Terrain] = new ContentLookup<TerrainAppearanceProjection>.Found(RailwayFixtures.TerrainAppearance) },
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
            { [RailwayFixtures.Rule] = new ContentLookup<RailwayAppearanceProjection>.Found(RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }) }
        };
        SceneWorldSnapshot Snapshot(TrainView train, ulong step)
        {
            var value = ShortNodeSnapshot(train, content, step);
            return RailwayFixtures.ReplaceCore(value, node: value.Core.World.Nodes.Single(item => item.Id == NodeFixtures.NodeId)
                with { Occupant = new NodeOccupant.Empty(), Platforms = 0 });
        }
        var approaching = RailwayFixtures.Train(0, direction: TravelDirection.Reverse);
        var cache = new NodeScene.Cache();
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), Snapshot(approaching, 0),
            WorldSceneSession.Generate, new(), cache);
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        var lineA = await RailwayFixtures.Settled(session, Request);
        Assert.AreEqual(SceneReadiness.Ready, lineA.Readiness);
        Assert.AreEqual(new EdgeId(1), lineA.Railway!.Edge.Id);
        var boundaryA = lineA.Railway.Sample(0).Position;
        var stopped = NodeFixtures.Train(enteredFrom: new(1), nextEdge: new(2));
        session.Observe(Snapshot(stopped, 1), []);
        var firstNode = Present(session, lineA);
        var entryA = firstNode.Node!.EntryFor(new(1));
        var expectedNodeOrigin = new ScenePoint(entryA.Point.X - boundaryA.X + lineA.Origin.X,
            entryA.Point.Y - boundaryA.Y + lineA.Origin.Y, entryA.Point.Z - boundaryA.Z + lineA.Origin.Z);
        Assert.AreEqual(expectedNodeOrigin, firstNode.Origin);
        Assert.AreEqual(lineA.Update.Frame.Space.Value + 1, firstNode.Update.Frame.Space.Value);
        var node = await RailwayFixtures.Settled(session, Request, firstNode);
        Assert.AreEqual(SceneReadiness.Ready, node.Readiness);
        Assert.IsFalse(node.Diagnostics.Any(problem => problem.Code == "content_missing"), "An empty node needs no concrete or settlement content.");
        var built = cache.Read(NodeFixtures.NodeId)!;
        Assert.IsTrue(built.Parts.Keys.Any(key => key.StartsWith("terrain:", StringComparison.Ordinal)));
        var visibleMeshes = node.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>()
            .Select(visual => visual.Mesh).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var prefix in new[] { "entry:1:", "entry:2:", "station-tracks:" })
        {
            var meshes = built.Parts.Where(part => part.Key.StartsWith(prefix, StringComparison.Ordinal))
                .SelectMany(part => part.Value.Geometry.Geometry.Objects).SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().ToArray();
            Assert.IsTrue(meshes.Length > 0 && meshes.All(visual => visibleMeshes.Contains(visual.Mesh)), prefix);
        }
        Assert.IsTrue(built.Parts.Where(part => part.Key.StartsWith("terrain:", StringComparison.Ordinal))
            .SelectMany(part => part.Value.Geometry.Geometry.Objects).SelectMany(item => item.Visuals)
            .OfType<SceneVisual.Geometry>().Any(visual => visibleMeshes.Contains(visual.Mesh)), "The node frame displays its generated terrain.");
        Assert.IsFalse(built.Parts.Keys.Any(key => key.StartsWith("platform:", StringComparison.Ordinal)));
        var expectedQueueCenter = node.Node!.Bypass.Center;
        var subject = new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)));
        var stoppedPose = ((Lookup<ScenePose>.Found)node.FindPose(subject)).Value;
        Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(expectedQueueCenter, node.Origin), stoppedPose.Position) < .001);
        Assert.AreEqual(NodeFixtures.Location, ((Lookup<WorldLocation>.Found)node.Locate(stoppedPose.Position)).Value);
        Assert.IsInstanceOfType<Lookup<double>.Found>(node.TerrainHeight(stoppedPose.Position.X, stoppedPose.Position.Z));
        Assert.AreEqual(new WorldLocation.Edge(new(2), 4), session.PrebuiltLocation);
        await Task.WhenAll(session.PendingPrebuilds);
        session.Observe(Snapshot(stopped with { Activity = TrainActivity.Servicing }, 2), []);
        var servicing = await RailwayFixtures.Settled(session, Request, node);
        Assert.AreEqual(SceneReadiness.Ready, servicing.Readiness);
        Assert.IsFalse(servicing.Diagnostics.Any(problem => problem.Code == "content_missing"));
        Assert.AreEqual(node.Update.Frame.Space, servicing.Update.Frame.Space);
        Assert.AreEqual(stoppedPose, ((Lookup<ScenePose>.Found)servicing.FindPose(subject)).Value);
        var leaving = RailwayFixtures.Train(3.9) with { Position = new TrainPosition.Edge(new(2), 1, TravelDirection.Reverse, 3.9) };
        session.Observe(Snapshot(leaving, 3), []);
        var firstLineB = Present(session, servicing);
        Assert.AreEqual(new EdgeId(2), firstLineB.Railway!.Edge.Id);
        var boundaryB = firstLineB.Railway.Sample(firstLineB.Railway.LengthCells).Position;
        var entryB = servicing.Node!.EntryFor(new(2)).Point;
        var expectedLineOrigin = new ScenePoint(boundaryB.X - entryB.X + servicing.Origin.X,
            boundaryB.Y - entryB.Y + servicing.Origin.Y, boundaryB.Z - entryB.Z + servicing.Origin.Z);
        Assert.AreEqual(expectedLineOrigin, firstLineB.Origin);
        Assert.AreEqual(servicing.Update.Frame.Space.Value + 1, firstLineB.Update.Frame.Space.Value);
        AssertVehicleBody(firstLineB);
        var lineB = await RailwayFixtures.Settled(session, Request, firstLineB);
        Assert.AreEqual(SceneReadiness.Ready, lineB.Readiness);
        Assert.AreEqual(new EdgeId(2), lineB.Railway!.Edge.Id);
    }

    [TestMethod]
    public async Task FrontNodePrefetchUsesSpeedAndDirectionWithTheExistingSegmentCap()
    {
        var content = NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 });
        var snapshot = RailwayFixtures.Snapshot(800, 800, 0, content: content);
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(snapshot, vehicles);
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
    [DataRow(false, TrainActivity.Servicing, false)]
    [DataRow(false, TrainActivity.Servicing, true)]
    [DataRow(false, TrainActivity.Stopped, false)]
    [DataRow(false, TrainActivity.Stopped, true)]
    [DataRow(true, TrainActivity.Stopped, false)]
    [DataRow(true, TrainActivity.Stopped, true)]
    public async Task SwitchFramesKeepBoundaryDisplayPositionTangentAndTerrain(bool reverse, TrainActivity activity, bool candidateReady)
    {
        var endpoint = reverse ? new NodeId(1) : new NodeId(2);
        var content = NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 },
            terrain: RailwayFixtures.TerrainAppearance with { LocalHeightNoise = [new(8, 1)] });
        var onEdge = RailwayFixtures.Train(reverse ? 0 : 4, track: 2,
            direction: reverse ? TravelDirection.Reverse : TravelDirection.Forward, speed: candidateReady ? 10 : 0);
        SceneWorldSnapshot Snapshot(TrainView train, ulong steps)
        {
            var value = RailwayFixtures.Snapshot(4, 4, 0, tracks: 2, trackLimit: 3,
                steps: steps, content: content, trains: [train], instances: [RailwayFixtures.Instance() with
                {
                    Location = train.Position is TrainPosition.Node atNode ? new WorldLocation.Node(atNode.Id) :
                        new WorldLocation.Edge(((TrainPosition.Edge)train.Position).Id, ((TrainPosition.Edge)train.Position).DistanceMeters)
                }]);
            var core = value.Core;
            var nodes = core.World.Nodes.Select(map => map with
            {
                Terrain = map.Terrain with { Relief = 3 },
                Platforms = map.Id == endpoint && activity == TrainActivity.Servicing ? 2 : 0,
                Occupant = map.Id == endpoint && activity == TrainActivity.Servicing
                    ? new NodeOccupant.Settlement(new(1), SettlementRelation.NonHostile) : map.Occupant
            }).ToImmutableArray();
            value = value with { Core = new FixedCoreReadView
            {
                Position = core.Position, InputIdentity = core.InputIdentity, World = core.World with { Nodes = nodes },
                Nodes = nodes.ToDictionary(map => map.Id, map => (Lookup<NodeView>)new Lookup<NodeView>.Found(
                    ((Lookup<NodeView>.Found)core.FindNode(map.Id)).Value with { Map = map })),
                Edges = core.World.Edges.ToDictionary(edge => edge.Id, edge => core.FindEdge(edge.Id)),
                Trains = new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = core.FindTrain(train.Id) },
                Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
                    { [new(1)] = new Lookup<SettlementView>.Found(NodeFixtures.Settlement() with { Node = endpoint }) },
                Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
                {
                    [new InstanceId.Vehicle(new(1))] = core.FindInstance(new InstanceId.Vehicle(new(1))),
                    [new InstanceId.Building(new(1))] = new Lookup<InstanceView>.Found(NodeFixtures.Station(2) with { Location = new WorldLocation.Node(endpoint) })
                }
            } };
            return value;
        }
        var initial = Snapshot(onEdge, 0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (candidateReady) release.SetResult();
        await using var session = Open(initial, (snapshot, id, previous, token) =>
        {
            release.Task.GetAwaiter().GetResult();
            return Boundary(snapshot, id, previous, token);
        });
        try
        {
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        var line = await RailwayFixtures.Settled(session, Request);
        if (candidateReady)
        {
            Assert.AreEqual(new WorldLocation.Node(endpoint), session.PrebuiltLocation);
            await Task.WhenAll(session.PendingPrebuilds);
        }
        var oldPath = line.Railway!;
        var arc = reverse ? 0 : oldPath.LengthCells;
        var physical = oldPath.Sample(arc, 17).Position;
        var display = RailwayCoordinates.Relative(physical, line.Origin);
        var hit = (ScenePick.Hit)line.Pick(new(display + new Vector3(0.5f, 20, 0.5f), -Vector3.UnitY, 50));
        var atNode = onEdge with { Position = new TrainPosition.Node(endpoint, RailwayFixtures.Edge, RailwayFixtures.Edge,
                activity == TrainActivity.Running ? NodeTravelPhase.Departing : NodeTravelPhase.Docked, 80, null), Activity = activity };
        session.Observe(Snapshot(atNode, 1), []);
        var firstNode = Present(session, line);
        var expectedOrigin = SceneFrame.SwitchOrigin(line, NodeLayout.Build(Snapshot(atNode, 1), endpoint, default), null);
        Assert.AreEqual(expectedOrigin, firstNode.Origin, "The first switched frame must already use the handoff origin.");
        AssertVehicleBody(firstNode);
        Assert.AreEqual(candidateReady ? 0 : 1, firstNode.Update.Environment.FoggedRegions.Length, "Only pending terrain may be fogged; the ready vehicle body stays visible.");
        var firstEntry = firstNode.Node!.EntryFor(RailwayFixtures.Edge);
        var firstPoint = RailwayCoordinates.Add(firstEntry.Point, Vector3.Cross(Vector3.UnitY, -firstEntry.Outward) * (reverse ? -17 : 17));
        Assert.IsTrue(Vector3.Distance(display, RailwayCoordinates.Relative(firstPoint, firstNode.Origin)) < 0.001);
        Assert.IsTrue(Vector3.Distance(oldPath.Sample(arc).Forward, reverse ? firstEntry.Outward : -firstEntry.Outward) < 0.001);
        release.TrySetResult();
        var node = await RailwayFixtures.Settled(session, Request, firstNode);
        Assert.AreEqual(line.Update.Frame.Request, node.Update.Frame.Request);
        Assert.AreEqual(line.Update.Frame.Space.Value + 1, node.Update.Frame.Space.Value);
        Assert.IsNull(firstNode.Update.Base);
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
        session.Observe(Snapshot(onEdge with { Position = ((TrainPosition.Edge)onEdge.Position) with { DistanceMeters = reverse ? 1 : 3 } }, 2), []);
        var firstBack = Present(session, node);
        Assert.AreEqual(SceneFrame.SwitchOrigin(node, null, firstBack.Railway), firstBack.Origin);
        AssertVehicleBody(firstBack);
        var back = await RailwayFixtures.Settled(session, Request, firstBack);
        var returned = back.Railway!.Sample(reverse ? 0 : back.Railway.LengthCells, 17).Position;
        Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(returned, back.Origin), display) < 0.001);
        AssertHeight(node, back, display + new Vector3(0.5f, 0, 0.5f));
        }
        finally { release.TrySetResult(); }
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
        var layout = NodeLayout.Build(snapshot, id, default);
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
        await using var session = Open(Snapshot(0, 0, 0), RailwayFixtures.EmptyNode);
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        await RailwayFixtures.Settled(session, Request);
        Assert.IsNull(session.PrebuiltLocation);
        session.Observe(Snapshot(3, 10, 1), []);
        await RailwayFixtures.Settled(session, Request);
        Assert.AreEqual(new WorldLocation.Node(new(2)), session.PrebuiltLocation);
        await Task.WhenAll(session.PendingPrebuilds);
        var atNode = RailwayFixtures.Train() with { Position = new TrainPosition.Node(new(2), RailwayFixtures.Edge, null, NodeTravelPhase.Approaching, 0, null) };
        session.Observe(RailwayFixtures.Snapshot(4, 4, 0, content: content, steps: 2, trains: [atNode], instances: [RailwayFixtures.Instance()]), []);
        var node = await RailwayFixtures.Settled(session, Request);
        Assert.AreEqual(new NodeId(2), node.Node!.Node);
        Assert.IsNull(session.PrebuiltLocation);
        session.Observe(RailwayFixtures.Snapshot(4, 4, 0, content: content, steps: 3,
            trains: [atNode with { Position = ((TrainPosition.Node)atNode.Position) with { NextEdge = RailwayFixtures.Edge } }], instances: [RailwayFixtures.Instance()]), []);
        session.Present(Request, node);
        Assert.IsInstanceOfType<WorldLocation.Edge>(session.PrebuiltLocation);
    }

    [TestMethod]
    public async Task NodeCandidateFollowsNextEdgeWhenThePlanPointsToAnotherNeighbor()
    {
        var content = NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 });
        var train = NodeFixtures.Train(enteredFrom: new(1)) with
        {
            Plan = new(false, [new PlanStop.Waypoint(new(2))]), NextStopIndex = 0
        };
        await using var session = Open(ShortNodeSnapshot(train, content, 0), RailwayFixtures.EmptyNode);
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        await RailwayFixtures.Settled(session, Request);
        Assert.IsNull(session.PrebuiltLocation, "Even with multiple neighbors and a run plan, a missing NextEdge must not guess a route.");
        session.Observe(ShortNodeSnapshot(train with { Position = ((TrainPosition.Node)train.Position) with { EnteredFrom = new(1), NextEdge = new(2) } }, content, 1), []);
        Present(session);
        Assert.AreEqual(new WorldLocation.Edge(new(2), 4), session.PrebuiltLocation);
    }

    [TestMethod]
    public async Task SwitchingIntoAnotherEdgeUsesTheNodeHandoffOnItsFirstFrame()
    {
        var content = NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 });
        var onFirst = RailwayFixtures.Train(0);
        SceneWorldSnapshot Snapshot(TrainView train, ulong step) => ShortNodeSnapshot(train, content, step);
        await using var session = Open(Snapshot(onFirst, 0), RailwayFixtures.EmptyNode);
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        var firstLine = await RailwayFixtures.Settled(session, Request);
        var inNode = NodeFixtures.Train(enteredFrom: new(1), nextEdge: new(2));
        session.Observe(Snapshot(inNode, 1), []);
        var node = await RailwayFixtures.Settled(session, Request, firstLine);
        var onSecond = RailwayFixtures.Train(3.9) with { Position = new TrainPosition.Edge(new(2), 1, TravelDirection.Reverse, 3.9) };
        session.Observe(Snapshot(onSecond, 2), []);
        var firstBack = Present(session, node);
        var expected = SceneFrame.SwitchOrigin(node, null, firstBack.Railway)!.Value;
        Assert.AreEqual(expected, firstBack.Origin);
        var focus = firstBack.Railway!.Sample(3.9 * RailwayCoordinates.CellsPerMeter).Position;
        var step = RailwayCoordinates.OriginStepCells;
        var rounded = new ScenePoint(Math.Floor(focus.X / step) * step, Math.Floor(focus.Y / step) * step, Math.Floor(focus.Z / step) * step);
        Assert.AreNotEqual(expected, rounded, "The handoff must differ from the new railway's normal rounded origin, so this detects replacement by that origin.");
        var entry = node.Node!.EntryFor(new(2)).Point;
        var endpoint = firstBack.Railway.Sample(firstBack.Railway.LengthCells).Position;
        Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(entry, node.Origin), RailwayCoordinates.Relative(endpoint, firstBack.Origin)) < .001);
        AssertVehicleBody(firstBack);
    }

    [TestMethod]
    public async Task DescendingLineUsesItsNonzeroVerticalOriginInTheFirstPendingNodeFrame()
    {
        var content = NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 });
        var train = RailwayFixtures.Train(3.9);
        SceneWorldSnapshot Snapshot(TrainView value, ulong steps, ulong seed)
        {
            var snapshot = RailwayFixtures.Snapshot(4, 3, 3, content: content, trains: [value], instances: [RailwayFixtures.Instance()], steps: steps);
            return RailwayFixtures.ReplaceCore(snapshot, worldSeed: seed);
        }
        var seed = Enumerable.Range(1, 32).Select(value => (ulong)value)
            .First(value => new RailwayPath(Snapshot(train, 0, value), RailwayFixtures.Edge).Sample(7.8).Position.Y < 0);
        var initial = Snapshot(train, 0, seed);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = Open(initial, (snapshot, id, previous, token) =>
        {
            release.Task.GetAwaiter().GetResult();
            return RailwayFixtures.EmptyNode(snapshot, id, previous, token);
        });
        try
        {
            session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
            var line = await RailwayFixtures.Settled(session, Request);
            Assert.AreEqual(-256d, line.Origin.Y);
            var inNode = train with { Position = new TrainPosition.Node(new(2), RailwayFixtures.Edge, null, NodeTravelPhase.Approaching, 0, null) };
            var snapshot = Snapshot(inNode, 1, seed);
            session.Observe(snapshot, []);
            var first = Present(session, line);
            Assert.AreEqual(SceneFrame.SwitchOrigin(line, NodeLayout.Build(snapshot, new(2), default), null), first.Origin);
            Assert.AreEqual(line.Origin.Y, first.Origin.Y);
            var bounds = first.NodePanBounds!;
            var layout = first.Node!;
            Assert.AreEqual(layout.PanBounds(first.Origin), bounds);
            Assert.AreNotEqual(layout.PanBounds(default), bounds);
            var fog = first.Update.Environment.FoggedRegions.Single();
            Assert.AreEqual((float)(-128 - first.Origin.Y), fog.Minimum.Y);
            Assert.AreEqual((float)(128 - first.Origin.Y), fog.Maximum.Y);
            AssertVehicleBody(first);
            release.SetResult();
            var ready = await RailwayFixtures.Settled(session, Request, first);
            Assert.AreEqual(first.NodePanBounds, ready.NodePanBounds);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task PendingNodeHasStablePanBoundsAndFogUntilItsBuildCompletes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [], content: NodeFixtures.Content(
            rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }));
        await using var session = Open(snapshot, (input, id, previous, token) =>
        {
            var result = RailwayFixtures.EmptyNode(input, id, previous, token);
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
            var ready = await RailwayFixtures.Settled(session, Request, loading);
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
        await using var session = Open(initial, (snapshot, id, previous, token) =>
        {
            started.SetResult(); release.Task.GetAwaiter().GetResult();
            return RailwayFixtures.EmptyNode(snapshot, id, previous, CancellationToken.None);
        }, cache);
        try
        {
            session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
            var current = await RailwayFixtures.Settled(session, Request);
            await started.Task;
            Assert.AreEqual(SceneReadiness.Ready, current.Readiness);
            session.RequestView(new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, 0)));
            var replacement = await RailwayFixtures.Settled(session, Request);
            Assert.IsNull(session.PrebuiltLocation);
            Assert.AreEqual(SceneReadiness.Ready, replacement.Readiness);
            release.SetResult();
            await Task.WhenAll(session.PendingPrebuilds);
            var afterRelease = Present(session, replacement);
            Assert.IsNull(session.PrebuiltLocation);
            Assert.IsNull(afterRelease.Node);
            Assert.AreEqual(replacement.Update.Frame.Space, afterRelease.Update.Frame.Space);
            Assert.IsNull(cache.Read(new(2)));
        }
        finally { release.TrySetResult(); }
    }

    private static SceneWorldSnapshot ShortNodeSnapshot(TrainView train, Train.Content.Contracts.IContentView content, ulong steps)
    {
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [train], content: content, steps: steps, platformLengthMeters: 0);
        foreach (var node in snapshot.Core.World.Nodes)
            snapshot = RailwayFixtures.ReplaceCore(snapshot, node: node with { Position = new(node.Position.X / 200, node.Position.Y / 200) });
        foreach (var edge in snapshot.Core.World.Edges)
            snapshot = RailwayFixtures.ReplaceCore(snapshot, edge: edge with { LengthMeters = 4 });
        return snapshot;
    }

    private static SceneFrame Present(WorldSceneSession session, SceneFrame? displayed = null) =>
        (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(Request, displayed)).Value;

    private static void AssertVehicleBody(SceneFrame frame)
    {
        var root = frame.Objects.Single(item => item.Subject == new WorldObjectId.Instance(new InstanceId.Vehicle(new(1))) && item.Parent is null);
        var ids = new HashSet<SceneObjectId> { root.Id };
        foreach (var item in frame.Objects) if (item.Parent is { } parent && ids.Contains(parent)) ids.Add(item.Id);
        Assert.IsTrue(frame.Objects.Where(item => ids.Contains(item.Id)).SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Any(),
            "The first switched frame must contain the built vehicle geometry, rather than a loading or unplaced identity.");
    }

    private static WorldSceneSession Open(SceneWorldSnapshot snapshot,
        Func<SceneWorldSnapshot, NodeId, NodeScene.Built?, CancellationToken, NodeScene.Built> generate,
        NodeScene.Cache? cache = null) => new(new(Guid.NewGuid()), snapshot, WorldSceneSession.Generate, new(), cache ?? new(), generate);
    private static NodeScene.Built Boundary(SceneWorldSnapshot snapshot, NodeId node, NodeScene.Built? previous,
        CancellationToken token)
    {
        var layout = NodeLayout.Build(snapshot, node, token);
        var entry = layout.Entries.Single();
        var boundary = RailwayTerrain.NodeBoundary(snapshot, new(snapshot, entry.Edge), node, entry, token);
        var geometry = WorldSceneSession.BuildGeometry(id =>
        {
            var grid = boundary.Grid; var root = id("");
            var pose = new ScenePose(RailwayCoordinates.Relative(boundary.Origin, default), Quaternion.Identity);
            return new([new(root, null, pose, null, DesignGeometry.Meshes(grid, false), [], [])], [new(root, pose, grid)], boundary.Problems);
        });
        return new(layout, ImmutableDictionary<string, NodeScene.Part>.Empty.Add("boundary", new("", geometry, boundary.Heights, boundary.Origin)), []);
    }
    private static void AssertHeight(ISceneFrame before, ISceneFrame after, Vector3 point)
    {
        Assert.IsInstanceOfType<Lookup<double>.Found>(before.TerrainHeight(point.X, point.Z));
        Assert.AreEqual(((Lookup<double>.Found)before.TerrainHeight(point.X, point.Z)).Value,
            ((Lookup<double>.Found)after.TerrainHeight(point.X, point.Z)).Value);
    }
}
