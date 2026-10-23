using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeCorridorTests
{
    [TestMethod]
    public void Straight_corridor_includes_the_32m_band_without_filling_the_node()
    {
        var template = NodeFixtures.Layout with { Plots = [], SceneModel = NodeFixtures.Layout.SceneModel with
        { Scene = SceneFixtures.Projection([]) with { OccupiedBounds = new(new(-640, 0, -640), new(639, 0, 639)) } } };
        var snapshot = NodeFixtures.EmptyNodeSnapshot(template, [], travelMeters: 240);
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var position = new TrainPosition.Node(layout.Node, null, null, null, null, NodeTravelPhase.Approaching, 0, null);
        var corridor = new NodeCorridor(layout, Dimensions(snapshot), position, 0, []);
        var path = new NodeTrainPath(layout, Dimensions(snapshot), position, layout.Bypass, 0, null);
        var middle = path.Sample(path.Length / 2);
        AssertCovered(corridor, RailwayCoordinates.Add(middle.Position, middle.Right * 63));
        Assert.IsFalse(corridor.Chunks.Contains(Column(RailwayCoordinates.Add(middle.Position, middle.Right * 97))),
            "A part wholly outside the 32m band must not enter the corridor.");
        AssertCovered(corridor, layout, path, 0, path.Length);
    }

    [TestMethod]
    public void Train_without_entry_history_in_an_empty_dead_end_covers_the_existing_fallback_route()
    {
        var snapshot = RailwayFixtures.Snapshot(4, 4, 0);
        var layout = NodeLayout.Build(snapshot, new(1));
        var dimensions = ((Lookup<NodeView>.Found)snapshot.Core.FindNode(new(1))).Value;
        var position = new TrainPosition.Node(new(1), null, null, null, null, NodeTravelPhase.Docked, 0, null);
        var corridor = new NodeCorridor(layout, dimensions, position, 104, []);
        var path = new NodeTrainPath(layout, dimensions, position, layout.Bypass, 104, null);
        AssertCovered(corridor, layout, path, path.Arc(0), path.Length);
    }

    [TestMethod]
    public void Unknown_choices_cover_the_finite_union_and_known_choices_narrow_it()
    {
        var snapshot = NodeFixtures.Snapshot(tracks: 2, sameSideEntry: true);
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var dimensions = Dimensions(snapshot);
        var position = new TrainPosition.Node(layout.Node, new(1), 1, null, null,
            NodeTravelPhase.Approaching, 0, null);
        var unknown = new NodeCorridor(layout, dimensions, position, 104, layout.Stops);
        var expected = new HashSet<GridColumn>();
        foreach (var exit in layout.Entries)
        foreach (var track in Enumerable.Range(1, layout.EntryTracks[exit.Edge]))
        {
            for (var number = 1; number <= layout.Stops.Length; number++)
            {
                var known = new NodeCorridor(layout, dimensions,
                    position with { PlatformNumber = number, NextEdge = exit.Edge, NextTrack = track }, 104, layout.Stops);
                Assert.IsTrue(known.Chunks.IsProperSubsetOf(unknown.Chunks),
                    "A known platform and exit must not prebuild unrelated alternatives.");
                expected.UnionWith(known.Chunks);
            }
            expected.UnionWith(new NodeCorridor(layout, dimensions,
                position with { NextEdge = exit.Edge, NextTrack = track }, 104, []).Chunks);
        }
        Assert.IsTrue(expected.SetEquals(unknown.Chunks),
            "Unknown choices must cover exactly the available platform, bypass and exit alternatives.");
    }

    [TestMethod]
    public void A_train_without_an_entered_edge_covers_the_turn_from_W_to_its_same_side_exit()
    {
        var snapshot = NodeFixtures.Snapshot(sameSideEntry: true);
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var dimensions = Dimensions(snapshot);
        var entry = layout.TrainEntry(null)!;
        var position = new TrainPosition.Node(layout.Node, null, null, entry.Edge, 1,
            NodeTravelPhase.Waiting, dimensions.ApproachLengthMeters, 1);
        var stop = layout.Stops[0];
        var path = new NodeTrainPath(layout, dimensions, position, stop, 104, null);
        var corridor = new NodeCorridor(layout, dimensions, position, 104, layout.Stops);
        Assert.IsFalse(path.Returning);
        AssertCovered(corridor, layout, path, path.Arc(position.DistanceMeters), path.StopArc);
        AssertCovered(corridor, layout, path, path.StopArc, path.Length);
        var turn = layout.TurnExit(-entry.Side, stop, entry, 1);
        for (var arc = 0.0; arc <= turn.Length; arc += 8)
            AssertCovered(corridor, turn.Sample(arc).Position, layout);
    }

    [TestMethod]
    public void Returning_corridor_covers_the_inbound_and_reversed_outbound_sections()
    {
        var snapshot = NodeFixtures.Snapshot();
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var dimensions = Dimensions(snapshot);
        var position = new TrainPosition.Node(layout.Node, new(1), 1, new(1), 1,
            NodeTravelPhase.Waiting, dimensions.ApproachLengthMeters, 1);
        var inbound = new NodeTrainPath(layout, dimensions, position, layout.Stops[0], 104, null);
        var outbound = new NodeTrainPath(layout, dimensions,
            position with { Phase = NodeTravelPhase.Departing }, layout.Stops[0], 104, null);
        var corridor = new NodeCorridor(layout, dimensions, position, 104, layout.Stops);
        Assert.IsTrue(outbound.Returning);
        AssertCovered(corridor, layout, inbound, inbound.Arc(position.DistanceMeters), inbound.StopArc);
        AssertCovered(corridor, layout, outbound, outbound.StopArc, outbound.Length);
    }

    [TestMethod]
    public async Task Active_remaining_corridor_builds_and_retains_parts_beyond_the_prefetch_circle()
    {
        var layout = NodeFixtures.Layout with { Plots = [], SceneModel = NodeFixtures.Layout.SceneModel with
        { Scene = SceneFixtures.Projection([]) with { OccupiedBounds = new(new(-640, 0, -640), new(639, 0, 639)) } } };
        var train = NodeFixtures.Train() with
        { Position = new TrainPosition.Node(NodeFixtures.NodeId, null, null, null, null, NodeTravelPhase.Approaching, 0, null) };
        var snapshot = NodeFixtures.EmptyNodeSnapshot(layout, [train], travelMeters: 240);
        var cache = new NodeScene.Cache();
        await using var vehicles = new SceneVehicles();
        ulong next = 0;
        var ids = new Dictionary<string, SceneObjectId>();
        await using var scene = new NodeScene(snapshot, NodeFixtures.NodeId, cache, key =>
        {
            if (!ids.TryGetValue(key, out var id)) ids[key] = id = new(++next);
            return id;
        }, vehicles, new(), _ => null, buildChunk: (data, from, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            return new(data.ChunkInputs(from), WorldSceneSession.BuildGeometry(id =>
                new([new(id(""), null, SceneFixtures.Identity, null, [], [], [])], [], [])),
                ImmutableDictionary<GridColumn, double>.Empty.Add(from, 1));
        });
        await scene.BuildWork!;
        SceneFrame? displayed = null;
        var target = new WorldSceneTarget.Follow(train.Id, new(1));
        do
        {
            displayed = scene.Present(snapshot, new(1, null, SceneDebugLayer.None),
                NodeFixtures.NextStamp(scene), displayed, default, target);
            await SceneFixtures.AwaitPendingWork(scene.PendingWork);
        } while (scene.HasPendingBuilds);
        var path = new NodeTrainPath(scene.Layout!, Dimensions(snapshot), (TrainPosition.Node)train.Position,
            scene.Layout!.Bypass, train.LengthMeters * RailwayCoordinates.CellsPerMeter, null);
        var far = Column(path.Sample(path.Length - 16).Position);
        Assert.IsFalse(new NodeRange(scene.StreamingFocus, NodeStreaming.PrefetchCells).Intersects(far));
        var key = NodeScene.ChunkKey(far);
        Assert.IsFalse(new NodeRange(scene.StreamingFocus, NodeStreaming.RetainCells).Intersects(far));
        // Completed parts still need ordinary displayed receipts before entering the frame.
        for (var attempt = 0; !cache.Read(NodeFixtures.NodeId)!.Parts.ContainsKey(key); attempt++)
        {
            Assert.IsTrue(attempt < 1000, "The finite completed corridor must eventually be admitted.");
            displayed = scene.Present(snapshot, new(1, null, SceneDebugLayer.None),
                NodeFixtures.NextStamp(scene), displayed, default, target);
        }
        var retained = cache.Read(NodeFixtures.NodeId)!.Parts[key];
        scene.Pump(followedTrain: train with
        { Position = ((TrainPosition.Node)train.Position) with { DistanceMeters = 120 } });
        Assert.IsFalse(new NodeRange(scene.StreamingFocus, NodeStreaming.RetainCells).Intersects(far));
        Assert.AreSame(retained, cache.Read(NodeFixtures.NodeId)!.Parts[key],
            "The active node corridor must retain its far exit even outside the focus retention circle.");
    }

    private static NodeView Dimensions(SceneWorldSnapshot snapshot) =>
        ((Lookup<NodeView>.Found)snapshot.Core.FindNode(NodeFixtures.NodeId)).Value;

    private static GridColumn Column(ScenePoint point) => new(
        (int)Math.Floor(point.X / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize,
        (int)Math.Floor(point.Z / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize);

    private static void AssertCovered(NodeCorridor corridor, ScenePoint point, NodeLayout? layout = null)
    {
        // The node's maximum edge belongs to its last interior chunk, not a chunk outside its bounds.
        var interior = layout is null ? point : point with
        {
            X = point.X == layout.Bounds.Maximum.X ? Math.BitDecrement(point.X) : point.X,
            Z = point.Z == layout.Bounds.Maximum.Y ? Math.BitDecrement(point.Z) : point.Z
        };
        Assert.IsTrue(corridor.Chunks.Contains(Column(interior)), $"The route point {point} must be covered.");
    }

    private static void AssertCovered(NodeCorridor corridor, NodeLayout layout, NodeTrainPath path, double begin, double end)
    {
        for (var arc = begin; arc <= end; arc += 8) AssertCovered(corridor, path.Sample(arc).Position, layout);
        AssertCovered(corridor, path.Sample(end).Position, layout);
    }
}
