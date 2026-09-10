using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
public sealed class CompositeReloadSaveRejectionTests
{
    [TestMethod]
    public void Save_rejects_when_vehicles_exist_and_leaves_the_session_unchanged()
    {
        var (reloader, logger) = CompositeFixtures.Load(CompositeFixtures.FormalFixturePackage());
        using (logger)
        {
            var store = new RecordingStore();
            var session = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, store, reloader.Composites);
            session.Manufacture(HollowShellFixture.DefinitionId);
            var before = session.CaptureAuthority();
            var saved = session.Save("Should Fail");
            Assert.AreEqual(WorldSaveStatus.UnsupportedVehicles, saved.Status);
            Assert.IsFalse(saved.Succeeded);
            Assert.IsFalse(store.Wrote);
            Assert.AreEqual(before, session.CaptureAuthority());
        }
    }

    [TestMethod]
    public void Load_rejects_when_vehicles_exist_and_leaves_the_session_unchanged()
    {
        var (reloader, logger) = CompositeFixtures.Load(CompositeFixtures.FormalFixturePackage());
        using (logger)
        {
            var store = new RecordingStore();
            var session = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, store, reloader.Composites);
            session.Manufacture(HollowShellFixture.DefinitionId);
            var before = session.CaptureAuthority();
            var loaded = session.Load(Guid.Parse("11111111-2222-4333-8444-555555555555"));
            Assert.AreEqual(WorldSaveStatus.UnsupportedVehicles, loaded.Status);
            Assert.IsFalse(loaded.Succeeded);
            Assert.AreEqual(before, session.CaptureAuthority());
        }
    }

    [TestMethod]
    public void Hot_reload_rejects_missing_vehicle_references_and_unsupported_adaptation()
    {
        using var session = LoggerSession.Create();
        var source = CompositeFixtures.FormalFixturePackage();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
        voxelSession.Manufacture(HollowShellFixture.DefinitionId);
        reloader.SetWorldReloadAdapter(VoxelWorldReloadAdapter.For(voxelSession));
        var before = voxelSession.CaptureAuthority();

        var later = CompositeFixtures.FormalFixturePackage();
        later.AddVoxelFile("core", "voxels/identity-core.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("identity-core", "核心", "核心说明", value: 20, tagsJson: "[\"identity_core\"]")));
        var adapted = ContentFixtures.Create(later, session.Logger, catalog: reloader.Catalog, settlements: reloader.Settlements, composites: reloader.Composites);
        adapted.SetWorldReloadAdapter(VoxelWorldReloadAdapter.For(voxelSession));
        var semanticChange = adapted.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, semanticChange.Status);
        Assert.AreEqual(before, voxelSession.CaptureAuthority());

        var missing = ContentFixtures.ValidIron();
        var stripped = ContentFixtures.Create(missing, session.Logger, catalog: reloader.Catalog, settlements: reloader.Settlements, composites: reloader.Composites);
        stripped.SetWorldReloadAdapter(VoxelWorldReloadAdapter.For(voxelSession));
        var missingResult = stripped.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, missingResult.Status);
        Assert.AreEqual(before, voxelSession.CaptureAuthority());
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
        var rebuilt = ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog, settlements: reloader.Settlements, composites: reloader.Composites);
        rebuilt.SetWorldReloadAdapter(VoxelWorldReloadAdapter.For(voxelSession));
        var result = rebuilt.Reload();
        Assert.IsTrue(result.IsCommitted);
        Assert.AreEqual(before.Vehicles[0].Id, voxelSession.QueryComposites()[0].Id);
        Assert.AreEqual(168, voxelSession.QueryComposites()[0].Parts.Count);
    }

    private sealed class RecordingStore : IWorldSaveStore
    {
        public bool Wrote { get; private set; }

        public bool TryWrite(Guid id, string json, out WorldSaveStatus status)
        {
            Wrote = true;
            status = WorldSaveStatus.Saved;
            return true;
        }

        public bool TryRead(Guid id, out string json, out WorldSaveStatus status)
        {
            json = "";
            status = WorldSaveStatus.NotFound;
            return false;
        }
    }
}
