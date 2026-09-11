using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
public sealed class CompositeVehicleReloadTests
{
    [TestMethod]
    public void Absolute_durability_adapts_world_complete_refit_fragment_and_dropped_identities()
    {
        using var world = Open();
        var dropId = world.Session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        Assert.AreEqual(CompositeDamageStatus.Removed, world.Session.Damage(new[]
        {
            new CompositeDamageTarget(dropId, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(dropId, BridgeFixture.LeftCoreCell, 100),
            new CompositeDamageTarget(dropId, BridgeFixture.RightCoreCell, 100)
        }).Status);
        var dropped = world.Session.World.OccupiedVoxels().First(item => item.Value.RuntimeId is not null && item.Value.DefinitionId == HollowShellFixture.ShellId);
        Assert.AreEqual(WorldCommandStatus.Damaged, world.Session.Damage(dropped.Key, 25).Status);

        var worldCell = new WorldVoxelCoord(2, 2, 2);
        Assert.AreEqual(WorldCommandStatus.Added, world.Session.Add(worldCell, HollowShellFixture.ShellId, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Damaged, world.Session.Damage(worldCell, 25).Status);

        var completeId = world.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var completeCell = new LocalVoxelCoord(0, 1, 0);
        Assert.IsTrue(world.Session.Damage(completeId, completeCell, 25).Succeeded);

        var refitId = world.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var trace = HollowShellRefit.ApplySuccess(world.Session, refitId);

        var fragmentId = world.Session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        Assert.IsTrue(world.Session.Damage(fragmentId, BridgeFixture.BridgeCell, 100).Succeeded);
        var fragments = world.Session.QueryComposites().Where(item => item.DefinitionId == BridgeFixture.DefinitionId).ToArray();
        Assert.AreEqual(2, fragments.Length);
        var fragmentPart = fragments[0].Parts[0];
        Assert.IsTrue(world.Session.Damage(fragments[0].Id, fragmentPart.Cell, 25).Succeeded);

        AssertDurability(world, worldCell, completeId, completeCell, trace.P, fragments[0].Id, fragmentPart.Cell, dropped.Key, 75, 100);

        var rise = ReloadMarkerAndShell(world, maxDurability: 150);
        Assert.AreEqual(ContentReloadStatus.Committed, rise.Status);
        Assert.IsTrue(rise.Changed);
        AssertDurability(world, worldCell, completeId, completeCell, trace.P, fragments[0].Id, fragmentPart.Cell, dropped.Key, 75, 150);
        Assert.AreEqual(trace.P, world.Session.QueryComposite(refitId)!.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id);
        Assert.AreEqual(HollowShellRefit.MarkerOrientation, world.Session.QueryComposite(refitId)!.Parts.Single(part => part.Id == trace.P).Orientation);

        var clamp = ReloadMarkerAndShell(world, maxDurability: 60);
        Assert.AreEqual(ContentReloadStatus.Committed, clamp.Status);
        AssertDurability(world, worldCell, completeId, completeCell, trace.P, fragments[0].Id, fragmentPart.Cell, dropped.Key, 60, 60);
        Assert.AreEqual(dropped.Value.RuntimeId, world.Session.Query(dropped.Key).Voxel!.RuntimeId);
    }

    [TestMethod]
    public void Blueprint_layout_update_keeps_actual_vehicles_and_part_updates_do_not_rewrite_save_local_revision()
    {
        using var world = Open();
        var stockId = world.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var refitId = world.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        HollowShellRefit.ApplySuccess(world.Session, refitId);
        var stockBefore = world.Session.QueryComposite(stockId)!;
        var refitted = world.Session.Vehicles.Single(item => item.Id == refitId);
        var saveLocalRevision = refitted.SaveLocalRevision;
        var blueprintRevision = world.Reloader.Composites.Current.Composites[HollowShellFixture.DefinitionId].SemanticRevision;
        var coreRevision = world.Reloader.Catalog.Current!.Entries[HollowShellFixture.CoreId].SemanticRevision;

        world.Source.AddCompositeFile(
            "core",
            "composites/dev-hollow-shell.composites.json",
            WithExtraInteriorCell(CompositeFixtures.FixtureFileText()));
        var layout = Reload(world);
        Assert.AreEqual(ContentReloadStatus.Committed, layout.Status);
        Assert.AreEqual(168, world.Session.QueryComposite(stockId)!.Parts.Count);
        Assert.AreEqual(stockBefore.Parts.Select(part => part.Cell).ToHashSet().Count, world.Session.QueryComposite(stockId)!.Parts.Count);
        Assert.IsFalse(world.Session.QueryComposite(stockId)!.Parts.Any(part => part.Cell == ExtraInteriorCell));
        Assert.AreEqual(169, world.Session.QueryComposite(refitId)!.Parts.Count);
        Assert.AreEqual(saveLocalRevision, world.Session.Vehicles.Single(item => item.Id == refitId).SaveLocalRevision);
        Assert.AreNotEqual(blueprintRevision, world.Reloader.Composites.Current.Composites[HollowShellFixture.DefinitionId].SemanticRevision);

        var manufactured = world.Session.Manufacture(HollowShellFixture.DefinitionId);
        Assert.IsTrue(manufactured.Succeeded);
        Assert.AreEqual(169, manufactured.Parts!.Count);
        Assert.IsTrue(manufactured.Parts.Any(part => part.Cell == ExtraInteriorCell));

        world.Source.AddVoxelFile(
            "core",
            "voxels/identity-core.voxels.json",
            ContentFixtures.GoodsFile(ContentFixtures.Good(
                "identity-core",
                "核心",
                "核心说明",
                value: 20,
                tagsJson: "[\"identity_core\"]")));
        var partUpdate = Reload(world);
        Assert.AreEqual(ContentReloadStatus.Committed, partUpdate.Status);
        var afterPart = world.Session.Vehicles.Single(item => item.Id == refitId);
        Assert.AreEqual(saveLocalRevision, afterPart.SaveLocalRevision);
        Assert.AreEqual(saveLocalRevision, afterPart.LastUsedRevision);
        Assert.AreNotEqual(coreRevision, world.Reloader.Catalog.Current!.Entries[HollowShellFixture.CoreId].SemanticRevision);
        Assert.AreEqual(20, world.Reloader.Catalog.Current.Entries[HollowShellFixture.CoreId].Definition.Value);
        Assert.AreEqual(
            world.Reloader.Composites.Current.Composites[HollowShellFixture.DefinitionId].SemanticRevision,
            world.Session.Vehicles.Single(item => item.Id == stockId).LastUsedRevision);

        var p = world.Session.QueryComposite(refitId)!.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id;
        var beforeIllegal = world.Session.CaptureAuthority();
        Assert.IsFalse(world.Session.Refit(refitId, CompositeRefitOperation.Move(p, HollowShellRefit.Occupied)).Succeeded);
        Assert.AreEqual(beforeIllegal, world.Session.CaptureAuthority());
        Assert.IsTrue(world.Session.Refit(refitId, CompositeRefitOperation.Move(p, HollowShellRefit.Moved)).Succeeded);
        Assert.AreEqual(HollowShellRefit.Moved, world.Session.QueryComposite(refitId)!.Parts.Single(part => part.Id == p).Cell);
    }

    [TestMethod]
    public void Fragments_adapt_without_construction_or_restored_contacts()
    {
        using var world = Open();
        var vehicleId = world.Session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        Assert.IsTrue(world.Session.Damage(vehicleId, BridgeFixture.BridgeCell, 100).Succeeded);
        var before = world.Session.QueryComposites().Where(item => item.DefinitionId == BridgeFixture.DefinitionId).ToArray();
        Assert.AreEqual(2, before.Length);
        Assert.IsTrue(before.All(item => item.OperationalCondition == OperationalCondition.Disabled));
        Assert.IsTrue(before.All(item => item.Contacts.Count == 1));

        world.Source.AddVoxelFile(
            "core",
            "voxels/vehicle-shell.voxels.json",
            ContentFixtures.GoodsFile(ContentFixtures.Good(
                "vehicle-shell",
                "壳体",
                "壳体说明",
                value: 10,
                maxDurability: 150,
                tagsJson: "[]")));
        Assert.AreEqual(ContentReloadStatus.Committed, Reload(world).Status);

        var after = world.Session.QueryComposites().Where(item => item.DefinitionId == BridgeFixture.DefinitionId).ToArray();
        Assert.AreEqual(2, after.Length);
        Assert.AreEqual(before[0].Id, after[0].Id);
        Assert.AreEqual(before[1].Id, after[1].Id);
        Assert.AreEqual(27, after[0].Parts.Count);
        Assert.AreEqual(27, after[1].Parts.Count);
        Assert.AreEqual(ConstructionStatus.MissingContact, after[0].Construction);
        Assert.AreEqual(IdentityRetentionStatus.Retained, after[0].IdentityRetention);
        Assert.AreEqual(OperationalCondition.Disabled, after[0].OperationalCondition);
        Assert.AreEqual(1, after[0].Contacts.Count);
        Assert.AreEqual(before[0].Contacts[0].BoundCell, after[0].Contacts[0].BoundCell);
        Assert.AreEqual(0, world.Session.World.VoxelCount);
    }

    [TestMethod]
    public void Identity_rule_changes_reject_the_whole_candidate()
    {
        using var world = Open();
        world.Session.Manufacture(HollowShellFixture.DefinitionId);
        var generation = world.Reloader.Catalog.Current!.Generation;
        var compositeGeneration = world.Reloader.Composites.Current.Generation;
        var snapshot = world.Reloader.Catalog.Current;
        var composites = world.Reloader.Composites.Current;
        var before = world.Session.CaptureAuthority();

        world.Source.AddCompositeFile(
            "core",
            "composites/dev-hollow-shell.composites.json",
            CompositeFixtures.File(null, CompositeFixtures.Archetype(coreTag: "other_core"), CompositeFixtures.CompositeFromFixture()));
        var identity = Reload(world);
        Assert.AreEqual(ContentReloadStatus.Rejected, identity.Status);
        Assert.IsTrue(identity.Diagnostics.Any(item =>
            item.Code == ContentDiagnosticCodes.IncompatibleVoxelState &&
            item.JsonPath == HollowShellFixture.DefinitionId.Value &&
            item.Message.Contains("identity", StringComparison.Ordinal)));
        Assert.AreSame(snapshot, world.Reloader.Catalog.Current);
        Assert.AreSame(composites, world.Reloader.Composites.Current);
        Assert.AreEqual(generation, world.Reloader.Catalog.Current!.Generation);
        Assert.AreEqual(compositeGeneration, world.Reloader.Composites.Current.Generation);
        Assert.AreEqual(before, world.Session.CaptureAuthority());
        Assert.AreEqual(ConstructionStatus.Valid, world.Session.QueryComposites()[0].Construction);
        Assert.AreEqual(IdentityRetentionStatus.Retained, world.Session.QueryComposites()[0].IdentityRetention);
    }

    [TestMethod]
    public void Missing_required_part_definitions_reject_the_whole_candidate()
    {
        using var world = Open();
        var refitId = world.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        HollowShellRefit.ApplySuccess(world.Session, refitId);
        var generation = world.Reloader.Catalog.Current!.Generation;
        var snapshot = world.Reloader.Catalog.Current;
        var before = world.Session.CaptureAuthority();

        var missing = CompositeFixtures.FormalFixturePackage();
        missing.AddVoxelFile("core", "voxels/refit-marker.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("other-marker", "其他", "其他说明", value: 10, tagsJson: "[]")));
        var stripped = Rebuild(world, missing);
        var missingResult = stripped.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, missingResult.Status);
        Assert.IsTrue(missingResult.Diagnostics.Any(item =>
            item.Code == ContentDiagnosticCodes.UnresolvedVoxelReference &&
            item.JsonPath == HollowShellRefit.MarkerId.Value));
        Assert.AreSame(snapshot, world.Reloader.Catalog.Current);
        Assert.AreEqual(before, world.Session.CaptureAuthority());
        Assert.AreEqual(generation, world.Reloader.Catalog.Current.Generation);
        Assert.AreEqual(169, world.Session.QueryComposite(refitId)!.Parts.Count);
    }

    [TestMethod]
    public void Single_disallowed_orientation_rejects_pending_adaptations_then_commits_after_restore()
    {
        using var world = Open();
        var worldCell = new WorldVoxelCoord(2, 2, 2);
        Assert.AreEqual(WorldCommandStatus.Added, world.Session.Add(worldCell, HollowShellFixture.ShellId, VoxelOrientation.Identity).Status);
        Assert.AreEqual(WorldCommandStatus.Damaged, world.Session.Damage(worldCell, 25).Status);

        var stockId = world.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var stockCell = new LocalVoxelCoord(0, 1, 0);
        Assert.IsTrue(world.Session.Damage(stockId, stockCell, 25).Succeeded);

        var refitId = world.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var trace = HollowShellRefit.ApplySuccess(world.Session, refitId);
        var generation = world.Reloader.Catalog.Current!.Generation;
        var compositeGeneration = world.Reloader.Composites.Current.Generation;
        var snapshot = world.Reloader.Catalog.Current;
        var composites = world.Reloader.Composites.Current;
        var before = world.Session.CaptureAuthority();
        var stockBefore = world.Session.QueryComposite(stockId)!;
        var refitBefore = world.Session.QueryComposite(refitId)!;
        var refitted = world.Session.Vehicles.Single(item => item.Id == refitId);

        WriteShellMax(world, 60);
        WriteMarker(world, orientations: "none");
        var rejected = Reload(world);
        Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status);
        Assert.IsFalse(rejected.Changed);
        Assert.AreEqual(1, rejected.Diagnostics.Count(item => item.Code == ContentDiagnosticCodes.IncompatibleVoxelState));
        Assert.IsTrue(rejected.Diagnostics.Any(item =>
            item.Code == ContentDiagnosticCodes.IncompatibleVoxelState &&
            item.JsonPath == HollowShellRefit.MarkerId.Value &&
            item.Message.Contains("orientation", StringComparison.Ordinal)));
        Assert.IsFalse(rejected.Diagnostics.Any(item => item.Message.Contains("identity", StringComparison.Ordinal)));
        Assert.AreSame(snapshot, world.Reloader.Catalog.Current);
        Assert.AreSame(composites, world.Reloader.Composites.Current);
        Assert.AreEqual(generation, world.Reloader.Catalog.Current!.Generation);
        Assert.AreEqual(compositeGeneration, world.Reloader.Composites.Current.Generation);
        Assert.AreEqual(before, world.Session.CaptureAuthority());
        Assert.AreEqual(75, world.Session.Query(worldCell).Voxel!.CurrentDurability);
        Assert.AreEqual(100, world.Session.Query(worldCell).MaxDurability);
        Assert.AreEqual(75, world.Session.QueryComposite(stockId)!.Parts.Single(part => part.Cell == stockCell).CurrentDurability);
        Assert.AreEqual(stockBefore.Id, world.Session.QueryComposite(stockId)!.Id);
        Assert.AreEqual(stockBefore.Transform, world.Session.QueryComposite(stockId)!.Transform);
        Assert.IsTrue(stockBefore.Contacts.SequenceEqual(world.Session.QueryComposite(stockId)!.Contacts));
        Assert.AreEqual(refitBefore.Id, world.Session.QueryComposite(refitId)!.Id);
        Assert.AreEqual(refitBefore.Transform, world.Session.QueryComposite(refitId)!.Transform);
        Assert.AreEqual(refitBefore.SaveLocalRevision, world.Session.QueryComposite(refitId)!.SaveLocalRevision);
        Assert.AreEqual(refitted.SaveLocalRevision, world.Session.Vehicles.Single(item => item.Id == refitId).SaveLocalRevision);
        Assert.AreEqual(HollowShellRefit.MarkerOrientation, world.Session.QueryComposite(refitId)!.Parts.Single(part => part.Id == trace.P).Orientation);
        Assert.AreEqual(75, world.Session.QueryComposite(refitId)!.Parts.Single(part => part.Id == trace.P).CurrentDurability);
        Assert.AreEqual(ConstructionStatus.Valid, world.Session.QueryComposite(stockId)!.Construction);
        Assert.AreEqual(IdentityRetentionStatus.Retained, world.Session.QueryComposite(stockId)!.IdentityRetention);
        Assert.AreEqual(IdentityRetentionStatus.Retained, world.Session.QueryComposite(refitId)!.IdentityRetention);

        Assert.IsNotNull(world.Session.QueryComposite(stockId));
        var illegal = world.Session.CaptureAuthority();
        Assert.IsFalse(world.Session.Refit(refitId, CompositeRefitOperation.Move(trace.P, HollowShellRefit.Occupied)).Succeeded);
        Assert.AreEqual(illegal, world.Session.CaptureAuthority());
        Assert.IsTrue(world.Session.Damage(stockId, stockCell, 10).Succeeded);
        Assert.AreEqual(65, world.Session.QueryComposite(stockId)!.Parts.Single(part => part.Cell == stockCell).CurrentDurability);

        WriteMarker(world, orientations: "all_24");
        var committed = Reload(world);
        Assert.AreEqual(ContentReloadStatus.Committed, committed.Status);
        Assert.IsTrue(committed.Changed);
        Assert.AreNotSame(snapshot, world.Reloader.Catalog.Current);
        Assert.AreNotEqual(generation, world.Reloader.Catalog.Current!.Generation);
        Assert.AreEqual(60, world.Session.Query(worldCell).Voxel!.CurrentDurability);
        Assert.AreEqual(60, world.Session.Query(worldCell).MaxDurability);
        Assert.AreEqual(60, world.Session.QueryComposite(stockId)!.Parts.Single(part => part.Cell == stockCell).CurrentDurability);
        Assert.AreEqual(stockBefore.Id, world.Session.QueryComposite(stockId)!.Id);
        Assert.AreEqual(refitBefore.Id, world.Session.QueryComposite(refitId)!.Id);
        Assert.AreEqual(HollowShellRefit.SecondAdd, world.Session.QueryComposite(refitId)!.Parts.Single(part => part.Id == trace.P).Cell);
        Assert.AreEqual(HollowShellRefit.MarkerOrientation, world.Session.QueryComposite(refitId)!.Parts.Single(part => part.Id == trace.P).Orientation);
        Assert.AreEqual(75, world.Session.QueryComposite(refitId)!.Parts.Single(part => part.Id == trace.P).CurrentDurability);
        Assert.AreEqual(refitted.SaveLocalRevision, world.Session.Vehicles.Single(item => item.Id == refitId).SaveLocalRevision);
        Assert.AreEqual(IdentityRetentionStatus.Retained, world.Session.QueryComposite(refitId)!.IdentityRetention);
        Assert.IsTrue(world.Session.Refit(refitId, CompositeRefitOperation.Move(trace.P, HollowShellRefit.Moved)).Succeeded);
        Assert.AreEqual(HollowShellRefit.Moved, world.Session.QueryComposite(refitId)!.Parts.Single(part => part.Id == trace.P).Cell);
    }

    [TestMethod]
    public void Construction_rule_changes_adapt_existing_vehicles_without_requiring_manufacture_validity()
    {
        using var world = Open();
        world.Session.Manufacture(HollowShellFixture.DefinitionId);
        var before = world.Session.CaptureAuthority();
        world.Source.AddCompositeFile(
            "core",
            "composites/dev-hollow-shell.composites.json",
            CompositeFixtures.File(null, CompositeFixtures.Archetype(contactCount: 3), CompositeFixtures.CompositeFromFixture()));
        var result = Reload(world);
        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        var query = world.Session.QueryComposites()[0];
        Assert.AreEqual(before.Vehicles[0].Id, query.Id);
        Assert.AreEqual(168, query.Parts.Count);
        Assert.AreEqual(ConstructionStatus.MissingContact, query.Construction);
        Assert.AreEqual(IdentityRetentionStatus.Retained, query.IdentityRetention);
        Assert.AreEqual(OperationalCondition.Unevaluated, query.OperationalCondition);
        Assert.AreEqual(2, query.Contacts.Count);
    }

    [TestMethod]
    public void Display_and_collision_publish_without_implicit_physics_or_generation_on_no_change()
    {
        using var world = Open();
        var worldCell = new WorldVoxelCoord(3, 3, 3);
        Assert.AreEqual(WorldCommandStatus.Added, world.Session.Add(worldCell, HollowShellFixture.ShellId, VoxelOrientation.Identity).Status);
        var vehicleId = world.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        HollowShellRefit.ApplySuccess(world.Session, vehicleId);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, world.Session.QueryCollision(worldCell).Status);
        var shell = world.Reloader.Catalog.Current!.Entries[HollowShellFixture.ShellId];
        var generation = world.Reloader.Catalog.Current.Generation;
        var notifications = 0;
        world.Session.Changed += () => notifications++;

        world.Source.AddVoxelFile(
            "core",
            "voxels/vehicle-shell.voxels.json",
            ContentFixtures.GoodsFile(ContentFixtures.Good(
                "vehicle-shell",
                "显示壳体",
                "壳体说明",
                value: 10,
                tagsJson: "[]")));
        var display = Reload(world);
        Assert.AreEqual(ContentReloadStatus.Committed, display.Status);
        Assert.IsTrue(display.Changed);
        var afterDisplay = world.Reloader.Catalog.Current!.Entries[HollowShellFixture.ShellId];
        Assert.AreEqual(shell.SemanticRevision, afterDisplay.SemanticRevision);
        Assert.AreEqual("显示壳体", afterDisplay.Definition.Name);
        Assert.AreEqual(VoxelCollisionKind.FullBox, afterDisplay.Definition.Collision.Kind);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, world.Session.QueryCollision(worldCell).Status);
        Assert.AreEqual(169, world.Session.QueryComposite(vehicleId)!.Parts.Count);
        Assert.IsTrue(notifications > 0);

