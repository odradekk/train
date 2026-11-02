using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeRetainedCacheTests
{
    [TestMethod]
    public async Task ReopeningKeepsUndemandedCachedChunksUntilTheyLeaveTheRetentionCircle()
    {
        var layout = NodeFixtures.Layout;
        layout = layout with { SceneModel = layout.SceneModel with { Scene = SceneFixtures.Projection(
            [SceneFixtures.Cell(new(-512, 0, -512)), SceneFixtures.Cell(new(512, 0, 512))]) } };
        var snapshot = NodeFixtures.EmptyNodeSnapshot(layout, []);
        var prepared = NodeScene.Prepare(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId));
        var retention = new NodeRange(default, NodeStreaming.RetainCells);
        var demand = new NodeRange(default, NodeStreaming.PrefetchCells);
        var from = retention.Chunks(prepared.Global.Layout).First(column => !demand.Intersects(column));
        var key = NodeScene.ChunkKey(from);
        Assert.IsTrue(retention.Intersects(from));
        Assert.IsFalse(demand.Intersects(from));
        // A stale key makes accidental validation reach the controlled builder instead of silently hitting the cache.
        var part = prepared.BuildChunk(from) with { Inputs = "unchecked retained terrain" };
        var cache = new NodeScene.Cache();
        var owner = cache.Open(NodeFixtures.NodeId);
        cache.Publish(NodeFixtures.NodeId, owner, prepared.Global with { Parts =
            prepared.Global.Parts.SetItem(key, part) });
        cache.Close(NodeFixtures.NodeId, owner);
        Assert.AreSame(part, cache.Read(NodeFixtures.NodeId)!.Parts[key]);
        var started = new ConcurrentQueue<GridColumn>();
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var vehicles = new SceneVehicles();
        ulong nextId = 0;
        await using var scene = new NodeScene(snapshot, NodeFixtures.NodeId, cache, _ => new(++nextId),
            vehicles, new(), _ => null, buildChunk: (data, column, old, token) =>
            {
                started.Enqueue(column);
                if (started.Count == RailwayCoordinates.MaxConcurrentBuilds) filled.TrySetResult();
                release.Wait(token);
                return new(data.ChunkInputs(column), WorldSceneSession.BuildGeometry(_ => new([], [], [])),
                    ImmutableDictionary<GridColumn, double>.Empty);
            });
        await scene.BuildWork!;
        scene.Pump(allowBuild: false);
        Assert.AreSame(part, cache.Read(NodeFixtures.NodeId)!.Parts.GetValueOrDefault(key),
            "Binding must preserve undemanded cached data without admitting it or using a build slot.");
        var cachedMeshes = part.Geometry.Geometry.Objects.SelectMany(item => item.Visuals)
            .OfType<SceneVisual.Geometry>().Select(item => item.Mesh).ToArray();
        Assert.IsTrue(cachedMeshes.Length > 0);
        Assert.IsFalse(scene.CandidateChanges(snapshot, true).Upserts.SelectMany(item => item.Visuals)
            .OfType<SceneVisual.Geometry>().Any(visual => cachedMeshes.Any(mesh => ReferenceEquals(mesh, visual.Mesh))),
            "Retaining unchecked data must not attach its geometry to the current binding.");
        try
        {
            scene.Pump();
            await filled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(RailwayCoordinates.MaxConcurrentBuilds, started.Count,
                "The test must open and fill the actual build slots before checking retained-cache dispatch.");
            release.Set();
            for (var attempt = 0; scene.HasPendingBuilds; attempt++)
            {
                Assert.IsTrue(attempt < 1000, "The finite demand must finish dispatching.");
                await SceneFixtures.AwaitPendingWork(scene.PendingWork);
                scene.Pump();
            }
            Assert.IsFalse(started.Contains(from),
                "Unchecked retained terrain outside demand must never occupy a validation/build slot, even after demanded work drains.");
            Assert.AreSame(part, cache.Read(NodeFixtures.NodeId)!.Parts.GetValueOrDefault(key));
        }
        finally { release.Set(); }
        var away = new ScenePoint(from.X + NodeStreaming.RetainCells * 3, 0, from.Z);
        Assert.IsFalse(new NodeRange(away, NodeStreaming.RetainCells).Intersects(from));
        scene.Pump(focus: away, allowBuild: false);
        Assert.IsFalse(cache.Read(NodeFixtures.NodeId)!.Parts.ContainsKey(key),
            "Retained cached data must retire when the focus leaves its retention circle.");
    }
}
