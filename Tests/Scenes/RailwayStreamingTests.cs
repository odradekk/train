using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class RailwayStreamingTests
{
    private sealed class Observer : ISceneStreamingObserver
    {
        internal readonly ConcurrentQueue<SceneStreamingEvent> Events = new();
        public void OnEvent(SceneStreamingEvent value) => Events.Enqueue(value);
        internal SceneStreamingEvent.Changed[] Changes => Events.OfType<SceneStreamingEvent.Changed>().ToArray();
    }

    private static SceneStreamingScope Scope(Observer observer, ulong id = 1) =>
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
        var observer = new Observer();
        var cache = new RailwayCache();
        await using var vehicles = new SceneVehicles();
        await using (var scene = Open(snapshot, vehicles, Scope(observer), cache))
        {
            scene.Pump(snapshot, new(RailwayFixtures.Edge, 100));
            await Task.WhenAll(scene.PendingWork);
            Assert.IsTrue(observer.Changes.Any(item => item.Stage == SceneStreamingStage.Started));
            Assert.IsTrue(observer.Changes.Any(item => item.Stage == SceneStreamingStage.Built));
            Assert.IsFalse(observer.Changes.Any(item => item.Stage == SceneStreamingStage.Admitted));
            scene.Pump(snapshot, new(RailwayFixtures.Edge, 100), allowBuild: false);
            Assert.IsTrue(observer.Changes.Any(item => item.Stage == SceneStreamingStage.Admitted));
        }
        var reopened = new Observer();
        await using var cached = Open(snapshot, vehicles, Scope(reopened, 2), cache,
            (_, _, _, _) => throw new AssertFailedException("A retained segment must use the cache."));
        cached.Pump(snapshot, new(RailwayFixtures.Edge, 100), allowBuild: false);
        Assert.IsTrue(reopened.Changes.Any(item => item.Stage == SceneStreamingStage.CacheHit));
        Assert.IsFalse(reopened.Changes.Any(item => item.Stage == SceneStreamingStage.Started));
    }

    [TestMethod]
    public async Task Late_completion_keeps_the_cancelled_attempt_when_demand_returns()
    {
        var snapshot = RailwayFixtures.Snapshot();
        var observer = new Observer();
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
            var cancelled = observer.Changes.First(item => item.Stage == SceneStreamingStage.Cancelled && item.Attempt.Part.Key == enteredIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)).Attempt;
            scene.Pump(snapshot, new(RailwayFixtures.Edge, 0), allowBuild: false);
            var renewed = observer.Changes.Last(item => item.Stage == SceneStreamingStage.Needed && item.Attempt.Part == cancelled.Part).Attempt;
            Assert.IsTrue(renewed.Value > cancelled.Value);
            release.Set();
            await Task.WhenAll(oldWork);
            Assert.IsTrue(observer.Changes.Any(item => item.Stage == SceneStreamingStage.Built && item.Attempt == cancelled));
            Assert.IsFalse(observer.Changes.Any(item => item.Stage == SceneStreamingStage.Admitted && item.Attempt == cancelled));
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public void Coverage_preserves_double_precision_at_the_strip_boundary()
    {
        var path = new RailwayPath(RailwayFixtures.Snapshot(lengthMeters: 800, directMeters: 800, relief: 0), RailwayFixtures.Edge);
        var coverage = new RailwayStreamingCoverage(path, default, new(new(Guid.Empty), 1));
        var width = RailwayCoordinates.TerrainHalfWidth(1);
        Assert.AreEqual(1, coverage.FindTerrainParts(width - 1e-7, 64).Length);
        Assert.IsTrue(coverage.FindTerrainParts(width + 1e-7, 64).IsEmpty);
    }

    [TestMethod]
    public async Task Coverage_includes_unneeded_curved_segments_and_survives_scene_disposal()
    {
        var snapshot = RailwayFixtures.Snapshot(relief: 0);
        var observer = new Observer();
        await using var vehicles = new SceneVehicles();
        var scene = Open(snapshot, vehicles, Scope(observer));
        var frame = scene.Present(snapshot, null, new WorldSceneTarget.Location(RailwayFixtures.Location),
            new(1, null, SceneDebugLayer.None), new(new(Guid.Empty), new(1), new(1), 1), null);
        var coverage = observer.Events.OfType<SceneStreamingEvent.Frame>().Last().Coverage!;
        var arc = scene.Path!.LengthCells - 64;
        var sample = scene.Path.Sample(arc);
        var x = sample.Position.X - frame.Origin.X;
        var z = sample.Position.Z - frame.Origin.Z;
        var expected = coverage.FindTerrainParts(x, z);
        Assert.AreEqual(1, expected.Length);
        Assert.IsFalse(observer.Changes.Any(item => item.Stage == SceneStreamingStage.Needed && item.Attempt.Part == expected[0]));
        var outside = RailwayCoordinates.Add(sample.Position, sample.Right * (float)(RailwayCoordinates.TerrainHalfWidth(1) + 10));
        Assert.IsTrue(coverage.FindTerrainParts(outside.X - frame.Origin.X, outside.Z - frame.Origin.Z).IsEmpty);
        await scene.DisposeAsync();
        CollectionAssert.AreEqual(expected.ToArray(), coverage.FindTerrainParts(x, z).ToArray());
    }
}
