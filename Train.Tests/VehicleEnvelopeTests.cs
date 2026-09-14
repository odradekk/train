using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
public sealed class VehicleEnvelopeTests
{
    [TestMethod]
    public void Five_tiers_accept_exact_boundary_and_reject_overflow_shift_and_unknown()
    {
        var (reloader, logger) = CompositeFixtures.Load();
        using (logger)
        {
            foreach (var size in Enum.GetValues<VehicleSizeClass>())
            {
                var dimensions = VehicleSize.Dimensions(size);
                var contacts = new[]
                {
                    new CompositeContactAnchor(1, -0.75, dimensions.Width / 2.0, new LocalVoxelCoord(1, 0, 0)),
                    new CompositeContactAnchor(dimensions.Length - 1, -0.75, dimensions.Width / 2.0, new LocalVoxelCoord(dimensions.Length - 1, 0, 0))
                };
                Assert.IsTrue(VehicleEnvelope.TryFromContacts(size, contacts, out var box));
                Assert.AreEqual(0, box.MinX);
                Assert.AreEqual(dimensions.Length, box.MaxX);
                Assert.AreEqual(0, box.MinY);
                Assert.AreEqual(dimensions.Height, box.MaxY);
                Assert.AreEqual(0, box.MinZ);
                Assert.AreEqual(dimensions.Width, box.MaxZ);

                var boundary = BuildBox(dimensions.Length, dimensions.Height, dimensions.Width, shiftZ: 0);
                Assert.AreEqual(ConstructionStatus.Valid, Validate(reloader, size, boundary, contacts));

                var overflowX = boundary.Append(new LocalVoxelCoord(dimensions.Length, 0, 0)).ToArray();
                Assert.AreEqual(ConstructionStatus.EnvelopeExceeded, Validate(reloader, size, overflowX, contacts));

                var overflowY = boundary.Append(new LocalVoxelCoord(0, dimensions.Height, 0)).ToArray();
                Assert.AreEqual(ConstructionStatus.EnvelopeExceeded, Validate(reloader, size, overflowY, contacts));

                var overflowZ = boundary.Append(new LocalVoxelCoord(0, 0, dimensions.Width)).ToArray();
                Assert.AreEqual(ConstructionStatus.EnvelopeExceeded, Validate(reloader, size, overflowZ, contacts));

                var shifted = BuildBox(dimensions.Length, dimensions.Height, dimensions.Width, shiftZ: 1);
                var shiftedContacts = new[]
                {
                    new CompositeContactAnchor(1, -0.75, dimensions.Width / 2.0, new LocalVoxelCoord(1, 0, 1)),
                    new CompositeContactAnchor(dimensions.Length - 1, -0.75, dimensions.Width / 2.0, new LocalVoxelCoord(dimensions.Length - 1, 0, 1))
                };
                Assert.AreEqual(ConstructionStatus.EnvelopeExceeded, Validate(reloader, size, shifted, shiftedContacts));
            }
        }
    }

    [TestMethod]
    public void Envelope_is_fixed_to_contacts_not_recentering_occupancy()
    {
        var (reloader, logger) = CompositeFixtures.Load();
        using (logger)
        {
            var contacts = new[]
            {
                new CompositeContactAnchor(1, -0.75, 2.5, new LocalVoxelCoord(1, 0, 2)),
                new CompositeContactAnchor(7, -0.75, 2.5, new LocalVoxelCoord(7, 0, 2))
            };
            var cells = new[]
            {
                new LocalVoxelCoord(1, 0, 2),
                new LocalVoxelCoord(2, 0, 2),
                new LocalVoxelCoord(3, 0, 2),
                new LocalVoxelCoord(4, 0, 2),
                new LocalVoxelCoord(5, 0, 2),
                new LocalVoxelCoord(6, 0, 2),
                new LocalVoxelCoord(7, 0, 2)
            };
            Assert.AreEqual(ConstructionStatus.Valid, Validate(reloader, VehicleSizeClass.Small, cells, contacts));
            Assert.IsTrue(VehicleEnvelope.TryFromContacts(VehicleSizeClass.Small, contacts, out var box));
            Assert.AreEqual(new VehicleEnvelopeBox(0, 0, 0, 8, 6, 5), box);
        }
    }

    [TestMethod]
    public void Unknown_size_class_is_rejected_by_the_parser()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        source.AddVoxelFile("core", "voxels/vehicle-shell.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("vehicle-shell", "壳体", "说明", value: 10, tagsJson: "[]")));
        source.AddVoxelFile("core", "voxels/identity-core.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("identity-core", "核心", "说明", value: 10, tagsJson: "[\"identity_core\"]")));
        ContentFixtures.AddSquareImages(source, "iron.png");
        source.AddCompositeFile(
            "core",
            "composites/bad.composites.json",
            CompositeFixtures.File(null, CompositeFixtures.Archetype(), CompositeFixtures.Composite(size: "huge")));
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Rejected, reloader.Reload().Status);
    }

    private static ConstructionStatus Validate(
        ContentReloader reloader,
        VehicleSizeClass size,
        IReadOnlyList<LocalVoxelCoord> cells,
        IReadOnlyList<CompositeContactAnchor> contacts)
    {
        var voxels = cells.Select((cell, index) =>
            new CompositeVoxelSpec(
                cell,
                index == 0 ? new VoxelDefinitionId("core", "identity-core") : new VoxelDefinitionId("core", "vehicle-shell"),
                VoxelOrientation.Identity)).ToArray();
        var definition = new CompositeDefinition(
            new CompositeDefinitionId("core", "probe"),
            "探测",
            "包络探测",
            HollowShellFixture.ArchetypeId,
            size,
            voxels,
            contacts);
        return ConstructionValidator.Validate(
            definition,
            reloader.Composites.Current.Archetypes[HollowShellFixture.ArchetypeId].Archetype,
            reloader.Catalog.Current!,
            HollowShellFixture.DefaultTransform);
    }

    private static LocalVoxelCoord[] BuildBox(int length, int height, int width, int shiftZ)
    {
        var cells = new List<LocalVoxelCoord>();
        for (var x = 0; x < length; x++)
        for (var y = 0; y < height; y++)
        for (var z = 0; z < width; z++)
            cells.Add(new LocalVoxelCoord(x, y, z + shiftZ));
        return cells.ToArray();
    }
}
