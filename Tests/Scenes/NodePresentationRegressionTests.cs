using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodePresentationRegressionTests
{
    [TestMethod]
    public async Task Moving_focus_after_near_completion_keeps_attached_objects_and_partial_readiness()
    {
        var snapshot = Snapshot(includeObjects: false);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = false;
        await using var vehicles = new SceneVehicles();
        await using var scene = new NodeScene(snapshot, NodeFixtures.NodeId, new(), Ids(), vehicles, new(), _ => null,
            buildChunk: (data, from, old, token) =>
            {
                if (block) { entered.TrySetResult(); release.Wait(token); }
                return Terrain(data, from, old, token);
            });
        try
        {
            await Settle(scene, snapshot, default);
            var first = Present(scene, snapshot);
            Assert.AreEqual(SceneReadiness.Ready, first.Readiness);
            Assert.IsTrue(first.Objects.Length > 0);
            block = true;
            var request = new SceneFrameRequest(1, new(first, new(160, 0, 0), 6656), SceneDebugLayer.None);
            var moved = scene.Present(snapshot, request, NodeFixtures.NextStamp(scene), first, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(SceneReadiness.Partial, moved.Readiness);
            Assert.IsTrue(moved.Objects.Any(item => first.Objects.Any(previous => previous.Id == item.Id)),
                "Moving into unbuilt terrain must keep the retained scene visible.");
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task New_nearby_train_body_keeps_an_already_presented_scene_partial()
    {
        var initial = Snapshot(includeObjects: false);
        var arriving = Snapshot(withTrain: true, includeObjects: false);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var vehicles = new SceneVehicles(build: (content, instance) =>
        {
            entered.TrySetResult();
            release.Wait();
            return RailwayVehicles.Build(content, instance);
        });
        await using var scene = new NodeScene(initial, NodeFixtures.NodeId, new(), Ids(), vehicles, new(), _ => null, buildChunk: (data, from, old, token) => Terrain(data, from, old, token));
        try
        {
            await Settle(scene, initial, default);
            var first = Present(scene, initial);
            Assert.AreEqual(SceneReadiness.Ready, first.Readiness);
            var next = scene.Present(arriving, new(1, null, SceneDebugLayer.None), NodeFixtures.NextStamp(scene), first, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(SceneReadiness.Partial, next.Readiness);
            Assert.IsTrue(next.Objects.Any(item => first.Objects.Any(previous => previous.Id == item.Id)));
            Assert.IsTrue(next.Update.Environment.FoggedRegions.Length > 0, "The pending train body must remain fogged.");
        }
        finally { release.Set(); }
    }

    [TestMethod]
    [DataRow("building")]
    [DataRow("parked")]
    [DataRow("rail")]
    public async Task Unselected_objects_over_built_terrain_are_fogged_and_not_pickable(string kind)
    {
        var snapshot = Snapshot();
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        SceneBounds bounds;
        if (kind == "rail")
            bounds = NodeScene.Prepare(snapshot, layout).Rails.Values.First().Bounds;
        else
        {
            await using var objects = new SettlementObjects(Ids());
            var fog = ImmutableArray.CreateBuilder<SceneFog>();
            var rendered = objects.Present(snapshot, layout, default, fog, _ => null);
            var id = kind == "building" ? (InstanceId)new InstanceId.Building(new(1)) : new InstanceId.Vehicle(new(2));
            var pose = rendered.Objects.First(item => item.Subject == new WorldObjectId.Instance(id)).Pose;
            var evaluation = ((ContentLookup<DesignEvaluation>.Found)snapshot.Content.FindDesign(
                ((Lookup<InstanceView>.Found)snapshot.Core.FindInstance(id)).Value.Design)).Value;
            var projection = DesignGeometry.Projection(evaluation);
            var local = kind == "building"
                ? new SceneBounds(new(projection.OccupiedBounds!.Minimum.X, projection.OccupiedBounds.Minimum.Y, projection.OccupiedBounds.Minimum.Z),
                    new(projection.OccupiedBounds.Maximum.X + 1, projection.OccupiedBounds.Maximum.Y + 1, projection.OccupiedBounds.Maximum.Z + 1))
                : SceneVehicles.VehicleBounds(projection, (InstanceDetails.Vehicle)((Lookup<InstanceView>.Found)snapshot.Core.FindInstance(id)).Value.Details);
            bounds = new SceneFog(local, Pose: pose).WorldBounds;
        }
        var center = (bounds.Minimum + bounds.Maximum) / 2;
        var options = new[]
        {
            (Point: new Vector3(bounds.Maximum.X - .01f, center.Y, center.Z), Focus: new ScenePoint(bounds.Maximum.X + NodeStreaming.PrefetchCells + .01, 0, center.Z)),
            (Point: new Vector3(bounds.Minimum.X + .01f, center.Y, center.Z), Focus: new ScenePoint(bounds.Minimum.X - NodeStreaming.PrefetchCells - .01, 0, center.Z))
        };
        var probe = options.First(option => new NodeRange(option.Focus, NodeStreaming.PrefetchCells).Intersects(Column(option.Point)));
        var range = new NodeRange(probe.Focus, NodeStreaming.PrefetchCells);
        Assert.IsFalse(range.Intersects(bounds), "The object must be outside the selection range.");
        await using var vehicles = new SceneVehicles();
        await using var scene = new NodeScene(snapshot, NodeFixtures.NodeId, new(), Ids(), vehicles, new(), _ => null,
            buildChunk: (data, from, old, token) => Terrain(data, from, old, token, probe.Point));
        await Settle(scene, snapshot, probe.Focus);
        var frame = Present(scene, snapshot);
        Assert.IsInstanceOfType<Lookup<double>.Found>(frame.TerrainHeight(probe.Point.X, probe.Point.Z),
            "The terrain beneath the omitted object is already built.");
        Assert.IsTrue(frame.Update.Environment.FoggedRegions.Any(region => Contains(region, probe.Point)),
            $"The unselected {kind} must not expose bare, interactive terrain.");
        Assert.IsInstanceOfType<ScenePick.NotReady>(frame.Pick(new(new(probe.Point.X, 100, probe.Point.Z), -Vector3.UnitY, 200)));
    }

    private static bool Contains(SceneBounds bounds, Vector3 point) => point.X >= bounds.Minimum.X && point.X <= bounds.Maximum.X &&
        point.Y >= bounds.Minimum.Y && point.Y <= bounds.Maximum.Y && point.Z >= bounds.Minimum.Z && point.Z <= bounds.Maximum.Z;

    private static GridColumn Column(Vector3 point) => new((int)Math.Floor(point.X / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize,
        (int)Math.Floor(point.Z / VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize);

    private static NodeScene.Part Terrain(NodeScene.Preparation data, GridColumn from, NodeScene.Part? old, CancellationToken token, Vector3? probe = null)
    {
        token.ThrowIfCancellationRequested();
        var position = probe is { } point && Column(point) == from ? new GridPosition((int)Math.Floor(point.X), -2, (int)Math.Floor(point.Z)) : new GridPosition(from.X, -2, from.Z);
        var geometry = WorldSceneSession.BuildGeometry(ids =>
        {
            var root = ids("");
            var grid = new VoxelGrid([new(position, new(VoxelOrientation.PxPy, SceneFixtures.Cube, null, null, new PickSubject.Terrain(NodeFixtures.Location), []))]);
            return new([new(root, null, SceneFixtures.Identity, null, DesignGeometry.Meshes(grid, false), [], [])], [new(root, SceneFixtures.Identity, grid)], []);
        });
        return new(data.ChunkInputs(from), geometry, ImmutableDictionary<GridColumn, double>.Empty.Add(new(position.X, position.Z), -1));
    }

    private static async Task Settle(NodeScene scene, SceneWorldSnapshot snapshot, ScenePoint focus)
    {
        await scene.BuildWork!;
        var state = NodeFixtures.Displays.GetValue(scene, static _ => new());
        SceneFrame? frame = null;
        int previousObjects;
        do
        {
            previousObjects = frame?.Objects.Length ?? -1;
            scene.Pump(focus: focus);
            await SceneFixtures.AwaitPendingWork(scene.PendingWork);
            frame = scene.Present(snapshot, new(1, null, SceneDebugLayer.None), NodeFixtures.NextStamp(scene), state.Displayed, default);
            if (frame.Readiness is SceneReadiness.Partial or SceneReadiness.Ready) state.Displayed = frame;
        } while (scene.HasPendingBuilds || frame.Readiness is SceneReadiness.Loading || frame.Objects.Length != previousObjects);
    }

    private static SceneFrame Present(NodeScene scene, SceneWorldSnapshot snapshot) =>
        // The caller requested a full replacement; internal settle receipts must not change its delta base.
        scene.Present(snapshot, new(1, null, SceneDebugLayer.None), NodeFixtures.NextStamp(scene), null, default);

    private static Func<string, SceneObjectId> Ids()
    {
        var ids = new Dictionary<string, SceneObjectId>();
        return key => ids.TryGetValue(key, out var id) ? id : ids[key] = new((ulong)ids.Count + 1);
    }

    private static SceneWorldSnapshot Snapshot(bool withTrain = false, bool includeObjects = true)
    {
        var template = NodeFixtures.Layout;
        if (!includeObjects) template = template with { Plots = [] };
        template = template with { SceneModel = template.SceneModel with { Scene = SceneFixtures.Projection([]) with
            { OccupiedBounds = new(new(-320, 0, -320), new(319, 0, 319)) } } };
        var station = NodeFixtures.StationEvaluation();
        station = station with { Scene = station.Scene with { OccupiedBounds = new(new(0, 0, -22), new(0, 0, 22)) } };
        var basis = NodeFixtures.Snapshot(platforms: includeObjects ? 2 : 0, trains: [], content: NodeFixtures.Content(layout: template,
            designs: new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [NodeFixtures.StationDesign] = new ContentLookup<DesignEvaluation>.Found(station) }));
        var node = basis.Core.World.Nodes[0];
        var settlement = includeObjects ? NodeFixtures.Settlement() with { ParkedVehicles = [new(2)] }
            : NodeFixtures.Settlement(0) with { Plots = [] };
        var train = NodeFixtures.Train(3);
        var instances = new List<InstanceView>();
        if (includeObjects) instances.AddRange([NodeFixtures.Station(2), RailwayFixtures.Instance(2) with { Location = NodeFixtures.Location }]);
        if (withTrain) instances.Add(RailwayFixtures.Instance(3, train: train.Id) with { Location = NodeFixtures.Location });
        return basis with { Core = new FixedCoreReadView
        {
            InputIdentity = basis.Core.InputIdentity, Position = basis.Core.Position, Revision = new(basis.Core.Position.CompletedSteps + basis.Core.Position.Sequence),
            World = basis.Core.World with { Nodes = [node], Edges = [], Trains = withTrain ? [new(train.Id, train.Position, train.Activity)] : [] },
            Trains = withTrain ? new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = new Lookup<TrainView>.Found(train) } : [],
            Nodes = new Dictionary<NodeId, Lookup<NodeView>> { [node.Id] = new Lookup<NodeView>.Found(new(node, [], 80, 0, 80)) },
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>> { [settlement.Id] = new Lookup<SettlementView>.Found(settlement) },
            Instances = instances.ToDictionary(instance => instance.Id, instance => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(instance))
        } };
    }
}
