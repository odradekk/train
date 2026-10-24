using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class RailwayStreamingTests
{
    private static SceneStreamingScope Scope(RecordingStreamingObserver observer, ulong id = 1) =>
        new(observer, new(new(Guid.Empty), id), RailwayFixtures.Location, SceneStreamingRole.Active);

    private static RailwayScene Open(SceneWorldSnapshot snapshot, SceneVehicles vehicles, SceneStreamingScope streaming,
        RailwayCache? cache = null, Func<SceneWorldSnapshot, RailwayPath, int, CancellationToken, RailwaySegment>? build = null)
    {
        ulong next = 0;
        var ids = new Dictionary<string, SceneObjectId>();
        return new(snapshot, RailwayFixtures.Edge, cache ?? new(), key =>
        {
            if (!ids.TryGetValue(key, out var id)) ids.Add(key, id = new(++next));
            return id;
        }, vehicles, new(), _ => null, build ?? RailwayFixtures.EmptySegment, streaming: streaming);
    }

    [TestMethod]
    public async Task Workers_report_completion_before_another_pump_and_cache_hits_do_not_build()
    {
        var snapshot = RailwayFixtures.Snapshot();
        var observer = new RecordingStreamingObserver();
        var cache = new RailwayCache();
        await using var vehicles = new SceneVehicles();
        await using (var scene = Open(snapshot, vehicles, Scope(observer), cache))
        {
            scene.Pump(snapshot, new(RailwayFixtures.Edge, 100));
            await Task.WhenAll(scene.PendingWork);
            Assert.IsTrue(observer.Snapshot().OfType<SceneStreamingEvent.Changed>().Any(item => item.Stage == SceneStreamingStage.Started));
            Assert.IsTrue(observer.Snapshot().OfType<SceneStreamingEvent.Changed>().Any(item => item.Stage == SceneStreamingStage.Built));
            Assert.IsFalse(observer.Snapshot().OfType<SceneStreamingEvent.Changed>().Any(item => item.Stage == SceneStreamingStage.Admitted));
            scene.Pump(snapshot, new(RailwayFixtures.Edge, 100), allowBuild: false);
            Assert.IsTrue(observer.Snapshot().OfType<SceneStreamingEvent.Changed>().Any(item => item.Stage == SceneStreamingStage.Admitted));
        }
        var reopened = new RecordingStreamingObserver();
        await using var cached = Open(snapshot, vehicles, Scope(reopened, 2), cache,
            (_, _, _, _) => throw new AssertFailedException("A retained segment must use the cache."));
        cached.Pump(snapshot, new(RailwayFixtures.Edge, 100), allowBuild: false);
        Assert.IsTrue(reopened.Snapshot().OfType<SceneStreamingEvent.Changed>().Any(item => item.Stage == SceneStreamingStage.CacheHit));
        Assert.IsFalse(reopened.Snapshot().OfType<SceneStreamingEvent.Changed>().Any(item => item.Stage == SceneStreamingStage.Started));
    }

    [TestMethod]
    public async Task Late_completion_keeps_the_cancelled_attempt_when_demand_returns()
    {
        var snapshot = RailwayFixtures.Snapshot();
        var observer = new RecordingStreamingObserver();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles, Scope(observer), build: (input, path, index, _) =>
        {
            entered.TrySetResult(index);
            release.Wait();
            return RailwayFixtures.EmptySegment(input, path, index, CancellationToken.None);
        });
        try
        {
            scene.Pump(snapshot, new(RailwayFixtures.Edge, 0));
            var enteredIndex = await entered.Task;
            var oldWork = scene.PendingWork;
            scene.Pump(snapshot, new(RailwayFixtures.Edge, 750));
            var cancelled = observer.Snapshot().OfType<SceneStreamingEvent.Changed>().First(item => item.Stage == SceneStreamingStage.Cancelled && item.Attempt.Part.Key == enteredIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)).Attempt;
            scene.Pump(snapshot, new(RailwayFixtures.Edge, 0), allowBuild: false);
            var renewed = observer.Snapshot().OfType<SceneStreamingEvent.Changed>().Last(item => item.Stage == SceneStreamingStage.Needed && item.Attempt.Part == cancelled.Part).Attempt;
            Assert.IsTrue(renewed.Value > cancelled.Value);
            release.Set();
            await Task.WhenAll(oldWork);
            Assert.IsTrue(observer.Snapshot().OfType<SceneStreamingEvent.Changed>().Any(item => item.Stage == SceneStreamingStage.Built && item.Attempt == cancelled));
            Assert.IsFalse(observer.Snapshot().OfType<SceneStreamingEvent.Changed>().Any(item => item.Stage == SceneStreamingStage.Admitted && item.Attempt == cancelled));
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public void Coverage_preserves_double_precision_at_the_strip_boundary()
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(lengthMeters: 800, directMeters: 800, relief: 0), RailwayFixtures.Edge);
        var coverage = new RailwayStreamingCoverage(path, new(new(Guid.Empty), 1)).At(default);
        var width = RailwayCoordinates.TerrainHalfWidth(1);
        Assert.AreEqual(1, coverage.FindTerrainParts(width - 1e-7, 64).Length);
        Assert.IsTrue(coverage.FindTerrainParts(width + 1e-7, 64).IsEmpty);
    }

    [TestMethod]
    public void Coverage_owns_whole_columns_and_excludes_unbuilt_endpoint_caps()
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(lengthMeters: 800, directMeters: 800, relief: 0), RailwayFixtures.Edge);
        var coverage = new RailwayStreamingCoverage(path, new(new(Guid.Empty), 1)).At(default);
        foreach (var z in new[] { 127.01, 128.01, 127.99, 128.99 })
        {
            var owner = coverage.FindTerrainParts(.1, z).Single();
            Assert.AreEqual(z < 128 ? "0" : "1", owner.Key);
        }
        Assert.IsTrue(coverage.FindTerrainParts(.1, -.01).IsEmpty);
        Assert.AreEqual(1, coverage.FindTerrainParts(.1, path.LengthCells + .99).Length);
        Assert.IsTrue(coverage.FindTerrainParts(.1, path.LengthCells + 1.01).IsEmpty);
    }

    [TestMethod]
    public void Coverage_keeps_segment_local_columns_far_from_the_world_origin()
    {
        var original = RailwayFixtures.Snapshot(lengthMeters: 800, directMeters: 800, relief: 0);
        var nodes = original.Core.World.Nodes.Select(node => node with
            { Position = node.Position with { X = node.Position.X + 3_000_000_000 } }).ToImmutableArray();
        var snapshot = original with { Core = new FixedCoreReadView
        {
            World = original.Core.World with { Nodes = nodes },
            Position = original.Core.Position, InputIdentity = original.Core.InputIdentity,
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>> { [RailwayFixtures.Edge] = original.Core.FindEdge(RailwayFixtures.Edge) },
            Nodes = nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(
                ((Lookup<NodeView>.Found)original.Core.FindNode(node.Id)).Value with { Map = node }))
        } };
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var coverage = new RailwayStreamingCoverage(path, new(new(Guid.Empty), 1));
        var first = coverage.At(new(6_000_000_000, 0, 0));
        var shifted = coverage.At(new(6_000_000_256, 0, 0));
        var owner = first.FindTerrainParts(.1, 64).Single();
        Assert.AreEqual("0", owner.Key);
        Assert.AreEqual(owner, shifted.FindTerrainParts(-255.9, 64).Single());
    }

    [TestMethod]
    public async Task Coverage_includes_unneeded_curved_segments_and_survives_scene_disposal()
    {
        var snapshot = RailwayFixtures.Snapshot(relief: 0);
        var observer = new RecordingStreamingObserver();
        await using var vehicles = new SceneVehicles();
        var scene = Open(snapshot, vehicles, Scope(observer));
        var frame = scene.Present(snapshot, null, new WorldSceneTarget.Location(RailwayFixtures.Location),
            new(1, null, SceneDebugLayer.None), new(new(Guid.Empty), new(1), new(1), 1), null);
        var coverage = observer.Snapshot().OfType<SceneStreamingEvent.Frame>().Last().Coverage!;
        var arc = scene.Path!.LengthCells - 64;
        var sample = scene.Path.Sample(arc);
        var x = sample.Position.X - frame.Origin.X;
        var z = sample.Position.Z - frame.Origin.Z;
        var expected = coverage.FindTerrainParts(x, z);
        Assert.AreEqual(1, expected.Length);
        CollectionAssert.AreEqual(expected.ToArray(), coverage.FindTerrainParts(
            Math.Floor(sample.Position.X) + .01 - frame.Origin.X,
            Math.Floor(sample.Position.Z) + .01 - frame.Origin.Z).ToArray());
        CollectionAssert.AreEqual(expected.ToArray(), coverage.FindTerrainParts(
            Math.Floor(sample.Position.X) + .99 - frame.Origin.X,
            Math.Floor(sample.Position.Z) + .99 - frame.Origin.Z).ToArray());
        Assert.IsFalse(observer.Snapshot().OfType<SceneStreamingEvent.Changed>().Any(item => item.Stage == SceneStreamingStage.Needed && item.Attempt.Part == expected[0]));
        var outside = RailwayCoordinates.Add(sample.Position, sample.Right * (float)(RailwayCoordinates.TerrainHalfWidth(1) + 10));
        Assert.IsTrue(coverage.FindTerrainParts(outside.X - frame.Origin.X, outside.Z - frame.Origin.Z).IsEmpty);
        await scene.DisposeAsync();
        CollectionAssert.AreEqual(expected.ToArray(), coverage.FindTerrainParts(x, z).ToArray());
    }
}