        var unchangedGeneration = world.Reloader.Catalog.Current.Generation;
        var unchanged = world.Reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, unchanged.Status);
        Assert.IsFalse(unchanged.Changed);
        Assert.AreEqual(unchangedGeneration, unchanged.CatalogVersion);

        world.Source.AddVoxelFile(
            "core",
            "voxels/vehicle-shell.voxels.json",
            ContentFixtures.GoodsFile(ContentFixtures.Good(
                "vehicle-shell",
                "显示壳体",
                "开发夹具壳体。",
                value: 10,
                tagsJson: "[]",
                collisionJson: "{\"kind\":\"none\"}")));
        var collision = Reload(world);
        Assert.AreEqual(ContentReloadStatus.Committed, collision.Status);
        Assert.AreNotEqual(shell.SemanticRevision, world.Reloader.Catalog.Current!.Entries[HollowShellFixture.ShellId].SemanticRevision);
        Assert.AreEqual(VoxelCollisionQueryStatus.Miss, world.Session.QueryCollision(worldCell).Status);
        Assert.AreNotEqual(generation, world.Reloader.Catalog.Current.Generation);
    }

    [TestMethod]
    public void Fracture_after_reload_uses_current_right_core_value()
    {
        using var world = Open();
        world.Source.AddVoxelFile(
            "core",
            "voxels/identity-core-heavy.voxels.json",
            ContentFixtures.GoodsFile(ContentFixtures.Good(
                "identity-core-heavy",
                "重核",
                "重核说明",
                value: 10,
                tagsJson: "[\"identity_core\"]")));
        Assert.AreEqual(ContentReloadStatus.Committed, Reload(world).Status);
        var manufactured = world.Session.Manufacture(BridgeFixture.RightHeavyDefinitionId);
        Assert.IsTrue(manufactured.Succeeded);
        var originalId = manufactured.VehicleId!.Value;
        Assert.AreEqual(10, world.Reloader.Catalog.Current!.Entries[BridgeFixture.HeavyCoreDefinitionId].Definition.Value);

        world.Source.AddVoxelFile(
            "core",
            "voxels/identity-core-heavy.voxels.json",
            ContentFixtures.GoodsFile(ContentFixtures.Good(
                "identity-core-heavy",
                "重核",
                "重核说明",
                value: 20,
                tagsJson: "[\"identity_core\"]")));
        Assert.AreEqual(ContentReloadStatus.Committed, Reload(world).Status);
        Assert.AreEqual(20, world.Reloader.Catalog.Current!.Entries[BridgeFixture.HeavyCoreDefinitionId].Definition.Value);

        Assert.IsTrue(world.Session.Damage(originalId, BridgeFixture.BridgeCell, 100).Succeeded);
        var vehicles = world.Session.QueryComposites();
        Assert.AreEqual(2, vehicles.Count);
        Assert.AreEqual(originalId, vehicles[0].Id);
        Assert.IsTrue(vehicles[0].Parts.All(part => BridgeFixture.IsRightCell(part.Cell)));
        Assert.IsTrue(vehicles[1].Parts.All(part => BridgeFixture.IsLeftCell(part.Cell)));
        Assert.AreEqual(OperationalCondition.Disabled, vehicles[0].OperationalCondition);
        Assert.AreEqual(ConstructionStatus.MissingContact, vehicles[0].Construction);
    }

    [TestMethod]
    public void Save_still_rejects_vehicles_after_successful_adaptation()
    {
        using var world = Open();
        var store = new RecordingStore();
        var session = new VoxelDevelopmentSession(world.Reloader.Catalog, VoxelWorldIdentity.Slice3, store, world.Reloader.Composites);
        var vehicleId = session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        Assert.IsTrue(session.Damage(vehicleId, new LocalVoxelCoord(0, 1, 0), 25).Succeeded);
        world.Reloader.SetWorldReloadAdapter(VoxelWorldReloadAdapter.For(session));
        world.Source.AddVoxelFile(
            "core",
            "voxels/vehicle-shell.voxels.json",
            ContentFixtures.GoodsFile(ContentFixtures.Good(
                "vehicle-shell",
                "壳体",
                "壳体说明",
                value: 10,
                maxDurability: 150,
                tagsJson: "[]")));
        Assert.AreEqual(ContentReloadStatus.Committed, world.Reloader.Reload().Status);
        var before = session.CaptureAuthority();
        Assert.AreEqual(WorldSaveStatus.UnsupportedVehicles, session.Save("Should Fail").Status);
        Assert.IsFalse(store.Wrote);
        Assert.AreEqual(before, session.CaptureAuthority());
        session.Detach();
    }

    private static readonly LocalVoxelCoord ExtraInteriorCell = new(2, 1, 0);

    private static void AssertDurability(
        SessionHandle world,
        WorldVoxelCoord worldCell,
        EntityId completeId,
        LocalVoxelCoord completeCell,
        EntityId refitPart,
        EntityId fragmentId,
        LocalVoxelCoord fragmentCell,
        WorldVoxelCoord droppedCell,
        int durability,
        int max)
    {
        Assert.AreEqual(durability, world.Session.Query(worldCell).Voxel!.CurrentDurability);
        Assert.AreEqual(max, world.Session.Query(worldCell).MaxDurability);
        Assert.AreEqual(durability, world.Session.QueryComposite(completeId)!.Parts.Single(part => part.Cell == completeCell).CurrentDurability);
        Assert.AreEqual(durability, world.Session.QueryComposite(world.Session.QueryComposites().Single(item => item.Parts.Any(part => part.Id == refitPart)).Id)!.Parts.Single(part => part.Id == refitPart).CurrentDurability);
        Assert.AreEqual(durability, world.Session.QueryComposite(fragmentId)!.Parts.Single(part => part.Cell == fragmentCell).CurrentDurability);
        Assert.AreEqual(durability, world.Session.Query(droppedCell).Voxel!.CurrentDurability);
        Assert.AreEqual(max, world.Session.Query(droppedCell).MaxDurability);
        if (durability < max)
        {
            Assert.IsTrue(world.Session.QueryDamagedParts().Any(part => part.PartId == refitPart && part.CurrentDurability == durability && part.MaxDurability == max));
            return;
        }

        Assert.IsFalse(world.Session.QueryDamagedParts().Any(part => part.PartId == refitPart));
    }

    private static void WriteShellMax(SessionHandle world, int maxDurability) =>
        world.Source.AddVoxelFile(
            "core",
            "voxels/vehicle-shell.voxels.json",
            ContentFixtures.GoodsFile(ContentFixtures.Good(
                "vehicle-shell",
                "壳体",
                "壳体说明",
                value: 10,
                maxDurability: maxDurability,
                tagsJson: "[]")));

    private static void WriteMarker(SessionHandle world, string orientations, int maxDurability = 100) =>
        world.Source.AddVoxelFile(
            "core",
            "voxels/refit-marker.voxels.json",
            ContentFixtures.GoodsFile(ContentFixtures.Good(
                "refit-marker",
                "改装标记",
                "带可见朝向标记的改装设备",
                value: 10,
                maxDurability: maxDurability,
                tagsJson: "[]",
                orientations: orientations,
                top: "voxels/refit-marker/top.png",
                side: "voxels/refit-marker/side.png",
                bottom: "voxels/refit-marker/bottom.png",
                icon: "voxels/refit-marker/icon.png")));

    private static ContentReloadResult ReloadMarkerAndShell(SessionHandle world, int maxDurability)
    {
        world.Source.AddVoxelFile(
            "core",
            "voxels/refit-marker.voxels.json",
            ContentFixtures.GoodsFile(ContentFixtures.Good(
                "refit-marker",
                "改装标记",
                "带可见朝向标记的改装设备",
                value: 10,
                maxDurability: maxDurability,
                tagsJson: "[]",
                top: "voxels/refit-marker/top.png",
                side: "voxels/refit-marker/side.png",
                bottom: "voxels/refit-marker/bottom.png",
                icon: "voxels/refit-marker/icon.png")));
        world.Source.AddVoxelFile(
            "core",
            "voxels/vehicle-shell.voxels.json",
            ContentFixtures.GoodsFile(ContentFixtures.Good(
                "vehicle-shell",
                "壳体",
                "壳体说明",
                value: 10,
                maxDurability: maxDurability,
                tagsJson: "[]")));
        return Reload(world);
    }

    private static string WithExtraInteriorCell(string fixture) =>
        fixture.Replace(
            "{ \"x\": 0, \"y\": 0, \"z\": -1, \"definition\": \"vehicle-shell\", \"orientation\": \"px_py\" }",
            "{ \"x\": 0, \"y\": 0, \"z\": -1, \"definition\": \"vehicle-shell\", \"orientation\": \"px_py\" },\n        { \"x\": 2, \"y\": 1, \"z\": 0, \"definition\": \"vehicle-shell\", \"orientation\": \"px_py\" }",
            StringComparison.Ordinal);

    private static ContentReloadResult Reload(SessionHandle world) =>
        Rebuild(world, world.Source).Reload();

    private static ContentReloader Rebuild(SessionHandle world, MemoryContentSource source)
    {
        var rebuilt = ContentFixtures.Create(
            source,
            world.Logger,
            catalog: world.Reloader.Catalog,
            settlements: world.Reloader.Settlements,
            composites: world.Reloader.Composites);
        rebuilt.SetWorldReloadAdapter(VoxelWorldReloadAdapter.For(world.Session));
        world.Reloader = rebuilt;
        return rebuilt;
    }

    private static SessionHandle Open()
    {
        var source = CompositeFixtures.FormalFixturePackage();
        var (reloader, logger) = CompositeFixtures.Load(source);
        var session = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
        reloader.SetWorldReloadAdapter(VoxelWorldReloadAdapter.For(session));
        return new SessionHandle(session, reloader, logger, source);
    }

    private sealed class SessionHandle : IDisposable
    {
        public VoxelDevelopmentSession Session { get; }
        public ContentReloader Reloader { get; set; }
        public MemoryContentSource Source { get; }
        public Train.Logging.TrainLogger Logger => _logger.Logger;
        private readonly LoggerSession _logger;

        public SessionHandle(
            VoxelDevelopmentSession session,
            ContentReloader reloader,
            LoggerSession logger,
            MemoryContentSource source)
        {
            Session = session;
            Reloader = reloader;
            Source = source;
            _logger = logger;
        }

        public void Dispose()
        {
            Session.Detach();
            _logger.Dispose();
        }
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
