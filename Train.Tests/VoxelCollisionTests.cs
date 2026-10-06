using Train.Core.Content.Voxels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Domain.Voxels;
using Train.Core.Logging;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:voxels")]
public sealed class VoxelCollisionTests
{
    private static readonly VoxelDefinitionId Iron = new("core", "ferrous-ore");
    private static readonly VoxelDefinitionId Hollow = new("core", "hollow");

    [TestMethod]
    public void None_occupies_the_cell_but_collision_query_misses()
    {
        using var harness = CollisionHarness.Create();
        var empty = new WorldVoxelCoord(0, 0, 0);

        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(empty, Hollow, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Occupied, harness.Commands.Add(empty, Iron, VoxelOrientation.Identity).Status);
        Assert.AreEqual(Hollow, harness.Commands.Query(empty).Voxel!.DefinitionId);
        Assert.AreEqual(513, harness.World.VoxelCount);

        var collide = harness.Commands.QueryCollision(empty);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, collide.Status);
        Assert.AreEqual(0, collide.Hits.Count);
    }

    [TestMethod]
    public void Full_box_cell_probe_hits_and_empty_or_out_of_bounds_do_not()
    {
        using var harness = CollisionHarness.Create();
        var occupied = new WorldVoxelCoord(4, 4, 4);
        var empty = new WorldVoxelCoord(0, 0, 0);

        var hit = harness.Commands.QueryCollision(occupied);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, hit.Status);
        Assert.AreEqual(1, hit.Hits.Count);
        Assert.AreEqual(occupied, hit.Hits[0].Cell);
        Assert.AreEqual(Iron, hit.Hits[0].DefinitionId);
        Assert.AreEqual(VoxelCollisionKind.FullBox, hit.Hits[0].Kind);

        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, harness.Commands.QueryCollision(empty).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.OutOfBounds, harness.Commands.QueryCollision(new WorldVoxelCoord(16, 4, 4)).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.InvalidQuery, harness.Commands.QueryCollision(1, 1, 1, 0, 2, 2).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.InvalidQuery, harness.Commands.QueryCollision(double.NaN, 0, 0, 1, 1, 1).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.OutOfBounds, harness.Commands.QueryCollision(32, 0, 0, 33, 1, 1).Status);
    }

    [TestMethod]
    public void Cell_extrema_and_invalid_probes_use_the_published_query_statuses()
    {
        using var harness = CollisionHarness.Create();

        AssertOutOfBoundsCell(harness.Commands, new WorldVoxelCoord(int.MaxValue, 0, 0));
        AssertOutOfBoundsCell(harness.Commands, new WorldVoxelCoord(0, int.MaxValue, 0));
        AssertOutOfBoundsCell(harness.Commands, new WorldVoxelCoord(0, 0, int.MaxValue));
        AssertOutOfBoundsCell(harness.Commands, new WorldVoxelCoord(int.MinValue, 0, 0));
        AssertOutOfBoundsCell(harness.Commands, new WorldVoxelCoord(0, int.MinValue, 0));
        AssertOutOfBoundsCell(harness.Commands, new WorldVoxelCoord(0, 0, int.MinValue));
        AssertOutOfBoundsCell(harness.Commands, new WorldVoxelCoord(32, 0, 0));

        Assert.AreEqual(VoxelCollisionQueryStatus.InvalidQuery, harness.Commands.QueryCollision(default(VoxelCollisionBox)).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.InvalidQuery, harness.Commands.QueryCollision(0, 0, 0, 0, 0, 0).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.InvalidQuery, harness.Commands.QueryCollision(1, 0, 0, 1, 1, 1).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.InvalidQuery, harness.Commands.QueryCollision(0, 1, 0, 1, 0, 1).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.InvalidQuery, harness.Commands.QueryCollision(double.NaN, 0, 0, 1, 1, 1).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.InvalidQuery, harness.Commands.QueryCollision(double.PositiveInfinity, 0, 0, 1, 1, 1).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.InvalidQuery, harness.Commands.QueryCollision(0, 0, 0, double.NegativeInfinity, 1, 1).Status);
    }

    [TestMethod]
    public void Remove_and_lethal_damage_clear_domain_collision_immediately()
    {
        using var harness = CollisionHarness.Create();
        var cell = new WorldVoxelCoord(4, 4, 4);
        var neighbor = new WorldVoxelCoord(5, 4, 4);

        Assert.AreEqual(WorldCommandStatus.Removed, harness.Commands.Remove(cell).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, harness.Commands.QueryCollision(cell).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Commands.QueryCollision(neighbor).Status);

        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(cell, Iron, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Damaged, harness.Commands.Damage(cell, 25).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Commands.QueryCollision(cell).Status);

        Assert.AreEqual(WorldCommandStatus.Removed, harness.Commands.Damage(cell, 75).Status);
        Assert.IsFalse(harness.Commands.Query(cell).IsOccupied);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, harness.Commands.QueryCollision(cell).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Commands.QueryCollision(neighbor).Status);
    }

    [TestMethod]
    public void Slice2_boundary_and_junction_collision_follow_occupancy()
    {
        using var harness = CollisionHarness.CreateSession(VoxelWorldIdentity.Slice2);
        var xMinus = new WorldVoxelCoord(15, 16, 15);
        var xPlus = new WorldVoxelCoord(16, 16, 15);
        var yMinus = new WorldVoxelCoord(15, 15, 15);
        var zPlus = new WorldVoxelCoord(15, 16, 16);
        var junction = new WorldVoxelCoord(16, 16, 16);

        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Session.QueryCollision(xMinus).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Session.QueryCollision(xPlus).Status);

        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Remove(xPlus).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, harness.Session.QueryCollision(xPlus).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Session.QueryCollision(xMinus).Status);
        Assert.IsTrue(harness.Session.Query(xMinus).IsOccupied);

        Assert.AreEqual(WorldCommandStatus.Added, harness.Session.Add(xPlus, Iron, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Remove(yMinus).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, harness.Session.QueryCollision(yMinus).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Session.QueryCollision(xMinus).Status);

        Assert.AreEqual(WorldCommandStatus.Added, harness.Session.Add(yMinus, Iron, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Remove(zPlus).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, harness.Session.QueryCollision(zPlus).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Session.QueryCollision(xMinus).Status);

        Assert.AreEqual(WorldCommandStatus.Added, harness.Session.Add(zPlus, Iron, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Remove(junction).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, harness.Session.QueryCollision(junction).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Session.QueryCollision(new WorldVoxelCoord(15, 16, 16)).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Session.QueryCollision(new WorldVoxelCoord(16, 15, 16)).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Session.QueryCollision(new WorldVoxelCoord(16, 16, 15)).Status);
    }

    [TestMethod]
    public void Straddling_box_hits_both_15_16_cells_until_one_side_is_removed()
    {
        using var harness = CollisionHarness.CreateSession(VoxelWorldIdentity.Slice2);
        var both = harness.Session.QueryCollision(15.25, 16, 15, 16.75, 17, 16);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, both.Status);
        CollectionAssert.AreEqual(
            new[] { new WorldVoxelCoord(15, 16, 15), new WorldVoxelCoord(16, 16, 15) },
            both.Hits.Select(hit => hit.Cell).ToArray());

        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Remove(new WorldVoxelCoord(16, 16, 15)).Status);
        var after = harness.Session.QueryCollision(15.25, 16, 15, 16.75, 17, 16);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, after.Status);
        Assert.AreEqual(1, after.Hits.Count);
        Assert.AreEqual(new WorldVoxelCoord(15, 16, 15), after.Hits[0].Cell);
    }

    [TestMethod]
    public void Same_event_cross_chunk_damage_updates_collision_and_rejected_batches_keep_hits()
    {
        using var harness = CollisionHarness.CreateSession(VoxelWorldIdentity.Slice2);
        var before = harness.Session.QueryCollision(new WorldVoxelCoord(15, 16, 15));
        var rejected = harness.Session.Damage(new[]
        {
            new VoxelDamageTarget(new WorldVoxelCoord(15, 16, 15), 100),
            new VoxelDamageTarget(new WorldVoxelCoord(0, 1, 0), 25)
        });
        Assert.AreEqual(WorldCommandStatus.Empty, rejected.Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Session.QueryCollision(new WorldVoxelCoord(15, 16, 15)).Status);
        Assert.AreEqual(before.Hits[0].DefinitionId, harness.Session.QueryCollision(new WorldVoxelCoord(15, 16, 15)).Hits[0].DefinitionId);

        var applied = harness.Session.Damage(new[]
        {
            new VoxelDamageTarget(new WorldVoxelCoord(15, 16, 15), 100),
            new VoxelDamageTarget(new WorldVoxelCoord(16, 16, 16), 100),
            new VoxelDamageTarget(new WorldVoxelCoord(15, 16, 16), 25)
        });
        Assert.IsTrue(applied.Succeeded);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, harness.Session.QueryCollision(new WorldVoxelCoord(15, 16, 15)).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, harness.Session.QueryCollision(new WorldVoxelCoord(16, 16, 16)).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Session.QueryCollision(new WorldVoxelCoord(15, 16, 16)).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Session.QueryCollision(new WorldVoxelCoord(16, 16, 15)).Status);
    }

    [TestMethod]
    public void Display_and_collision_dirty_queues_do_not_consume_each_other()
    {
        using var harness = CollisionHarness.CreateSession(VoxelWorldIdentity.Slice2);
        CollectionAssert.AreEquivalent(
            VoxelChunks.All(harness.Session.Identity).ToArray(),
            harness.Session.TakePendingDirtyChunks().ToArray());
        CollectionAssert.AreEquivalent(
            VoxelChunks.All(harness.Session.Identity).ToArray(),
            harness.Session.TakePendingCollisionDirtyChunks().ToArray());

        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Remove(new WorldVoxelCoord(16, 16, 15)).Status);
        CollectionAssert.AreEquivalent(
            new[]
            {
                new ChunkCoord(0, 1, 0),
                new ChunkCoord(1, 0, 0),
                new ChunkCoord(1, 1, 0),
                new ChunkCoord(1, 1, 1)
            },
            harness.Session.TakePendingDirtyChunks().ToArray());
        CollectionAssert.AreEqual(
            new[] { new ChunkCoord(1, 1, 0) },
            harness.Session.TakePendingCollisionDirtyChunks().ToArray());

        Assert.AreEqual(WorldCommandStatus.Damaged, harness.Session.Damage(new WorldVoxelCoord(16, 16, 16), 25).Status);
        CollectionAssert.AreEqual(new[] { new ChunkCoord(1, 1, 1) }, harness.Session.TakePendingDirtyChunks().ToArray());
        Assert.AreEqual(0, harness.Session.TakePendingCollisionDirtyChunks().Count);

        Assert.AreEqual(WorldCommandStatus.Added, harness.Session.Add(
            new WorldVoxelCoord(8, 1, 8), Iron, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Remove(new WorldVoxelCoord(16, 16, 16)).Status);
        var meshDirty = harness.Session.TakePendingDirtyChunks();
        var collisionDirty = harness.Session.TakePendingCollisionDirtyChunks();
        CollectionAssert.AreEquivalent(
            new[]
            {
                new ChunkCoord(0, 0, 0),
                new ChunkCoord(1, 0, 1),
                new ChunkCoord(1, 1, 0),
                new ChunkCoord(1, 1, 1),
                new ChunkCoord(0, 1, 1)
            },
            meshDirty.ToArray());
        CollectionAssert.AreEquivalent(
            new[] { new ChunkCoord(0, 0, 0), new ChunkCoord(1, 1, 1) },
            collisionDirty.ToArray());
        Assert.IsTrue(meshDirty.Count < VoxelChunks.All(harness.Session.Identity).Count);
        Assert.IsTrue(collisionDirty.Count < VoxelChunks.All(harness.Session.Identity).Count);
    }

    [TestMethod]
    public void Live_reload_updates_collision_from_current_definitions_and_rejects_keep_hits()
    {
        using var harness = CollisionHarness.CreateSession();
        var cell = new WorldVoxelCoord(4, 4, 4);
        var generation = harness.Reloader.Catalog.Current!.Generation;
        var semantic = harness.Reloader.Catalog.Current.Entries[Iron].SemanticRevision;
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Session.QueryCollision(cell).Status);

        harness.Source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明", collisionJson: "{\"kind\":\"none\"}")));
        var none = harness.Reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, none.Status);
        Assert.AreNotEqual(semantic, harness.Reloader.Catalog.Current!.Entries[Iron].SemanticRevision);
        Assert.IsTrue(harness.Session.Query(cell).IsOccupied);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, harness.Session.QueryCollision(cell).Status);
        var noneSemantic = harness.Reloader.Catalog.Current!.Entries[Iron].SemanticRevision;
        CollectionAssert.AreEquivalent(
            VoxelChunks.All(harness.Session.Identity).ToArray(),
            harness.Session.TakePendingCollisionDirtyChunks().ToArray());

        harness.Source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "新亚铁矿", "新说明", collisionJson: "{\"kind\":\"none\"}")));
        var display = harness.Reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, display.Status);
        Assert.AreEqual(noneSemantic, harness.Reloader.Catalog.Current!.Entries[Iron].SemanticRevision);
        Assert.AreEqual("新亚铁矿", harness.Reloader.Catalog.Current.Entries[Iron].Definition.Name);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, harness.Session.QueryCollision(cell).Status);

        harness.Source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "新亚铁矿", "新说明", collisionJson: "{\"kind\":\"full_box\"}")));
        var restored = harness.Reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, restored.Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Session.QueryCollision(cell).Status);

        var snapshot = harness.Reloader.Catalog.Current;
        var afterHit = harness.Reloader.Catalog.Current.Generation;
        harness.Session.Add(new WorldVoxelCoord(0, 0, 0), Iron, VoxelOrientation.Yaw90);
        harness.Source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明", orientations: "none", collisionJson: "{\"kind\":\"none\"}")));
        var rejected = harness.Reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status);
        Assert.AreSame(snapshot, harness.Reloader.Catalog.Current);
        Assert.AreEqual(afterHit, rejected.CatalogVersion);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, harness.Session.QueryCollision(cell).Status);
        Assert.AreNotEqual(generation, afterHit);
    }

    private static void AssertOutOfBoundsCell(VoxelWorldCommands commands, WorldVoxelCoord cell)
    {
        var result = commands.QueryCollision(cell);
        Assert.AreEqual(VoxelCollisionQueryStatus.OutOfBounds, result.Status);
        Assert.IsNotNull(result.Probe);
        Assert.AreEqual(0, result.Hits.Count);
    }

    private sealed class CollisionHarness : IDisposable
    {
        private readonly LoggerSession _session;

        public VoxelWorld World { get; }
        public VoxelWorldCommands Commands { get; }
        public VoxelDevelopmentSession Session { get; }
        public ContentReloader Reloader { get; }
        public MemoryContentSource Source { get; }

        private CollisionHarness(
            LoggerSession session,
            MemoryContentSource source,
            ContentReloader reloader,
            VoxelWorld world,
            VoxelWorldCommands commands,
            VoxelDevelopmentSession voxelSession)
        {
            _session = session;
            Source = source;
            Reloader = reloader;
            World = world;
            Commands = commands;
            Session = voxelSession;
        }

        public static MemoryContentSource Content()
        {
            var source = ContentFixtures.ValidFerrousOre();
            source.AddVoxelFile("core", "voxels/hollow.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("hollow", "空心占格", "占格但不碰撞", tagsJson: "[]", collisionJson: "{\"kind\":\"none\"}")));
            return source;
        }

        public static CollisionHarness Create()
        {
            var session = LoggerSession.Create();
            var source = Content();
            var reloader = ContentFixtures.Create(source, session.Logger);
            Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
            var world = VoxelWorldFactory.CreateDevelopmentWorld(reloader.Catalog.Current!);
            var voxelSession = new VoxelDevelopmentSession(reloader.Catalog);
            reloader.SetReloadParticipant(VoxelWorldReloadAdapter.For(voxelSession));
            return new CollisionHarness(
                session,
                source,
                reloader,
                world,
                new VoxelWorldCommands(world, reloader.Catalog),
                voxelSession);
        }

        public static CollisionHarness CreateSession(VoxelWorldIdentity? identity = null)
        {
            var session = LoggerSession.Create();
            var source = Content();
            var reloader = ContentFixtures.Create(source, session.Logger);
            Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
            var voxelSession = new VoxelDevelopmentSession(reloader.Catalog, identity);
            reloader.SetReloadParticipant(VoxelWorldReloadAdapter.For(voxelSession));
            return new CollisionHarness(
                session,
                source,
                reloader,
                voxelSession.World,
                voxelSession.Commands,
                voxelSession);
        }

        public void Dispose() => _session.Dispose();
    }
}
