using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Voxels;
using Train.Core.Logging;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:voxels")]
public sealed class VoxelMultiChunkTests
{
    [TestMethod]
    public void Slice2_world_is_32_cubed_with_eight_chunks_and_1088_integer_voxels()
    {
        using var harness = MultiChunkHarness.Create();

        Assert.AreEqual(32, harness.World.Bounds.SizeX);
        Assert.AreEqual(32, harness.World.Bounds.SizeY);
        Assert.AreEqual(32, harness.World.Bounds.SizeZ);
        Assert.AreEqual(16, harness.World.ChunkSize);
        Assert.AreEqual(8, VoxelChunks.All(harness.World.Identity).Count);
        Assert.AreEqual(1088, harness.World.VoxelCount);
        Assert.AreEqual(VoxelWorldIdentity.Slice2.Seed, harness.World.Identity.Seed);
        Assert.AreEqual(VoxelWorldIdentity.Slice2.GeneratorVersion, harness.World.Identity.GeneratorVersion);

        var ground = harness.Commands.Query(new WorldVoxelCoord(0, 0, 0));
        Assert.IsTrue(ground.IsOccupied);
        Assert.AreEqual(VoxelWorldFactory.DevelopmentDefinitionId, ground.Voxel!.DefinitionId);
        Assert.AreEqual(VoxelOrientation.Identity, ground.Voxel.Orientation);
        Assert.AreEqual(100, ground.Voxel.CurrentDurability);
        Assert.IsTrue(harness.Commands.Query(new WorldVoxelCoord(31, 0, 31)).IsOccupied);
        Assert.IsFalse(harness.Commands.Query(new WorldVoxelCoord(0, 1, 0)).IsOccupied);

        var cube = harness.Commands.Query(new WorldVoxelCoord(14, 14, 14));
        Assert.IsTrue(cube.IsOccupied);
        Assert.AreEqual(VoxelOrientation.Identity, cube.Voxel!.Orientation);
        Assert.AreEqual(100, cube.Voxel.CurrentDurability);
        Assert.IsTrue(harness.Commands.Query(new WorldVoxelCoord(17, 17, 17)).IsOccupied);
        Assert.IsFalse(harness.Commands.Query(new WorldVoxelCoord(13, 14, 14)).IsOccupied);
        Assert.IsFalse(harness.Commands.Query(new WorldVoxelCoord(14, 13, 14)).IsOccupied);
    }

    [TestMethod]
    public void Same_generation_inputs_repeat_and_seed_is_identity_only()
    {
        using var first = MultiChunkHarness.Create();
        using var second = MultiChunkHarness.Create();
        var otherSeed = VoxelWorldIdentity.Slice2 with { Seed = 99 };
        using var third = MultiChunkHarness.Create(otherSeed);

        AssertState(first, Snapshot(second));
        AssertState(first, Snapshot(third));
        Assert.AreEqual(VoxelWorldIdentity.Slice2.Seed, first.World.Identity.Seed);
        Assert.AreEqual(99ul, third.World.Identity.Seed);
        Assert.AreEqual(first.World.Identity.GeneratorVersion, third.World.Identity.GeneratorVersion);
        Assert.AreEqual(1088, third.World.VoxelCount);
    }

    [TestMethod]
    public void Reset_rebuilds_the_slice2_base_through_the_same_generation_entry()
    {
        using var harness = MultiChunkHarness.CreateSession();
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Remove(new WorldVoxelCoord(16, 16, 16)).Status);
        Assert.AreEqual(WorldCommandStatus.Added, harness.Session.Add(
            new WorldVoxelCoord(0, 1, 0),
            VoxelWorldFactory.DevelopmentDefinitionId,
            VoxelOrientation.Identity).Status);
        Assert.AreEqual(1088, harness.Session.World.VoxelCount);

        harness.Session.Reset();

