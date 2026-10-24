using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeCorridorInvalidationTests
{
    [TestMethod]
    public async Task New_read_views_and_unchanged_invalidated_stops_do_not_restart_corridor_work()
    {
        var content = Content();
        var initial = Snapshot(content, secondPlatform: false);
        var train = Train();
        var cache = new NodeScene.Cache();
        var started = new ConcurrentQueue<GridColumn>();
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(initial, cache, vehicles, started);
        await Settle(scene, initial, train);
        var revision = scene.StreamingPlanRevision;
        var published = cache.Read(NodeFixtures.NodeId);
        var dispatched = started.Count;
        for (ulong step = 1; step <= 3; step++)
            scene.Pump(Snapshot(content, secondPlatform: false, steps: step), enteringTrain: train);
        scene.InvalidatePlatforms();
        scene.Pump(Snapshot(content, secondPlatform: false, steps: 4), enteringTrain: train);
        Assert.AreEqual(revision, scene.StreamingPlanRevision,
            "A fresh core projection or invalidation with unchanged actual stops must preserve the corridor plan.");
        Assert.AreEqual(dispatched, started.Count, "Stable corridor inputs must not dispatch terrain again.");
        Assert.AreSame(published, cache.Read(NodeFixtures.NodeId));
    }

    [TestMethod]
    public async Task Swapping_live_station_parts_with_the_same_platform_count_dispatches_the_new_corridor_chunks()
    {
        var content = Content();
        var initial = Snapshot(content, secondPlatform: false);
        var changed = Snapshot(content, secondPlatform: true, steps: 1);
        var train = Train();
        var cache = new NodeScene.Cache();
        var started = new ConcurrentQueue<GridColumn>();
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(initial, cache, vehicles, started);
        await Settle(scene, initial, train);
        var layout = scene.Layout!;
        var before = Corridor(initial, layout, train);
        var after = Corridor(changed, layout, train);
        var built = Terrain(cache);
        var required = after.Chunks.Except(before.Chunks).Where(chunk => !built.Contains(chunk)).ToArray();
        Assert.IsTrue(required.Length > 0, "The swapped live platform must require corridor chunks beyond the initial entry circles.");
        var revision = scene.StreamingPlanRevision;
        var alreadyStarted = started.ToHashSet();
        scene.InvalidatePlatforms();
        await Settle(scene, changed, train);
        Assert.IsGreaterThan(revision, scene.StreamingPlanRevision);
        var dispatched = started.Where(chunk => !alreadyStarted.Contains(chunk)).ToHashSet();
        Assert.IsTrue(required.All(dispatched.Contains), "Changing stop positions at constant platform count must dispatch the newly required corridor terrain.");
        Assert.IsTrue(required.All(Terrain(cache).Contains), "New corridor terrain must be admitted into the published candidate, not merely scheduled.");
    }

    private static TrainView Train() => RailwayFixtures.Train(direction: TravelDirection.Reverse, speed: 1);

    private static IContentView Content() => NodeFixtures.Content(rules: RailwayFixtures.Rules with
    { MinimumCurveRadiusMeters = 1 }, layout: NodeFixtures.Layout with
    {
        SceneModel = NodeFixtures.Layout.SceneModel with
        { Scene = SceneFixtures.Projection([]) with { OccupiedBounds = new(new(-192, 0, -192), new(191, 0, 191)) } }
    });

    private static SceneWorldSnapshot Snapshot(IContentView content, bool secondPlatform, ulong steps = 0)
    {
        var train = Train();
        var basis = NodeFixtures.Snapshot(platforms: 1, trains: [train], content: content, steps: steps);
        var edges = basis.Core.World.Edges.Where(edge => edge.Id == new EdgeId(1)).ToImmutableArray();
        var station = NodeFixtures.Station(1);
        if (secondPlatform) station = station with { Parts = station.Parts.Select(part => part with
        { Destroyed = part.Part.Value == 1, Durability = new(part.Part.Value == 1 ? 0 : 100, 100) }).ToImmutableArray() };
        return basis with { Core = new FixedCoreReadView
        {
            Revision = new(steps), Position = basis.Core.Position, InputIdentity = basis.Core.InputIdentity,
            World = basis.Core.World with { Edges = edges },
            Nodes = basis.Core.World.Nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(
                ((Lookup<NodeView>.Found)basis.Core.FindNode(node.Id)).Value with
                { AdjacentEdges = edges.Where(edge => edge.Start == node.Id || edge.End == node.Id).Select(edge => edge.Id).ToImmutableArray() })),
            Edges = edges.ToDictionary(edge => edge.Id, edge => basis.Core.FindEdge(edge.Id)),
            Trains = new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = new Lookup<TrainView>.Found(train) },
            Instances = train.Vehicles.ToDictionary(id => (InstanceId)new InstanceId.Vehicle(id), id => basis.Core.FindInstance(new InstanceId.Vehicle(id)))
                .Append(new(station.Id, new Lookup<InstanceView>.Found(station))).ToDictionary(pair => pair.Key, pair => pair.Value),
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>> { [new(1)] = basis.Core.FindSettlement(new(1)) }
        } };
    }

    private static NodeCorridor Corridor(SceneWorldSnapshot snapshot, NodeLayout layout, TrainView train)
    {
        var station = ((Lookup<InstanceView>.Found)snapshot.Core.FindInstance(new InstanceId.Building(new(1)))).Value;
        var stop = layout.Stops.Single(stop => stop.PlotNumber == 1 &&
            stop.LocalZ == (station.Parts[0].Destroyed ? 40 : -40));
        return new(layout, ((Lookup<NodeView>.Found)snapshot.Core.FindNode(NodeFixtures.NodeId)).Value,
            new(NodeFixtures.NodeId, new(1), 1, null, null, NodeTravelPhase.Approaching, 0, null),
            train.LengthMeters * RailwayCoordinates.CellsPerMeter, [stop]);
    }

    private static NodeScene Open(SceneWorldSnapshot snapshot, NodeScene.Cache cache, SceneVehicles vehicles,
        ConcurrentQueue<GridColumn> started)
    {
        ulong next = 0;
        var ids = new Dictionary<string, SceneObjectId>();
        var geometry = WorldSceneSession.BuildGeometry(id => new([new(id(""), null, SceneFixtures.Identity, null, [], [], [])], [], []));
        return new(snapshot, NodeFixtures.NodeId, cache, key =>
        {
            if (!ids.TryGetValue(key, out var id)) ids[key] = id = new(++next);
            return id;
        }, vehicles, new(), _ => null, buildChunk: (data, from, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            started.Enqueue(from);
            return new(data.ChunkInputs(from), geometry, ImmutableDictionary<GridColumn, double>.Empty.Add(from, 1));
        }, buildRail: (_, segment, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            return new(segment.Inputs, geometry, ImmutableDictionary<GridColumn, double>.Empty);
        });
    }

    private static HashSet<GridColumn> Terrain(NodeScene.Cache cache) => cache.Read(NodeFixtures.NodeId)!.Parts.Keys
        .Where(key => key.StartsWith("terrain:", StringComparison.Ordinal)).Select(key => key.Split(':'))
        .Select(parts => new GridColumn(int.Parse(parts[1]), int.Parse(parts[2]))).ToHashSet();

    private static async Task Settle(NodeScene scene, SceneWorldSnapshot snapshot, TrainView train)
    {
        await scene.BuildWork!;
        do
        {
            scene.Pump(snapshot, enteringTrain: train);
            await SceneFixtures.AwaitPendingWork(scene.PendingWork);
        } while (scene.HasPendingBuilds);
        scene.Pump(snapshot, enteringTrain: train);
        Assert.IsFalse(scene.Failed);
    }
}
