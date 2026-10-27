using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeSwitchTests
{
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.None);

    [TestMethod]
    public async Task Node_candidate_waits_for_a_displayed_ready_frame_in_the_current_railway_scope()
    {
        var snapshot = RailwayFixtures.Snapshot(content: RailwayFixtures.Content(design: RailwayFixtures.Vehicle()),
            trains: [RailwayFixtures.Train()], instances: [RailwayFixtures.Instance()]);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), snapshot, new(), new(), RailwayFixtures.EmptyNode);
        var follow = new WorldSceneTarget.Follow(new(1), new(1));
        session.RequestView(follow);
        var loading = Present(session, null);
        Assert.AreNotEqual(SceneReadiness.Ready, loading.Readiness);
        var ready = await ReadyWithReceipt(loading);
        Present(session, null);
        Assert.IsNull(session.PrebuiltLocation, "Computing Ready without a displayed receipt must not start a node candidate.");
        Present(session, ready);
        Assert.IsInstanceOfType<WorldLocation.Node>(session.PrebuiltLocation);

        session.RequestView(follow);
        var current = await ReadyWithReceipt(ready);
        Present(session, current);
        Assert.IsInstanceOfType<WorldLocation.Node>(session.PrebuiltLocation,
            "Only a Ready receipt from the new request and space may start its candidate.");

        async Task<SceneFrame> ReadyWithReceipt(SceneFrame receipt)
        {
            for (var attempt = 0; attempt < 1000; attempt++)
            {
                await SceneFixtures.AwaitPendingWork(session.PendingRailwayBuilds.Concat(session.PendingObjectBuilds).ToArray());
                var frame = Present(session, receipt);
                Assert.IsNull(session.PrebuiltLocation, "Loading or an old scope's Ready receipt must not start prebuilding.");
                if (frame.Readiness == SceneReadiness.Ready) return frame;
            }
            throw new AssertFailedException("The active railway did not reach Ready.");
        }
    }

    [TestMethod]
    [DataRow(0d, 8d, 0d)]
    [DataRow(20d, 1d, 10d)]
    [DataRow(20d, 8d, 80d)]
    [DataRow(80d, 8d, 320d)]
    [DataRow(200d, 8d, 320d)]
    public void Node_prefetch_lead_uses_mapped_speed_and_display_rate_without_exceeding_its_cap(
        double mappedSpeedCells, double rate, double expectedCells)
    {
        Assert.AreEqual(expectedCells, NodeStreaming.AdvanceCells(mappedSpeedCells, rate), 1e-9);
    }

    [TestMethod]
    public async Task Follow_candidate_prebuilds_the_finite_corridor_and_head_path_before_handover()
    {
        var source = NodeFixtures.Content(design: RailwayFixtures.Vehicle(lengthCells: 52),
            rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 });
        var layout = NodeFixtures.Layout with
        {
            Plots = [],
            SceneModel = NodeFixtures.Layout.SceneModel with
            {
                Scene = SceneFixtures.Projection([SceneFixtures.Cell(new(-96, 0, -96)), SceneFixtures.Cell(new(95, 0, 95), 2)]) with
                { OccupiedBounds = new(new(-320, 0, -320), new(319, 0, 319)) }
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
        var initial = NodeFixtures.Snapshot(platforms: 0, trains: [RailwayFixtures.Train(0, direction: TravelDirection.Reverse) with { LengthMeters = 26 }], content: content,
            tracks: 2, trackLimit: 2, platformLengthMeters: 0);
        var cache = new NodeScene.Cache();
        var observer = new RecordingStreamingObserver();
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), initial, new(), cache, observer: observer);
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        var displayed = await RailwayFixtures.Settled(session, Request);
        var active = observer.Snapshot().OfType<SceneStreamingEvent.Opened>()
            .Single(value => value.Role == SceneStreamingRole.Active);
        var candidate = observer.Snapshot().OfType<SceneStreamingEvent.Opened>()
            .Single(value => value.Role == SceneStreamingRole.Candidate);
        Assert.AreNotEqual(active.Scene, candidate.Scene);
        Assert.AreEqual(active.Scene, StreamingScenario.AssertFrameMatches(observer.Snapshot(), displayed).Scene);
        Assert.AreEqual(new WorldLocation.Node(NodeFixtures.NodeId), session.PrebuiltLocation);
        var expectedLayout = NodeLayout.Build(initial, NodeFixtures.NodeId);
        var inlet = expectedLayout.EntryFor(new(1));
        var entry = expectedLayout.TrainBoundary(inlet).Position;
        var train = ((Lookup<TrainView>.Found)initial.Core.FindTrain(new(1))).Value;
        var approach = expectedLayout.Approach(inlet, expectedLayout.Bypass);
        var route = NodeRailPath.Join(approach.Inlet, approach.Branch);
        var entryRange = new NodeRange(entry, NodeStreaming.PrefetchCells);
        var headRange = new NodeRange(route.Sample(26).Position, NodeStreaming.PrefetchCells);
        var corridor = new NodeCorridor(expectedLayout,
            ((Lookup<NodeView>.Found)initial.Core.FindNode(NodeFixtures.NodeId)).Value,
            new(NodeFixtures.NodeId, inlet.Edge, 1, null, null, NodeTravelPhase.Approaching, 0, null),
            train.LengthMeters * RailwayCoordinates.CellsPerMeter, []);
        var expectedTerrain = entryRange.Chunks(expectedLayout).Concat(headRange.Chunks(expectedLayout))
            .Concat(corridor.Chunks).Distinct().Select(NodeScene.ChunkKey).ToArray();
        var railSegments = NodeScene.Prepare(initial, expectedLayout).Rails;
        var expectedRails = railSegments.Values.Where(segment => entryRange.Intersects(segment.Bounds) ||
            headRange.Intersects(segment.Bounds) || corridor.Intersects(segment.Bounds)).Select(segment => segment.Key).ToArray();
        SceneFrameStamp? preparedCandidate = null;
        for (var attempt = 0; ; attempt++)
        {
            Assert.IsTrue(attempt < 1000, "The finite candidate corridor must finish after its preparation receipts.");
            await SceneFixtures.AwaitPendingWork(session.PendingPrebuilds);
            var update = ((SceneResult<SceneUpdate?>.Success)session.PresentCandidate(preparedCandidate)).Value;
            Assert.IsNotNull(update);
            preparedCandidate = update.Frame;
            displayed = Present(session, displayed);
            var parts = cache.Read(NodeFixtures.NodeId)?.Parts;
            if (parts is not null && expectedTerrain.All(parts.ContainsKey) && expectedRails.All(parts.ContainsKey)) break;
        }
        var near = new NodeRange(entry, NodeStreaming.NearCells);
        var expectedNear = new NodeRange(entry, NodeStreaming.PrefetchCells).Chunks(expectedLayout)
            .Where(near.Intersects).Select(NodeScene.ChunkKey).ToArray();
        var actualNear = cache.Read(NodeFixtures.NodeId)!.Parts.Keys.Where(key => key.StartsWith("terrain:", StringComparison.Ordinal)).ToArray();
        Assert.IsTrue(expectedNear.All(actualNear.Contains), "Prefetch from the head must keep the boundary near circle built.");
        Assert.IsTrue(actualNear.All(expectedTerrain.Contains),
            "Candidate terrain must stay inside the two prefetch circles and the finite path corridor.");
        Assert.IsTrue(corridor.Chunks.Any(from => !entryRange.Intersects(from) && !headRange.Intersects(from)),
            "This fixture must exercise corridor work beyond the old prefetch circles.");
        var front = route.Sample(26 + 64 * RailwayCoordinates.CellsPerMeter).Position;
        var forwardChunks = new NodeRange(front, 0).Chunks(expectedLayout).Select(NodeScene.ChunkKey).ToArray();
        Assert.IsTrue(forwardChunks.Length > 0 && Math.Pow(front.X - entry.X, 2) + Math.Pow(front.Z - entry.Z, 2) > NodeStreaming.NearCells * NodeStreaming.NearCells,
            "The first displayed path ahead must reach beyond the boundary near circle.");
        Assert.IsTrue(forwardChunks.All(actualNear.Contains), "The candidate must admit the first train head's 64m path before switching.");
        Assert.IsTrue(railSegments.Values.Where(segment => near.Intersects(segment.Bounds))
            .All(segment => cache.Read(NodeFixtures.NodeId)!.Parts.ContainsKey(segment.Key)));

        var loadingEnvelope = new NodeRange(route.Sample(26).Position,
            NodeStreaming.ViewCells + 2 * NodeStreaming.PathSampleCells);
        var deferredChunk = corridor.Chunks.First(from => !loadingEnvelope.Intersects(from) && !near.Intersects(from));
        var deferredKey = NodeScene.ChunkKey(deferredChunk);
        var fullCandidate = ((SceneResult<SceneUpdate?>.Success)session.PresentCandidate(null)).Value!;
        var candidateFrame = observer.Snapshot().OfType<SceneStreamingEvent.Frame>()
            .Single(value => value.Stamp == fullCandidate.Frame);
        Assert.IsTrue(candidateFrame.Parts.Any(part => part.Part.Key == deferredKey));
        var deferredIds = observer.Snapshot().OfType<SceneStreamingEvent.Changed>()
            .Last(value => value.Stage == SceneStreamingStage.Admitted && value.Attempt.Part.Key == deferredKey).Objects;
        Assert.IsTrue(deferredIds.Length > 0 && deferredIds.All(id => fullCandidate.Upserts.Any(item => item.Id == id)));
        Assert.AreSame(fullCandidate, ((SceneResult<SceneUpdate?>.Success)session.PresentCandidate(null)).Value);
        session.PresentCandidate(fullCandidate.Frame);
        Assert.AreEqual(1, observer.Snapshot().OfType<SceneStreamingEvent.Frame>().Count(value => value.Stamp == fullCandidate.Frame),
            "Returning a held offer or its unchanged acknowledgement must not duplicate a streaming Frame stamp.");
        var buildsBefore = observer.Snapshot().OfType<SceneStreamingEvent.Changed>()
            .Count(value => value.Stage == SceneStreamingStage.Started && value.Attempt.Part.Key == deferredKey);

        session.Observe(NodeFixtures.Snapshot(platforms: 0, trains: [train with
        { Position = new TrainPosition.Node(NodeFixtures.NodeId, inlet.Edge, 1, null, null, NodeTravelPhase.Approaching, 0, null) }],
            content: content, steps: 1, tracks: 2, trackLimit: 2, platformLengthMeters: 0), []);
        var first = Present(session, displayed);
        Assert.AreEqual(candidate.Scene, StreamingScenario.AssertFrameMatches(observer.Snapshot(), first).Scene);
        Assert.IsTrue(observer.Snapshot().OfType<SceneStreamingEvent.Activated>()
            .Any(value => value.Scene == candidate.Scene));
        Assert.AreNotEqual(SceneReadiness.Loading, first.Readiness,
            "The candidate must supply the near zone before handover; the train head may extend the forward demand.");
        Assert.IsFalse(deferredIds.Any(first.ContainsObject), "The first promoted frame must leave the far corridor queued.");
        Assert.IsFalse(StreamingScenario.AssertFrameMatches(observer.Snapshot(), first).Parts.Any(part => part.Part.Key == deferredKey));
        var settled = await RailwayFixtures.Settled(session, Request, first);
        Assert.IsTrue(deferredIds.All(settled.ContainsObject), "Later admitted batches must keep the exact candidate object identities.");
        Assert.AreEqual(buildsBefore, observer.Snapshot().OfType<SceneStreamingEvent.Changed>()
            .Count(value => value.Stage == SceneStreamingStage.Started && value.Attempt.Part.Key == deferredKey),
            "Moving a prebuilt part back to the admission queue must not build it again.");
        var retained = cache.Read(NodeFixtures.NodeId)!;
        var expected = new NodeRange(entry, NodeStreaming.PrefetchCells).Chunks(expectedLayout)
            .Select(NodeScene.ChunkKey).ToArray();
        var terrain = retained.Parts.Keys.Where(key => key.StartsWith("terrain:", StringComparison.Ordinal)).ToArray();
        Assert.IsTrue(expected.All(terrain.Contains));
        Assert.IsTrue(corridor.Chunks.Select(NodeScene.ChunkKey).All(terrain.Contains),
            "The promoted node must keep its prebuilt finite corridor, including parts outside the handover circles.");
        Assert.IsTrue(railSegments.Values.Where(segment => entryRange.Intersects(segment.Bounds))
            .All(segment => retained.Parts.ContainsKey(segment.Key)));
        Assert.IsTrue(expectedRails.All(retained.Parts.ContainsKey));
        var wholeCount = (expectedLayout.Bounds.Maximum.X - expectedLayout.Bounds.Minimum.X) / VoxelGrid.ChunkSize *
            (expectedLayout.Bounds.Maximum.Y - expectedLayout.Bounds.Minimum.Y) / VoxelGrid.ChunkSize;
        Assert.IsTrue(terrain.Length < wholeCount, "The corridor must leave distant node chunks unbuilt.");
        await session.DisposeAsync();
        var closed = observer.Snapshot().OfType<SceneStreamingEvent.Closed>().Select(value => value.Scene).ToArray();
        Assert.IsTrue(closed.Contains(active.Scene));
        Assert.IsTrue(closed.Contains(candidate.Scene));
    }

    [TestMethod]
    [DataRow(false, 1)]
    [DataRow(true, 1)]
    [DataRow(false, 2)]
    [DataRow(true, 2)]
    public async Task ActualEntryAndReturnTracksKeepVehiclePoseWithoutInterpolatingAcrossSpaces(bool reverse, int track)
    {
        var endpoint = reverse ? new NodeId(1) : new NodeId(2);
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle(),
            rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 });
        var onEdge = RailwayFixtures.Train(reverse ? 0 : 4, track: track,
            direction: reverse ? TravelDirection.Reverse : TravelDirection.Forward);
        SceneWorldSnapshot Snapshot(TrainView train, ulong step) => RailwayFixtures.Snapshot(4, 4, 0, tracks: track,
            steps: step, content: content, trains: [train], instances: [RailwayFixtures.Instance() with
            {
                Location = train.Position is TrainPosition.Node ? new WorldLocation.Node(endpoint) :
                    new WorldLocation.Edge(RailwayFixtures.Edge, ((TrainPosition.Edge)train.Position).DistanceMeters)
            }]);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), Snapshot(onEdge, 0),
            new(), new());
        session.RequestView(new WorldSceneTarget.Follow(onEdge.Id, onEdge.Vehicles[0]));
        var line = await RailwayFixtures.Settled(session, Request);
        var subject = new WorldObjectId.Instance(new InstanceId.Vehicle(onEdge.Vehicles[0]));
        var before = ((Lookup<ScenePose>.Found)line.FindPose(subject)).Value;
        var entering = onEdge with
        {
            Position = new TrainPosition.Node(endpoint, RailwayFixtures.Edge, track, null, null, NodeTravelPhase.Approaching, 0, null)
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

        var departing = entering with
        {
            Position = new TrainPosition.Node(endpoint, RailwayFixtures.Edge, track, RailwayFixtures.Edge, 1,
                NodeTravelPhase.Departing, 160, null), OrderFacing = TrainOrderFacing.LastEndLeading
        };
        session.Observe(Snapshot(departing, 2), []);
        var atExit = await RailwayFixtures.Settled(session, Request, node);
        var beforeExit = ((Lookup<ScenePose>.Found)atExit.FindPose(subject)).Value;
        var returned = departing with { Position = new TrainPosition.Edge(RailwayFixtures.Edge, 1,
            reverse ? TravelDirection.Forward : TravelDirection.Reverse, reverse ? 0 : 4) };
        session.Observe(Snapshot(returned, 3), []);
        var firstLine = (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(Request with { Interpolation = 0 }, atExit)).Value;
        var afterExit = ((Lookup<ScenePose>.Found)firstLine.FindPose(subject)).Value;
        Assert.IsTrue(Vector3.Distance(beforeExit.Position, afterExit.Position) < .001);
        Assert.IsTrue(Math.Abs(Quaternion.Dot(beforeExit.Rotation, afterExit.Rotation)) > .99999);
        var settledLine = await RailwayFixtures.Settled(session, Request, firstLine);
        Assert.AreEqual(afterExit, ((Lookup<ScenePose>.Found)settledLine.FindPose(subject)).Value);
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
            new(), cache);
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
        Assert.AreNotEqual(lineA.Update.Frame.Space, firstNode.Update.Frame.Space);
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
        await SceneFixtures.AwaitPendingWork(session.PendingPrebuilds);
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
        Assert.AreNotEqual(servicing.Update.Frame.Space, firstLineB.Update.Frame.Space);
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
        long now = 0;
        var displayRate = new DisplayRate { Timestamp = () => now };
        await using var scene = RailwayFixtures.Controlled(snapshot, vehicles, displayRate: displayRate);
        await RailwayFixtures.Settled(scene, snapshot, new WorldSceneTarget.Location(RailwayFixtures.Location), Request,
            new(new(Guid.NewGuid()), new(1), new(1), 1));
        Assert.IsNull(scene.FrontNode(snapshot, RailwayFixtures.Train(735, speed: 10)));
        Assert.AreEqual(new WorldLocation.Node(new(2)), scene.FrontNode(snapshot, RailwayFixtures.Train(736, speed: 10)));
        Assert.IsNull(scene.FrontNode(snapshot, RailwayFixtures.Train(65, direction: TravelDirection.Reverse, speed: 10)));
        Assert.AreEqual(new WorldLocation.Node(new(1)), scene.FrontNode(snapshot, RailwayFixtures.Train(64, direction: TravelDirection.Reverse, speed: 10)));
        Assert.AreEqual(3, RailwayScene.PrefetchSegments(10, 4));
        Assert.AreEqual(4, RailwayScene.PrefetchSegments(100, 8));
        Assert.AreEqual(0, RailwayScene.PrefetchSegments(0, 8));
        now = Stopwatch.Frequency;
        scene.Present(RailwayFixtures.Snapshot(800, 800, 0, steps: 240, content: content), null,
            new WorldSceneTarget.Location(RailwayFixtures.Location), Request, new(new(Guid.NewGuid()), new(1), new(1), 2), null);
        Assert.IsNull(scene.FrontNode(snapshot, RailwayFixtures.Train(607, speed: 10)));
        Assert.AreEqual(new WorldLocation.Node(new(2)), scene.FrontNode(snapshot, RailwayFixtures.Train(608, speed: 10)));
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
                Position = core.Position, Revision = new(core.Position.CompletedSteps + core.Position.Sequence), InputIdentity = core.InputIdentity, World = core.World with { Nodes = nodes },
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
            await SceneFixtures.AwaitPendingWork(session.PendingPrebuilds);
        }
        var oldPath = line.Railway!;
        var arc = reverse ? 0 : oldPath.LengthCells;
        var physical = oldPath.Sample(arc, 17).Position;
        var display = RailwayCoordinates.Relative(physical, line.Origin);
        var hit = (ScenePick.Hit)line.Pick(new(display + new Vector3(0.5f, 20, 0.5f), -Vector3.UnitY, 50));
        var atNode = onEdge with { Position = new TrainPosition.Node(endpoint, RailwayFixtures.Edge, 2, RailwayFixtures.Edge, activity == TrainActivity.Running ? 1 : null,
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
        Assert.AreNotEqual(line.Update.Frame.Space, node.Update.Frame.Space);
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
            node: layout, nodeHeights: NodeHeights.Empty.WithPart((0, 0, 0, "boundary"), new("", empty, boundary.Heights, boundary.Origin)));
        var endpoint = path.Sample(reverse ? 0 : path.LengthCells).Position;
        var point = RailwayCoordinates.Relative(new(Math.Floor(endpoint.X) + 40.5, 0, Math.Floor(endpoint.Z) + .5), line.Origin);
        AssertHeight(line, node, point);
        Assert.AreNotEqual(-RailwayCoordinates.TerrainBaselineOffset + 1,
            ((Lookup<double>.Found)line.TerrainHeight(point.X, point.Z)).Value + line.Origin.Y);
    }

    [TestMethod]
    public async Task KnownFrontNodeStartsAtZeroSpeedAndUnknownNextEdgeDoesNotGuessAnOutgoingRoute()
    {
        var content = NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 });
        SceneWorldSnapshot Snapshot(double distance, double speed, ulong step) => RailwayFixtures.Snapshot(4, 4, 0,
            content: content, steps: step, trains: [RailwayFixtures.Train(distance, speed: speed)], instances: [RailwayFixtures.Instance()]);
        await using var session = Open(Snapshot(0, 0, 0), RailwayFixtures.EmptyNode);
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        await RailwayFixtures.Settled(session, Request);
        Assert.AreEqual(new WorldLocation.Node(new(2)), session.PrebuiltLocation);
        session.Observe(Snapshot(3, 10, 1), []);
        await RailwayFixtures.Settled(session, Request);
        Assert.AreEqual(new WorldLocation.Node(new(2)), session.PrebuiltLocation);
        await SceneFixtures.AwaitPendingWork(session.PendingPrebuilds);
        var atNode = RailwayFixtures.Train() with { Position = new TrainPosition.Node(new(2), RailwayFixtures.Edge, 1, null, null, NodeTravelPhase.Approaching, 0, null) };
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
    public async Task CandidateSurvivesStoppingAndDirectionChangePreparesTheNewFrontNode()
    {
        var calls = 0;
        var content = NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 });
        SceneWorldSnapshot Snapshot(double speed, TravelDirection direction, ulong step) => RailwayFixtures.Snapshot(4, 4, 0,
            content: content, steps: step, trains: [RailwayFixtures.Train(3, speed: speed, direction: direction)],
            instances: [RailwayFixtures.Instance()]);
        await using var session = Open(Snapshot(10, TravelDirection.Forward, 0), (snapshot, node, previous, token) =>
        {
            Interlocked.Increment(ref calls);
            return RailwayFixtures.EmptyNode(snapshot, node, previous, token);
        });
        session.Timestamp = () => 0;
        session.RequestView(new WorldSceneTarget.Follow(new(1), new(1)));
        var displayed = await RailwayFixtures.Settled(session, Request);
        await SceneFixtures.AwaitPendingWork(session.PendingPrebuilds);
        Assert.AreEqual(new WorldLocation.Node(new(2)), session.PrebuiltLocation);
        Assert.AreEqual(1, calls);
        session.Observe(Snapshot(0, TravelDirection.Forward, 1), []);
        displayed = Present(session, displayed);
        Assert.AreEqual(new WorldLocation.Node(new(2)), session.PrebuiltLocation,
            "Zero speed does not change the known front node.");
        Assert.AreEqual(1, calls);
        session.Observe(Snapshot(0, TravelDirection.Reverse, 2), []);
        Present(session, displayed);
        await SceneFixtures.AwaitPendingWork(session.PendingPrebuilds);
        await RailwayFixtures.Settled(session, Request, displayed);
        Assert.AreEqual(new WorldLocation.Node(new(1)), session.PrebuiltLocation,
            "Reversal must replace the old destination with the newly known front node even at zero speed.");
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
        await SceneFixtures.AwaitPendingWork(session.PendingPrebuilds);
        session.Observe(ShortNodeSnapshot(train with { Position = ((TrainPosition.Node)train.Position) with { NextEdge = new(1) } }, content, 2), []);
        await RailwayFixtures.Settled(session, Request);
        await SceneFixtures.AwaitPendingWork(session.PendingPrebuilds);
        Present(session);
        Assert.AreEqual(new WorldLocation.Edge(new(1), 0), session.PrebuiltLocation);
        session.Observe(ShortNodeSnapshot(train with { Position = ((TrainPosition.Node)train.Position) with { NextEdge = null } }, content, 3), []);
        Present(session);
        Assert.IsNull(session.PrebuiltLocation, "Removing the chosen route invalidates an already created candidate.");
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
            var inNode = train with { Position = new TrainPosition.Node(new(2), RailwayFixtures.Edge, 1, null, null, NodeTravelPhase.Approaching, 0, null) };
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
            await SceneFixtures.AwaitPendingWork(session.PendingPrebuilds);
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
        Func<SceneWorldSnapshot, NodeId, NodeScene.Cached?, CancellationToken, NodeScene.Built> generate,
        NodeScene.Cache? cache = null) => new(new(Guid.NewGuid()), snapshot, new(), cache ?? new(), generate);
    private static NodeScene.Built Boundary(SceneWorldSnapshot snapshot, NodeId node, NodeScene.Cached? previous,
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
