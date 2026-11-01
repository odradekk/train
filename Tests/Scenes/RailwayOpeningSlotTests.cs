using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class RailwayOpeningSlotTests
{
    [TestMethod]
    public async Task Candidate_pump_keeps_two_slots_and_first_present_adds_only_one_near_segment()
    {
        await using var run = new OpeningRun(tracks: 20);
        run.Pump(4.5);
        await run.WaitForStarts(2);
        CollectionAssert.AreEqual(new[] { 4, 3 }, run.Queued);

        run.Present(4.5);
        CollectionAssert.AreEqual(new[] { 4, 3, 5 }, run.Queued);
        await run.WaitForStarts(1);
        run.Present(4.5);
        run.Present(4.5);
        CollectionAssert.AreEqual(new[] { 4, 3, 5 }, run.Queued);
        Assert.AreEqual(3, run.StartedCount);
    }

    [TestMethod]
    [DataRow(4.05, false)]
    [DataRow(4.95, true)]
    [DataRow(4.1171875, true)]
    public async Task Extra_slot_requires_the_next_segment_envelope_within_32_meters(double focus, bool near)
    {
        await using var run = new OpeningRun();
        run.Present(focus);
        CollectionAssert.AreEqual(near ? new[] { 4, 3, 5 } : new[] { 4, 3 }, run.Queued);
        await run.WaitForStarts(near ? 3 : 2);
    }

    [TestMethod]
    public async Task Completed_opening_does_not_reopen_a_third_slot_after_focus_moves()
    {
        await using var run = new OpeningRun();
        run.Present(4.95);
        CollectionAssert.AreEqual(new[] { 4, 3, 5 }, run.Queued);
        await run.WaitForStarts(3);
        var opening = run.Scene.PendingWork;
        run.Release();
        await Task.WhenAll(opening);
        run.Pump(4.95, allowBuild: false);
        run.Block();

        var before = run.Queued.Length;
        run.Present(10.5);
        await run.WaitForStarts(2);
        CollectionAssert.AreEqual(new[] { 10, 9 }, run.Queued.Skip(before).ToArray());
    }

    [TestMethod]
    public async Task Cancelled_workers_keep_their_slots_until_they_exit()
    {
        await using var run = new OpeningRun();
        run.Present(4.95);
        CollectionAssert.AreEqual(new[] { 4, 3, 5 }, run.Queued);
        await run.WaitForStarts(3);
        var opening = run.Scene.PendingWork;
        run.Present(10.5);
        CollectionAssert.AreEqual(new[] { 4, 3, 5 }, run.Queued);
        Assert.AreEqual(3, run.StartedCount);

        run.Release();
        await Task.WhenAll(opening);
        run.Block();
        run.Present(10.5);
        await run.WaitForStarts(2);
        CollectionAssert.AreEqual(new[] { 4, 3, 5, 10, 9 }, run.Queued);
        Assert.AreEqual(5, run.StartedCount);
    }

    [TestMethod]
    public async Task Reopened_cached_window_never_queues_or_starts_a_build()
    {
        var cache = new RailwayCache();
        await using (var run = new OpeningRun(cache: cache))
        {
            run.Release();
            run.Present(4.95);
            while (run.Scene.HasPendingBuilds)
            {
                await Task.WhenAll(run.Scene.PendingWork);
                run.Pump(4.95);
            }
        }

        await using var cached = new OpeningRun(cache: cache, rejectBuild: true);
        cached.Present(4.95);
        Assert.AreEqual(0, cached.Queued.Length);
        Assert.AreEqual(0, cached.StartedCount);
        Assert.IsTrue(cached.Events.Any(item => item.Stage == SceneStreamingStage.CacheHit));
    }

    private sealed class OpeningRun : IAsyncDisposable
    {
        private readonly SceneWorldSnapshot snapshot;
        private readonly SceneVehicles vehicles = new();
        private readonly RecordingStreamingObserver observer = new();
        private readonly ManualResetEventSlim release = new();
        private readonly SemaphoreSlim started = new(0);
        private ulong revision;
        internal RailwayScene Scene { get; }
        internal SceneStreamingEvent.Changed[] Events => observer.Snapshot().OfType<SceneStreamingEvent.Changed>()
            .Where(item => item.Attempt.Part.Kind == SceneStreamingPartKind.RailwaySegment).ToArray();
        internal int[] Queued => Events.Where(item => item.Stage == SceneStreamingStage.Queued)
            .Select(item => int.Parse(item.Attempt.Part.Key, CultureInfo.InvariantCulture)).ToArray();
        internal int StartedCount => Events.Count(item => item.Stage == SceneStreamingStage.Started);

        internal OpeningRun(int tracks = 1, RailwayCache? cache = null, bool rejectBuild = false)
        {
            snapshot = RailwayFixtures.Snapshot(lengthMeters: 1200, directMeters: 1200, relief: 0, tracks: tracks);
            ulong nextId = 0;
            var ids = new Dictionary<string, SceneObjectId>();
            Scene = new(snapshot, RailwayFixtures.Edge, cache ?? new(), key =>
            {
                if (!ids.TryGetValue(key, out var id)) ids.Add(key, id = new(++nextId));
                return id;
            }, vehicles, new(), _ => null, (input, path, index, _) =>
            {
                if (rejectBuild) throw new AssertFailedException("Cached terrain must not be rebuilt.");
                started.Release();
                // Deliberately ignore cancellation: a cancelled worker still occupies its slot.
                release.Wait();
                return RailwayFixtures.EmptySegment(input, path, index, CancellationToken.None);
            }, streaming: new(observer, new(new(Guid.Empty), 1), RailwayFixtures.Location, SceneStreamingRole.Active));
            Scene.SetFarBuildAllowed(false);
            Scene.SetFineTerrainBuildAllowed(false);
        }

        internal void Pump(double segment, bool allowBuild = true) =>
            Scene.Pump(snapshot, Location(segment), allowBuild);

        internal void Present(double segment) => Scene.Present(snapshot, null,
            new WorldSceneTarget.Location(Location(segment)), new(1, null, SceneDebugLayer.None),
            new(new(Guid.Empty), new(1), new(1), ++revision), null);

        private static WorldLocation.Edge Location(double segment) => new(RailwayFixtures.Edge, segment * 64);

        internal async Task WaitForStarts(int count)
        {
            for (var index = 0; index < count; index++)
                await started.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        internal void Release() => release.Set();
        internal void Block() => release.Reset();

        public async ValueTask DisposeAsync()
        {
            release.Set();
            try { await Scene.DisposeAsync(); }
            finally
            {
                await vehicles.DisposeAsync();
                release.Dispose();
                started.Dispose();
            }
        }
    }
}
