using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeStreamingLifecycleTests
{
    private sealed class Observer : ISceneStreamingObserver
    {
        internal readonly ConcurrentQueue<SceneStreamingEvent> Events = new();
        internal readonly TaskCompletionSource TerrainBuilt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource RailBuilt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void OnEvent(SceneStreamingEvent value)
        {
            Events.Enqueue(value);
            if (value is SceneStreamingEvent.Changed { Stage: SceneStreamingStage.Built } change)
            {
                if (change.Attempt.Part.Kind == SceneStreamingPartKind.NodeTerrain) TerrainBuilt.TrySetResult();
                if (change.Attempt.Part.Kind == SceneStreamingPartKind.NodeRail) RailBuilt.TrySetResult();
            }
        }
        internal SceneStreamingEvent.Changed[] Changes => Events.OfType<SceneStreamingEvent.Changed>().ToArray();
    }

    private static SceneWorldSnapshot Snapshot(bool boundaries = false)
    {
        var layout = NodeFixtures.Layout with
        {
        Plots = [], SceneModel = NodeFixtures.Layout.SceneModel with
        { Scene = SceneFixtures.Projection([]) with { OccupiedBounds = new(new(-320, 0, -320), new(319, 0, 319)) } }
        };
        return boundaries ? NodeFixtures.Snapshot(platforms: 0, trains: [], content: NodeFixtures.Content(layout: layout))
            : NodeFixtures.EmptyNodeSnapshot(layout, []);
    }

    private static NodeScene.Part SmallPart(NodeScene.Preparation data, GridColumn from,
        NodeScene.Part? old, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return new(data.ChunkInputs(from), WorldSceneSession.BuildGeometry(_ => new([], [], [])),
            ImmutableDictionary<GridColumn, double>.Empty);
    }

    private static NodeScene Open(SceneWorldSnapshot snapshot, SceneVehicles vehicles, Observer observer,
        NodeScene.Cache? cache = null,
        Func<NodeScene.Preparation, GridColumn, NodeScene.Part?, CancellationToken, NodeScene.Part>? chunk = null)
    {
        ulong next = 0;
        var ids = new Dictionary<string, SceneObjectId>();
        return new(snapshot, NodeFixtures.NodeId, cache ?? new(), key =>
        {
            if (!ids.TryGetValue(key, out var id)) ids.Add(key, id = new(++next));
            return id;
        }, vehicles, new(), _ => null, buildChunk: chunk ?? SmallPart,
            buildRail: (_, rail, _, _) => new(rail.Inputs, WorldSceneSession.BuildGeometry(_ => new([], [], [])),
                ImmutableDictionary<GridColumn, double>.Empty),
            streaming: new(observer, new(new(Guid.Empty), 1), NodeFixtures.Location, SceneStreamingRole.Active));
    }

    [TestMethod]
    public async Task Preparation_and_parts_report_built_without_a_consuming_pump()
    {
        var snapshot = Snapshot(boundaries: true);
        var observer = new Observer();
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles, observer);
        await scene.BuildWork!;
        Assert.IsTrue(observer.Changes.Any(item => item.Attempt.Part.Kind == SceneStreamingPartKind.NodePreparation &&
            item.Stage == SceneStreamingStage.Built));
        Assert.IsFalse(observer.Changes.Any(item => item.Stage == SceneStreamingStage.Admitted));
        scene.Pump(focus: scene.Layout!.Entries[0].Point);
        await Task.WhenAll(observer.TerrainBuilt.Task, observer.RailBuilt.Task).WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var kind in new[] { SceneStreamingPartKind.NodeTerrain, SceneStreamingPartKind.NodeRail })
        {
            var built = observer.Changes.First(item => item.Stage == SceneStreamingStage.Built && item.Attempt.Part.Kind == kind);
            Assert.IsTrue(observer.Changes.Any(item => item.Stage == SceneStreamingStage.Started && item.Attempt == built.Attempt));
            Assert.IsFalse(observer.Changes.Any(item => item.Stage == SceneStreamingStage.Admitted && item.Attempt == built.Attempt));
        }
    }

    [TestMethod]
    public async Task Retained_chunks_report_cache_hit_before_admission()
    {
        var snapshot = Snapshot();
        var cache = new NodeScene.Cache();
        await using var vehicles = new SceneVehicles();
        await using (var first = Open(snapshot, vehicles, new(), cache))
        {
            await first.BuildWork!;
            first.Pump();
            first.SetBuildAllowed(false);
            await SceneFixtures.AwaitPendingWork(first.PendingWork);
            first.Pump(allowBuild: false);
        }
        var observer = new Observer();
        await using var reopened = Open(snapshot, vehicles, observer, cache);
        await reopened.BuildWork!;
        reopened.Pump();
        reopened.SetBuildAllowed(false);
        await SceneFixtures.AwaitPendingWork(reopened.PendingWork);
        reopened.Pump(allowBuild: false);
        var changes = observer.Changes;
        var admitted = changes.First(item => item.Stage == SceneStreamingStage.Admitted &&
            item.Attempt.Part.Kind == SceneStreamingPartKind.NodeTerrain);
        var hitIndex = Array.FindIndex(changes, item => item.Stage == SceneStreamingStage.CacheHit && item.Attempt == admitted.Attempt);
        var admittedIndex = Array.IndexOf(changes, admitted);
        Assert.IsTrue(hitIndex >= 0 && hitIndex < admittedIndex);
        Assert.IsFalse(changes.Any(item => item.Stage == SceneStreamingStage.Started && item.Attempt == admitted.Attempt));
    }

    [TestMethod]
    public async Task Leaving_queued_demand_cancels_attempt_and_returning_queues_a_new_attempt()
    {
        var snapshot = Snapshot();
        var observer = new Observer();
        var occupied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        using var release = new ManualResetEventSlim();
        await using var vehicles = new SceneVehicles();
        var scene = Open(snapshot, vehicles, observer, chunk: (data, from, old, _) =>
        {
            if (Interlocked.Increment(ref entered) == RailwayCoordinates.MaxConcurrentBuilds)
                occupied.TrySetResult();
            release.Wait();
            return SmallPart(data, from, old, CancellationToken.None);
        });
        try
        {
            await scene.BuildWork!;
            var focus = new ScenePoint(-240, 0, -240);
            scene.Pump(focus: focus);
            await occupied.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var changes = observer.Changes;
            var queued = changes.First(item => item.Stage == SceneStreamingStage.Queued &&
                item.Attempt.Part.Kind == SceneStreamingPartKind.NodeTerrain &&
                !changes.Any(other => other.Attempt == item.Attempt && other.Stage == SceneStreamingStage.Started)).Attempt;

            scene.Pump(focus: new ScenePoint(240, 0, 240));
            Assert.IsTrue(observer.Changes.Any(item => item.Attempt == queued && item.Stage == SceneStreamingStage.DemandEnded));
            Assert.IsTrue(observer.Changes.Any(item => item.Attempt == queued && item.Stage == SceneStreamingStage.Cancelled));

            scene.Pump(focus: focus);
            var requeued = observer.Changes.Last(item => item.Attempt.Part == queued.Part && item.Stage == SceneStreamingStage.Queued).Attempt;
            Assert.AreNotEqual(queued, requeued);
            Assert.IsTrue(observer.Changes.Any(item => item.Attempt == requeued && item.Stage == SceneStreamingStage.Needed));
        }
        finally
        {
            scene.SetBuildAllowed(false);
            release.Set();
            await scene.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Closing_cancels_attempts_but_late_builds_keep_their_identity()
    {
        var snapshot = Snapshot();
        var observer = new Observer();
        var entered = new TaskCompletionSource<GridColumn>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var vehicles = new SceneVehicles();
        var scene = Open(snapshot, vehicles, observer, chunk: (data, from, old, _) =>
        {
            entered.TrySetResult(from);
            release.Wait();
            return SmallPart(data, from, old, CancellationToken.None);
        });
        Task? closing = null;
        try
        {
            await scene.BuildWork!;
            scene.Pump();
            var from = await entered.Task;
            var key = NodeScene.ChunkKey(from);
            var attempt = observer.Changes.First(item => item.Stage == SceneStreamingStage.Started && item.Attempt.Part.Key == key).Attempt;
            closing = scene.DisposeAsync().AsTask();
            Assert.IsTrue(observer.Changes.Any(item => item.Stage == SceneStreamingStage.Cancelled && item.Attempt == attempt));
            release.Set();
            await closing;
            Assert.IsTrue(observer.Changes.Any(item => item.Stage == SceneStreamingStage.Built && item.Attempt == attempt));
            Assert.IsFalse(observer.Changes.Any(item => item.Stage == SceneStreamingStage.Admitted && item.Attempt == attempt));
            Assert.IsInstanceOfType<SceneStreamingEvent.Closed>(observer.Events.Last());
        }
        finally
        {
            release.Set();
            await (closing ?? scene.DisposeAsync().AsTask());
        }
    }

    [TestMethod]
    public async Task Coverage_owns_unneeded_chunks_and_boundaries_and_preserves_its_origin()
    {
        var snapshot = Snapshot(boundaries: true);
        var cache = new NodeScene.Cache();
        var observer = new Observer();
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles, observer, cache);
        await scene.BuildWork!;
        scene.Pump(allowBuild: false);
        var origin = new ScenePoint(48, 0, 64);
        scene.Present(snapshot, new(1, null, SceneDebugLayer.None), NodeFixtures.NextStamp(scene), null, origin);
        var coverage = observer.Events.OfType<SceneStreamingEvent.Frame>().Last().Coverage!;
        var distant = coverage.FindTerrainParts(300 - origin.X, 300 - origin.Z);
        Assert.AreEqual(1, distant.Length);
        Assert.AreEqual(SceneStreamingPartKind.NodeTerrain, distant[0].Kind);
        Assert.IsFalse(observer.Changes.Any(item => item.Stage == SceneStreamingStage.Needed && item.Attempt.Part == distant[0]));
        var boundary = cache.Read(NodeFixtures.NodeId)!.Parts.First(item => item.Key.StartsWith("boundary:") && item.Value.Heights.Count > 0);
        var column = boundary.Value.Heights.Keys.First();
        var owners = coverage.FindTerrainParts(column.X + boundary.Value.Origin.X + .5 - origin.X,
            column.Z + boundary.Value.Origin.Z + .5 - origin.Z);
        Assert.AreEqual(1, owners.Length);
        Assert.AreEqual(SceneStreamingPartKind.NodeBoundary, owners[0].Kind);
        scene.Present(snapshot, new(1, null, SceneDebugLayer.None), NodeFixtures.NextStamp(scene), null, new(512, 0, 512));
        CollectionAssert.AreEqual(distant.ToArray(), coverage.FindTerrainParts(300 - origin.X, 300 - origin.Z).ToArray());
    }
}
