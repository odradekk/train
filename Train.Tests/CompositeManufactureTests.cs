using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:composites")]
public sealed class CompositeManufactureTests
{
    [TestMethod]
    public void Formal_fixture_manufactures_168_parts_with_stable_ids_and_fixed_pose()
    {
        var (reloader, logger) = CompositeFixtures.Load(CompositeFixtures.FormalFixturePackage());
        using (logger)
        {
            var session = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
            Assert.AreEqual(0, session.World.VoxelCount);
            Assert.AreEqual(42UL, session.Identity.Seed);
            Assert.AreEqual(16, session.World.ChunkSize);

            var before = session.CaptureAuthority();
            var first = session.Manufacture(HollowShellFixture.DefinitionId);
            Assert.IsTrue(first.Succeeded);
            var vehicleId = first.VehicleId!.Value;
            var query = session.QueryComposite(vehicleId)!;
            Assert.AreEqual(168, query.Parts.Count);
            Assert.AreEqual(HollowShellFixture.DefaultTransform, query.Transform);
            Assert.AreEqual(ConstructionStatus.Valid, query.Construction);
            Assert.AreEqual(IdentityRetentionStatus.Retained, query.IdentityRetention);
            Assert.AreEqual(OperationalCondition.Unevaluated, query.OperationalCondition);
            Assert.AreEqual(0, session.World.VoxelCount);
            Assert.AreEqual(before.WorldVoxelCount, session.World.VoxelCount);

            var core = query.Parts.Single(part => part.Cell == HollowShellFixture.CoreCell);
            var rear = query.Parts.Single(part => part.Cell == HollowShellFixture.RearBoundCell);
            Assert.AreEqual(new VoxelDefinitionId("core", "identity-core"), core.DefinitionId);
            Assert.AreEqual(query.Contacts[0].BoundPartId, core.Id);
            Assert.AreEqual(query.Contacts[1].BoundPartId, rear.Id);
            Assert.AreEqual(1.0, query.Contacts[0].X);
            Assert.AreEqual(-0.75, query.Contacts[0].Y);
            Assert.AreEqual(HollowShellFixture.CoreCell, query.Contacts[0].BoundCell);
            Assert.AreNotEqual(query.Contacts[0].Y, query.Contacts[0].BoundCell.Y);
            Assert.AreEqual(new VehicleEnvelopeBox(0, 0, -1, 8, 6, 4), query.Envelope);

            var allocated = first.Parts!.Select(part => part.Id.Value).ToArray();
            CollectionAssert.AreEqual(allocated.OrderBy(id => id).ToArray(), allocated);
            Assert.AreEqual(vehicleId.Value, allocated.Min() - 1);

            session.Reset();
            var second = session.Manufacture(HollowShellFixture.DefinitionId);
            CollectionAssert.AreEqual(
                first.Parts!.Select(part => (part.Cell, part.DefinitionId)).ToArray(),
                second.Parts!.Select(part => (part.Cell, part.DefinitionId)).ToArray());
            CollectionAssert.AreEqual(
                first.Parts!.Select(part => part.Id.Value).ToArray(),
                second.Parts!.Select(part => part.Id.Value).ToArray());
        }
    }

