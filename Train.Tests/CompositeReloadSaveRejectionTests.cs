using Train.Core.Saves;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:persistence")]
public sealed class CompositeReloadSaveRejectionTests
{
    [TestMethod]
    public void Save_writes_vehicles_and_keeps_the_session_authority()
    {
        var (reloader, logger) = CompositeFixtures.Load(CompositeFixtures.FormalFixturePackage());
        using (logger)
        {
            var store = new RecordingStore();
            var session = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, store, reloader.Composites);
            session.Manufacture(HollowShellFixture.DefinitionId);
            var before = session.CaptureAuthority();
            var saved = session.Save("Should Persist");
            Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
            Assert.IsTrue(saved.Succeeded);
            Assert.IsTrue(store.Wrote);
            Assert.AreEqual(before, session.CaptureAuthority());
            Assert.AreEqual(168, session.QueryComposites()[0].Parts.Count);
        }
    }

    [TestMethod]
    public void Missing_save_with_vehicles_present_keeps_the_session_unchanged()
    {
        var (reloader, logger) = CompositeFixtures.Load(CompositeFixtures.FormalFixturePackage());
        using (logger)
        {
            var store = new RecordingStore();
            var session = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, store, reloader.Composites);
            session.Manufacture(HollowShellFixture.DefinitionId);
            var before = session.CaptureAuthority();
            var loaded = session.Load(Guid.Parse("11111111-2222-4333-8444-555555555555"));
            Assert.AreEqual(WorldSaveStatus.NotFound, loaded.Status);
            Assert.IsFalse(loaded.Succeeded);
            Assert.AreEqual(before, session.CaptureAuthority());
            Assert.AreEqual(ConstructionStatus.Valid, session.QueryComposites()[0].Construction);
        }
    }

    [TestMethod]
    public void Save_keeps_dropped_runtime_ids_on_slice_2_and_still_writes_ordinary_world_saves()
    {
        var (reloader, logger) = CompositeFixtures.Load(CompositeFixtures.FormalFixturePackage());
        using (logger)
        {
            var store = new RecordingStore();
            var session = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice2, store, reloader.Composites);
            var manufactured = session.Manufacture(BridgeFixture.DefinitionId);
            Assert.IsTrue(manufactured.Succeeded);
            var vehicleId = manufactured.VehicleId!.Value;
            Assert.IsTrue(session.Damage(new[]
            {
                new CompositeDamageTarget(vehicleId, BridgeFixture.BridgeCell, 100),
                new CompositeDamageTarget(vehicleId, BridgeFixture.LeftCoreCell, 100),
                new CompositeDamageTarget(vehicleId, BridgeFixture.RightCoreCell, 100)
            }).Succeeded);
            Assert.AreEqual(0, session.QueryComposites().Count);
            Assert.AreEqual(52, session.World.OccupiedVoxels().Count(item => item.Value.RuntimeId is not null));
            var before = session.CaptureAuthority();

            var saved = session.Save("Keep Identities");
            Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
            Assert.IsTrue(saved.Succeeded);
            Assert.IsTrue(store.Wrote);
            Assert.AreEqual(before, session.CaptureAuthority());
            Assert.AreEqual(52, session.World.OccupiedVoxels().Count(item => item.Value.RuntimeId is not null));

            var missing = session.Load(Guid.Parse("11111111-2222-4333-8444-555555555555"));
            Assert.AreEqual(WorldSaveStatus.NotFound, missing.Status);
            Assert.AreEqual(before, session.CaptureAuthority());

            var ordinaryStore = new RecordingStore();
            var ordinary = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice2, ordinaryStore, reloader.Composites);
            var ordinarySaved = ordinary.Save("Ordinary World");
            Assert.AreEqual(WorldSaveStatus.Saved, ordinarySaved.Status);
            Assert.IsTrue(ordinarySaved.Succeeded);
            Assert.IsTrue(ordinaryStore.Wrote);
        }
    }

    [TestMethod]
    public void Hot_reload_adapts_current_part_specs_and_still_rejects_missing_vehicle_references()
    {
        using var session = LoggerSession.Create();
        var source = CompositeFixtures.FormalFixturePackage();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
        voxelSession.Manufacture(HollowShellFixture.DefinitionId);
        reloader.SetReloadParticipant(VoxelWorldReloadAdapter.For(voxelSession));
        var before = voxelSession.CaptureAuthority();
        var coreRevision = reloader.Catalog.Current!.Entries[HollowShellFixture.CoreId].SemanticRevision;

        var later = CompositeFixtures.FormalFixturePackage();
        later.AddVoxelFile("core", "voxels/identity-core.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("identity-core", "核心", "核心说明", value: 20, tagsJson: "[\"identity_core\"]")));
        var adapted = ContentFixtures.Create(later, session.Logger, catalog: reloader.Catalog, composites: reloader.Composites);
        adapted.SetReloadParticipant(VoxelWorldReloadAdapter.For(voxelSession));
        var semanticChange = adapted.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, semanticChange.Status);
        Assert.AreEqual(before.Vehicles[0].Id, voxelSession.QueryComposites()[0].Id);
        Assert.AreEqual(168, voxelSession.QueryComposites()[0].Parts.Count);
        Assert.AreEqual(20, adapted.Catalog.Current!.Entries[HollowShellFixture.CoreId].Definition.Value);
        Assert.AreNotEqual(coreRevision, adapted.Catalog.Current.Entries[HollowShellFixture.CoreId].SemanticRevision);

        var missing = ContentFixtures.ValidFerrousOre();
        var stripped = ContentFixtures.Create(missing, session.Logger, catalog: reloader.Catalog, composites: reloader.Composites);
        stripped.SetReloadParticipant(VoxelWorldReloadAdapter.For(voxelSession));
        var afterAdapt = voxelSession.CaptureAuthority();
        var missingResult = stripped.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, missingResult.Status);
        Assert.AreEqual(afterAdapt, voxelSession.CaptureAuthority());
    }

    [TestMethod]
    public void Hot_reload_rejects_identity_rule_changes_that_cannot_retain_existing_vehicles()
    {
        using var session = LoggerSession.Create();
        var source = CompositeFixtures.FormalFixturePackage();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
        voxelSession.Manufacture(HollowShellFixture.DefinitionId);
        var before = voxelSession.CaptureAuthority();
        var catalogGeneration = reloader.Catalog.Current!.Generation;
        var compositeGeneration = reloader.Composites.Current.Generation;

        source.AddCompositeFile(
            "core",
            "composites/dev-hollow-shell.composites.json",
            CompositeFixtures.File(null, CompositeFixtures.Archetype(coreTag: "other_core"), CompositeFixtures.CompositeFromFixture()));
        var identity = ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog, composites: reloader.Composites);
        identity.SetReloadParticipant(VoxelWorldReloadAdapter.For(voxelSession));
        var identityReload = identity.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, identityReload.Status);
        Assert.IsTrue(identityReload.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.IncompatibleVoxelState));
        Assert.AreEqual(before, voxelSession.CaptureAuthority());
        Assert.AreEqual(ConstructionStatus.Valid, voxelSession.QueryComposites()[0].Construction);
        Assert.AreEqual(catalogGeneration, reloader.Catalog.Current.Generation);
        Assert.AreEqual(compositeGeneration, reloader.Composites.Current.Generation);
    }

    [TestMethod]
    public void Display_only_reload_is_allowed_while_vehicles_exist()
    {
        using var session = LoggerSession.Create();
        var source = CompositeFixtures.FormalFixturePackage();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
        voxelSession.Manufacture(HollowShellFixture.DefinitionId);
        var before = voxelSession.CaptureAuthority();

        source.AddCompositeFile(
            "core",
            "composites/dev-hollow-shell.composites.json",
            CompositeFixtures.FixtureFileText().Replace("开发空心壳", "显示名更新", StringComparison.Ordinal));
        var rebuilt = ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog, composites: reloader.Composites);
        rebuilt.SetReloadParticipant(VoxelWorldReloadAdapter.For(voxelSession));
        var result = rebuilt.Reload();
        Assert.IsTrue(result.IsCommitted);
        Assert.AreEqual(before.Vehicles[0].Id, voxelSession.QueryComposites()[0].Id);
        Assert.AreEqual(168, voxelSession.QueryComposites()[0].Parts.Count);
    }

    private sealed class RecordingStore : ISaveStore
    {
        public bool Wrote { get; private set; }

        public bool TryWrite(Guid id, string json, out SaveStoreStatus status)
        {
            Wrote = true;
            status = SaveStoreStatus.Saved;
            return true;
        }

        public bool TryRead(Guid id, out string json, out SaveStoreStatus status)
        {
            json = "";
            status = SaveStoreStatus.NotFound;
            return false;
        }
    }
}
