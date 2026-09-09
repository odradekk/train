using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Voxels;
using Train.Logging;

namespace Train.Tests;

[TestClass]
public sealed class WorldSaveTests
{
    private static readonly VoxelDefinitionId Iron = VoxelWorldFactory.DevelopmentDefinitionId;
    private static readonly VoxelDefinitionId Hollow = WorldSaveFixture.HollowId;
    private static readonly VoxelDefinitionId Ledge = WorldSaveFixture.LedgeId;
    private static readonly VoxelDefinitionId Copper = new(ModFixtures.SampleUuid, "copper");

    [TestMethod]
    public void Fixed_sequence_saves_a_runtime_delta_and_restores_through_formal_queries()
    {
        using var first = SaveHarness.Create();
        WorldSaveFixture.ApplyFixedSequence(first.Session);
        var saved = first.Session.Save("Fixture A");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        Assert.IsTrue(saved.SaveId.HasValue);
        Assert.AreEqual("Fixture A", saved.DisplayName);
        var json = File.ReadAllText(first.Store.PathFor(saved.SaveId!.Value));
        StringAssert.Contains(json, "\"save_format_version\":1");
        StringAssert.Contains(json, "\"generator_version\":\"slice-2-integer-fixture-v1\"");
        StringAssert.Contains(json, "\"seed\":2");
        StringAssert.Contains(json, "\"x\":32");
        StringAssert.Contains(json, "\"chunk_size\":16");
        StringAssert.Contains(json, "\"base_layout\"");
        StringAssert.Contains(json, "core:iron-ore");
        StringAssert.Contains(json, "semantic_revision");
        Assert.IsFalse(json.Contains("\"value\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("full_box", StringComparison.Ordinal));
        AssertRemoved(json, WorldSaveFixture.RemovedGround);
        AssertAdded(json, WorldSaveFixture.AddedHollow, "core:hollow");
        AssertChanged(json, WorldSaveFixture.DamagedCube, durability: 75);
        AssertChanged(json, WorldSaveFixture.ReorientedCube, orientation: "nz_py");
        Assert.IsTrue(json.IndexOf("\"removed\"", StringComparison.Ordinal) >= 0);
        Assert.IsTrue(CountCoordObjects(json) < 20, json);

        var id = saved.SaveId.Value;
        first.DisposeSessionOnly();
        using var restored = SaveHarness.Create(first.UserData);
        Assert.AreEqual(1088, restored.Session.World.VoxelCount);
        var loaded = restored.Session.Load(id);
        Assert.AreEqual(WorldSaveStatus.Loaded, loaded.Status);
        Assert.AreEqual(id, loaded.SaveId);
        Assert.AreEqual("Fixture A", loaded.DisplayName);
        AssertRestoredSequence(restored.Session);
        Assert.AreEqual(1088, restored.Session.World.VoxelCount);
    }

    [TestMethod]
    public void Restore_and_uninterrupted_branches_match_after_continuing_damage()
    {
        using var live = SaveHarness.Create();
        using var saved = SaveHarness.Create();
        WorldSaveFixture.ApplyFixedSequence(live.Session);
        WorldSaveFixture.ApplyFixedSequence(saved.Session);
        var write = saved.Session.Save();
        Assert.AreEqual(WorldSaveStatus.Saved, write.Status);
        using var resumed = SaveHarness.Create(saved.UserData);
        Assert.AreEqual(WorldSaveStatus.Loaded, resumed.Session.Load(write.SaveId!.Value).Status);

        Assert.AreEqual(WorldCommandStatus.Removed, live.Session.Damage(WorldSaveFixture.DamagedCube, 75).Status);
        Assert.AreEqual(WorldCommandStatus.Removed, resumed.Session.Damage(WorldSaveFixture.DamagedCube, 75).Status);
        Assert.IsFalse(live.Session.Query(WorldSaveFixture.DamagedCube).IsOccupied);
        Assert.IsFalse(resumed.Session.Query(WorldSaveFixture.DamagedCube).IsOccupied);
        Assert.AreEqual(1087, live.Session.World.VoxelCount);
        Assert.AreEqual(1087, resumed.Session.World.VoxelCount);
        CollectionAssert.AreEquivalent(VisibleFaces(live.Session.World).ToArray(), VisibleFaces(resumed.Session.World).ToArray());
        Assert.AreEqual(
            live.Session.QueryCollision(new WorldVoxelCoord(14, 14, 15)).Status,
            resumed.Session.QueryCollision(new WorldVoxelCoord(14, 14, 15)).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, resumed.Session.QueryCollision(new WorldVoxelCoord(14, 14, 15)).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, resumed.Session.QueryCollision(WorldSaveFixture.DamagedCube).Status);
    }

    [TestMethod]
    public void Restored_session_keeps_commands_reload_and_resave_on_the_new_world()
    {
        using var harness = SaveHarness.Create();
        WorldSaveFixture.ApplyFixedSequence(harness.Session, includeLedge: true);
        var first = harness.Session.Save("Keep");
        using var restored = SaveHarness.Create(harness.UserData, harness.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, restored.Session.Load(first.SaveId!.Value).Status);
        restored.Reloader.SetWorldReloadAdapter(VoxelWorldReloadAdapter.For(restored.Session));

        Assert.AreEqual(WorldCommandStatus.Added, restored.Session.Add(
            new WorldVoxelCoord(0, 1, 0), Iron, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Removed, restored.Session.Remove(new WorldVoxelCoord(0, 1, 0)).Status);
        Assert.AreEqual(75, restored.Session.Query(WorldSaveFixture.DamagedCube).Voxel!.CurrentDurability);
        Assert.AreEqual(VoxelOrientation.Yaw90, restored.Session.Query(WorldSaveFixture.SavedLedge).Voxel!.Orientation);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, restored.Session.QueryCollision(WorldSaveFixture.AddedHollow).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, restored.Session.QueryCollision(WorldSaveFixture.ReorientedCube).Status);
        AssertLocalBox(restored.Session, WorldSaveFixture.SavedLedge, VoxelOrientation.Yaw90);

        restored.Source.AddVoxelFile("core", "voxels/iron-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("iron-ore", "铁矿", "说明", value: 250, mass: 2, maxDurability: 150)));
        Assert.AreEqual(ContentReloadStatus.Committed, restored.Reloader.Reload().Status);
        Assert.AreEqual(75, restored.Session.Query(WorldSaveFixture.DamagedCube).Voxel!.CurrentDurability);
        Assert.AreEqual(150, restored.Session.Query(WorldSaveFixture.DamagedCube).MaxDurability);
        Assert.AreEqual(250, restored.Session.Query(WorldSaveFixture.DamagedCube).Value);

        var again = restored.Session.Save("Keep Two");
        Assert.AreEqual(WorldSaveStatus.Saved, again.Status);
        Assert.AreEqual(first.SaveId, again.SaveId);
        Assert.AreEqual("Keep Two", again.DisplayName);
        Assert.AreEqual(first.SaveId, restored.Session.SaveId);
    }

    [TestMethod]
    public void Repeat_save_reuses_uuid_and_renaming_does_not_change_identity_or_path()
    {
        using var harness = SaveHarness.Create();
        WorldSaveFixture.ApplyFixedSequence(harness.Session);
        var first = harness.Session.Save("One");
        var path = harness.Store.PathFor(first.SaveId!.Value);
        Assert.IsTrue(File.Exists(path));
        StringAssert.Contains(path, Path.Combine("saves", first.SaveId.Value.ToString("D"), "save.json"));

        var renamed = harness.Session.Save("Two");
        Assert.AreEqual(first.SaveId, renamed.SaveId);
        Assert.AreEqual("Two", renamed.DisplayName);
        Assert.AreEqual(path, harness.Store.PathFor(renamed.SaveId!.Value));
        var json = File.ReadAllText(path);
        StringAssert.Contains(json, "\"display_name\":\"Two\"");
        StringAssert.Contains(json, first.SaveId.Value.ToString("D"));
    }

    [TestMethod]
    public void Current_definition_adapts_durability_value_mass_and_collision_without_locking_old_specs()
    {
        using var writer = SaveHarness.Create();
        WorldSaveFixture.ApplyFixedSequence(writer.Session, includeLedge: true);
        var saved = writer.Session.Save();
        var ironRevision = writer.Reloader.Catalog.Current!.Entries[Iron].SemanticRevision;

        using var risen = SaveHarness.Create(writer.UserData);
        risen.Source.AddVoxelFile("core", "voxels/iron-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("iron-ore", "新铁矿", "说明", value: 250, mass: 3, maxDurability: 150,
                collisionJson: "{\"kind\":\"none\"}")));
        Assert.AreEqual(ContentReloadStatus.Committed, risen.Reloader.Reload().Status);
        Assert.AreNotEqual(ironRevision, risen.Reloader.Catalog.Current!.Entries[Iron].SemanticRevision);
        Assert.AreEqual(WorldSaveStatus.Loaded, risen.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(75, risen.Session.Query(WorldSaveFixture.DamagedCube).Voxel!.CurrentDurability);
        Assert.AreEqual(150, risen.Session.Query(WorldSaveFixture.DamagedCube).MaxDurability);
        Assert.AreEqual(100, risen.Session.Query(new WorldVoxelCoord(15, 16, 15)).Voxel!.CurrentDurability);
        Assert.AreEqual(150, risen.Session.Query(new WorldVoxelCoord(15, 16, 15)).MaxDurability);
        Assert.AreEqual(250, risen.Session.Query(WorldSaveFixture.DamagedCube).Value);
        Assert.AreEqual(3, risen.Session.Query(WorldSaveFixture.DamagedCube).Mass);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, risen.Session.QueryCollision(WorldSaveFixture.DamagedCube).Status);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, risen.Session.QueryCollision(WorldSaveFixture.AddedHollow).Status);
        AssertLocalBox(risen.Session, WorldSaveFixture.SavedLedge, VoxelOrientation.Yaw90);

        using var clamped = SaveHarness.Create(writer.UserData);
        clamped.Source.AddVoxelFile("core", "voxels/iron-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("iron-ore", "铁矿", "说明", maxDurability: 60)));
        Assert.AreEqual(ContentReloadStatus.Committed, clamped.Reloader.Reload().Status);
        Assert.AreEqual(WorldSaveStatus.Loaded, clamped.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(60, clamped.Session.Query(WorldSaveFixture.DamagedCube).Voxel!.CurrentDurability);
        Assert.AreEqual(60, clamped.Session.Query(WorldSaveFixture.DamagedCube).MaxDurability);
        Assert.AreEqual(60, clamped.Session.Query(new WorldVoxelCoord(15, 16, 15)).Voxel!.CurrentDurability);
        var truncated = clamped.Session.Save();
        using var risenAfterClamp = SaveHarness.Create(writer.UserData);
        risenAfterClamp.Source.AddVoxelFile("core", "voxels/iron-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("iron-ore", "新铁矿", "说明", maxDurability: 150)));
        Assert.AreEqual(ContentReloadStatus.Committed, risenAfterClamp.Reloader.Reload().Status);
        Assert.AreEqual(WorldSaveStatus.Loaded, risenAfterClamp.Session.Load(truncated.SaveId!.Value).Status);
        Assert.AreEqual(60, risenAfterClamp.Session.Query(WorldSaveFixture.DamagedCube).Voxel!.CurrentDurability);
        Assert.AreEqual(60, risenAfterClamp.Session.Query(new WorldVoxelCoord(15, 16, 15)).Voxel!.CurrentDurability);
    }

    [TestMethod]
    public void Display_and_unrelated_content_changes_are_not_base_incompatibility()
    {
        using var writer = SaveHarness.Create();
        WorldSaveFixture.ApplyFixedSequence(writer.Session);
        var saved = writer.Session.Save();

        using var reader = SaveHarness.Create(writer.UserData);
        reader.Source.AddVoxelFile("core", "voxels/iron-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("iron-ore", "显示更新", "只改名称", value: 10)));
        reader.Source.AddVoxelFile("core", "voxels/fixed.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("fixed", "固定改名", "无关", orientations: "none")));
        Assert.AreEqual(ContentReloadStatus.Committed, reader.Reloader.Reload().Status);
        Assert.AreEqual(WorldSaveStatus.Loaded, reader.Session.Load(saved.SaveId!.Value).Status);
        AssertRestoredSequence(reader.Session);
        Assert.AreEqual("显示更新", reader.Reloader.Catalog.Current!.Entries[Iron].Definition.Name);
    }

    [TestMethod]
    public void Tampered_base_layout_is_rejected_and_leaves_the_current_world_editable()
    {
        using var harness = SaveHarness.Create();
        WorldSaveFixture.ApplyFixedSequence(harness.Session);
        var saved = harness.Session.Save();
        var path = harness.Store.PathFor(saved.SaveId!.Value);
        var json = File.ReadAllText(path);
        File.WriteAllText(path, json.Replace(
            "\"base_layout\":\"",
            "\"base_layout\":\"FFFF",
            StringComparison.Ordinal));
        Assert.AreEqual(WorldSaveStatus.IncompatibleBase, harness.Session.Load(saved.SaveId.Value).Status);
        AssertRestoredSequence(harness.Session);
        Assert.AreEqual(WorldCommandStatus.Added, harness.Session.Add(
            new WorldVoxelCoord(0, 1, 0), Iron, VoxelOrientation.Identity).Status);
        Assert.AreEqual(1089, harness.Session.World.VoxelCount);
    }

    [TestMethod]
    public void Other_slice_identity_is_base_incompatible_and_keeps_that_world_editable()
    {
        using var writer = SaveHarness.Create();
        WorldSaveFixture.ApplyFixedSequence(writer.Session);
        var saved = writer.Session.Save();
        using var slice1 = SaveHarness.Create(writer.UserData, identity: VoxelWorldIdentity.Slice1);
        Assert.AreEqual(512, slice1.Session.World.VoxelCount);
        Assert.AreEqual(WorldSaveStatus.IncompatibleBase, slice1.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(512, slice1.Session.World.VoxelCount);
        Assert.AreEqual(WorldCommandStatus.Added, slice1.Session.Add(
            new WorldVoxelCoord(0, 0, 0), Iron, VoxelOrientation.Identity).Status);
    }

    [TestMethod]
    public void Failed_loads_keep_the_editable_world_and_valid_files_remain_restorable()
    {
        using var harness = SaveHarness.Create();
        WorldSaveFixture.ApplyFixedSequence(harness.Session);
        var good = harness.Session.Save("Good");
        var goodPath = harness.Store.PathFor(good.SaveId!.Value);
        var goodJson = File.ReadAllText(goodPath);

        AssertLoadKeepsWorld(harness, Mutate(goodJson, "\"save_format_version\":1", "\"save_format_version\":99"), WorldSaveStatus.UnknownFormat);
        AssertLoadKeepsWorld(harness, Mutate(goodJson, "slice-2-integer-fixture-v1", "future-generator-v9"), WorldSaveStatus.UnknownGenerator);
        AssertLoadKeepsWorld(harness, goodJson[..40], WorldSaveStatus.Truncated);
        AssertLoadKeepsWorld(harness, MutateAdded(goodJson, 32, 0, 0, "core:hollow"), WorldSaveStatus.OutOfBounds);
        AssertLoadKeepsWorld(harness, MutateAdded(goodJson, 5, 0, 5, "core:hollow"), WorldSaveStatus.Occupied);
        AssertLoadKeepsWorld(harness, DuplicateAdded(goodJson), WorldSaveStatus.Occupied);

        using var missingMod = SaveHarness.Create();
        Assert.AreEqual(WorldCommandStatus.Added, missingMod.Session.Add(
            new WorldVoxelCoord(0, 1, 0), Copper, VoxelOrientation.Identity).Status);
        var withMod = missingMod.Session.Save("Modded");
        using var withoutMod = SaveHarness.Create(missingMod.UserData, ContentFixtures.ValidIron());
        withoutMod.AddHollowAndLedge();
        Assert.AreEqual(ContentReloadStatus.Committed, withoutMod.Reloader.Reload().Status);
        Assert.AreEqual(1088, withoutMod.Session.World.VoxelCount);
        Assert.AreEqual(WorldSaveStatus.MissingDefinition, withoutMod.Session.Load(withMod.SaveId!.Value).Status);
        Assert.AreEqual(1088, withoutMod.Session.World.VoxelCount);
        Assert.IsTrue(withoutMod.Session.Query(new WorldVoxelCoord(15, 16, 15)).IsOccupied);
        Assert.AreEqual(WorldCommandStatus.Removed, withoutMod.Session.Remove(new WorldVoxelCoord(16, 16, 16)).Status);

        File.WriteAllText(goodPath, goodJson);
        using var reread = SaveHarness.Create(harness.UserData);
        Assert.AreEqual(WorldSaveStatus.Loaded, reread.Session.Load(good.SaveId!.Value).Status);
        AssertRestoredSequence(reread.Session);
    }

    [TestMethod]
    public void Write_failure_keeps_the_previous_file_and_the_current_world()
    {
        using var harness = SaveHarness.Create();
        WorldSaveFixture.ApplyFixedSequence(harness.Session);
        var first = harness.Session.Save("Keep File");
        var path = harness.Store.PathFor(first.SaveId!.Value);
        var previous = File.ReadAllText(path);
        harness.Store.FailNextWrite = true;
        Assert.AreEqual(WorldCommandStatus.Added, harness.Session.Add(
            new WorldVoxelCoord(0, 1, 0), Iron, VoxelOrientation.Identity).Status);
        var failed = harness.Session.Save("Should Fail");
        Assert.AreEqual(WorldSaveStatus.WriteFailed, failed.Status);
        Assert.AreEqual(first.SaveId, harness.Session.SaveId);
        Assert.AreEqual("Keep File", harness.Session.DisplayName);
        Assert.AreEqual(previous, File.ReadAllText(path));
        Assert.IsTrue(harness.Session.Query(new WorldVoxelCoord(0, 1, 0)).IsOccupied);

        using var reader = SaveHarness.Create(harness.UserData);
        Assert.AreEqual(WorldSaveStatus.Loaded, reader.Session.Load(first.SaveId!.Value).Status);
        AssertRestoredSequence(reader.Session);
        Assert.IsFalse(reader.Session.Query(new WorldVoxelCoord(0, 1, 0)).IsOccupied);
    }

    [TestMethod]
    public void Console_save_and_load_use_the_session_and_report_failures_without_mutating()
    {
        using var harness = SaveHarness.Create();
        var dispatcher = new ConsoleCommandDispatcher(
            harness.Logger,
            static () => { },
            static () => { },
            static () => { },
            allowReload: true,
            new VoxelConsoleCommands(harness.Session));
        WorldSaveFixture.ApplyFixedSequence(harness.Session);
        dispatcher.Execute("voxel save Fixture Console");
        Assert.AreEqual(WorldSaveStatus.Saved, harness.LastConsoleSaveHint());
        var id = harness.Session.SaveId!.Value;
        Assert.AreEqual(1088, harness.Session.World.VoxelCount);

        dispatcher.Execute("voxel reset");
        Assert.AreEqual(1088, harness.Session.World.VoxelCount);
        Assert.IsTrue(harness.Session.Query(WorldSaveFixture.RemovedGround).IsOccupied);
        dispatcher.Execute($"voxel load {id:D}");
        AssertRestoredSequence(harness.Session);

        dispatcher.Execute("voxel load not-a-uuid");
        dispatcher.Execute("voxel save ../../escape");
        AssertRestoredSequence(harness.Session);
        var usage = harness.Memory.Snapshot().Select(item => item.Message).ToArray();
        Assert.IsTrue(usage.Any(message => message.Contains("voxel load", StringComparison.Ordinal)));
        Assert.IsTrue(usage.Any(message => message.Contains("InvalidName", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Cross_boundary_pairs_after_restore_leave_the_world_unchanged()
    {
        using var harness = SaveHarness.Create();
        WorldSaveFixture.ApplyFixedSequence(harness.Session);
        var saved = harness.Session.Save();
        using var restored = SaveHarness.Create(harness.UserData);
        Assert.AreEqual(WorldSaveStatus.Loaded, restored.Session.Load(saved.SaveId!.Value).Status);
        var before = Snapshot(restored.Session);
        for (var index = 0; index < 100; index++)
        {
            var position = new WorldVoxelCoord(15 + index % 2, 1, (index / 2) % 32);
            Assert.AreEqual(WorldCommandStatus.Added, restored.Session.Add(position, Iron, VoxelOrientation.Identity).Status);
            Assert.AreEqual(WorldCommandStatus.Removed, restored.Session.Remove(position).Status);
        }

        AssertState(restored.Session, before);
        AssertRestoredSequence(restored.Session);
    }

    private static void AssertRestoredSequence(VoxelDevelopmentSession session)
    {
        Assert.IsFalse(session.Query(WorldSaveFixture.RemovedGround).IsOccupied);
        var hollow = session.Query(WorldSaveFixture.AddedHollow);
        Assert.IsTrue(hollow.IsOccupied);
        Assert.AreEqual(Hollow, hollow.Voxel!.DefinitionId);
        Assert.AreEqual(VoxelOrientation.Identity, hollow.Voxel.Orientation);
        Assert.AreEqual(100, hollow.Voxel.CurrentDurability);
        Assert.AreEqual(75, session.Query(WorldSaveFixture.DamagedCube).Voxel!.CurrentDurability);
        Assert.AreEqual(Iron, session.Query(WorldSaveFixture.DamagedCube).Voxel!.DefinitionId);
        var rotated = session.Query(WorldSaveFixture.ReorientedCube);
        Assert.AreEqual(WorldSaveFixture.Reoriented, rotated.Voxel!.Orientation);
        Assert.AreEqual(Iron, rotated.Voxel.DefinitionId);
        Assert.AreEqual(100, rotated.Voxel.CurrentDurability);
    }

    private static void AssertLocalBox(VoxelDevelopmentSession session, WorldVoxelCoord cell, VoxelOrientation orientation)
    {
        Assert.AreEqual(orientation, session.Query(cell).Voxel!.Orientation);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, session.QueryCollision(cell).Status);
        if (orientation == VoxelOrientation.Yaw90)
        {
            Assert.AreEqual(
                VoxelCollisionQueryStatus.Hit,
                session.QueryCollision(cell.X + 0.48, cell.Y + 0.38, cell.Z + 0.18, cell.X + 0.52, cell.Y + 0.42, cell.Z + 0.22).Status);
            Assert.AreEqual(
                VoxelCollisionQueryStatus.Miss,
                session.QueryCollision(cell.X + 0.18, cell.Y + 0.08, cell.Z + 0.48, cell.X + 0.22, cell.Y + 0.12, cell.Z + 0.52).Status);
            return;
        }

        Assert.AreEqual(
            VoxelCollisionQueryStatus.Hit,
            session.QueryCollision(cell.X + 0.18, cell.Y + 0.08, cell.Z + 0.48, cell.X + 0.22, cell.Y + 0.12, cell.Z + 0.52).Status);
    }

    private static void AssertRemoved(string json, WorldVoxelCoord position) =>
        StringAssert.Contains(json, $"\"x\":{position.X},\"y\":{position.Y},\"z\":{position.Z}");

    private static void AssertAdded(string json, WorldVoxelCoord position, string definitionId)
    {
        StringAssert.Contains(json, $"\"x\":{position.X}");
        StringAssert.Contains(json, definitionId);
    }

    private static void AssertChanged(string json, WorldVoxelCoord position, int? durability = null, string? orientation = null)
    {
        StringAssert.Contains(json, $"\"x\":{position.X},\"y\":{position.Y},\"z\":{position.Z}");
        if (durability is int value)
            StringAssert.Contains(json, $"\"durability\":{value}");
        if (orientation is not null)
            StringAssert.Contains(json, orientation);
    }

    private static int CountCoordObjects(string json) =>
        json.Split("\"x\":", StringSplitOptions.None).Length - 1;

    private static void AssertLoadKeepsWorld(SaveHarness harness, string json, WorldSaveStatus expected)
    {
        var brokenId = Guid.NewGuid();
        Directory.CreateDirectory(harness.Store.DirectoryFor(brokenId));
        File.WriteAllText(harness.Store.PathFor(brokenId), json);
        var before = Snapshot(harness.Session);
        Assert.AreEqual(expected, harness.Session.Load(brokenId).Status, expected.ToString());
        AssertState(harness.Session, before);
        Assert.AreEqual(WorldCommandStatus.Added, harness.Session.Add(
            new WorldVoxelCoord(2, 1, 2), Iron, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Removed, harness.Session.Remove(new WorldVoxelCoord(2, 1, 2)).Status);
        AssertState(harness.Session, before);
    }

    private static string Mutate(string json, string from, string to) =>
        json.Replace(from, to, StringComparison.Ordinal);

    private static string MutateAdded(string json, int x, int y, int z, string id)
    {
        var insertion =
            $"{{\"x\":{x},\"y\":{y},\"z\":{z},\"definition_id\":\"{id}\",\"orientation\":\"px_py\",\"durability\":100}}";
        return json.Replace("\"added\":[", "\"added\":[" + insertion + ",", StringComparison.Ordinal);
    }

    private static string DuplicateAdded(string json)
    {
        var instance =
            "{\"x\":4,\"y\":1,\"z\":4,\"definition_id\":\"core:hollow\",\"orientation\":\"px_py\",\"durability\":100}";
        return json.Replace("\"added\":[", "\"added\":[" + instance + ",", StringComparison.Ordinal);
    }

    private static Dictionary<WorldVoxelCoord, WorldVoxel> Snapshot(VoxelDevelopmentSession session)
    {
        var snapshot = new Dictionary<WorldVoxelCoord, WorldVoxel>();
        foreach (var (position, voxel) in session.World.OccupiedVoxels())
            snapshot.Add(position, voxel);
        return snapshot;
    }

    private static void AssertState(VoxelDevelopmentSession session, IReadOnlyDictionary<WorldVoxelCoord, WorldVoxel> expected)
    {
        var actual = Snapshot(session);
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

    private sealed class ControllableStore : IWorldSaveStore
    {
        private readonly FileWorldSaveStore _inner;
        public bool FailNextWrite;

        public ControllableStore(string userDataDirectory) => _inner = new FileWorldSaveStore(userDataDirectory);

        public string PathFor(Guid id) => _inner.PathFor(id);
        public string DirectoryFor(Guid id) => _inner.DirectoryFor(id);

        public bool TryRead(Guid id, out string json, out WorldSaveStatus status) =>
            _inner.TryRead(id, out json, out status);

        public bool TryWrite(Guid id, string json, out WorldSaveStatus status)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                status = WorldSaveStatus.WriteFailed;
                return false;
            }

            return _inner.TryWrite(id, json, out status);
        }
    }

    private sealed class SaveHarness : IDisposable
    {
        private readonly LoggerSession _session;
        private bool _ownsUserData = true;

        public string UserData { get; }
        public ControllableStore Store { get; }
        public MemoryContentSource Source { get; }
        public ContentReloader Reloader { get; }
        public VoxelDevelopmentSession Session { get; }
        public TrainLogger Logger => _session.Logger;
        public MemoryLogSink Memory => _session.Memory;

        private SaveHarness(
            LoggerSession session,
            string userData,
            ControllableStore store,
            MemoryContentSource source,
            ContentReloader reloader,
            VoxelDevelopmentSession voxelSession,
            bool ownsUserData)
        {
            _session = session;
            UserData = userData;
            Store = store;
            Source = source;
            Reloader = reloader;
            Session = voxelSession;
            _ownsUserData = ownsUserData;
        }

        public static SaveHarness Create(
            string? existingUserData = null,
            MemoryContentSource? source = null,
            VoxelWorldIdentity? identity = null)
        {
            var logger = LoggerSession.Create();
            var userData = existingUserData ?? Directory.CreateTempSubdirectory("train-saves-").FullName;
            var store = new ControllableStore(userData);
            var content = source ?? ModFixtures.CorePlusCopper();
            AddSliceDefinitions(content);
            var reloader = ContentFixtures.Create(content, logger.Logger);
            Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
            var voxelSession = new VoxelDevelopmentSession(
                reloader.Catalog,
                identity ?? VoxelWorldIdentity.Slice2,
                store);
            reloader.SetWorldReloadAdapter(VoxelWorldReloadAdapter.For(voxelSession));
            return new SaveHarness(logger, userData, store, content, reloader, voxelSession, existingUserData is null);
        }

        public void AddHollowAndLedge() => AddSliceDefinitions(Source);

        public void DisposeSessionOnly() => Session.Detach();

        public WorldSaveStatus LastConsoleSaveHint()
        {
            var messages = Memory.Snapshot().Select(item => item.Message);
            if (messages.Any(item => item.Contains("Saved", StringComparison.Ordinal)))
                return WorldSaveStatus.Saved;
            return WorldSaveStatus.Unavailable;
        }

        public void Dispose()
        {
            Session.Detach();
            _session.Dispose();
            if (_ownsUserData && Directory.Exists(UserData))
                Directory.Delete(UserData, true);
        }

        private static void AddSliceDefinitions(MemoryContentSource source)
        {
            source.AddVoxelFile("core", "voxels/hollow.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("hollow", "空心占格", "占格但不碰撞", tagsJson: "[]", collisionJson: "{\"kind\":\"none\"}")));
            source.AddVoxelFile("core", "voxels/ledge.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("ledge", "台沿", "偏心局部盒", value: 10, tagsJson: "[]",
                    collisionJson: ContentFixtures.LocalBoxCollision())));
            source.AddVoxelFile("core", "voxels/fixed.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("fixed", "固定", "固定方向", orientations: "none")));
        }
    }
}
