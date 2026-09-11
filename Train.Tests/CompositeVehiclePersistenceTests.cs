using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
public sealed class CompositeVehiclePersistenceTests
{
    [TestMethod]
    public void Refit_shell_writes_format_two_and_restores_p_without_q_or_r()
    {
        using var writer = Open();
        var vehicleId = writer.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var trace = HollowShellRefit.ApplySuccess(writer.Session, vehicleId);
        var before = writer.Session.CaptureAuthority();
        var savedLocal = RequireSaveLocal(writer.Session, vehicleId);
        var saved = writer.Session.Save("Slice 3 Refit");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        Assert.IsTrue(saved.SaveId.HasValue);
        var json = File.ReadAllText(writer.Store.PathFor(saved.SaveId!.Value));
        StringAssert.Contains(json, "\"save_format_version\":2");
        StringAssert.Contains(json, "\"next_entity_id\"");
        StringAssert.Contains(json, "\"transaction_count\"");
        StringAssert.Contains(json, "\"vehicles\"");
        StringAssert.Contains(json, "\"save_local_definitions\"");
        Assert.IsFalse(json.Contains("MeshInstance", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("Collider", StringComparison.Ordinal));

        using var restored = Open(writer.UserData, writer.Source);
        Assert.AreEqual(0, restored.Session.QueryComposites().Count);
        Assert.AreEqual(WorldSaveStatus.Loaded, restored.Session.Load(saved.SaveId.Value).Status);
        AssertRefitShell(restored.Session, trace);
        Assert.AreEqual(before, restored.Session.CaptureAuthority());
        AssertSaveLocalEqual(savedLocal, RequireSaveLocal(restored.Session, vehicleId));
        Assert.AreEqual(before.NextEntityId, restored.Session.NextEntityId);
        Assert.AreEqual(before.TransactionCount, restored.Session.TransactionCount);
    }

    [TestMethod]
    public void Bridge_fracture_restores_successors_and_dropped_runtime_ids()
    {
        using var writer = Open();
        var original = writer.Session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        Assert.IsTrue(writer.Session.Damage(new[]
        {
            new CompositeDamageTarget(original, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(original, BridgeFixture.LeftCoreCell, 100)
        }).Succeeded);
        var before = writer.Session.CaptureAuthority();
        Assert.AreEqual(1, writer.Session.QueryComposites().Count);
        Assert.AreEqual(original, writer.Session.QueryComposites()[0].Id);
        var dropped = writer.Session.World.OccupiedVoxels()
            .Where(item => item.Value.RuntimeId is not null)
            .OrderBy(item => item.Key.X).ThenBy(item => item.Key.Y).ThenBy(item => item.Key.Z)
            .ToArray();
        Assert.AreNotEqual(0, dropped.Length);
        var saved = writer.Session.Save("Slice 3 Fracture");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);

        using var restored = Open(writer.UserData, writer.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, restored.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(before, restored.Session.CaptureAuthority());
        Assert.AreEqual(original, restored.Session.QueryComposites()[0].Id);
        Assert.AreEqual(OperationalCondition.Disabled, restored.Session.QueryComposites()[0].OperationalCondition);
        Assert.AreEqual(ConstructionStatus.MissingContact, restored.Session.QueryComposites()[0].Construction);
        foreach (var (position, voxel) in dropped)
        {
            var loaded = restored.Session.Query(position);
            Assert.IsTrue(loaded.IsOccupied, position.ToString());
            Assert.AreEqual(voxel.RuntimeId, loaded.Voxel!.RuntimeId);
            Assert.AreEqual(voxel.DefinitionId, loaded.Voxel.DefinitionId);
            Assert.AreEqual(voxel.CurrentDurability, loaded.Voxel.CurrentDurability);
        }

        Assert.IsFalse(HasDualOwnership(restored.Session));
    }

    [TestMethod]
    public void Complete_refit_and_fragment_restore_without_forcing_construction()
    {
        using var writer = Open();
        var complete = writer.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var refit = writer.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        HollowShellRefit.ApplySuccess(writer.Session, refit);
        var fragment = writer.Session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        Assert.IsTrue(writer.Session.Damage(fragment, BridgeFixture.BridgeCell, 100).Succeeded);
        var before = writer.Session.CaptureAuthority();
        var saved = writer.Session.Save("Mixed");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);

        using var restored = Open(writer.UserData, writer.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, restored.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(before, restored.Session.CaptureAuthority());
        Assert.AreEqual(ConstructionStatus.Valid, restored.Session.QueryComposite(complete)!.Construction);
        Assert.AreEqual(169, restored.Session.QueryComposite(refit)!.Parts.Count);
        var fragments = restored.Session.QueryComposites().Where(item => item.DefinitionId == BridgeFixture.DefinitionId).ToArray();
        Assert.AreEqual(2, fragments.Length);
        Assert.IsTrue(fragments.All(item => item.Construction == ConstructionStatus.MissingContact));
        Assert.IsTrue(fragments.All(item => item.OperationalCondition == OperationalCondition.Disabled));
        Assert.IsTrue(fragments.All(item => item.IdentityRetention == IdentityRetentionStatus.Retained));
    }

    [TestMethod]
    public void Load_adapts_current_definitions_and_does_not_remanufacture_from_blueprint()
    {
        using var writer = Open();
        var complete = writer.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        Assert.IsTrue(writer.Session.Damage(complete, new LocalVoxelCoord(0, 1, 0), 25).Succeeded);
        var refit = writer.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var trace = HollowShellRefit.ApplySuccess(writer.Session, refit);
        var savedLocal = RequireSaveLocal(writer.Session, refit);
        Assert.AreEqual(
            WorldCommandStatus.Added,
            writer.Session.Add(new WorldVoxelCoord(1, 1, 1), HollowShellFixture.ShellId, VoxelOrientation.Identity).Status);
        var saved = writer.Session.Save("Adapt");

        using var risen = Open(writer.UserData, writer.Source);
        WriteMarkerAndShell(risen, 150, value: 25, mass: 3, collisionJson: "{\"kind\":\"none\"}");
        Assert.AreEqual(ContentReloadStatus.Committed, risen.Reloader.Reload().Status);
        risen.Source.AddCompositeFile(
            "core",
            "composites/dev-hollow-shell.composites.json",
            WithExtraInteriorCell(CompositeFixtures.FixtureFileText()));
        Assert.AreEqual(ContentReloadStatus.Committed, Rebuild(risen).Reload().Status);
        Assert.AreEqual(WorldSaveStatus.Loaded, risen.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(75, risen.Session.QueryComposite(complete)!.Parts.Single(part => part.Cell.Equals(new LocalVoxelCoord(0, 1, 0))).CurrentDurability);
        var risenShell = risen.Reloader.Catalog.Current!.Entries[HollowShellFixture.ShellId].Definition;
        Assert.AreEqual(150, risenShell.MaxDurability);
        Assert.AreEqual(25, risenShell.Value);
        Assert.AreEqual(3, risenShell.Mass);
        var risenWorld = risen.Session.Query(new WorldVoxelCoord(1, 1, 1));
        Assert.AreEqual(25, risenWorld.Value);
        Assert.AreEqual(3, risenWorld.Mass);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, risen.Session.QueryCollision(new WorldVoxelCoord(1, 1, 1)).Status);
        Assert.AreEqual(168, risen.Session.QueryComposite(complete)!.Parts.Count);
        AssertRefitShell(risen.Session, trace, expectedWorldVoxels: 1);
        Assert.AreEqual(savedLocal.Voxels.Count, RequireSaveLocal(risen.Session, refit).Voxels.Count);
        Assert.IsFalse(risen.Session.QueryComposite(refit)!.Parts.Any(part => part.Cell.Equals(new LocalVoxelCoord(2, 1, 0)) && part.DefinitionId == HollowShellFixture.ShellId));

        using var clamped = Open(writer.UserData, writer.Source);
        WriteMarkerAndShell(clamped, 60);
        Assert.AreEqual(ContentReloadStatus.Committed, clamped.Reloader.Reload().Status);
        Assert.AreEqual(WorldSaveStatus.Loaded, clamped.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(60, clamped.Session.QueryComposite(complete)!.Parts.Single(part => part.Cell.Equals(new LocalVoxelCoord(0, 1, 0))).CurrentDurability);
        Assert.AreEqual(60, clamped.Session.QueryComposite(refit)!.Parts.Single(part => part.Id == trace.P).CurrentDurability);
        Assert.AreEqual(trace.P, clamped.Session.QueryComposite(refit)!.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id);
    }

    [TestMethod]
    public void Transform_round_trips_non_integer_doubles_and_rejects_non_finite()
    {
        using var writer = Open();
        const double originX = 1.0 / 3.0;
        const double originY = 8.1;
        var originZ = 8.0 + Math.Pow(2, -40);
        Assert.AreNotEqual((double)(float)originX, originX);
        Assert.AreNotEqual((double)(float)originY, originY);
        Assert.AreNotEqual((double)(float)originZ, originZ);
        Assert.IsTrue(CompositeTransform.TryCreate(
            originX,
            originY,
            originZ,
            1,
            0,
            0,
            0,
            1,
            0,
            out var pose));
        var manufactured = writer.Session.Manufacture(HollowShellFixture.DefinitionId, pose);
        Assert.IsTrue(manufactured.Succeeded);
        var vehicleId = manufactured.VehicleId!.Value;
        var saved = writer.Session.Save("Pose");
        var json = File.ReadAllText(writer.Store.PathFor(saved.SaveId!.Value));
        StringAssert.Contains(json, VoxelDefinition.FormatMass(originX));
        StringAssert.Contains(json, VoxelDefinition.FormatMass(originY));
        StringAssert.Contains(json, VoxelDefinition.FormatMass(originZ));
        Assert.IsFalse(json.Contains(((float)originX).ToString("G9", CultureInfo.InvariantCulture), StringComparison.Ordinal));

        using var restored = Open(writer.UserData, writer.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, restored.Session.Load(saved.SaveId!.Value).Status);
        var loaded = restored.Session.QueryComposite(vehicleId)!.Transform;
        Assert.AreEqual(originX, loaded.OriginX);
        Assert.AreEqual(originY, loaded.OriginY);
        Assert.AreEqual(originZ, loaded.OriginZ);
        Assert.AreEqual(
            BitConverter.DoubleToInt64Bits(originX),
            BitConverter.DoubleToInt64Bits(loaded.OriginX));
        Assert.AreEqual(writer.Session.QueryComposite(vehicleId)!.Transform, loaded);

        var nan = MutateDouble(json, VoxelDefinition.FormatMass(originX), "NaN");
        AssertLoadKeepsSession(restored, nan, WorldSaveStatus.InvalidDelta);
        var inf = MutateDouble(File.ReadAllText(writer.Store.PathFor(saved.SaveId.Value)), VoxelDefinition.FormatMass(originY), "Infinity");
        File.WriteAllText(writer.Store.PathFor(saved.SaveId.Value), File.ReadAllText(writer.Store.PathFor(saved.SaveId.Value)));
        AssertLoadKeepsSession(restored, inf, WorldSaveStatus.InvalidDelta);
    }

    [TestMethod]
    public void Transform_round_trips_non_axial_rotation_bits()
    {
        using var writer = Open();
        Assert.IsTrue(CompositeTransform.TryCreate(8, 8, 8, 1, 0, 1, 0, 1, 0, out var pose));
        Assert.AreNotEqual(0, pose.RightX);
        Assert.AreNotEqual(0, pose.RightZ);
        var manufactured = writer.Session.Manufacture(HollowShellFixture.DefinitionId, pose);
        Assert.IsTrue(manufactured.Succeeded);
        var vehicleId = manufactured.VehicleId!.Value;
        var before = writer.Session.QueryComposite(vehicleId)!.Transform;
        var saved = writer.Session.Save("Yaw");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        var json = File.ReadAllText(writer.Store.PathFor(saved.SaveId!.Value));
        StringAssert.Contains(json, VoxelDefinition.FormatMass(before.RightX));
        StringAssert.Contains(json, VoxelDefinition.FormatMass(before.RightZ));

        using var restored = Open(writer.UserData, writer.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, restored.Session.Load(saved.SaveId.Value).Status);
        var loaded = restored.Session.QueryComposite(vehicleId)!.Transform;
        AssertEqualBits(before.OriginX, loaded.OriginX);
        AssertEqualBits(before.OriginY, loaded.OriginY);
        AssertEqualBits(before.OriginZ, loaded.OriginZ);
        AssertEqualBits(before.RightX, loaded.RightX);
        AssertEqualBits(before.RightY, loaded.RightY);
        AssertEqualBits(before.RightZ, loaded.RightZ);
        AssertEqualBits(before.UpX, loaded.UpX);
        AssertEqualBits(before.UpY, loaded.UpY);
        AssertEqualBits(before.UpZ, loaded.UpZ);
        Assert.AreEqual(before, loaded);
        var collapsed = JsonNode.Parse(json)!.AsObject();
        var axes = collapsed["vehicles"]![0]!["transform"]!.AsObject();
        axes["right_x"] = 0;
        axes["right_y"] = 0;
        axes["right_z"] = 0;
        AssertLoadKeepsSession(restored, collapsed.ToJsonString(), WorldSaveStatus.InvalidDelta);
        AssertEqualBits(before.RightX, restored.Session.QueryComposite(vehicleId)!.Transform.RightX);
    }

    [TestMethod]
    public void Isolated_duplicate_id_rejects_without_polluting_the_next_case()
    {
        using var harness = Open();
        var vehicleId = harness.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        HollowShellRefit.ApplySuccess(harness.Session, vehicleId);
        var good = harness.Session.Save("Good");
        var goodJson = File.ReadAllText(harness.Store.PathFor(good.SaveId!.Value));
        var before = harness.Session.CaptureAuthority();
        var savedLocal = RequireSaveLocal(harness.Session, vehicleId);

        AssertLoadKeepsSession(harness, DuplicateVehicleId(goodJson), WorldSaveStatus.InvalidDelta);
        Assert.AreEqual(before, harness.Session.CaptureAuthority());
        AssertSaveLocalEqual(savedLocal, RequireSaveLocal(harness.Session, vehicleId));
        Assert.IsTrue(harness.Session.Refit(vehicleId, CompositeRefitOperation.Move(
            harness.Session.QueryComposite(vehicleId)!.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id,
            HollowShellRefit.Moved)).Succeeded);
        harness.Session.Refit(vehicleId, CompositeRefitOperation.Move(
            harness.Session.QueryComposite(vehicleId)!.Parts.Single(part => part.Cell == HollowShellRefit.Moved).Id,
            HollowShellRefit.SecondAdd));

        using var missing = Open();
        var missingVehicle = missing.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        HollowShellRefit.ApplySuccess(missing.Session, missingVehicle);
        var missingSave = missing.Session.Save("Missing");
        var missingJson = RemoveSaveLocal(File.ReadAllText(missing.Store.PathFor(missingSave.SaveId!.Value)));
        var missingBefore = missing.Session.CaptureAuthority();
        AssertLoadKeepsSession(missing, missingJson, WorldSaveStatus.MissingDefinition);
        Assert.AreEqual(missingBefore, missing.Session.CaptureAuthority());
        Assert.AreEqual(ConstructionStatus.Valid, missing.Session.QueryComposites()[0].Construction);
    }

    [TestMethod]
    public void Isolated_occupancy_binding_and_missing_mod_reject_independently()
    {
        using var occupancy = Open();
        occupancy.Session.Manufacture(HollowShellFixture.DefinitionId);
        var occupancySave = occupancy.Session.Save("Occupancy");
        var occupancyJson = File.ReadAllText(occupancy.Store.PathFor(occupancySave.SaveId!.Value));
        var occupancyBefore = occupancy.Session.CaptureAuthority();
        AssertLoadKeepsSession(occupancy, DuplicatePartCell(occupancyJson), WorldSaveStatus.InvalidDelta);
        Assert.AreEqual(occupancyBefore, occupancy.Session.CaptureAuthority());
        Assert.AreEqual(168, occupancy.Session.QueryComposites()[0].Parts.Count);

        using var binding = Open();
        binding.Session.Manufacture(HollowShellFixture.DefinitionId);
        var bindingSave = binding.Session.Save("Binding");
        var bindingJson = File.ReadAllText(binding.Store.PathFor(bindingSave.SaveId!.Value));
        var bindingBefore = binding.Session.CaptureAuthority();
        AssertLoadKeepsSession(binding, UnboundContact(bindingJson), WorldSaveStatus.InvalidDelta);
        Assert.AreEqual(bindingBefore, binding.Session.CaptureAuthority());
        Assert.AreEqual(ConstructionStatus.Valid, binding.Session.QueryComposites()[0].Construction);

        var withMod = CompositeFixtures.FormalFixturePackage();
        withMod.AddMod("pretty-name", ModFixtures.SampleUuid);
        withMod.AddVoxelFile(
            ModFixtures.SampleUuid,
            "natural/copper.voxels.json",
            ContentFixtures.GoodsFile(ContentFixtures.Good(
                "copper",
                "铜",
                "铜说明",
                value: 10,
                tagsJson: "[]",
                icon: "copper.png",
                voxel: "copper.png")));
        withMod.AddImage(ModFixtures.SampleUuid, "copper.png", ContentFixtures.Square(16));
        using var modded = Open(source: withMod);
        var modVehicle = modded.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        Assert.IsTrue(modded.Session.Refit(
            modVehicle,
            CompositeRefitOperation.Add(
                HollowShellRefit.FirstAdd,
                new VoxelDefinitionId(ModFixtures.SampleUuid, "copper"),
                VoxelOrientation.Identity)).Succeeded);
        var modSave = modded.Session.Save("Mod Part");
        Assert.AreEqual(WorldSaveStatus.Saved, modSave.Status);
        using var withoutMod = Open(modded.UserData);
        var withoutBefore = withoutMod.Session.CaptureAuthority();
        Assert.AreEqual(WorldSaveStatus.MissingDefinition, withoutMod.Session.Load(modSave.SaveId!.Value).Status);
        Assert.AreEqual(withoutBefore, withoutMod.Session.CaptureAuthority());
        Assert.AreEqual(0, withoutMod.Session.QueryComposites().Count);
        Assert.IsTrue(withoutMod.Session.Manufacture(HollowShellFixture.DefinitionId).Succeeded);
    }

    [TestMethod]
    public void Isolated_illegal_orientation_and_bad_counter_reject_independently()
    {
        using var orientation = Open();
        var vehicleId = orientation.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var trace = HollowShellRefit.ApplySuccess(orientation.Session, vehicleId);
        var saved = orientation.Session.Save("Orient");
        var json = File.ReadAllText(orientation.Store.PathFor(saved.SaveId!.Value));
        var before = orientation.Session.CaptureAuthority();
        AssertLoadKeepsSession(orientation, ForcePartOrientation(json, "none"), WorldSaveStatus.InvalidDelta);
        Assert.AreEqual(before, orientation.Session.CaptureAuthority());
        Assert.AreEqual(trace.P, orientation.Session.QueryComposite(vehicleId)!.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id);

        using var counter = Open();
        counter.Session.Manufacture(HollowShellFixture.DefinitionId);
        var counterSave = counter.Session.Save("Counter");
        var counterJson = File.ReadAllText(counter.Store.PathFor(counterSave.SaveId!.Value));
        var counterBefore = counter.Session.CaptureAuthority();
        AssertLoadKeepsSession(counter, MutateField(counterJson, "next_entity_id", "1"), WorldSaveStatus.InvalidDelta);
        Assert.AreEqual(counterBefore, counter.Session.CaptureAuthority());
        Assert.AreEqual(1UL, counter.Session.QueryComposites()[0].Id.Value);

        using var transactions = Open();
        transactions.Session.Manufacture(HollowShellFixture.DefinitionId);
        var transactionSave = transactions.Session.Save("Transactions");
        var transactionJson = File.ReadAllText(transactions.Store.PathFor(transactionSave.SaveId!.Value));
        var transactionBefore = transactions.Session.CaptureAuthority();
        var transactionLocals = RequireSaveLocalIfPresent(transactions.Session);
        AssertLoadKeepsSession(
            transactions,
            MutateField(transactionJson, "transaction_count", ulong.MaxValue.ToString(CultureInfo.InvariantCulture)),
            WorldSaveStatus.InvalidDelta);
        Assert.AreEqual(transactionBefore, transactions.Session.CaptureAuthority());
        Assert.AreEqual(1UL, transactions.Session.TransactionCount);
        Assert.AreEqual(transactionLocals.Length, transactions.Session.Vehicles.Count);
        Assert.IsTrue(transactions.Session.Manufacture(HollowShellFixture.DefinitionId, CompositeTransform.Translation(20, 8, 8)).Succeeded);
        Assert.AreEqual(2, transactions.Session.QueryComposites().Count);
    }

    [TestMethod]
    public void Save_local_restores_without_the_published_composite()
    {
        using var writer = Open();
        var vehicleId = writer.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var trace = HollowShellRefit.ApplySuccess(writer.Session, vehicleId);
        var savedLocal = RequireSaveLocal(writer.Session, vehicleId);
        var before = writer.Session.CaptureAuthority();
        var saved = writer.Session.Save("Save Local Only");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);

        RemovePublishedComposites(writer.Source, "composites/dev-hollow-shell.composites.json");
        using var restored = Open(writer.UserData, writer.Source);
        Assert.IsFalse(restored.Reloader.Composites.Current.TryGet(HollowShellFixture.DefinitionId, out _));
        Assert.IsTrue(restored.Reloader.Composites.Current.TryGet(HollowShellFixture.ArchetypeId, out _));
        Assert.AreEqual(WorldSaveStatus.Loaded, restored.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(before, restored.Session.CaptureAuthority());
        AssertRefitShell(restored.Session, trace);
        AssertSaveLocalEqual(savedLocal, RequireSaveLocal(restored.Session, vehicleId));
        Assert.IsTrue(restored.Session.Refit(
            vehicleId,
            CompositeRefitOperation.Move(trace.P, HollowShellRefit.Moved)).Succeeded);
        Assert.AreEqual(trace.P, restored.Session.QueryComposite(vehicleId)!.Parts.Single(part => part.Cell == HollowShellRefit.Moved).Id);

        using var stock = Open();
        var stockId = stock.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var stockSave = stock.Session.Save("Stock External");
        RemovePublishedComposites(stock.Source, "composites/dev-hollow-shell.composites.json");
        using var stockReader = Open(stock.UserData, stock.Source);
        Assert.AreEqual(WorldSaveStatus.MissingDefinition, stockReader.Session.Load(stockSave.SaveId!.Value).Status);
        Assert.AreEqual(0, stockReader.Session.QueryComposites().Count);
    }

    [TestMethod]
    public void Hot_reload_keeps_save_local_when_the_published_composite_is_removed()
    {
        using var world = Open();
        var vehicleId = world.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var trace = HollowShellRefit.ApplySuccess(world.Session, vehicleId);
        var before = world.Session.CaptureAuthority();
        RemovePublishedComposites(world.Source, "composites/dev-hollow-shell.composites.json");
        Assert.AreEqual(ContentReloadStatus.Committed, Rebuild(world).Reload().Status);
        Assert.IsFalse(world.Reloader.Composites.Current.TryGet(HollowShellFixture.DefinitionId, out _));
        Assert.AreEqual(before.Vehicles[0].Id, world.Session.QueryComposite(vehicleId)!.Id);
        AssertRefitShell(world.Session, trace);
        Assert.IsTrue(world.Session.Refit(
            vehicleId,
            CompositeRefitOperation.Move(trace.P, HollowShellRefit.Moved)).Succeeded);
    }

    [TestMethod]
    public void Save_local_still_requires_archetype_and_part_definitions()
    {
        using var writer = Open();
        var vehicleId = writer.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        HollowShellRefit.ApplySuccess(writer.Session, vehicleId);
        var saved = writer.Session.Save("Save Local Deps");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);

        var noArchetype = CompositeFixtures.FormalFixturePackage();
        noArchetype.AddCompositeFile(
            "core",
            "composites/dev-hollow-shell.composites.json",
            CompositeFixtures.File(null, CompositeFixtures.Archetype("dev-unused-shell"), ""));
        noArchetype.AddCompositeFile(
            "core",
            "composites/dev-bridge.composites.json",
            CompositeFixtures.File(null, CompositeFixtures.Archetype("dev-unused-bridge"), ""));
        using var archetypeReader = Open(writer.UserData, noArchetype);
        Assert.IsFalse(archetypeReader.Reloader.Composites.Current.TryGet(HollowShellFixture.DefinitionId, out _));
        Assert.IsFalse(archetypeReader.Reloader.Composites.Current.TryGet(HollowShellFixture.ArchetypeId, out _));
        Assert.AreEqual(WorldSaveStatus.MissingDefinition, archetypeReader.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(0, archetypeReader.Session.QueryComposites().Count);

        var noMarker = CompositeFixtures.FormalFixturePackage();
        RemovePublishedComposites(noMarker, "composites/dev-hollow-shell.composites.json");
        noMarker.AddVoxelFile(
            "core",
            "voxels/refit-marker.voxels.json",
            ContentFixtures.GoodsFile(ContentFixtures.Good("other-marker", "其他", "其他说明", value: 10, tagsJson: "[]")));
        using var partReader = Open(writer.UserData, noMarker);
        Assert.IsTrue(partReader.Reloader.Composites.Current.TryGet(HollowShellFixture.ArchetypeId, out _));
        Assert.IsFalse(partReader.Reloader.Catalog.Current!.TryGet(HollowShellRefit.MarkerId, out _));
        Assert.AreEqual(WorldSaveStatus.MissingDefinition, partReader.Session.Load(saved.SaveId.Value).Status);
        Assert.AreEqual(0, partReader.Session.QueryComposites().Count);
    }

    [TestMethod]
    public void Stock_vehicle_hot_reload_rejects_when_the_published_composite_is_removed()
    {
        using var world = Open();
        world.Session.Manufacture(HollowShellFixture.DefinitionId);
        var before = world.Session.CaptureAuthority();
        var generation = world.Reloader.Composites.Current.Generation;
        var snapshot = world.Reloader.Composites.Current;
        RemovePublishedComposites(world.Source, "composites/dev-hollow-shell.composites.json");
        var rejected = Rebuild(world).Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status);
        Assert.AreEqual(before, world.Session.CaptureAuthority());
        Assert.AreSame(snapshot, world.Reloader.Composites.Current);
        Assert.AreEqual(generation, world.Reloader.Composites.Current.Generation);
        Assert.AreEqual(1, world.Session.QueryComposites().Count);
        Assert.IsNull(world.Session.Vehicles.Single().SaveLocalDefinition);
    }

    [TestMethod]
    public void Mismatched_save_local_identity_is_rejected_independently()
    {
        using var harness = Open();
        var hollow = harness.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        HollowShellRefit.ApplySuccess(harness.Session, hollow);
        var bridge = harness.Session.Manufacture(BridgeFixture.DefinitionId, CompositeTransform.Translation(20, 8, 8)).VehicleId!.Value;
        Assert.IsTrue(harness.Session.Refit(
            bridge,
            CompositeRefitOperation.Add(new LocalVoxelCoord(3, 0, 1), HollowShellRefit.MarkerId, VoxelOrientation.Identity)).Succeeded);
        var saved = harness.Session.Save("Mixed Locals");
        var json = File.ReadAllText(harness.Store.PathFor(saved.SaveId!.Value));
        var before = harness.Session.CaptureAuthority();
        AssertLoadKeepsSession(harness, SwapVehicleRevisions(json), WorldSaveStatus.InvalidDelta);
        Assert.AreEqual(before, harness.Session.CaptureAuthority());
        Assert.AreEqual(169, harness.Session.QueryComposite(hollow)!.Parts.Count);
        Assert.AreEqual(56, harness.Session.QueryComposite(bridge)!.Parts.Count);
    }

    [TestMethod]
    public void Format_one_is_unknown_and_failed_io_keeps_the_real_file()
    {
        using var harness = Open();
        harness.Session.Manufacture(HollowShellFixture.DefinitionId);
        var first = harness.Session.Save("Keep File");
        var path = harness.Store.PathFor(first.SaveId!.Value);
        var previous = File.ReadAllText(path);
        StringAssert.Contains(previous, "\"save_format_version\":2");
        AssertLoadKeepsSession(harness, previous.Replace("\"save_format_version\":2", "\"save_format_version\":1", StringComparison.Ordinal), WorldSaveStatus.UnknownFormat);
        AssertLoadKeepsSession(harness, previous.Replace("\"save_format_version\":2", "\"save_format_version\":99", StringComparison.Ordinal), WorldSaveStatus.UnknownFormat);

        harness.Store.FailNextWrite = true;
        Assert.AreEqual(WorldSaveStatus.WriteFailed, harness.Session.Save("Should Fail").Status);
        Assert.AreEqual(first.SaveId, harness.Session.SaveId);
        Assert.AreEqual("Keep File", harness.Session.DisplayName);
        Assert.AreEqual(previous, File.ReadAllText(path));

        using var reader = Open(harness.UserData, harness.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, reader.Session.Load(first.SaveId!.Value).Status);
        Assert.AreEqual(168, reader.Session.QueryComposites()[0].Parts.Count);
        var renamed = reader.Session.Save("Renamed");
        Assert.AreEqual(first.SaveId, renamed.SaveId);
        Assert.AreEqual("Renamed", renamed.DisplayName);
        StringAssert.Contains(reader.Store.PathFor(first.SaveId.Value), first.SaveId.Value.ToString("D"));
    }

    [TestMethod]
    public void Real_file_replace_failure_keeps_the_previous_save()
    {
        using var harness = Open();
        var vehicleId = harness.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        HollowShellRefit.ApplySuccess(harness.Session, vehicleId);
        var first = harness.Session.Save("Keep File");
        var path = harness.Store.PathFor(first.SaveId!.Value);
        var previous = File.ReadAllText(path);
        var before = harness.Session.CaptureAuthority();
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.AreEqual(WorldSaveStatus.WriteFailed, harness.Session.Save("Should Fail").Status);
        }

        Assert.AreEqual(first.SaveId, harness.Session.SaveId);
        Assert.AreEqual("Keep File", harness.Session.DisplayName);
        Assert.AreEqual(before, harness.Session.CaptureAuthority());
        Assert.AreEqual(previous, File.ReadAllText(path));
        Assert.IsFalse(File.Exists(Path.Combine(harness.Store.DirectoryFor(first.SaveId.Value), FileWorldSaveStore.CandidateFileName)));

        using var reader = Open(harness.UserData, harness.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, reader.Session.Load(first.SaveId.Value).Status);
        Assert.AreEqual(before, reader.Session.CaptureAuthority());
        Assert.AreEqual(169, reader.Session.QueryComposites()[0].Parts.Count);
    }

    [TestMethod]
    public void Exhausted_transaction_count_does_not_wrap_or_partially_commit()
    {
        using var harness = Open();
        var first = harness.Session.Manufacture(HollowShellFixture.DefinitionId);
        Assert.IsTrue(first.Succeeded);
        typeof(VoxelDevelopmentSession)
            .GetProperty(nameof(VoxelDevelopmentSession.TransactionCount))!
            .SetValue(harness.Session, ulong.MaxValue);
        var before = harness.Session.CaptureAuthority();
        Assert.AreEqual(
            ConstructionStatus.TransactionExhausted,
            harness.Session.Manufacture(
                HollowShellFixture.DefinitionId,
                CompositeTransform.Translation(20, 8, 8)).Status);
        Assert.AreEqual(
            ConstructionStatus.TransactionExhausted,
            harness.Session.Refit(
                first.VehicleId!.Value,
                CompositeRefitOperation.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId, VoxelOrientation.Identity)).Status);
        Assert.AreEqual(
            CompositeDamageStatus.TransactionExhausted,
            harness.Session.Damage(first.VehicleId.Value, HollowShellRefit.Occupied, 1).Status);
        Assert.AreEqual(ulong.MaxValue, harness.Session.TransactionCount);
        Assert.AreEqual(before, harness.Session.CaptureAuthority());
        Assert.AreEqual(1, harness.Session.QueryComposites().Count);
        Assert.AreEqual(168, harness.Session.QueryComposites()[0].Parts.Count);
    }

