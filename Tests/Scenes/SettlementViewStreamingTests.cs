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
public sealed class SettlementViewStreamingTests
{
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.None);

    [TestMethod]
    public async Task Location_open_includes_every_building_and_parked_vehicle_without_terrain_fog()
    {
        var snapshot = Snapshot();
        await using var vehicles = new SceneVehicles();
        await using var scene = new NodeScene(snapshot, NodeFixtures.NodeId, new(), SceneFixtures.NextId(), vehicles, new(), _ => null,
            buildChunk: SmallPart, buildRail: SmallRail);
        await scene.BuildWork!;
        var frame = await Settle(scene, snapshot, new WorldSceneTarget.Location(NodeFixtures.Location));

        var subjects = new List<WorldObjectId>
        {
            new WorldObjectId.Instance(new InstanceId.Building(new(1))),
            new WorldObjectId.Instance(new InstanceId.Building(new(3)))
        };
        subjects.AddRange(Enumerable.Range(2, SettlementObjects.ParkedVehicleLimit)
            .Select(id => new WorldObjectId.Instance(new InstanceId.Vehicle(new((ulong)id)))));
        foreach (var subject in subjects)
        {
            var item = frame.Objects.FirstOrDefault(item => item.Parent is null && item.Subject == subject);
            Assert.IsNotNull(item, $"{subject} must be in the Location frame.");
            Assert.IsFalse(item.Visuals.IsEmpty, $"{subject} must have built visuals.");
            Assert.IsFalse(frame.Update.Environment.FoggedRegions.Any(region => Contains(region, item.Pose.Position)),
                $"{subject} must not be covered by terrain fog.");
        }
    }

    [TestMethod]
    public async Task Follow_keeps_the_80_meter_chunk_set_on_a_settlement_template()
    {
        var snapshot = Snapshot();
        var cache = new NodeScene.Cache();
        await using var vehicles = new SceneVehicles();
        await using var scene = new NodeScene(snapshot, NodeFixtures.NodeId, cache, SceneFixtures.NextId(), vehicles, new(), _ => null,
            buildChunk: SmallPart, buildRail: SmallRail);
        await scene.BuildWork!;
        await Settle(scene, snapshot, new WorldSceneTarget.Follow(new(1), new(1)));

        var expected = new NodeRange(default, NodeStreaming.PrefetchCells).Chunks(scene.Layout!).ToArray();
        var actual = cache.Read(NodeFixtures.NodeId)!.Parts
            .Where(item => item.Key.StartsWith("terrain:", StringComparison.Ordinal)).Select(item => item.Key).ToArray();
        CollectionAssert.AreEquivalent(expected.Select(NodeScene.ChunkKey).ToArray(), actual);
    }

    private static bool Contains(SceneBounds bounds, Vector3 point) => point.X >= bounds.Minimum.X &&
        point.X <= bounds.Maximum.X && point.Y >= bounds.Minimum.Y && point.Y <= bounds.Maximum.Y &&
        point.Z >= bounds.Minimum.Z && point.Z <= bounds.Maximum.Z;

    private static SceneWorldSnapshot Snapshot()
    {
        var layout = NodeFixtures.Layout with
        {
            Plots =
            [
                new(1, PlotSize.SmallStation, new(448, 0, 0), VoxelOrientation.PxPy),
                new(2, PlotSize.Small, new(896, 0, 0), VoxelOrientation.PxPy)
            ]
        };
        var settlement = NodeFixtures.Settlement() with
        {
            Plots =
            [
                new(new(1), 1, PlotSize.SmallStation, [BuildingKind.Station], new PlotOccupant.Building(new(1))),
                new(new(2), 2, PlotSize.Small, [BuildingKind.Station], new PlotOccupant.Building(new(3)))
            ],
            ParkedVehicles = Enumerable.Range(2, SettlementObjects.ParkedVehicleLimit)
                .Select(id => new VehicleId((ulong)id)).ToImmutableArray()
        };
        var far = NodeFixtures.Station(2) with
        {
            Id = new InstanceId.Building(new(3)),
            Details = new InstanceDetails.Building(new(2), PlotSize.Small)
        };
        var instances = new List<InstanceView> { far };
        for (var id = 2; id <= SettlementObjects.ParkedVehicleLimit + 1; id++)
            instances.Add(RailwayFixtures.Instance((ulong)id) with { Location = NodeFixtures.Location });
        var station = NodeFixtures.StationEvaluation();
        station = station with { Scene = station.Scene with { OccupiedBounds = new(new(-1, 0, -24), new(1, 1, 24)) } };
        var content = NodeFixtures.Content(layout: layout,
            designs: new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [NodeFixtures.StationDesign] = new ContentLookup<DesignEvaluation>.Found(station) });
        var source = (FixedCoreReadView)NodeFixtures.Snapshot(trains: [], instances: [.. instances], content: content).Core;
        var node = source.World.Nodes[0];
        return new(new FixedCoreReadView
        {
            InputIdentity = source.InputIdentity, Position = source.Position,
            World = source.World with { Nodes = [node], Edges = [] },
            Nodes = new Dictionary<NodeId, Lookup<NodeView>> { [node.Id] = new Lookup<NodeView>.Found(new(node, [], 80, 96, 80)) },
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
            { [settlement.Id] = new Lookup<SettlementView>.Found(settlement) },
            Instances = instances.Append(NodeFixtures.Station(2)).ToDictionary(instance => instance.Id,
                instance => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(instance))
        }, content);
    }

    private static async Task<SceneFrame> Settle(NodeScene scene, SceneWorldSnapshot snapshot, WorldSceneTarget target)
    {
        var state = NodeFixtures.Displays.GetValue(scene, static _ => new());
        SceneFrame? frame = null;
        int previousObjects;
        do
        {
            previousObjects = frame?.Objects.Length ?? -1;
            frame = scene.Present(snapshot, Request, NodeFixtures.NextStamp(scene), state.Displayed, default, target);
            await SceneFixtures.AwaitPendingWork(scene.PendingWork);
            if (frame.Readiness is SceneReadiness.Partial or SceneReadiness.Ready) state.Displayed = frame;
        } while (scene.HasPendingBuilds || frame.Readiness is SceneReadiness.Loading || frame.Objects.Length != previousObjects);
        return frame;
    }

    private static NodeScene.Part SmallPart(NodeScene.Preparation data, GridColumn from, NodeScene.Part? old, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var geometry = WorldSceneSession.BuildGeometry(id =>
        {
            var root = id("");
            var grid = new VoxelGrid([new(new(from.X, 0, from.Z), new(VoxelOrientation.PxPy, SceneFixtures.Cube,
                null, null, new PickSubject.Terrain(NodeFixtures.Location), []))]);
            return new([new(root, null, SceneFixtures.Identity, null, DesignGeometry.Meshes(grid, false), [], [])],
                [new(root, SceneFixtures.Identity, grid)], []);
        });
        return new(data.ChunkInputs(from), geometry, ImmutableDictionary<GridColumn, double>.Empty.Add(from, 1));
    }

    private static NodeScene.Part SmallRail(NodeScene.Preparation data, NodeScene.RailSegment segment, NodeScene.Part? old,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var geometry = WorldSceneSession.BuildGeometry(id =>
            new([new(id(""), null, SceneFixtures.Identity, null, [], [], [])], [], []));
        return new(segment.Inputs, geometry, ImmutableDictionary<GridColumn, double>.Empty);
    }
}
