using System.Collections.Concurrent;
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
public sealed class RailwayBreakTests
{
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.None);
    private static readonly SceneSessionId SessionId = new(Guid.NewGuid());
    private static SceneFrameStamp Stamp(ulong revision) => new(SessionId, new(1), new(1), revision);
    private static readonly WorldSceneTarget Middle = new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, 64));

    [TestMethod]
    public async Task BreakWithinFourMetersOfAnEdgeEndStopsAtTheEdgeAndLeavesTheNodeUnchanged()
    {
        var path = Path([new TrackBreakView(new(1), 1, 2)], [Repair(new(1), 1, new(1))]);
        var meshes = RailwayMeshes.Build(path, 0, default, _ => 4);
        var points = meshes.SelectMany(mesh => mesh.Vertices).Select(vertex => vertex.Position).ToArray();
        Assert.IsTrue(points.All(point => Along(path, point) >= -1e-3f && Along(path, point) <= path.LengthCells + 1e-3f));
        var rails = RailsNear(meshes, path, 0);
        Assert.IsFalse(rails.Any(point => Along(path, point) is > .5f and < 11f));
        Assert.IsTrue(rails.Any(point => Along(path, point) > 13f));
        var construction = Of(meshes, BuiltInMaterial.Construction);
        Assert.IsTrue(construction.Any(point => Along(path, point) < 1f));
        Assert.IsTrue(construction.All(point => Along(path, point) >= -1e-3f));

        var builds = 0;
        var initial = NodeFixtures.Snapshot(platforms: 0, tracks: 1, trackLimit: 2, trains: []);
        await using var vehicles = new SceneVehicles();
        await using var scene = new NodeScene(initial, NodeFixtures.NodeId, new(), SceneFixtures.NextId(), vehicles, new(), (_, node, _, token) =>
        {
            Interlocked.Increment(ref builds);
            return new(NodeLayout.Build(initial, node, token), ImmutableDictionary<string, NodeScene.Part>.Empty.Add("ground",
                new("fixed", WorldSceneSession.BuildGeometry(id => new([new(id(""), null, SceneFixtures.Identity, null, [], [], [])], [], [])),
                    ImmutableDictionary<GridColumn, double>.Empty)), []);
        });
        await scene.BuildWork!;
        SceneFrame? displayed = null;
        SceneFrame frame;
        do
        {
            await SceneFixtures.AwaitPendingWork(scene.PendingWork);
            frame = scene.Present(initial, Request, NodeFixtures.NextStamp(scene), displayed, default);
            if (frame.Readiness is not SceneReadiness.Loading) displayed = frame;
        } while (scene.PendingWork.Length > 0);
        var broken = RailwayFixtures.ReplaceCore(initial, edge: initial.Core.World.Edges[0] with
            { Breaks = [new TrackBreakView(new(1), 1, 2)] });
        var after = scene.Present(broken, Request, NodeFixtures.NextStamp(scene), frame, default);
        Assert.AreEqual(1, builds);
        Assert.AreEqual(0, after.Update.Upserts.Length);
        Assert.AreEqual(0, after.Update.Removed.Length);
    }

    [TestMethod]
    public void BreakOpensACappedGapAndRepairPropsStayOffOpenTracks()
    {
        var bare = Path(Breaks: [new TrackBreakView(new(1), 1, 20)]);
        var open = RailwayMeshes.Build(bare, 0, default);
        Assert.IsFalse(RailsNear(open, bare, 0).Any(point => Along(bare, point) is > 32.5f and < 47.5f));
        Assert.IsTrue(SleepersNear(open, bare, 0).Any(point => point.Y > 0 && Along(bare, point) is > 32f and < 48f));
        Assert.IsFalse(open.Any(mesh => mesh.Material is SceneMaterial.BuiltIn { Kind: BuiltInMaterial.Construction }));

        var repaired = Path([new TrackBreakView(new(1), 1, 20)], [Repair(new(1), 1, new(1))]);
        var grounded = RailwayMeshes.Build(repaired, 0, default, _ => 4);
        var barriers = ConstructionNear(grounded, repaired, 0, 8);
        var yard = ConstructionBeyond(grounded, repaired, 0, 8);
        Assert.IsTrue(barriers.Any());
        Assert.IsTrue(yard.Any());
        Assert.AreEqual(4, yard.Min(point => point.Y), 1e-3);
        Assert.IsTrue(yard.All(point => point.Y >= 4 - 1e-3f));
        var steelYard = Of(grounded, BuiltInMaterial.RailSteel).Where(point => Lateral(repaired, point, 40, 0) > 8).ToArray();
        Assert.IsTrue(steelYard.Any());
        Assert.AreEqual(4, steelYard.Min(point => point.Y), 1e-3);

        var bridge = RailwayMeshes.Build(repaired, 0, default, _ => null);
        Assert.IsTrue(ConstructionNear(bridge, repaired, 0, 8).Any());
        Assert.IsFalse(ConstructionBeyond(bridge, repaired, 0, 8).Any());

        var two = Path([new TrackBreakView(new(1), 1, 20)], [Repair(new(1), 1, new(1))], tracks: 2);
        var props = ConstructionBeyond(RailwayMeshes.Build(two, 0, default, _ => 4), two, RailwayCoordinates.TrackOffset(1, 2), 8);
        var openOffset = RailwayCoordinates.TrackOffset(2, 2);
        Assert.IsTrue(props.Any());
        Assert.IsTrue(props.All(point => Lateral(two, point, 40, openOffset) > 16));
    }

    [TestMethod]
    public async Task BreakRebuildsOnlyCrossingSegmentsAndKeepsTheOldMeshUntilReplacement()
    {
        var counts = new ConcurrentDictionary<int, int>();
        var generation = 0;
        using var release = new ManualResetEventSlim(true);
        var basis = RailwayFixtures.Snapshot(130, 130, 0);
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(basis, vehicles, (snapshot, path, index, token) =>
        {
            var gen = Volatile.Read(ref generation);
            counts.AddOrUpdate(index, 1, (_, count) => count + 1);
            if (gen > 0 && index is 0 or 1) release.Wait(token);
            return Mark(snapshot, path, index, token, gen);
        });
        var before = await RailwayFixtures.Settled(scene, basis, Middle, Request, Stamp(1));
        Volatile.Write(ref generation, 1);
        release.Reset();
        var broken = RailwayFixtures.ReplaceCore(basis, edge: basis.Core.World.Edges[0] with { Breaks = [new TrackBreakView(new(1), 1, 64)] });
        SceneFrame held;
        try
        {
            held = scene.Present(broken, basis, Middle, Request, Stamp(2), before);
            Assert.IsTrue(held.Update.Environment.FoggedRegions.IsEmpty);
            for (var index = 0; index <= 2; index++)
            {
                Assert.AreSame(Marker(before, index), Marker(held, index));
                Assert.IsFalse(held.Update.Upserts.Any(item => item.Id == MarkerObject(before, index).Id));
            }
        }
        finally { release.Set(); }
        var after = await RailwayFixtures.Settled(scene, broken, Middle, Request, Stamp(3), basis, held);
        Assert.AreEqual(2, counts[0]);
        Assert.AreEqual(2, counts[1]);
        Assert.AreEqual(1, counts[2]);
        Assert.AreNotSame(Marker(before, 0), Marker(after, 0));
        Assert.AreNotSame(Marker(before, 1), Marker(after, 1));
        Assert.AreSame(Marker(before, 2), Marker(after, 2));
        Assert.IsTrue(after.Update.Upserts.Any(item => item.Id == MarkerObject(before, 0).Id));
        Assert.IsTrue(after.Update.Upserts.Any(item => item.Id == MarkerObject(before, 1).Id));
        Assert.IsFalse(after.Update.Upserts.Any(item => item.Id == MarkerObject(before, 2).Id));
    }

    [TestMethod]
    public async Task ProgressAndAddTrackDoNotRebuildSegments()
    {
        var counts = new ConcurrentDictionary<int, int>();
        var basis = RailwayFixtures.Snapshot(130, 130, 0);
        var repair = Repair(new(9), 1, new(1));
        var opened = RailwayFixtures.ReplaceCore(basis, edge: basis.Core.World.Edges[0] with
        {
            Breaks = [new TrackBreakView(new(1), 1, 64)],
            Works = [repair]
        });
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(opened, vehicles, (snapshot, path, index, token) =>
        {
            counts.AddOrUpdate(index, 1, (_, count) => count + 1);
            return Mark(snapshot, path, index, token, 0);
        });
        var before = await RailwayFixtures.Settled(scene, opened, Middle, Request, Stamp(1));
        var built = counts.ToDictionary(pair => pair.Key, pair => pair.Value);
        var next = RailwayFixtures.ReplaceCore(opened, edge: opened.Core.World.Edges[0] with
        {
            Works = [repair with { Progress = .9 }, Repair(new(8), 2, null, RailwayWorkKind.AddTrack)]
        });
        var frame = await RailwayFixtures.Settled(scene, next, Middle, Request, Stamp(2), opened, before);
        CollectionAssert.AreEquivalent(built.Keys.ToArray(), counts.Keys.ToArray());
        foreach (var pair in built) Assert.AreEqual(pair.Value, counts[pair.Key]);
        for (var index = 0; index <= 2; index++) Assert.AreSame(Marker(before, index), Marker(frame, index));
        Assert.IsTrue(frame.Update.Upserts.IsEmpty);
        var identity = RailwayFixtures.ReplaceCore(next, edge: next.Core.World.Edges[0] with { Works = [repair with { Id = new JobId(10) }] });
        var updated = await RailwayFixtures.Settled(scene, identity, Middle, Request, Stamp(3), next, frame);
        Assert.AreEqual(built[0] + 1, counts[0]);
        Assert.AreEqual(built[1] + 1, counts[1]);
        Assert.AreEqual(built[2], counts[2]);
        Assert.AreNotSame(Marker(frame, 0), Marker(updated, 0));
        Assert.AreNotSame(Marker(frame, 1), Marker(updated, 1));
        Assert.AreSame(Marker(frame, 2), Marker(updated, 2));
    }

    [TestMethod]
    public async Task TrackCountRebuildsOnlyThatLineAndPlacesTheNewTrackTrain()
    {
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle());
        var initial = LinePair(1, content);
        var counts = new ConcurrentDictionary<(ulong Edge, int Index), int>();
        await using var vehiclesA = new SceneVehicles();
        await using var vehiclesB = new SceneVehicles();
        await using var line = RailwayFixtures.Controlled(initial, vehiclesA, Count(counts, 1));
        await using var other = RailwayFixtures.Controlled(initial, vehiclesB, Count(counts, 2), edge: new(2));
        var before = await RailwayFixtures.Settled(line, initial, Middle, Request, Stamp(1));
        var otherBefore = await RailwayFixtures.Settled(other, initial, Middle, Request, Stamp(1));
        var next = LinePair(2, content, [RailwayFixtures.Train(64, track: 2)], [RailwayFixtures.Instance()]);
        var frame = await RailwayFixtures.Settled(line, next, Middle, Request, Stamp(2), initial, before);
        var untouched = await RailwayFixtures.Settled(other, next, Middle, Request, Stamp(2), initial, otherBefore);
        Assert.IsFalse(frame.Diagnostics.Any(item => item.Code == "railway_update_pending"));
        Assert.AreEqual(128, Pose(frame, 1).Position.Z + frame.Origin.Z, 1e-3);
        for (var index = 0; index <= 2; index++)
        {
            Assert.AreEqual(2, counts[(1, index)]);
            Assert.AreEqual(1, counts[(2, index)]);
            Assert.AreSame(Marker(otherBefore, index), Marker(untouched, index));
            Assert.IsFalse(untouched.Update.Upserts.Any(item => item.Id == MarkerObject(otherBefore, index).Id));
        }
    }

    private static Func<SceneWorldSnapshot, RailwayPath, int, CancellationToken, RailwaySegment> Count(
        ConcurrentDictionary<(ulong Edge, int Index), int> counts, ulong edge) => (snapshot, path, index, token) =>
    {
        counts.AddOrUpdate((edge, index), 1, (_, count) => count + 1);
        return Mark(snapshot, path, index, token, edge == 1 ? 1 : 0);
    };

    private static RailwaySegment Mark(SceneWorldSnapshot snapshot, RailwayPath path, int index, CancellationToken token, int generation)
    {
        var mesh = new SceneMesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel),
            [new SceneVertex(new(generation, index, 0), Vector3.UnitY, Vector2.Zero, Vector2.Zero, ExposedFaceEdges.None, 0, 0)], [0, 0, 0]);
        var segment = RailwayFixtures.EmptySegment(snapshot, path, index, token);
        return segment with
        {
            Geometry = new([new SceneObject(new(1), null, new(Vector3.Zero, Quaternion.Identity), null,
                [new SceneVisual.Geometry(mesh, new(false, 0, 0, false))], [], [])], [], [])
        };
    }

    private static SceneMesh Marker(SceneFrame frame, int index) => MarkerObject(frame, index).Visuals.OfType<SceneVisual.Geometry>().Single().Mesh;
    private static SceneObject MarkerObject(SceneFrame frame, int index) => frame.Objects.Single(item =>
        item.Visuals.OfType<SceneVisual.Geometry>().Any(visual => visual.Mesh.Vertices.Length == 1 && visual.Mesh.Vertices[0].Position.Y == index));

    private static SceneWorldSnapshot LinePair(int tracks, IContentView content,
        ImmutableArray<TrainView> trains = default, ImmutableArray<InstanceView> instances = default)
    {
        var first = RailwayFixtures.Snapshot(130, 130, 0, tracks, trackLimit: 2, content: content, trains: trains, instances: instances);
        var edge = first.Core.World.Edges[0];
        var second = edge with { Id = new EdgeId(2), Tracks = 1, Breaks = [], Works = [] };
        return first with
        {
            Core = new FixedCoreReadView
            {
                Position = first.Core.Position, InputIdentity = first.Core.InputIdentity,
                World = first.Core.World with { Edges = [edge, second] },
                Nodes = first.Core.World.Nodes.ToDictionary(item => item.Id, item => first.Core.FindNode(item.Id)),
                Edges = new Dictionary<EdgeId, Lookup<EdgeView>>
                {
                    [edge.Id] = first.Core.FindEdge(edge.Id),
                    [second.Id] = new Lookup<EdgeView>.Found(new(second, [new TrackView(1, null, [])], null))
                },
                Trains = first.Core.World.Trains.ToDictionary(item => item.Id, item => first.Core.FindTrain(item.Id)),
                Instances = first.Core.World.Trains.SelectMany(item => ((Lookup<TrainView>.Found)first.Core.FindTrain(item.Id)).Value.Vehicles)
                    .Select(id => (InstanceId)new InstanceId.Vehicle(id)).Distinct()
                    .ToDictionary(id => id, id => first.Core.FindInstance(id))
            }
        };
    }

    private static RailwayPath Path(ImmutableArray<TrackBreakView> Breaks, ImmutableArray<RailwayWorkView> Works = default, int tracks = 1)
    {
        if (Works.IsDefault) Works = [];
        var basis = RailwayFixtures.Snapshot(100, 100, 0, tracks, trackLimit: Math.Max(tracks, 1));
        return new(RailwayFixtures.ReplaceCore(basis, edge: basis.Core.World.Edges[0] with { Breaks = Breaks, Works = Works }), RailwayFixtures.Edge);
    }

    private static RailwayWorkView Repair(JobId id, int track, BreakId? br, RailwayWorkKind kind = RailwayWorkKind.RepairBreak) =>
        new(id, kind, track, br, new(1), [], kind == RailwayWorkKind.RepairBreak ? .25 : 0, []);

    private static float Along(RailwayPath path, Vector3 point)
    {
        var sample = path.Sample(0);
        return Vector3.Dot(point - RailwayCoordinates.Relative(sample.Position, default), sample.Forward);
    }

    private static float Lateral(RailwayPath path, Vector3 point, double arc, double trackOffset)
    {
        var sample = path.Sample(arc);
        var center = RailwayCoordinates.Relative(sample.Position, default) + sample.Right * (float)trackOffset;
        var delta = point - center;
        return Math.Abs(delta.X * sample.Right.X + delta.Z * sample.Right.Z);
    }

    private static Vector3[] Of(ImmutableArray<SceneMesh> meshes, BuiltInMaterial kind) => meshes
        .Where(mesh => mesh.Material is SceneMaterial.BuiltIn built && built.Kind == kind)
        .SelectMany(mesh => mesh.Vertices).Select(vertex => vertex.Position).ToArray();
    private static Vector3[] RailsNear(ImmutableArray<SceneMesh> meshes, RailwayPath path, double offset) =>
        Of(meshes, BuiltInMaterial.RailSteel).Where(point => Lateral(path, point, 40, offset) < 8).ToArray();
    private static Vector3[] SleepersNear(ImmutableArray<SceneMesh> meshes, RailwayPath path, double offset) =>
        Of(meshes, BuiltInMaterial.Sleeper).Where(point => Lateral(path, point, 40, offset) < 8).ToArray();
    private static Vector3[] ConstructionNear(ImmutableArray<SceneMesh> meshes, RailwayPath path, double offset, float limit) =>
        Of(meshes, BuiltInMaterial.Construction).Where(point => Lateral(path, point, 40, offset) <= limit).ToArray();
    private static Vector3[] ConstructionBeyond(ImmutableArray<SceneMesh> meshes, RailwayPath path, double offset, float limit) =>
        Of(meshes, BuiltInMaterial.Construction).Where(point => Lateral(path, point, 40, offset) > limit).ToArray();

    private static ScenePose Pose(SceneFrame frame, ulong vehicle) =>
        ((Lookup<ScenePose>.Found)frame.FindPose(new WorldObjectId.Instance(new InstanceId.Vehicle(new(vehicle))))).Value;
}