    [TestMethod]
    public void Illegal_designs_leave_world_vehicles_and_counters_unchanged()
    {
        var (reloader, logger) = CompositeFixtures.Load(CompositeFixtures.FormalFixtureWithIllegalDesigns());
        using (logger)
        {
            var session = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
            session.Add(new WorldVoxelCoord(1, 1, 1), new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Identity);
            var before = session.CaptureAuthority();

            Assert.AreEqual(ConstructionStatus.UnknownDefinition, session.Manufacture(new CompositeDefinitionId("core", "missing-car")).Status);
            Assert.AreEqual(before, session.CaptureAuthority());
            Assert.AreEqual(ConstructionStatus.Disconnected, session.Manufacture(new CompositeDefinitionId("core", "disconnected")).Status);
            Assert.AreEqual(before, session.CaptureAuthority());
            Assert.AreEqual(ConstructionStatus.DuplicateOccupancy, session.Manufacture(new CompositeDefinitionId("core", "duplicate-cell")).Status);
            Assert.AreEqual(before, session.CaptureAuthority());
            Assert.AreEqual(ConstructionStatus.MissingContact, session.Manufacture(new CompositeDefinitionId("core", "missing-contact")).Status);
            Assert.AreEqual(before, session.CaptureAuthority());
            Assert.AreEqual(ConstructionStatus.DuplicateContact, session.Manufacture(new CompositeDefinitionId("core", "extra-contact")).Status);
            Assert.AreEqual(before, session.CaptureAuthority());
            Assert.AreEqual(ConstructionStatus.UnboundContact, session.Manufacture(new CompositeDefinitionId("core", "unbound-contact")).Status);
            Assert.AreEqual(before, session.CaptureAuthority());
            Assert.AreEqual(ConstructionStatus.EnvelopeExceeded, session.Manufacture(new CompositeDefinitionId("core", "envelope-overflow")).Status);
            Assert.AreEqual(before, session.CaptureAuthority());
            Assert.AreEqual(ConstructionStatus.DisallowedOrientation, session.Manufacture(new CompositeDefinitionId("core", "locked-orientation")).Status);
            Assert.AreEqual(before, session.CaptureAuthority());

            using var isolated = LoggerSession.Create();
            var source = CompositeFixtures.FormalFixturePackage();
            source.AddCompositeFile("core", "composites/broken.composites.json", CompositeFixtures.File(
                null,
                CompositeFixtures.Archetype("broken"),
                CompositeFixtures.Composite(
                    id: "broken",
                    archetype: "broken",
                    voxels: CompositeFixtures.Voxel(0, 0, 0, "missing"))));
            var rejected = ContentFixtures.Create(source, isolated.Logger);
            Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Reload().Status);
            Assert.AreEqual(before, session.CaptureAuthority());
        }
    }

    [TestMethod]
    public void Construction_failures_cover_disconnect_duplicate_and_contact_errors()
    {
        var (reloader, logger) = CompositeFixtures.Load();
        using (logger)
        {
            var session = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
            var before = session.CaptureAuthority();

            Assert.AreEqual(
                ConstructionStatus.Disconnected,
                ValidateMemory(reloader, new[] { new LocalVoxelCoord(0, 0, 0), new LocalVoxelCoord(2, 0, 0) },
                    new[] { new LocalVoxelCoord(0, 0, 0), new LocalVoxelCoord(2, 0, 0) }));
            Assert.AreEqual(
                ConstructionStatus.DuplicateOccupancy,
                ValidateMemory(reloader, new[] { new LocalVoxelCoord(0, 0, 0), new LocalVoxelCoord(0, 0, 0) },
                    new[] { new LocalVoxelCoord(0, 0, 0), new LocalVoxelCoord(1, 0, 0) }));
            Assert.AreEqual(
                ConstructionStatus.MissingContact,
                ValidateMemory(reloader, new[] { new LocalVoxelCoord(0, 0, 0), new LocalVoxelCoord(1, 0, 0) },
                    Array.Empty<LocalVoxelCoord>(), contactCount: 0));
            Assert.AreEqual(
                ConstructionStatus.UnboundContact,
                ValidateMemory(reloader, new[] { new LocalVoxelCoord(0, 0, 0), new LocalVoxelCoord(1, 0, 0) },
                    new[] { new LocalVoxelCoord(0, 0, 0), new LocalVoxelCoord(3, 0, 0) }));
            Assert.AreEqual(before, session.CaptureAuthority());
        }
    }

    [TestMethod]
    public void Identity_retention_is_independent_of_construction()
    {
        var (reloader, logger) = CompositeFixtures.Load();
        using (logger)
        {
            var definition = new CompositeDefinition(
                new CompositeDefinitionId("core", "no-core"),
                "无核心",
                "构造仍合法",
                HollowShellFixture.ArchetypeId,
                VehicleSizeClass.Small,
                new[]
                {
                    new CompositeVoxelSpec(new LocalVoxelCoord(0, 0, 0), new VoxelDefinitionId("core", "vehicle-shell"), VoxelOrientation.Identity),
                    new CompositeVoxelSpec(new LocalVoxelCoord(1, 0, 0), new VoxelDefinitionId("core", "vehicle-shell"), VoxelOrientation.Identity)
                },
                new[]
                {
                    new CompositeContactAnchor(0, -0.75, 0.5, new LocalVoxelCoord(0, 0, 0)),
                    new CompositeContactAnchor(1, -0.75, 0.5, new LocalVoxelCoord(1, 0, 0))
                });
            var archetype = reloader.Composites.Current.Archetypes[HollowShellFixture.ArchetypeId].Archetype;
            Assert.AreEqual(
                ConstructionStatus.Valid,
                ConstructionValidator.Validate(definition, archetype, reloader.Catalog.Current!, HollowShellFixture.DefaultTransform));
            Assert.AreEqual(
                IdentityRetentionStatus.MissingCoreTag,
                ConstructionValidator.EvaluateIdentity(definition, archetype, reloader.Catalog.Current!));
        }
    }

    [TestMethod]
    public void Non_finite_transform_is_rejected_and_godot_types_are_not_authoritative()
    {
        Assert.IsFalse(CompositeTransform.TryCreate(double.NaN, 8, 8, VoxelOrientation.Identity, out _));
        Assert.IsFalse(CompositeTransform.TryCreate(8, double.PositiveInfinity, 8, VoxelOrientation.Identity, out _));
        var transform = CompositeTransform.Translation(8, 8, 8);
        Assert.AreEqual(8, transform.OriginX);
        Assert.AreEqual(VoxelOrientation.Identity, transform.Orientation);
        Assert.IsFalse(CompositeTransform.TryCreate(8, 8, 8, 1, 0, 0, 1, 0, 0, out _));
        Assert.IsTrue(CompositeTransform.TryCreate(8, 8, 8, 1, 0, 1, 0, 1, 0, out var continuous));
        Assert.AreEqual(Math.Sqrt(0.5), continuous.RightX, 1e-12);
        Assert.AreEqual(VoxelOrientation.Identity, continuous.Orientation);
    }

    [TestMethod]
    public void Default_transform_is_rejected_at_manufacture_and_does_not_advance_ids()
    {
        var (reloader, logger) = CompositeFixtures.Load(CompositeFixtures.FormalFixturePackage());
        using (logger)
        {
            var session = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
            var before = session.CaptureAuthority();
            Assert.IsFalse(default(CompositeTransform).IsValid);
            Assert.IsTrue(HollowShellFixture.DefaultTransform.IsValid);
            var rejected = session.Manufacture(BridgeFixture.DefinitionId, default(CompositeTransform));
            Assert.AreEqual(ConstructionStatus.NonFiniteTransform, rejected.Status);
            Assert.IsFalse(rejected.Succeeded);
            Assert.AreEqual(before, session.CaptureAuthority());
            Assert.AreEqual(1UL, session.NextEntityId);
            Assert.AreEqual(0UL, session.TransactionCount);

            Assert.IsTrue(session.Manufacture(BridgeFixture.DefinitionId, HollowShellFixture.DefaultTransform).Succeeded);
            Assert.AreEqual(1, session.QueryComposites().Count);
            Assert.AreEqual(55, session.QueryComposites()[0].Parts.Count);
        }
    }

    [TestMethod]
    public void Rebuilding_the_derived_mesh_does_not_change_identity_or_layout()
    {
        var (reloader, logger) = CompositeFixtures.Load(CompositeFixtures.FormalFixturePackage());
        using (logger)
        {
            var session = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
            var manufactured = session.Manufacture(HollowShellFixture.DefinitionId);
            var before = session.CaptureAuthority();
            var mesh = CompositeSurfaceMesher.Build(session.Vehicles[0]);
            Assert.AreEqual(1, mesh.BatchCount);
            Assert.IsTrue(mesh.FaceCount > 0);
            Assert.AreEqual(before, session.CaptureAuthority());
            Assert.AreEqual(manufactured.VehicleId, session.Vehicles[0].Id);
            Assert.AreEqual(0, session.World.VoxelCount);
        }
    }

    private static ConstructionStatus ValidateMemory(
        ContentReloader reloader,
        IReadOnlyList<LocalVoxelCoord> cells,
        IReadOnlyList<LocalVoxelCoord> boundCells,
        int? contactCount = null)
    {
        var voxels = cells.Select(cell =>
            new CompositeVoxelSpec(cell, new VoxelDefinitionId("core", "vehicle-shell"), VoxelOrientation.Identity)).ToArray();
        var contacts = (contactCount ?? 2) == 0
            ? Array.Empty<CompositeContactAnchor>()
            : boundCells.Select((cell, index) => new CompositeContactAnchor(index, -0.75, 0.5, cell)).ToArray();
        var definition = new CompositeDefinition(
            new CompositeDefinitionId("core", "probe"),
            "探测",
            "构造探测",
            HollowShellFixture.ArchetypeId,
            VehicleSizeClass.Small,
            voxels,
            contacts);
        return ConstructionValidator.Validate(
            definition,
            reloader.Composites.Current.Archetypes[HollowShellFixture.ArchetypeId].Archetype,
            reloader.Catalog.Current!,
            HollowShellFixture.DefaultTransform);
    }
}
