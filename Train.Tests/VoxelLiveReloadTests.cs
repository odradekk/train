using Train.Core.Content.Voxels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Domain.Voxels;
using Train.Core.Logging;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:voxels")]
public sealed class VoxelLiveReloadTests
{
    [TestMethod]
    public void Formal_damage_then_max_150_then_60_keeps_absolute_durability()
    {
        using var harness = LiveReloadHarness.Create();
        var corner = new WorldVoxelCoord(4, 4, 4);
        var other = new WorldVoxelCoord(5, 5, 5);

        Assert.AreEqual(WorldCommandStatus.Damaged, harness.Session.Damage(corner, 20).Status);
        Assert.AreEqual(WorldCommandStatus.Damaged, harness.Session.Damage(other, 10).Status);
        AssertInspection(harness, corner, durability: 80, value: 100, mass: 1, max: 100);
        AssertInspection(harness, other, durability: 90, value: 100, mass: 1, max: 100);

        var afterRise = harness.ReloadFerrousOre(maxDurability: 150, value: 250, mass: 2);
        Assert.AreEqual(ContentReloadStatus.Committed, afterRise.Status);
        Assert.IsTrue(afterRise.Changed);
        Assert.AreEqual(2, afterRise.CatalogVersion);
        Assert.AreEqual(512, harness.Session.World.VoxelCount);
        AssertInspection(harness, corner, durability: 80, value: 250, mass: 2, max: 150);
        AssertInspection(harness, other, durability: 90, value: 250, mass: 2, max: 150);
        Assert.AreEqual(VoxelOrientation.Identity, harness.Session.Query(corner).Voxel!.Orientation);

        var afterClamp = harness.ReloadFerrousOre(maxDurability: 60, value: 250, mass: 2);
        Assert.AreEqual(ContentReloadStatus.Committed, afterClamp.Status);
        Assert.AreEqual(3, afterClamp.CatalogVersion);
        Assert.AreEqual(512, harness.Session.World.VoxelCount);
        AssertInspection(harness, corner, durability: 60, value: 250, mass: 2, max: 60);
        AssertInspection(harness, other, durability: 60, value: 250, mass: 2, max: 60);
        Assert.AreEqual(new VoxelDefinitionId("core", "ferrous-ore"), harness.Session.Query(corner).Voxel!.DefinitionId);
        Assert.AreEqual(VoxelOrientation.Identity, harness.Session.Query(corner).Voxel!.Orientation);
    }

