using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
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
    public async Task Outside_window_fog_follows_the_curve_and_excludes_built_columns()
    {
        var snapshot = RailwayFixtures.Snapshot(lengthMeters: 800, directMeters: 100, relief: 0);
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles, Scope(new()));
        scene.SetTerrainViewRadius(2048);
        scene.SetFarBuildAllowed(false);
        var target = new WorldSceneTarget.Location(RailwayFixtures.Location);
        var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
        var stamp = new SceneFrameStamp(new(Guid.Empty), new(1), new(1), 1);
        var frame = await RailwayFixtures.Settled(scene, snapshot, target, request, stamp);
        var path = scene.Path!;
        var fog = frame.Update.Environment.FoggedRegions;
        Assert.IsFalse(fog.IsEmpty, "Unbuilt segments outside the loaded window must still be fogged.");
        var band = ConstructionFog.Band(arc => path.Sample(arc), 0, path.LengthCells,
            RailwayCoordinates.FogHalfWidth(path.Edge.Tracks), -128, 128);
        var worldFog = fog.Select(box => new SceneBounds(
            box.Minimum + RailwayCoordinates.Relative(frame.Origin, default),
            box.Maximum + RailwayCoordinates.Relative(frame.Origin, default)));
        Assert.IsTrue(ConstructionFog.Clip(worldFog, band).IsEmpty,
            "A curved line's construction fog must not fill the empty ground inside a long segment AABB.");
        var loaded = path.Sample(64).Position;
        Assert.IsFalse(fog.Any(box => loaded.X - frame.Origin.X > box.Minimum.X && loaded.X - frame.Origin.X < box.Maximum.X &&
            loaded.Z - frame.Origin.Z > box.Minimum.Z && loaded.Z - frame.Origin.Z < box.Maximum.Z),
            "Fog for distant curved segments must not cover the attached window.");
    }

    [TestMethod]
    public async Task Far_terrain_removes_outside_window_fog_but_keeps_required_missing_segments_fogged()
    {
        var snapshot = RailwayFixtures.Snapshot(lengthMeters: 400, directMeters: 400, relief: 0);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        using var release = new ManualResetEventSlim();
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles, Scope(new()), build: (input, railway, index, token) =>
        {
            release.Wait(token);
            return RailwayFixtures.EmptySegment(input, railway, index, token);
        });
        scene.SetTerrainViewRadius(1024);
        scene.SetFineTerrainBuildAllowed(false);
        scene.SetFarBuildAllowed(false);
        try
        {
            var target = new WorldSceneTarget.Location(RailwayFixtures.Location);
            var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
            var stamp = new SceneFrameStamp(new(Guid.Empty), new(1), new(1), 1);
            var before = scene.Present(snapshot, null, target, request, stamp, null);
            var distant = path.Sample(path.LengthCells - 32).Position;
            Assert.IsTrue(FogCovers(before, distant), "The required distant strip must be fogged before far terrain is displayed.");
            Assert.IsInstanceOfType<Lookup<double>.Missing>(before.DisplayTerrainHeight(distant.X - before.Origin.X, distant.Z - before.Origin.Z),
                "The initial frame must have demand but no displayed L0 or far terrain at the distant point.");
            var far = (FarTerrain)typeof(RailwayScene).GetField("farTerrain",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(scene)!;
            while (far.HasPendingCoarse) far.Advance(65536, CancellationToken.None);
            var after = scene.Present(snapshot, null, target, request, stamp with { Revision = 2 }, null);
            Assert.IsInstanceOfType<Lookup<double>.Found>(after.DisplayTerrainHeight(distant.X - after.Origin.X, distant.Z - after.Origin.Z),
                "The distant point must have displayed far terrain, not merely no fog.");
            Assert.IsFalse(FogCovers(after, distant), "Outside-window fog must disappear once far terrain is displayed.");
            var required = path.Sample(32).Position;
            Assert.IsTrue(FogCovers(after, required), "Far terrain must not remove fog from required missing L0.");
            Assert.IsInstanceOfType<ScenePick.NotReady>(after.Pick(Ray(required, after)));
        }
        finally { release.Set(); }
        static bool FogCovers(SceneFrame frame, ScenePoint point)
        {
            var local = RailwayCoordinates.Relative(point, frame.Origin);
            return frame.Update.Environment.FoggedRegions.Any(box => local.X >= box.Minimum.X && local.X < box.Maximum.X &&
                local.Y >= box.Minimum.Y && local.Y < box.Maximum.Y && local.Z >= box.Minimum.Z && local.Z < box.Maximum.Z);
        }
        static SceneRay Ray(ScenePoint point, SceneFrame frame) => new(
            RailwayCoordinates.Relative(point with { Y = 100 }, frame.Origin), -System.Numerics.Vector3.UnitY, 200);
    }

    [TestMethod]
    public async Task Follow_far_terrain_covers_only_the_bounded_prefetch_interval_on_a_long_curve()
    {
        const double focal = (4000 - 8) * RailwayCoordinates.CellsPerMeter;
        const double radius = 16;
        var snapshot = RailwayFixtures.Snapshot(lengthMeters: 4000, directMeters: 3000, relief: 0,
            content: RailwayFixtures.Content(design: RailwayFixtures.Vehicle()),
            trains: [RailwayFixtures.Train(focal / RailwayCoordinates.CellsPerMeter, speed: 1000)],
            instances: [RailwayFixtures.Instance()]);
        // Keep the short remaining interval inside one parent tile near its leading edge,
        // so generating its height caps does not process a long stretch of the railway.
        foreach (var node in snapshot.Core.World.Nodes)
            snapshot = RailwayFixtures.ReplaceCore(snapshot, node: node with
                { Position = node.Position with { X = 256, Y = node.Position.Y - 2472 } });
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(snapshot, vehicles);
        scene.SetTerrainViewRadius(radius);
        scene.SetFineTerrainBuildAllowed(false);
        var target = new WorldSceneTarget.Follow(new(1), new(1));
        var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
        var stamp = new SceneFrameStamp(new(Guid.Empty), new(1), new(1), 1);
        var frame = scene.Present(snapshot, null, target, request, stamp, null);
        while (scene.HasPendingBuilds || scene.HasPendingCoarseTerrain || scene.PendingFarWork.Length > 0)
        {
            await Task.WhenAll(scene.PendingWork.Concat(scene.PendingFarWork));
            stamp = stamp with { Revision = stamp.Revision + 1 };
            frame = scene.Present(snapshot, null, target, request, stamp, null);
        }

        var path = scene.Path!;
        var sweep = path.Bounds(focal, Math.Min(path.LengthCells, focal + 256 * RailwayCoordinates.CellsPerMeter), default, 0);
        var distantStart = path.Sample(0).Position;
        var coarseCount = 0;
        foreach (var item in frame.Objects)
        foreach (var geometry in item.Visuals.OfType<SceneVisual.Geometry>())
        {
            var vertices = geometry.Mesh.Vertices;
            var minX = vertices.Min(vertex => vertex.Position.X);
            var maxX = vertices.Max(vertex => vertex.Position.X);
            var minZ = vertices.Min(vertex => vertex.Position.Z);
            var maxZ = vertices.Max(vertex => vertex.Position.Z);
            if (maxX - minX != TerrainTile.ParentCells || maxZ - minZ != TerrainTile.ParentCells) continue;
            coarseCount++;
            var x = frame.Origin.X + item.Pose.Position.X;
            var z = frame.Origin.Z + item.Pose.Position.Z;
            Assert.IsTrue(x + maxX >= sweep.Minimum.X - radius && x + minX <= sweep.Maximum.X + radius &&
                z + maxZ >= sweep.Minimum.Z - radius && z + minZ <= sweep.Maximum.Z + radius,
                "Every coarse tile must intersect the current bounded prefetch interval.");
            Assert.IsFalse(distantStart.X >= x + minX && distantStart.X <= x + maxX &&
                distantStart.Z >= z + minZ && distantStart.Z <= z + maxZ,
                "Following near the end must not generate terrain at the distant line start.");
        }
        Assert.IsTrue(coarseCount > 0, "The assertion must inspect generated coarse terrain.");
    }

    [TestMethod]
    public async Task New_L0_waits_for_only_the_current_far_unit_and_one_more_pump()
    {
        var content = new BlockedFarInitialization();
        var snapshot = RailwayFixtures.Snapshot(relief: 0, content: content);
        var observer = new RecordingStreamingObserver();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseL0 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newBuilds = 0;
        var moved = false;
        Task? movingPump = null;
        await using var vehicles = new SceneVehicles();
        await using var scene = Open(snapshot, vehicles, Scope(observer), build: (input, path, index, token) =>
        {
            if (Volatile.Read(ref moved))
            {
                var count = Interlocked.Increment(ref newBuilds);
                if (count == 1) firstStarted.TrySetResult();
                if (count == 2) secondStarted.TrySetResult();
                releaseL0.Task.GetAwaiter().GetResult();
            }
            return RailwayFixtures.EmptySegment(input, path, index, token);
        });
        var initialLocation = new WorldLocation.Edge(RailwayFixtures.Edge, 100);
        var newLocation = new WorldLocation.Edge(RailwayFixtures.Edge, 650);
        try
        {
            scene.Pump(snapshot, initialLocation);
            while (scene.HasPendingBuilds)
            {
                await Task.WhenAll(scene.PendingWork);
                scene.Pump(snapshot, initialLocation);
            }
            content.Armed = true;
            scene.SetTerrainViewRadius(1);
            scene.Pump(snapshot, initialLocation);
            await content.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var currentFar = scene.PendingFarWork;
            Assert.AreEqual(1, currentFar.Length);

            Volatile.Write(ref moved, true);
            // Block a real far initialization unit while the new L0 demand takes the remaining slot.
            movingPump = Task.Run(() => scene.Pump(snapshot, newLocation));
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await movingPump.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(content.Release.Task.IsCompleted,
                "Demand must return while the real far initialization is still blocked.");
            var farTerrain = (FarTerrain)typeof(RailwayScene).GetField("farTerrain",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(scene)!;
            await Task.Run(() =>
            {
                var view = farTerrain.View;
                var objects = farTerrain.Objects(default, _ => throw new AssertFailedException(
                    "Blocked initialization cannot have published a tile."), view);
                Assert.IsTrue(view.Tiles.IsEmpty);
                Assert.IsTrue(objects.IsEmpty);
            }).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, Volatile.Read(ref newBuilds));
            content.Release.TrySetResult();
            await Task.WhenAll(currentFar).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.IsTrue(scene.PendingFarWork.All(task => task.IsCompleted),
                "Waiting L0 must prevent another far unit from taking the released slot.");
            Assert.IsTrue(farTerrain.View.Tiles.IsEmpty,
                "The worker must yield while real far terrain still needs work.");
            Assert.AreEqual(1, Volatile.Read(ref newBuilds));
            var queuedBefore = observer.Snapshot().OfType<SceneStreamingEvent.Changed>()
                .Count(item => item.Stage == SceneStreamingStage.Queued);
            var waiting = observer.Snapshot().OfType<SceneStreamingEvent.Changed>()
                .Where(item => item.Stage == SceneStreamingStage.Needed)
                .Select(item => item.Attempt).Except(observer.Snapshot().OfType<SceneStreamingEvent.Changed>()
                    .Where(item => item.Stage == SceneStreamingStage.Queued).Select(item => item.Attempt)).ToHashSet();
            Assert.IsTrue(waiting.Count > 0);

            scene.Pump(snapshot, newLocation);
            var queued = observer.Snapshot().OfType<SceneStreamingEvent.Changed>()
                .Where(item => item.Stage == SceneStreamingStage.Queued).ToArray();
            Assert.AreEqual(queuedBefore + 1, queued.Length);
            Assert.IsTrue(waiting.Contains(queued.Last().Attempt));
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(2, Volatile.Read(ref newBuilds));
        }
        finally
        {
            content.Release.TrySetResult();
            releaseL0.TrySetResult();
            if (movingPump is not null) await movingPump;
        }
    }

    private sealed class BlockedFarInitialization : IContentView
    {
        private readonly IContentView source = RailwayFixtures.Content();
        private int blocked;
        internal bool Armed;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ContentGeneration Generation => source.Generation;
        public CoreContentProjection Core => source.Core;
        public ContentLookup<RailwayAppearanceProjection> FindRailwayAppearance(RuleDefinitionId rule) => source.FindRailwayAppearance(rule);
        public ContentLookup<VoxelAppearanceProjection> FindVoxelAppearance(VoxelDefinitionId definition) => source.FindVoxelAppearance(definition);
        public ContentLookup<TerrainAppearanceProjection> FindTerrain(TerrainTypeId terrain)
        {
            if (Armed && Interlocked.Exchange(ref blocked, 1) == 0)
            {
                Entered.TrySetResult();
                Release.Task.GetAwaiter().GetResult();
            }
            return source.FindTerrain(terrain);
        }
        public ContentRevision PersistentRevision => throw new NotSupportedException();
        public ImmutableArray<PackageStatus> Packages => throw new NotSupportedException();
        public ImmutableArray<VoxelCatalogEntry> Voxels => throw new NotSupportedException();
        public ImmutableArray<BlueprintSummary> Blueprints => throw new NotSupportedException();
        public ImmutableArray<SavedContentDraft> LocalDrafts => throw new NotSupportedException();
        public ImmutableArray<GenerationProfileSummary> GenerationProfiles => throw new NotSupportedException();
        public ContentLookup<BlueprintRecord> FindBlueprint(BlueprintAddress address) => throw new NotSupportedException();
        public ContentLookup<DesignEvaluation> FindDesign(DesignHash hash) => throw new NotSupportedException();
        public ContentLookup<VoxelDisplayProjection> FindVoxelDisplay(VoxelDefinitionId definition) => throw new NotSupportedException();
        public ContentLookup<ContentDisplay> FindDefinitionDisplay(CoreDefinitionReference definition) => throw new NotSupportedException();
        public ContentLookup<GoodPresentation> FindGood(GoodId good) => throw new NotSupportedException();
        public ContentLookup<ContentAsset> FindAsset(AssetRevision revision) => throw new NotSupportedException();
        public ContentLookup<SettlementLayoutProjection> FindSettlementLayout(SettlementId settlement) => throw new NotSupportedException();
        public ContentLookup<CreatureAppearanceProjection> FindCreature(CreatureDefinitionId creature) => throw new NotSupportedException();
        public ContentLookup<LairAppearanceProjection> FindLair(LairTypeId lair) => throw new NotSupportedException();
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