    [TestMethod]
    public void WorldSeed_42_save_resume_matches_uninterrupted_authority_including_ties()
    {
        using var live = Open();
        using var writer = Open();
        SessionHandle? resumed = null;
        try
        {
            var liveFinal = RunFixedAuthoritySequence(live.Session);
            var savedFinal = RunFixedAuthoritySequence(writer.Session, mid =>
            {
                var write = mid.Save("Checkpoint");
                Assert.AreEqual(WorldSaveStatus.Saved, write.Status);
                resumed = Open(writer.UserData, writer.Source);
                Assert.AreEqual(WorldSaveStatus.Loaded, resumed.Session.Load(write.SaveId!.Value).Status);
                return resumed.Session;
            });
            Assert.AreEqual(42UL, live.Session.Identity.Seed);
            Assert.AreEqual(liveFinal.CaptureAuthority(), savedFinal.CaptureAuthority());
            AssertSaveLocalTreesEqual(liveFinal, savedFinal);
            Assert.AreEqual(liveFinal.NextEntityId, savedFinal.NextEntityId);
            Assert.AreEqual(liveFinal.TransactionCount, savedFinal.TransactionCount);
            Assert.IsTrue(liveFinal.NextEntityId > 1);
            Assert.IsTrue(liveFinal.TransactionCount > 1);
            Assert.AreEqual(4, liveFinal.QueryComposites().Count);
            Assert.AreEqual(26, liveFinal.World.VoxelCount);
            var equalSuccessors = liveFinal.QueryComposites().Where(item => item.DefinitionId == BridgeFixture.DefinitionId).ToArray();
            Assert.AreEqual(2, equalSuccessors.Length);
            Assert.IsTrue(equalSuccessors.All(item => item.Construction == ConstructionStatus.MissingContact));
            var heavy = liveFinal.QueryComposites().Single(item => item.DefinitionId == BridgeFixture.RightHeavyDefinitionId);
            Assert.AreEqual(ConstructionStatus.MissingContact, heavy.Construction);
            Assert.AreEqual(OperationalCondition.Disabled, heavy.OperationalCondition);
        }
        finally
        {
            resumed?.Dispose();
        }
    }

