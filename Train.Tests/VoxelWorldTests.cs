using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Voxels;
using Train.Logging;

namespace Train.Tests;

[TestClass]
public sealed class VoxelWorldTests
{
    [TestMethod]
    public void Development_world_is_a_16_cubed_chunk_with_the_required_solid_block()
    {
        using var harness = WorldHarness.Create();

        Assert.AreEqual(16, harness.World.Bounds.SizeX);
        Assert.AreEqual(16, harness.World.Bounds.SizeY);
        Assert.AreEqual(16, harness.World.Bounds.SizeZ);
        Assert.AreEqual(512, harness.World.VoxelCount);

        var corner = harness.Commands.Query(new WorldVoxelCoord(4, 4, 4));
        Assert.IsTrue(corner.IsOccupied);
        Assert.AreEqual(new VoxelDefinitionId("core", "iron-ore"), corner.Voxel!.DefinitionId);
        Assert.AreEqual(100, corner.Voxel.CurrentDurability);
        Assert.AreEqual(VoxelOrientation.Identity, corner.Voxel.Orientation);
        Assert.IsFalse(harness.Commands.Query(new WorldVoxelCoord(3, 4, 4)).IsOccupied);
    }

    [TestMethod]
    public void Add_remove_and_read_only_query_share_the_formal_world_boundary()
    {
        using var harness = WorldHarness.Create();
        var position = new WorldVoxelCoord(4, 4, 4);

        var removed = harness.Commands.Remove(position);
        Assert.AreEqual(WorldCommandStatus.Removed, removed.Status);
        Assert.AreEqual(511, harness.World.VoxelCount);
        Assert.IsFalse(harness.Commands.Query(position).IsOccupied);

        var added = harness.Commands.Add(position, new VoxelDefinitionId("core", "iron-ore"), VoxelOrientation.Identity);
        Assert.AreEqual(WorldCommandStatus.Added, added.Status);
        Assert.AreEqual(512, harness.World.VoxelCount);
        var query = harness.Commands.Query(position);
        Assert.IsTrue(query.IsOccupied);
        Assert.AreEqual(100, query.Voxel!.CurrentDurability);

        Assert.AreEqual(WorldCommandStatus.Occupied, harness.Commands.Add(position, new VoxelDefinitionId("core", "iron-ore"), VoxelOrientation.Identity).Status);
        Assert.AreEqual(512, harness.World.VoxelCount);
        Assert.AreEqual(WorldCommandStatus.Empty, harness.Commands.Remove(new WorldVoxelCoord(0, 0, 0)).Status);
        Assert.IsFalse(harness.Commands.Query(new WorldVoxelCoord(0, 0, 0)).IsOccupied);
    }