        Assert.AreEqual(VoxelWorldIdentity.Slice2, harness.Session.Identity);
        Assert.AreEqual(1088, harness.Session.World.VoxelCount);
        Assert.IsTrue(harness.Session.Query(new WorldVoxelCoord(16, 16, 16)).IsOccupied);
        Assert.IsFalse(harness.Session.Query(new WorldVoxelCoord(0, 1, 0)).IsOccupied);
        Assert.AreEqual(100, harness.Session.Query(new WorldVoxelCoord(15, 16, 15)).Voxel!.CurrentDurability);
    }

    [TestMethod]
    public void Occupancy_air_and_orientation_rules_do_not_change_across_chunks()
    {
        using var harness = MultiChunkHarness.Create();
        var empty = new WorldVoxelCoord(16, 1, 16);

        Assert.IsFalse(harness.Commands.Query(empty).IsOccupied);
        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(
            empty, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Yaw90).Status);
        Assert.AreEqual(WorldCommandStatus.Occupied, harness.Commands.Add(
            empty, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Status);
        Assert.AreEqual(VoxelOrientation.Yaw90, harness.Commands.Query(empty).Voxel!.Orientation);
        Assert.AreEqual(WorldCommandStatus.DisallowedOrientation, harness.Commands.Add(
            new WorldVoxelCoord(17, 1, 16),
            new VoxelDefinitionId("core", "fixed"),
            VoxelOrientation.Yaw90).Status);
        Assert.IsFalse(harness.Commands.Query(new WorldVoxelCoord(17, 1, 16)).IsOccupied);

        foreach (var orientation in VoxelOrientation.All)
        {
            var cell = new WorldVoxelCoord(18, 1, 16);
            Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(
                cell, VoxelWorldFactory.DevelopmentDefinitionId, orientation).Status, orientation.ToString());
            Assert.AreEqual(orientation, harness.Commands.Query(cell).Voxel!.Orientation);
            Assert.AreEqual(WorldCommandStatus.Removed, harness.Commands.Remove(cell).Status);
        }
    }

    [TestMethod]
    public void Face_culling_uses_world_neighbors_on_each_15_16_axis_and_the_junction()
    {
        using var harness = MultiChunkHarness.Create();
        var xMinus = new WorldVoxelCoord(15, 16, 15);
        var xPlus = new WorldVoxelCoord(16, 16, 15);
        var yMinus = new WorldVoxelCoord(15, 15, 15);
        var yPlus = new WorldVoxelCoord(15, 16, 15);
        var zMinus = new WorldVoxelCoord(15, 16, 15);
        var zPlus = new WorldVoxelCoord(15, 16, 16);
        var junctionMinus = new WorldVoxelCoord(15, 15, 15);
        var junctionPlus = new WorldVoxelCoord(16, 16, 16);

        var initial = VisibleFaces(harness.World);
        Assert.IsFalse(initial.Contains((xMinus, VoxelFaceNormal.PositiveX)));
        Assert.IsFalse(initial.Contains((xPlus, VoxelFaceNormal.NegativeX)));
        Assert.IsFalse(initial.Contains((yMinus, VoxelFaceNormal.PositiveY)));
        Assert.IsFalse(initial.Contains((yPlus, VoxelFaceNormal.NegativeY)));
        Assert.IsFalse(initial.Contains((zMinus, VoxelFaceNormal.PositiveZ)));
        Assert.IsFalse(initial.Contains((zPlus, VoxelFaceNormal.NegativeZ)));

        Assert.AreEqual(WorldCommandStatus.Removed, harness.Commands.Remove(xPlus).Status);
        var afterX = VisibleFaces(harness.World);
        Assert.IsTrue(afterX.Contains((xMinus, VoxelFaceNormal.PositiveX)));
        Assert.IsFalse(afterX.Any(face => face.Position == xPlus));

        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(
            xPlus, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Commands.Remove(yMinus).Status);
        var afterY = VisibleFaces(harness.World);
        Assert.IsTrue(afterY.Contains((yPlus, VoxelFaceNormal.NegativeY)));
        Assert.IsFalse(afterY.Any(face => face.Position == yMinus));

        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(
            yMinus, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Commands.Remove(zPlus).Status);
        var afterZ = VisibleFaces(harness.World);
        Assert.IsTrue(afterZ.Contains((zMinus, VoxelFaceNormal.PositiveZ)));
        Assert.IsFalse(afterZ.Any(face => face.Position == zPlus));

        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(
            zPlus, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Commands.Remove(junctionPlus).Status);
        var afterJunction = VisibleFaces(harness.World);
        Assert.IsTrue(afterJunction.Contains((new WorldVoxelCoord(15, 16, 16), VoxelFaceNormal.PositiveX)));
        Assert.IsTrue(afterJunction.Contains((new WorldVoxelCoord(16, 15, 16), VoxelFaceNormal.PositiveY)));
        Assert.IsTrue(afterJunction.Contains((new WorldVoxelCoord(16, 16, 15), VoxelFaceNormal.PositiveZ)));
        Assert.IsFalse(afterJunction.Any(face => face.Position == junctionPlus));
        Assert.IsFalse(afterJunction.Contains((junctionMinus, VoxelFaceNormal.PositiveX)));
        Assert.IsFalse(afterJunction.Contains((junctionMinus, VoxelFaceNormal.PositiveY)));
        Assert.IsFalse(afterJunction.Contains((junctionMinus, VoxelFaceNormal.PositiveZ)));

        var chunkUnion = VoxelChunks.All(harness.World.Identity)
            .SelectMany(chunk => VoxelSurfaceMesher.Build(harness.World, chunk).Faces)
            .Select(face => (face.Position, face.Normal))
            .ToHashSet();
        Assert.IsTrue(VisibleFaces(harness.World).SetEquals(chunkUnion));
    }

    [TestMethod]
    public void Same_event_cross_chunk_damage_is_atomic_and_rejected_batches_keep_state()
    {
        using var harness = MultiChunkHarness.Create();
        var before = Snapshot(harness);
        var empty = new WorldVoxelCoord(0, 1, 0);

        var rejected = harness.Commands.Damage(new[]
        {
            new VoxelDamageTarget(new WorldVoxelCoord(15, 16, 15), 100),
            new VoxelDamageTarget(new WorldVoxelCoord(16, 16, 15), 100),
            new VoxelDamageTarget(empty, 25)
        });
        Assert.AreEqual(WorldCommandStatus.Empty, rejected.Status);
        Assert.AreEqual(empty, rejected.Position);
        Assert.AreEqual(0, rejected.Targets.Count);
        AssertState(harness, before);

        var applied = harness.Commands.Damage(new[]
        {
            new VoxelDamageTarget(new WorldVoxelCoord(15, 16, 15), 100),
            new VoxelDamageTarget(new WorldVoxelCoord(16, 16, 16), 100),
            new VoxelDamageTarget(new WorldVoxelCoord(15, 16, 16), 25)
        });
        Assert.IsTrue(applied.Succeeded);
        Assert.AreEqual(1086, harness.World.VoxelCount);
        Assert.IsFalse(harness.Commands.Query(new WorldVoxelCoord(15, 16, 15)).IsOccupied);
        Assert.IsFalse(harness.Commands.Query(new WorldVoxelCoord(16, 16, 16)).IsOccupied);
        Assert.AreEqual(75, harness.Commands.Query(new WorldVoxelCoord(15, 16, 16)).Voxel!.CurrentDurability);

        var faces = VisibleFaces(harness.World);
        Assert.IsTrue(faces.Contains((new WorldVoxelCoord(16, 16, 15), VoxelFaceNormal.NegativeX)));
        Assert.IsTrue(faces.Contains((new WorldVoxelCoord(15, 15, 15), VoxelFaceNormal.PositiveY)));
        Assert.IsFalse(faces.Any(face => face.Position == new WorldVoxelCoord(15, 16, 15)));
    }

    [TestMethod]
    public void Local_edits_dirty_only_necessary_chunks_and_coalesce_before_refresh()
    {
        using var harness = MultiChunkHarness.CreateSession();
        var initial = harness.Session.TakePendingDirtyChunks();
        CollectionAssert.AreEquivalent(VoxelChunks.All(harness.Session.Identity).ToArray(), initial.ToArray());

        Assert.AreEqual(WorldCommandStatus.Added, harness.Session.Add(
            new WorldVoxelCoord(8, 1, 8),
            VoxelWorldFactory.DevelopmentDefinitionId,
            VoxelOrientation.Identity).Status);
        CollectionAssert.AreEqual(new[] { new ChunkCoord(0, 0, 0) }, harness.Session.TakePendingDirtyChunks().ToArray());

        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Remove(new WorldVoxelCoord(15, 0, 8)).Status);
        CollectionAssert.AreEqual(
            new[] { new ChunkCoord(0, 0, 0), new ChunkCoord(1, 0, 0) },
            harness.Session.TakePendingDirtyChunks().ToArray());

        Assert.AreEqual(WorldCommandStatus.Damaged, harness.Session.Damage(new WorldVoxelCoord(16, 16, 16), 25).Status);
        CollectionAssert.AreEqual(new[] { new ChunkCoord(1, 1, 1) }, harness.Session.TakePendingDirtyChunks().ToArray());

        Assert.AreEqual(WorldCommandStatus.Added, harness.Session.Add(
            new WorldVoxelCoord(16, 1, 8),
            VoxelWorldFactory.DevelopmentDefinitionId,
            VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Remove(new WorldVoxelCoord(16, 16, 16)).Status);
        CollectionAssert.AreEquivalent(
            new[]
            {
                new ChunkCoord(0, 0, 0),
                new ChunkCoord(1, 0, 0),
                new ChunkCoord(1, 0, 1),
                new ChunkCoord(1, 1, 0),
                new ChunkCoord(1, 1, 1),
                new ChunkCoord(0, 1, 1)
            },
            harness.Session.TakePendingDirtyChunks().ToArray());

        var rejected = harness.Session.Damage(new[]
        {
            new VoxelDamageTarget(new WorldVoxelCoord(16, 16, 15), 100),
            new VoxelDamageTarget(new WorldVoxelCoord(32, 0, 0), 100)
        });
        Assert.AreEqual(WorldCommandStatus.OutOfBounds, rejected.Status);
        Assert.AreEqual(0, harness.Session.TakePendingDirtyChunks().Count);
        Assert.AreEqual(1088, harness.Session.World.VoxelCount);
    }

    [TestMethod]
    public void Console_and_session_share_the_slice2_command_boundary()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice2);
        var dispatcher = new ConsoleCommandDispatcher(
            session.Logger,
            static () => { },
            static () => { },
            static () => { },
            allowReload: true,
            new VoxelConsoleCommands(voxelSession));

        dispatcher.Execute("voxel query 15 16 15");
        dispatcher.Execute("voxel remove 16 16 15");
        dispatcher.Execute("voxel add 16 16 15 core:ferrous-ore px_py");
        dispatcher.Execute("voxel damage 15 16 15 100 16 16 16 25");
        Assert.IsFalse(voxelSession.Query(new WorldVoxelCoord(15, 16, 15)).IsOccupied);
        Assert.AreEqual(75, voxelSession.Query(new WorldVoxelCoord(16, 16, 16)).Voxel!.CurrentDurability);
        Assert.AreEqual(1087, voxelSession.World.VoxelCount);

        dispatcher.Execute("voxel reset discard");
        Assert.AreEqual(1088, voxelSession.World.VoxelCount);
        Assert.IsTrue(voxelSession.Query(new WorldVoxelCoord(15, 16, 15)).IsOccupied);

        var before = voxelSession.World.VoxelCount;
        dispatcher.Execute("voxel add 32 0 0 core:ferrous-ore px_py");
        Assert.AreEqual(before, voxelSession.World.VoxelCount);
    }

    [TestMethod]
    public void Slice2_live_reload_keeps_absolute_durability_and_rejects_without_mutating_the_world()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice2);
        reloader.SetWorldReloadAdapter(VoxelWorldReloadAdapter.For(voxelSession));
        var cell = new WorldVoxelCoord(15, 16, 15);
        Assert.AreEqual(WorldCommandStatus.Damaged, voxelSession.Damage(cell, 20).Status);
        Assert.AreEqual(80, voxelSession.Query(cell).Voxel!.CurrentDurability);

        source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明", value: 250, mass: 2, maxDurability: 150)));
        var risen = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, risen.Status);
        Assert.AreEqual(1088, voxelSession.World.VoxelCount);
        Assert.AreEqual(80, voxelSession.Query(cell).Voxel!.CurrentDurability);
        Assert.AreEqual(150, voxelSession.Query(cell).MaxDurability);
        Assert.AreEqual(250, voxelSession.Query(cell).Value);

        source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明", value: 250, mass: 2, maxDurability: 60)));
        var clamped = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, clamped.Status);
        Assert.AreEqual(60, voxelSession.Query(cell).Voxel!.CurrentDurability);
        Assert.AreEqual(60, voxelSession.Query(cell).MaxDurability);

        var yaw = new WorldVoxelCoord(0, 1, 0);
        Assert.AreEqual(WorldCommandStatus.Added, voxelSession.Add(
            yaw, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Yaw90).Status);
        var generation = reloader.Catalog.Current!.Generation;
        var snapshot = reloader.Catalog.Current;
        source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明", value: 250, mass: 2, maxDurability: 60, orientations: "none")));
        var rejected = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status);
        Assert.AreSame(snapshot, reloader.Catalog.Current);
        Assert.AreEqual(generation, rejected.CatalogVersion);
        Assert.AreEqual(VoxelOrientation.Yaw90, voxelSession.Query(yaw).Voxel!.Orientation);
        Assert.AreEqual(60, voxelSession.Query(cell).Voxel!.CurrentDurability);
        Assert.AreEqual(1089, voxelSession.World.VoxelCount);
    }

    private static IReadOnlyDictionary<WorldVoxelCoord, WorldVoxel> Snapshot(MultiChunkHarness harness)
    {
        var snapshot = new Dictionary<WorldVoxelCoord, WorldVoxel>();
        foreach (var (position, voxel) in harness.World.OccupiedVoxels())
            snapshot.Add(position, voxel);
        return snapshot;
    }

    private static void AssertState(MultiChunkHarness harness, IReadOnlyDictionary<WorldVoxelCoord, WorldVoxel> expected)
    {
        var actual = Snapshot(harness);
        Assert.AreEqual(expected.Count, actual.Count);
        foreach (var (position, voxel) in expected)
        {
            Assert.IsTrue(actual.TryGetValue(position, out var found), position.ToString());
            Assert.AreEqual(voxel.DefinitionId, found.DefinitionId);
            Assert.AreEqual(voxel.Orientation, found.Orientation);
            Assert.AreEqual(voxel.CurrentDurability, found.CurrentDurability);
        }
    }

    private static HashSet<(WorldVoxelCoord Position, VoxelFaceNormal Normal)> VisibleFaces(VoxelWorld world) =>
        VoxelSurfaceMesher.Build(world).Faces.Select(face => (face.Position, face.Normal)).ToHashSet();

    private sealed class MultiChunkHarness : IDisposable
    {
        private readonly LoggerSession _session;

        public VoxelWorld World { get; }
        public VoxelWorldCommands Commands { get; }
        public VoxelDevelopmentSession Session { get; }

        private MultiChunkHarness(
            LoggerSession session,
            VoxelWorld world,
            VoxelWorldCommands commands,
            VoxelDevelopmentSession voxelSession)
        {
            _session = session;
            World = world;
            Commands = commands;
            Session = voxelSession;
        }

        public static MultiChunkHarness Create(VoxelWorldIdentity? identity = null)
        {
            var session = LoggerSession.Create();
            var source = ContentFixtures.ValidFerrousOre();
            source.AddVoxelFile("core", "voxels/fixed.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("fixed", "固定", "固定方向", orientations: "none")));
            var reloader = ContentFixtures.Create(source, session.Logger);
            Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
            var world = VoxelWorldFactory.Create(reloader.Catalog.Current!, identity ?? VoxelWorldIdentity.Slice2);
            return new MultiChunkHarness(
                session,
                world,
                new VoxelWorldCommands(world, reloader.Catalog),
                new VoxelDevelopmentSession(reloader.Catalog, identity ?? VoxelWorldIdentity.Slice2));
        }

        public static MultiChunkHarness CreateSession()
        {
            var session = LoggerSession.Create();
            var source = ContentFixtures.ValidFerrousOre();
            var reloader = ContentFixtures.Create(source, session.Logger);
            Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
            var voxelSession = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice2);
            return new MultiChunkHarness(session, voxelSession.World, voxelSession.Commands, voxelSession);
        }

        public void Dispose() => _session.Dispose();
    }
}