    [TestMethod]
    public void Restored_session_keeps_refit_damage_reload_and_resave()
    {
        using var writer = Open();
        var vehicleId = writer.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var trace = HollowShellRefit.ApplySuccess(writer.Session, vehicleId);
        var first = writer.Session.Save("Bind");
        using var restored = Open(writer.UserData, writer.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, restored.Session.Load(first.SaveId!.Value).Status);
        restored.Reloader.SetWorldReloadAdapter(VoxelWorldReloadAdapter.For(restored.Session));
        HollowShellRefit.RunMoveReturnPairs(restored.Session, vehicleId, trace.P, 2);
        Assert.AreEqual(75, restored.Session.QueryComposite(vehicleId)!.Parts.Single(part => part.Id == trace.P).CurrentDurability);
        Assert.IsTrue(restored.Session.Damage(vehicleId, HollowShellRefit.Occupied, 10).Succeeded);
        WriteMarkerAndShell(restored, 150);
        Assert.AreEqual(ContentReloadStatus.Committed, restored.Reloader.Reload().Status);
        Assert.AreEqual(75, restored.Session.QueryComposite(vehicleId)!.Parts.Single(part => part.Id == trace.P).CurrentDurability);
        var again = restored.Session.Save("Bind Two");
        Assert.AreEqual(first.SaveId, again.SaveId);
        using var reread = Open(writer.UserData, writer.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, reread.Session.Load(first.SaveId!.Value).Status);
        Assert.AreEqual(75, reread.Session.QueryComposite(vehicleId)!.Parts.Single(part => part.Id == trace.P).CurrentDurability);
        Assert.AreEqual(90, reread.Session.QueryComposite(vehicleId)!.Parts.Single(part => part.Cell == HollowShellRefit.Occupied).CurrentDurability);
    }

