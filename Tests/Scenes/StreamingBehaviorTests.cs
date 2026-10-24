using System.Collections.Concurrent;
using System.Collections.Immutable;
using Train.Content.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class StreamingBehaviorTests
{
    [TestMethod]
    public async Task ObserverDoesNotChangeFramesAcrossAdmissionRetentionRemovalAndCancellation()
    {
        var without = await Run(null);
        var observer = new RecordingStreamingObserver();
        var with = await Run(observer);
        Assert.AreEqual(without.Count, with.Count);
        for (var index = 0; index < without.Count; index++)
            AssertUpdate(without[index], with[index]);
        Assert.IsTrue(without.Any(update => update.Upserts.Any(item => !item.Visuals.IsEmpty)));
        Assert.IsTrue(without.Any(update => !update.Removed.IsEmpty));
        Assert.IsTrue(observer.Snapshot().OfType<SceneStreamingEvent.Changed>()
            .Any(value => value.Stage == SceneStreamingStage.Cancelled));
    }

    private static async Task<List<SceneUpdate>> Run(ISceneStreamingObserver? observer)
    {
        var snapshot = RailwayFixtures.Snapshot(2400, 2400, 0);
        var session = new SceneSessionId(Guid.Parse("00000000-0000-0000-0000-000000000740"));
        var scope = observer is null ? null : new SceneStreamingScope(observer, new(session, 1),
            RailwayFixtures.Location, SceneStreamingRole.Active);
        var gates = new ConcurrentQueue<TaskCompletionSource>();
        using var entered = new SemaphoreSlim(0);
        var geometry = SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]));
        RailwaySegment Build(SceneWorldSnapshot input, RailwayPath path, int index, CancellationToken token)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gates.Enqueue(gate);
            entered.Release();
            gate.Task.GetAwaiter().GetResult();
            return RailwayFixtures.EmptySegment(input, path, index, token) with { Geometry = geometry };
        }
        var ids = new Dictionary<string, SceneObjectId>();
        SceneObjectId Id(string key)
        {
            if (!ids.TryGetValue(key, out var value)) ids.Add(key, value = new((ulong)ids.Count + 1));
            return value;
        }
        await using var vehicles = new SceneVehicles();
        await using var scene = new RailwayScene(snapshot, RailwayFixtures.Edge, new(), Id, vehicles,
            new DisplayRate { Timestamp = () => 0 }, key => ids.TryGetValue(key, out var id) ? id : null,
            Build, streaming: scope);
        var updates = new List<SceneUpdate>();
        SceneFrame? displayed = null;
        ulong revision = 0;
        double distance = 100;
        void Present()
        {
            var stamp = new SceneFrameStamp(session, new(1), new(1), ++revision);
            displayed = scene.Present(snapshot, null,
                new WorldSceneTarget.Location(new WorldLocation.Edge(RailwayFixtures.Edge, distance)),
                new(1, null, SceneDebugLayer.None), stamp, displayed);
            updates.Add(displayed.Update);
            if (observer is RecordingStreamingObserver recording)
                StreamingScenario.AssertFrameMatches(recording.Snapshot(), displayed);
        }
        async Task WaitForBatchEntry()
        {
            for (var index = 0; index < scene.PendingWork.Length; index++)
                await entered.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        async Task CompleteBatch(bool alreadyEntered = false)
        {
            var pending = scene.PendingWork;
            if (!alreadyEntered) await WaitForBatchEntry();
            while (gates.TryDequeue(out var gate)) gate.SetResult();
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Present();
        await CompleteBatch();
        Present(); // Admit one deterministic batch and queue the next.
        await WaitForBatchEntry();
        distance = 1800;
        Present(); // Remove the admitted batch and cancel its pending neighbours.
        await CompleteBatch(alreadyEntered: true);
        Present(); // Late completion cannot restore the removed geometry.
        while (scene.PendingWork.Length > 0)
        {
            await CompleteBatch();
            Present();
        }
        Present(); // Retain all admitted objects with no further work.
        return updates;
    }

    [TestMethod]
    public async Task NodeObserverDoesNotChangeUpdatesAcrossPreparationAdmissionReturnAndRebuild()
    {
        var without = await RunNode(null);
        var observer = new RecordingStreamingObserver();
        var with = await RunNode(observer);
        Assert.AreEqual(without.Count, with.Count);
        for (var index = 0; index < without.Count; index++) AssertUpdate(without[index], with[index]);
        Assert.IsTrue(without.Any(update => !update.Removed.IsEmpty));
        Assert.IsTrue(observer.Snapshot().OfType<SceneStreamingEvent.Changed>().Count(value =>
            value.Attempt.Part.Kind == SceneStreamingPartKind.NodePreparation &&
            value.Stage == SceneStreamingStage.Admitted) > 1, "The changed entry tracks must rebuild preparation.");
    }

    private static async Task<List<SceneUpdate>> RunNode(ISceneStreamingObserver? observer)
    {
        var layout = NodeFixtures.Layout with
        {
            Plots = [], SceneModel = NodeFixtures.Layout.SceneModel with
            { Scene = SceneFixtures.Projection([]) with { OccupiedBounds = new(new(-320, 0, -320), new(319, 0, 319)) } }
        };
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [], tracks: 1, trackLimit: 2,
            content: NodeFixtures.Content(layout: layout));
        var gates = new ConcurrentQueue<TaskCompletionSource>();
        using var entered = new SemaphoreSlim(0);
        void WaitForRelease(CancellationToken token)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gates.Enqueue(gate);
            entered.Release();
            gate.Task.GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
        }
        NodeScene.Part Build(NodeScene.Preparation data, GridColumn from, NodeScene.Part? old, CancellationToken token)
        {
            WaitForRelease(token);
            var geometry = WorldSceneSession.BuildGeometry(id =>
            {
                var root = id("");
                var grid = new VoxelGrid([new(new(from.X, 0, from.Z), new(VoxelOrientation.PxPy, SceneFixtures.Cube,
                    null, null, new PickSubject.Terrain(NodeFixtures.Location), []))]);
                return new([new(root, null, SceneFixtures.Identity, null, DesignGeometry.Meshes(grid, false), [], [])],
                    [new(root, SceneFixtures.Identity, grid)], []);
            });
            return new(data.ChunkInputs(from), geometry, ImmutableDictionary<GridColumn, double>.Empty);
        }
        var ids = new Dictionary<string, SceneObjectId>();
        SceneObjectId Id(string key)
        {
            if (!ids.TryGetValue(key, out var value)) ids.Add(key, value = new((ulong)ids.Count + 1));
            return value;
        }
        var session = new SceneSessionId(Guid.Empty);
        var scope = observer is null ? null : new SceneStreamingScope(observer, new(session, 1),
            NodeFixtures.Location, SceneStreamingRole.Active);
        await using var vehicles = new SceneVehicles();
        await using var scene = new NodeScene(snapshot, NodeFixtures.NodeId, new(), Id, vehicles,
            new DisplayRate { Timestamp = () => 0 }, key => ids.TryGetValue(key, out var id) ? id : null,
            buildChunk: Build, buildRail: (_, rail, _, token) =>
            {
                WaitForRelease(token);
                return new(rail.Inputs, WorldSceneSession.BuildGeometry(_ => new([], [], [])),
                    ImmutableDictionary<GridColumn, double>.Empty);
            }, streaming: scope);
        var updates = new List<SceneUpdate>();
        SceneFrame? displayed = null;
        ulong revision = 0;
        void Present()
        {
            displayed = scene.Present(snapshot, new(1, null, SceneDebugLayer.None),
                new(session, new(1), new(1), ++revision), displayed, default);
            updates.Add(displayed.Update);
            if (observer is RecordingStreamingObserver recording)
                StreamingScenario.AssertFrameMatches(recording.Snapshot(), displayed);
        }
        async Task WaitForBatchEntry()
        {
            foreach (var work in scene.PendingWork.Where(work => !work.IsCompleted))
                await entered.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        async Task CompleteBatch(bool alreadyEntered = false)
        {
            scene.SetBuildAllowed(false);
            var pending = scene.PendingWork.Where(work => !work.IsCompleted).ToArray();
            if (!alreadyEntered) await WaitForBatchEntry();
            while (gates.TryDequeue(out var gate)) gate.SetResult();
            await SceneFixtures.AwaitPendingWork(pending);
        }
        try
        {
            await scene.BuildWork!;
            Present(); // Consume preparation and begin the first controlled batch.
            await CompleteBatch();
            Present();
            await CompleteBatch();
            Present(); // Admit a second batch while more terrain remains pending.
            await WaitForBatchEntry();
            scene.Pump(focus: new(10000, 0, 10000), allowBuild: false);
            await CompleteBatch(alreadyEntered: true);
            Present(); // Retain the distant demand after cancelled builders finish.
            scene.Pump(focus: default(ScenePoint));
            await CompleteBatch();
            Present(); // Demand returns and terrain is rebuilt.
            await CompleteBatch();
            scene.Pump(allowBuild: false);
            var edge = snapshot.Core.World.Edges[0];
            snapshot = RailwayFixtures.ReplaceCore(snapshot, edge: edge with { Tracks = 2 });
            scene.Pump(snapshot, allowBuild: false);
            await SceneFixtures.AwaitPendingWork(scene.PendingWork);
            Present(); // Adopt refreshed preparation before another controlled batch.
            await CompleteBatch();
            Present();
            return updates;
        }
        finally
        {
            scene.SetBuildAllowed(false);
            var pending = scene.PendingWork.Where(work => !work.IsCompleted).ToArray();
            for (var index = 0; index < pending.Length; index++)
                await entered.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            while (gates.TryDequeue(out var gate)) gate.SetResult();
            await SceneFixtures.AwaitPendingWork(pending);
        }
    }

    private static void AssertUpdate(SceneUpdate expected, SceneUpdate actual)
    {
        Assert.AreEqual(expected.Base, actual.Base);
        Assert.AreEqual(expected.Frame, actual.Frame);
        Assert.AreEqual(expected.ShiftExistingRoots, actual.ShiftExistingRoots);
        CollectionAssert.AreEqual(expected.Removed.ToArray(), actual.Removed.ToArray());
        CollectionAssert.AreEqual(expected.Effects.ToArray(), actual.Effects.ToArray());
        CollectionAssert.AreEqual(expected.Environment.FoggedRegions.ToArray(), actual.Environment.FoggedRegions.ToArray());
        Assert.AreEqual(expected.Environment.TerrainCollision.Length, actual.Environment.TerrainCollision.Length);
        for (var index = 0; index < expected.Environment.TerrainCollision.Length; index++)
        {
            var left = expected.Environment.TerrainCollision[index];
            var right = actual.Environment.TerrainCollision[index];
            Assert.AreEqual(left with { Heights = right.Heights }, right);
            CollectionAssert.AreEqual(left.Heights.ToArray(), right.Heights.ToArray());
        }
        Assert.AreEqual(expected.Upserts.Length, actual.Upserts.Length);
        for (var index = 0; index < expected.Upserts.Length; index++)
        {
            var left = expected.Upserts[index];
            var right = actual.Upserts[index];
            Assert.AreEqual(left with { Visuals = right.Visuals, Lights = right.Lights, Effects = right.Effects }, right);
            CollectionAssert.AreEqual(left.Lights.ToArray(), right.Lights.ToArray());
            CollectionAssert.AreEqual(left.Effects.ToArray(), right.Effects.ToArray());
            Assert.AreEqual(left.Visuals.Length, right.Visuals.Length);
            for (var visual = 0; visual < left.Visuals.Length; visual++)
            {
                var a = (SceneVisual.Geometry)left.Visuals[visual];
                var b = (SceneVisual.Geometry)right.Visuals[visual];
                Assert.AreEqual(a.Surface, b.Surface);
                Assert.AreEqual(a.Mesh.Material, b.Mesh.Material);
                CollectionAssert.AreEqual(a.Mesh.Vertices.ToArray(), b.Mesh.Vertices.ToArray());
                CollectionAssert.AreEqual(a.Mesh.TriangleIndices.ToArray(), b.Mesh.TriangleIndices.ToArray());
            }
        }
    }
}
