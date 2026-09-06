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