    private static void AssertRefitShell(
        VoxelDevelopmentSession session,
        HollowShellRefitTrace trace,
        int expectedWorldVoxels = 0)
    {
        var query = session.QueryComposite(trace.VehicleId)!;
        Assert.AreEqual(169, query.Parts.Count);
        Assert.AreEqual(trace.P, query.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id);
        Assert.AreEqual(75, query.Parts.Single(part => part.Id == trace.P).CurrentDurability);
        Assert.AreEqual(HollowShellRefit.MarkerOrientation, query.Parts.Single(part => part.Id == trace.P).Orientation);
        Assert.IsFalse(query.Parts.Any(part => part.Id == trace.Q));
        Assert.IsFalse(query.Parts.Any(part => part.Id == trace.R));
        Assert.AreEqual(expectedWorldVoxels, session.World.VoxelCount);
    }

    private static void AssertEqualBits(double expected, double actual) =>
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));

    private static CompositeDefinition?[] RequireSaveLocalIfPresent(VoxelDevelopmentSession session) =>
        session.Vehicles.Select(item => item.SaveLocalDefinition).ToArray();

    private static void RemovePublishedComposites(MemoryContentSource source, string relativePath)
    {
        var package = source.ListPackages().Single(item => item.Namespace == "core");
        var read = source.ReadFile(package, relativePath, ContentLimits.JsonFileMaxBytes);
        Assert.IsNotNull(read.Bytes);
        var root = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(read.Bytes))!.AsObject();
        root["composites"] = new JsonArray();
        source.AddCompositeFile("core", relativePath, root.ToJsonString());
    }

    private static string SwapVehicleRevisions(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var vehicles = root["vehicles"]!.AsArray();
        var first = vehicles[0]!;
        var second = vehicles[1]!;
        var firstLocal = first["save_local_revision"]!.GetValue<string>();
        var firstUsed = first["last_used_revision"]!.GetValue<string>();
        var secondLocal = second["save_local_revision"]!.GetValue<string>();
        var secondUsed = second["last_used_revision"]!.GetValue<string>();
        first["save_local_revision"] = secondLocal;
        first["last_used_revision"] = secondUsed;
        second["save_local_revision"] = firstLocal;
        second["last_used_revision"] = firstUsed;
        return root.ToJsonString();
    }

    private static CompositeDefinition RequireSaveLocal(VoxelDevelopmentSession session, EntityId vehicleId)
    {
        var vehicle = session.Vehicles.Single(item => item.Id == vehicleId);
        Assert.IsNotNull(vehicle.SaveLocalDefinition);
        return vehicle.SaveLocalDefinition!;
    }

    private static void AssertSaveLocalEqual(CompositeDefinition expected, CompositeDefinition actual)
    {
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.ArchetypeId, actual.ArchetypeId);
        Assert.AreEqual(expected.SizeClass, actual.SizeClass);
        Assert.AreEqual(expected.Voxels.Count, actual.Voxels.Count);
        Assert.AreEqual(expected.Contacts.Count, actual.Contacts.Count);
        CollectionAssert.AreEqual(
            expected.Voxels.Select(item => (item.Cell, item.DefinitionId, item.Orientation)).ToArray(),
            actual.Voxels.Select(item => (item.Cell, item.DefinitionId, item.Orientation)).ToArray());
        CollectionAssert.AreEqual(
            expected.Contacts.Select(item => (item.X, item.Y, item.Z, item.BoundCell)).ToArray(),
            actual.Contacts.Select(item => (item.X, item.Y, item.Z, item.BoundCell)).ToArray());
    }

    private static void AssertSaveLocalTreesEqual(VoxelDevelopmentSession left, VoxelDevelopmentSession right)
    {
        Assert.AreEqual(left.Vehicles.Count, right.Vehicles.Count);
        for (var index = 0; index < left.Vehicles.Count; index++)
        {
            var a = left.Vehicles[index].SaveLocalDefinition;
            var b = right.Vehicles[index].SaveLocalDefinition;
            if (a is null || b is null)
            {
                Assert.AreEqual(a is null, b is null);
                continue;
            }

            AssertSaveLocalEqual(a, b);
        }
    }

    private static VoxelDevelopmentSession RunFixedAuthoritySequence(
        VoxelDevelopmentSession session,
        Func<VoxelDevelopmentSession, VoxelDevelopmentSession>? checkpoint = null)
    {
        var current = session;
        var hollow = current.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var trace = HollowShellRefit.ApplySuccess(current, hollow);
        var nextBeforeContinue = current.NextEntityId;
        var transactionsBeforeContinue = current.TransactionCount;
        if (checkpoint is not null)
            current = checkpoint(current);
        Assert.AreEqual(nextBeforeContinue, current.NextEntityId);
        Assert.AreEqual(transactionsBeforeContinue, current.TransactionCount);
        HollowShellRefit.RunMoveReturnPairs(current, hollow, trace.P, 2);
        var equal = current.Manufacture(BridgeFixture.DefinitionId, CompositeTransform.Translation(20, 8, 8));
        Assert.IsTrue(equal.Succeeded);
        Assert.IsTrue(current.Damage(equal.VehicleId!.Value, BridgeFixture.BridgeCell, 100).Succeeded);
        var heavy = current.Manufacture(BridgeFixture.RightHeavyDefinitionId, CompositeTransform.Translation(8, 8, 20));
        Assert.IsTrue(heavy.Succeeded);
        Assert.IsTrue(current.Damage(new[]
        {
            new CompositeDamageTarget(heavy.VehicleId!.Value, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(heavy.VehicleId.Value, BridgeFixture.LeftCoreCell, 100)
        }).Succeeded);
        Assert.IsTrue(current.NextEntityId > nextBeforeContinue);
        Assert.IsTrue(current.TransactionCount > transactionsBeforeContinue);
        return current;
    }

    private static bool HasDualOwnership(VoxelDevelopmentSession session)
    {
        var vehicleIds = session.QueryComposites().SelectMany(item => item.Parts).Select(part => part.Id.Value).ToArray();
        if (vehicleIds.Length != vehicleIds.Distinct().Count())
            return true;
        var worldIds = session.World.OccupiedVoxels()
            .Select(item => item.Value.RuntimeId?.Value)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToArray();
        return worldIds.Length != worldIds.Distinct().Count() || vehicleIds.Intersect(worldIds).Any();
    }

    private static void WriteMarkerAndShell(
        SessionHandle world,
        int maxDurability,
        long value = 10,
        double mass = 1,
        string collisionJson = "{\"kind\":\"full_box\"}")
    {
        world.Source.AddVoxelFile(
            "core",
            "voxels/refit-marker.voxels.json",
            ContentFixtures.GoodsFile(ContentFixtures.Good(
                "refit-marker",
                "改装标记",
                "带可见朝向标记的改装设备",
                value: value,
                mass: mass,
                maxDurability: maxDurability,
                tagsJson: "[]",
                top: "voxels/refit-marker/top.png",
                side: "voxels/refit-marker/side.png",
                bottom: "voxels/refit-marker/bottom.png",
                icon: "voxels/refit-marker/icon.png",
                collisionJson: collisionJson)));
        world.Source.AddVoxelFile(
            "core",
            "voxels/vehicle-shell.voxels.json",
            ContentFixtures.GoodsFile(ContentFixtures.Good(
                "vehicle-shell",
                "壳体",
                "壳体说明",
                value: value,
                mass: mass,
                maxDurability: maxDurability,
                tagsJson: "[]",
                collisionJson: collisionJson)));
    }

    private static string WithExtraInteriorCell(string fixture) =>
        fixture.Replace(
            "{ \"x\": 0, \"y\": 0, \"z\": -1, \"definition\": \"vehicle-shell\", \"orientation\": \"px_py\" }",
            "{ \"x\": 0, \"y\": 0, \"z\": -1, \"definition\": \"vehicle-shell\", \"orientation\": \"px_py\" },\n        { \"x\": 2, \"y\": 1, \"z\": 0, \"definition\": \"vehicle-shell\", \"orientation\": \"px_py\" }",
            StringComparison.Ordinal);

    private static ContentReloader Rebuild(SessionHandle world)
    {
        var rebuilt = ContentFixtures.Create(
            world.Source,
            world.Logger,
            catalog: world.Reloader.Catalog,
            settlements: world.Reloader.Settlements,
            composites: world.Reloader.Composites);
        rebuilt.SetWorldReloadAdapter(VoxelWorldReloadAdapter.For(world.Session));
        world.Reloader = rebuilt;
        return rebuilt;
    }

    private static void AssertLoadKeepsSession(SessionHandle harness, string json, WorldSaveStatus expected)
    {
        var brokenId = Guid.NewGuid();
        Directory.CreateDirectory(harness.Store.DirectoryFor(brokenId));
        File.WriteAllText(harness.Store.PathFor(brokenId), AlignDocumentId(json, brokenId));
        var before = harness.Session.CaptureAuthority();
        var locals = harness.Session.Vehicles.Select(item => item.SaveLocalDefinition).ToArray();
        var loaded = harness.Session.Load(brokenId);
        Assert.AreEqual(expected, loaded.Status, expected.ToString());
        Assert.IsFalse(loaded.Succeeded);
        Assert.AreEqual(before, harness.Session.CaptureAuthority());
        Assert.AreEqual(locals.Length, harness.Session.Vehicles.Count);
        for (var index = 0; index < locals.Length; index++)
        {
            if (locals[index] is null)
                Assert.IsNull(harness.Session.Vehicles[index].SaveLocalDefinition);
            else
                AssertSaveLocalEqual(locals[index]!, harness.Session.Vehicles[index].SaveLocalDefinition!);
        }
    }

    private static string AlignDocumentId(string json, Guid id)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            json,
            "\"id\":\"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\"");
        return match.Success
            ? string.Concat(json[..match.Index], $"\"id\":\"{id:D}\"", json[(match.Index + match.Length)..])
            : json;
    }

    private static string DuplicateVehicleId(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var vehicles = root["vehicles"]!.AsArray();
        vehicles.Add(vehicles[0]!.DeepClone());
        return root.ToJsonString();
    }

    private static string DuplicatePartCell(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        foreach (var vehicle in root["vehicles"]!.AsArray())
        {
            var parts = vehicle!["parts"]!.AsArray();
            if (parts.Count < 2)
                continue;
            parts[1]!["x"] = parts[0]!["x"]!.GetValue<int>();
            parts[1]!["y"] = parts[0]!["y"]!.GetValue<int>();
            parts[1]!["z"] = parts[0]!["z"]!.GetValue<int>();
            break;
        }

        return root.ToJsonString();
    }

    private static string UnboundContact(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var contact = root["vehicles"]![0]!["contacts"]![0]!;
        contact["bound_x"] = 99;
        contact["bound_y"] = 99;
        contact["bound_z"] = 99;
        return root.ToJsonString();
    }

    private static string RemoveSaveLocal(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root["save_local_definitions"] = new JsonArray();
        return root.ToJsonString();
    }

    private static string ForcePartOrientation(string json, string orientation)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        foreach (var vehicle in root["vehicles"]!.AsArray())
        {
            foreach (var part in vehicle!["parts"]!.AsArray())
            {
                if (part?["definition_id"]?.GetValue<string>() == HollowShellRefit.MarkerId.Value)
                    part["orientation"] = orientation;
            }
        }

        return root.ToJsonString();
    }

    private static string MutateField(string json, string name, string value)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root[name] = JsonNode.Parse(value);
        return root.ToJsonString();
    }

    private static string MutateDouble(string json, string from, string to) =>
        json.Replace(from, to, StringComparison.Ordinal);

    private static SessionHandle Open(string? existingUserData = null, MemoryContentSource? source = null)
    {
        var logger = LoggerSession.Create();
        var userData = existingUserData ?? Directory.CreateTempSubdirectory("train-slice3-saves-").FullName;
        var store = new ControllableStore(userData);
        var content = source ?? CompositeFixtures.FormalFixturePackage();
        var reloader = ContentFixtures.Create(content, logger.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var session = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, store, reloader.Composites);
        reloader.SetWorldReloadAdapter(VoxelWorldReloadAdapter.For(session));
        return new SessionHandle(session, reloader, logger, content, store, userData, existingUserData is null);
    }

    private sealed class SessionHandle : IDisposable
    {
        public VoxelDevelopmentSession Session { get; }
        public ContentReloader Reloader { get; set; }
        public MemoryContentSource Source { get; }
        public ControllableStore Store { get; }
        public string UserData { get; }
        public Train.Logging.TrainLogger Logger => _logger.Logger;
        private readonly LoggerSession _logger;
        private readonly bool _ownsUserData;

        public SessionHandle(
            VoxelDevelopmentSession session,
            ContentReloader reloader,
            LoggerSession logger,
            MemoryContentSource source,
            ControllableStore store,
            string userData,
            bool ownsUserData)
        {
            Session = session;
            Reloader = reloader;
            Source = source;
            Store = store;
            UserData = userData;
            _logger = logger;
            _ownsUserData = ownsUserData;
        }

        public void Dispose()
        {
            Session.Detach();
            _logger.Dispose();
            if (_ownsUserData && Directory.Exists(UserData))
                Directory.Delete(UserData, true);
        }
    }

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
}