    [TestMethod]
    public void Failed_adds_are_atomic_and_report_the_reason()
    {
        using var harness = WorldHarness.Create();
        var before = harness.World.VoxelCount;

        Assert.AreEqual(WorldCommandStatus.OutOfBounds, harness.Commands.Add(new WorldVoxelCoord(16, 4, 4), new VoxelDefinitionId("core", "iron-ore"), VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.UnknownDefinition, harness.Commands.Add(new WorldVoxelCoord(0, 0, 0), new VoxelDefinitionId("core", "unknown"), VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.DisallowedOrientation, harness.Commands.Add(new WorldVoxelCoord(0, 0, 0), new VoxelDefinitionId("core", "fixed"), VoxelOrientation.Yaw90).Status);

        Assert.AreEqual(before, harness.World.VoxelCount);
        Assert.IsFalse(harness.Commands.Query(new WorldVoxelCoord(0, 0, 0)).IsOccupied);
    }

    [TestMethod]
    public void All_24_orthogonal_orientations_are_unique_and_constraints_are_enforced()
    {
        Assert.AreEqual(24, VoxelOrientation.All.Count);
        Assert.AreEqual(24, VoxelOrientation.All.Distinct().Count());
        Assert.IsTrue(VoxelOrientation.All.All(orientation => orientation.IsOrthogonal));
        Assert.AreEqual(4, VoxelOrientation.FourWay.Count);

        using var harness = WorldHarness.Create();
        var position = new WorldVoxelCoord(0, 0, 0);
        foreach (var orientation in VoxelOrientation.All)
        {
            var add = harness.Commands.Add(position, new VoxelDefinitionId("core", "iron-ore"), orientation);
            Assert.AreEqual(WorldCommandStatus.Added, add.Status, orientation.ToString());
            Assert.AreEqual(orientation, harness.Commands.Query(position).Voxel!.Orientation);
            Assert.AreEqual(WorldCommandStatus.Removed, harness.Commands.Remove(position).Status);
        }
    }

    [TestMethod]
    public void Surface_mesher_culls_internal_faces_and_exposes_faces_after_removal()
    {
        using var harness = WorldHarness.Create();
        var initial = VoxelSurfaceMesher.Build(harness.World);
        Assert.AreEqual(384, initial.FaceCount);

        var internalPosition = new WorldVoxelCoord(5, 5, 5);
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Commands.Remove(internalPosition).Status);
        var afterRemoval = VoxelSurfaceMesher.Build(harness.World);
        Assert.AreEqual(initial.FaceCount + 6, afterRemoval.FaceCount);
        Assert.IsTrue(afterRemoval.Faces.Any(face => face.Position == new WorldVoxelCoord(4, 5, 5) && face.Normal == VoxelFaceNormal.PositiveX));
    }

    [TestMethod]
    public void Partial_damage_keeps_occupancy_definition_and_orientation()
    {
        using var harness = WorldHarness.Create();
        var position = new WorldVoxelCoord(4, 4, 4);
        var before = harness.Commands.Query(position);

        var result = harness.Commands.Damage(position, 25);

        Assert.AreEqual(WorldCommandStatus.Damaged, result.Status);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(512, harness.World.VoxelCount);
        var after = harness.Commands.Query(position);
        Assert.IsTrue(after.IsOccupied);
        Assert.AreEqual(before.Voxel!.DefinitionId, after.Voxel!.DefinitionId);
        Assert.AreEqual(before.Voxel.Orientation, after.Voxel.Orientation);
        Assert.AreEqual(75, after.Voxel.CurrentDurability);
    }

    [TestMethod]
    public void Exact_and_overkill_damage_remove_the_voxel()
    {
        using var harness = WorldHarness.Create();
        var first = new WorldVoxelCoord(4, 4, 4);
        var second = new WorldVoxelCoord(5, 4, 4);

        Assert.AreEqual(WorldCommandStatus.Damaged, harness.Commands.Damage(first, 25).Status);
        var exact = harness.Commands.Damage(first, 75);
        Assert.AreEqual(WorldCommandStatus.Removed, exact.Status);
        Assert.IsFalse(harness.Commands.Query(first).IsOccupied);
        Assert.AreEqual(511, harness.World.VoxelCount);

        var overkill = harness.Commands.Damage(second, 150);
        Assert.AreEqual(WorldCommandStatus.Removed, overkill.Status);
        Assert.IsFalse(harness.Commands.Query(second).IsOccupied);
        Assert.AreEqual(510, harness.World.VoxelCount);
    }

    [TestMethod]
    public void Failed_damage_is_atomic_and_does_not_create_voxels()
    {
        using var harness = WorldHarness.Create();
        var occupied = new WorldVoxelCoord(4, 4, 4);
        var empty = new WorldVoxelCoord(0, 0, 0);
        var before = Snapshot(harness);

        Assert.AreEqual(WorldCommandStatus.InvalidDamage, harness.Commands.Damage(occupied, 0).Status);
        Assert.AreEqual(WorldCommandStatus.InvalidDamage, harness.Commands.Damage(occupied, -1).Status);
        Assert.AreEqual(WorldCommandStatus.OutOfBounds, harness.Commands.Damage(new WorldVoxelCoord(16, 4, 4), 25).Status);
        Assert.AreEqual(WorldCommandStatus.Empty, harness.Commands.Damage(empty, 25).Status);
        Assert.IsFalse(harness.Commands.Query(empty).IsOccupied);

        var batch = harness.Commands.Damage(new[]
        {
            new VoxelDamageTarget(occupied, 25),
            new VoxelDamageTarget(empty, 25)
        });
        Assert.AreEqual(WorldCommandStatus.Empty, batch.Status);
        Assert.AreEqual(empty, batch.Position);
        Assert.AreEqual(0, batch.Targets.Count);

        AssertState(harness, before);
        Assert.AreEqual(100, harness.Commands.Query(occupied).Voxel!.CurrentDurability);
    }

    [TestMethod]
    public void Parent_spec_damage_sequence_reaches_the_required_occupancy_and_durability()
    {
        using var harness = WorldHarness.Create();
        var position = new WorldVoxelCoord(4, 4, 4);

        Assert.AreEqual(WorldCommandStatus.Damaged, harness.Commands.Damage(position, 25).Status);
        Assert.AreEqual(75, harness.Commands.Query(position).Voxel!.CurrentDurability);
        Assert.AreEqual(512, harness.World.VoxelCount);

        Assert.AreEqual(WorldCommandStatus.Removed, harness.Commands.Damage(position, 75).Status);
        Assert.IsFalse(harness.Commands.Query(position).IsOccupied);
        Assert.AreEqual(511, harness.World.VoxelCount);
        var exposed = VoxelSurfaceMesher.Build(harness.World);
        Assert.IsTrue(exposed.Faces.Any(face => face.Position == new WorldVoxelCoord(5, 4, 4) && face.Normal == VoxelFaceNormal.NegativeX));

        Assert.AreEqual(WorldCommandStatus.Added, harness.Commands.Add(position, new VoxelDefinitionId("core", "iron-ore"), VoxelOrientation.Identity).Status);
        Assert.AreEqual(100, harness.Commands.Query(position).Voxel!.CurrentDurability);
        Assert.AreEqual(512, harness.World.VoxelCount);
        Assert.AreEqual(WorldCommandStatus.Occupied, harness.Commands.Add(position, new VoxelDefinitionId("core", "iron-ore"), VoxelOrientation.Identity).Status);
        Assert.AreEqual(512, harness.World.VoxelCount);
        Assert.AreEqual(100, harness.Commands.Query(position).Voxel!.CurrentDurability);
    }

    [TestMethod]
    public void Internal_lethal_damage_opens_a_cavity_without_collapsing_neighbors()
    {
        using var harness = WorldHarness.Create();
        var cavity = new WorldVoxelCoord(5, 5, 5);

        var result = harness.Commands.Damage(cavity, 100);
        Assert.AreEqual(WorldCommandStatus.Removed, result.Status);
        Assert.IsFalse(harness.Commands.Query(cavity).IsOccupied);
        Assert.AreEqual(511, harness.World.VoxelCount);

        WorldVoxelCoord[] neighbors =
        [
            new(4, 5, 5), new(6, 5, 5), new(5, 4, 5), new(5, 6, 5), new(5, 5, 4), new(5, 5, 6)
        ];
        foreach (var neighbor in neighbors)
        {
            var query = harness.Commands.Query(neighbor);
            Assert.IsTrue(query.IsOccupied, neighbor.ToString());
            Assert.AreEqual(100, query.Voxel!.CurrentDurability);
        }

        var after = VoxelSurfaceMesher.Build(harness.World);
        Assert.IsTrue(after.Faces.Any(face => face.Position == new WorldVoxelCoord(4, 5, 5) && face.Normal == VoxelFaceNormal.PositiveX));
        Assert.IsTrue(after.Faces.Any(face => face.Position == new WorldVoxelCoord(6, 5, 5) && face.Normal == VoxelFaceNormal.NegativeX));
    }

    [TestMethod]
    public void Same_event_multi_target_damage_is_order_independent()
    {
        var firstOrder = new[]
        {
            new VoxelDamageTarget(new WorldVoxelCoord(4, 4, 4), 100),
            new VoxelDamageTarget(new WorldVoxelCoord(5, 4, 4), 100),
            new VoxelDamageTarget(new WorldVoxelCoord(6, 4, 4), 25)
        };
        var reversed = firstOrder.Reverse().ToArray();

        using var first = WorldHarness.Create();
        using var second = WorldHarness.Create();
        var firstResult = first.Commands.Damage(firstOrder);
        var secondResult = second.Commands.Damage(reversed);

        Assert.IsTrue(firstResult.Succeeded);
        Assert.IsTrue(secondResult.Succeeded);
        Assert.AreEqual(510, first.World.VoxelCount);
        Assert.AreEqual(510, second.World.VoxelCount);
        Assert.IsFalse(first.Commands.Query(new WorldVoxelCoord(4, 4, 4)).IsOccupied);
        Assert.IsFalse(first.Commands.Query(new WorldVoxelCoord(5, 4, 4)).IsOccupied);
        Assert.AreEqual(75, first.Commands.Query(new WorldVoxelCoord(6, 4, 4)).Voxel!.CurrentDurability);
        AssertState(first, Snapshot(second));
        Assert.IsTrue(VisibleFaces(first.World).SetEquals(VisibleFaces(second.World)));
    }

    private static IReadOnlyDictionary<WorldVoxelCoord, WorldVoxel> Snapshot(WorldHarness harness)
    {
        var snapshot = new Dictionary<WorldVoxelCoord, WorldVoxel>();
        var bounds = harness.World.Bounds;
        for (var x = 0; x < bounds.SizeX; x++)
        for (var y = 0; y < bounds.SizeY; y++)
        for (var z = 0; z < bounds.SizeZ; z++)
        {
            var query = harness.Commands.Query(new WorldVoxelCoord(x, y, z));
            if (query.Voxel is not null)
                snapshot.Add(query.Position, query.Voxel);
        }

        return snapshot;
    }

    private static void AssertState(WorldHarness harness, IReadOnlyDictionary<WorldVoxelCoord, WorldVoxel> expected)
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

    private sealed class WorldHarness : IDisposable
    {
        private readonly LoggerSession _session;

        public VoxelWorld World { get; }
        public VoxelWorldCommands Commands { get; }

        private WorldHarness(LoggerSession session, VoxelWorld world, VoxelWorldCommands commands)
        {
            _session = session;
            World = world;
            Commands = commands;
        }

        public static WorldHarness Create()
        {
            var session = LoggerSession.Create();
            var source = ContentFixtures.ValidIron();
            source.AddVoxelFile("core", "voxels/fixed.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("fixed", "固定", "固定方向", orientations: "none")));
            var reloader = ContentFixtures.Create(source, session.Logger);
            Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
            var world = VoxelWorldFactory.CreateDevelopmentWorld(reloader.Catalog.Current!);
            return new WorldHarness(session, world, new VoxelWorldCommands(world, reloader.Catalog));
        }

        public void Dispose() => _session.Dispose();
    }
}