    [TestMethod]
    public void Mixed_candidate_rejects_and_does_not_leak_prepared_clamps()
    {
        using var harness = LiveReloadHarness.Create();
        var corner = new WorldVoxelCoord(4, 4, 4);
        var oriented = new WorldVoxelCoord(0, 0, 0);
        Assert.AreEqual(WorldCommandStatus.Damaged, harness.Session.Damage(corner, 20).Status);
        Assert.AreEqual(WorldCommandStatus.Added, harness.Session.Add(oriented, new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Yaw90).Status);
        var generation = harness.Reloader.Catalog.Current!.Generation;
        var snapshot = harness.Reloader.Catalog.Current;

        var rejected = harness.ReloadFerrousOre(maxDurability: 60, orientations: "none");

        Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status);
        Assert.IsFalse(rejected.Changed);
        Assert.AreEqual(generation, rejected.CatalogVersion);
        Assert.AreSame(snapshot, harness.Reloader.Catalog.Current);
        Assert.IsTrue(rejected.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.IncompatibleVoxelState));
        Assert.AreEqual(513, harness.Session.World.VoxelCount);
        AssertInspection(harness, corner, durability: 80, value: 100, mass: 1, max: 100);
        Assert.AreEqual(VoxelOrientation.Yaw90, harness.Session.Query(oriented).Voxel!.Orientation);
        Assert.AreEqual(100, harness.Session.Query(oriented).Voxel!.CurrentDurability);
    }

    [TestMethod]
    public void Removing_a_required_placed_mod_definition_rejects_the_whole_reload()
    {
        using var harness = LiveReloadHarness.Create(withCopper: true);
        var position = new WorldVoxelCoord(0, 0, 0);
        var copperId = new VoxelDefinitionId(ModFixtures.SampleUuid, "copper");
        Assert.AreEqual(WorldCommandStatus.Added, harness.Session.Add(position, copperId, VoxelOrientation.Identity).Status);
        var generation = harness.Reloader.Catalog.Current!.Generation;
        var snapshot = harness.Reloader.Catalog.Current;

        var later = ContentFixtures.ValidFerrousOre();
        var rebuilt = ContentFixtures.Create(later, harness.Logger, catalog: harness.Reloader.Catalog);
        rebuilt.SetReloadParticipant(VoxelWorldReloadAdapter.For(harness.Session));
        var rejected = rebuilt.Reload();

        Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status);
        Assert.AreSame(snapshot, harness.Reloader.Catalog.Current);
        Assert.AreEqual(generation, rejected.CatalogVersion);
        Assert.IsTrue(rejected.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UnresolvedVoxelReference));
        Assert.AreEqual(copperId, harness.Session.Query(position).Voxel!.DefinitionId);
        Assert.AreEqual(513, harness.Session.World.VoxelCount);
        Assert.IsTrue(harness.Reloader.Catalog.Current.TryGet(copperId, out _));
    }

    [TestMethod]
    public void Invalid_mod_exclusion_that_drops_a_placed_definition_rejects_the_reload()
    {
        using var harness = LiveReloadHarness.Create(withCopper: true);
        var position = new WorldVoxelCoord(0, 0, 0);
        var copperId = new VoxelDefinitionId(ModFixtures.SampleUuid, "copper");
        Assert.AreEqual(WorldCommandStatus.Added, harness.Session.Add(position, copperId, VoxelOrientation.Identity).Status);
        var generation = harness.Reloader.Catalog.Current!.Generation;
        var snapshot = harness.Reloader.Catalog.Current;

        var later = ContentFixtures.ValidFerrousOre();
        later.AddMod("broken", ModFixtures.SampleUuid);
        later.AddVoxelFile(ModFixtures.SampleUuid, "bad.voxels.json", "{ \"schema_version\": 2, \"voxels\": [] }");
        var rebuilt = ContentFixtures.Create(later, harness.Logger, catalog: harness.Reloader.Catalog);
        rebuilt.SetReloadParticipant(VoxelWorldReloadAdapter.For(harness.Session));
        var rejected = rebuilt.Reload();

        Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status);
        Assert.AreSame(snapshot, harness.Reloader.Catalog.Current);
        Assert.AreEqual(generation, rejected.CatalogVersion);
        Assert.IsTrue(rejected.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UnresolvedVoxelReference));
        Assert.AreEqual(copperId, harness.Session.Query(position).Voxel!.DefinitionId);
        Assert.IsTrue(harness.Reloader.Catalog.Current.TryGet(copperId, out _));
    }

    [TestMethod]
    public void Unchanged_reload_does_not_adapt_or_increment_generation()
    {
        using var harness = LiveReloadHarness.Create();
        var corner = new WorldVoxelCoord(4, 4, 4);
        Assert.AreEqual(WorldCommandStatus.Damaged, harness.Session.Damage(corner, 20).Status);
        var first = harness.Reloader.Reload();
        var snapshot = harness.Reloader.Catalog.Current;
        var notifications = 0;
        harness.Reloader.Catalog.Changed += _ => notifications++;
        var worldNotifications = 0;
        harness.Session.Changed += () => worldNotifications++;

        var second = harness.Reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, second.Status);
        Assert.IsFalse(second.Changed);
        Assert.AreEqual(first.CatalogVersion, second.CatalogVersion);
        Assert.AreSame(snapshot, harness.Reloader.Catalog.Current);
        Assert.AreEqual(0, notifications);
        Assert.AreEqual(0, worldNotifications);
        AssertInspection(harness, corner, durability: 80, value: 100, mass: 1, max: 100);
    }

    [TestMethod]
    public void Display_only_reload_updates_existing_voxels_without_changing_semantic_revision()
    {
        using var harness = LiveReloadHarness.Create();
        var corner = new WorldVoxelCoord(4, 4, 4);
        Assert.AreEqual(WorldCommandStatus.Damaged, harness.Session.Damage(corner, 20).Status);
        var baseline = harness.Reloader.Catalog.Current!.Entries[new VoxelDefinitionId("core", "ferrous-ore")];
        var worldNotifications = 0;
        harness.Session.Changed += () => worldNotifications++;

        var display = harness.ReloadFerrousOre(name: "新亚铁矿", description: "新说明");

        Assert.AreEqual(ContentReloadStatus.Committed, display.Status);
        Assert.IsTrue(display.Changed);
        var after = harness.Reloader.Catalog.Current!.Entries[new VoxelDefinitionId("core", "ferrous-ore")];
        Assert.AreEqual(baseline.SemanticRevision, after.SemanticRevision);
        Assert.AreEqual("新亚铁矿", after.Definition.Name);
        AssertInspection(harness, corner, durability: 80, value: 100, mass: 1, max: 100);
        Assert.AreEqual(1, worldNotifications);
    }

    [TestMethod]
    public void Console_query_shows_current_catalog_specs_on_field_safe_lines()
    {
        using var harness = LiveReloadHarness.Create();
        var dispatcher = new ConsoleCommandDispatcher(
            harness.Logger,
            static () => { },
            static () => { },
            () => harness.Reloader.Reload(),
            allowReload: true,
            new VoxelConsoleCommands(harness.Session));

        Assert.AreEqual(WorldCommandStatus.Damaged, harness.Session.Damage(new WorldVoxelCoord(4, 4, 4), 20).Status);
        Assert.AreEqual(ContentReloadStatus.Committed, harness.ReloadFerrousOre(maxDurability: 150).Status);

        var before = harness.Memory.Snapshot().Count;
        dispatcher.Execute("voxel query 4 4 4");
        var display = string.Join("\n", harness.Memory.Snapshot()
            .Skip(before)
            .Where(item => item.EventId == ConsoleCommandDispatcher.VoxelEventId)
            .Select(LogLevelText.FormatConsole));

        StringAssert.Contains(display, "core:ferrous-ore");
        StringAssert.Contains(display, "durability 80");
        StringAssert.Contains(display, "value 100");
        StringAssert.Contains(display, "mass 1");
        StringAssert.Contains(display, "max 150");
        StringAssert.Contains(display, "Voxel count: 512");
        foreach (var item in harness.Memory.Snapshot().Skip(before).Where(item => item.EventId == ConsoleCommandDispatcher.VoxelEventId))
            Assert.IsTrue(item.Message.Length <= 200, item.Message);
    }

    private static void AssertInspection(
        LiveReloadHarness harness,
        WorldVoxelCoord position,
        int durability,
        long value,
        double mass,
        int max)
    {
        var query = harness.Session.Query(position);
        Assert.IsTrue(query.IsOccupied);
        Assert.AreEqual(durability, query.Voxel!.CurrentDurability);
        Assert.AreEqual(value, query.Value);
        Assert.AreEqual(mass, query.Mass);
        Assert.AreEqual(max, query.MaxDurability);
    }

    private sealed class LiveReloadHarness : IDisposable
    {
        private readonly LoggerSession _session;

        public TrainLogger Logger => _session.Logger;
        public MemoryLogSink Memory => _session.Memory;
        public ContentReloader Reloader { get; }
        public VoxelDevelopmentSession Session { get; }
        public MemoryContentSource Source { get; }

        private LiveReloadHarness(
            LoggerSession session,
            MemoryContentSource source,
            ContentReloader reloader,
            VoxelDevelopmentSession voxelSession)
        {
            _session = session;
            Source = source;
            Reloader = reloader;
            Session = voxelSession;
        }

        public static LiveReloadHarness Create(bool withCopper = false)
        {
            var session = LoggerSession.Create();
            var source = withCopper ? ModFixtures.CorePlusCopper() : ContentFixtures.ValidFerrousOre();
            var reloader = ContentFixtures.Create(source, session.Logger);
            Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
            var voxelSession = new VoxelDevelopmentSession(reloader.Catalog);
            reloader.SetReloadParticipant(VoxelWorldReloadAdapter.For(voxelSession));
            return new LiveReloadHarness(session, source, reloader, voxelSession);
        }

        public ContentReloadResult ReloadFerrousOre(
            int maxDurability = 100,
            long value = 100,
            double mass = 1,
            string name = "Ferrous Ore",
            string description = "说明",
            string orientations = "all_24")
        {
            Source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good(
                    "ferrous-ore",
                    name,
                    description,
                    value: value,
                    mass: mass,
                    maxDurability: maxDurability,
                    orientations: orientations)));
            return Reloader.Reload();
        }

        public void Dispose() => _session.Dispose();
    }
}
