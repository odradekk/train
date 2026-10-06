using Train.Core.Saves;
using Train.Core.Content.Voxels;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:composites")]
public sealed class CompositeRefitTests
{
    [TestMethod]
    public void Official_success_sequence_keeps_identity_durability_and_orientation()
    {
        using var world = Open();
        var session = world.Session;
        var manufactured = session.Manufacture(HollowShellFixture.DefinitionId);
        Assert.IsTrue(manufactured.Succeeded);
        var vehicleId = manufactured.VehicleId!.Value;
        var published = world.Reloader.Composites.Current.Composites[HollowShellFixture.DefinitionId];
        var blueprintRevision = published.SemanticRevision;
        var blueprintCells = published.Definition.Voxels.Select(item => item.Cell).ToArray();
        var coreId = session.QueryComposite(vehicleId)!.Parts.Single(part => part.Cell == HollowShellFixture.CoreCell).Id;
        var changed = new List<CompositeAuthoritySnapshot>();
        session.Changed += () => changed.Add(session.CaptureAuthority());

        var added = session.Refit(vehicleId, CompositeRefitOperation.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId, VoxelOrientation.Identity));
        Assert.IsTrue(added.Succeeded);
        var p = added.AllocatedIds!.Single();
        Assert.AreEqual(1, changed.Count);
        var afterAdd = session.QueryComposite(vehicleId)!;
        Assert.AreEqual(169, afterAdd.Parts.Count);
        Assert.AreEqual(p, afterAdd.Parts.Single(part => part.Cell == HollowShellRefit.FirstAdd).Id);
        Assert.AreEqual(100, afterAdd.Parts.Single(part => part.Cell == HollowShellRefit.FirstAdd).CurrentDurability);
        Assert.AreEqual(HollowShellRefit.MarkerId, afterAdd.Parts.Single(part => part.Cell == HollowShellRefit.FirstAdd).DefinitionId);
        Assert.AreEqual(vehicleId, afterAdd.Id);
        Assert.AreEqual(coreId, afterAdd.Parts.Single(part => part.Cell == HollowShellFixture.CoreCell).Id);
        Assert.AreNotEqual(blueprintRevision, afterAdd.LastUsedRevision);
        Assert.AreEqual(afterAdd.LastUsedRevision, afterAdd.SaveLocalRevision);
        Assert.AreEqual(blueprintRevision, world.Reloader.Composites.Current.Composites[HollowShellFixture.DefinitionId].SemanticRevision);
        CollectionAssert.AreEqual(blueprintCells, world.Reloader.Composites.Current.Composites[HollowShellFixture.DefinitionId].Definition.Voxels.Select(item => item.Cell).ToArray());

        var damaged = session.Damage(vehicleId, HollowShellRefit.FirstAdd, 25);
        Assert.IsTrue(damaged.Succeeded);
        Assert.AreEqual(75, session.QueryComposite(vehicleId)!.Parts.Single(part => part.Id == p).CurrentDurability);

        Assert.IsTrue(session.Refit(vehicleId, CompositeRefitOperation.Move(p, HollowShellRefit.Moved)).Succeeded);
        var afterMove = session.QueryComposite(vehicleId)!;
        Assert.AreEqual(p, afterMove.Parts.Single(part => part.Cell == HollowShellRefit.Moved).Id);
        Assert.AreEqual(75, afterMove.Parts.Single(part => part.Id == p).CurrentDurability);
        Assert.IsFalse(afterMove.Parts.Any(part => part.Cell == HollowShellRefit.FirstAdd));

        Assert.IsTrue(session.Refit(vehicleId, CompositeRefitOperation.Rotate(p, HollowShellRefit.MarkerOrientation)).Succeeded);
        var afterRotate = session.QueryComposite(vehicleId)!;
        var rotated = afterRotate.Parts.Single(part => part.Id == p);
        Assert.AreEqual(HollowShellRefit.Moved, rotated.Cell);
        Assert.AreEqual(75, rotated.CurrentDurability);
        Assert.AreEqual(HollowShellRefit.MarkerOrientation, rotated.Orientation);
        Assert.AreEqual("nz_py", rotated.Orientation.ToString());

        var addedQ = session.Refit(vehicleId, CompositeRefitOperation.Add(HollowShellRefit.SecondAdd, HollowShellRefit.MarkerId, VoxelOrientation.Identity));
        Assert.IsTrue(addedQ.Succeeded);
        var q = addedQ.AllocatedIds!.Single();
        Assert.AreNotEqual(p, q);

        changed.Clear();
        Assert.IsTrue(session.Refit(vehicleId, new[]
        {
            CompositeRefitOperation.Move(p, HollowShellRefit.SecondAdd),
            CompositeRefitOperation.Move(q, HollowShellRefit.Moved)
        }).Succeeded);
        Assert.AreEqual(1, changed.Count);
        var afterSwap = session.QueryComposite(vehicleId)!;
        Assert.AreEqual(changed[0], session.CaptureAuthority());
        Assert.AreEqual(p, afterSwap.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id);
        Assert.AreEqual(q, afterSwap.Parts.Single(part => part.Cell == HollowShellRefit.Moved).Id);
        Assert.AreEqual(75, afterSwap.Parts.Single(part => part.Id == p).CurrentDurability);
        Assert.AreEqual(HollowShellRefit.MarkerOrientation, afterSwap.Parts.Single(part => part.Id == p).Orientation);

        var replaced = session.Refit(vehicleId, CompositeRefitOperation.Replace(HollowShellRefit.Moved, HollowShellRefit.ReplacementId, VoxelOrientation.Identity));
        Assert.IsTrue(replaced.Succeeded);
        var r = replaced.AllocatedIds!.Single();
        Assert.AreNotEqual(p, r);
        Assert.AreNotEqual(q, r);
        var afterReplace = session.QueryComposite(vehicleId)!;
        Assert.IsFalse(afterReplace.Parts.Any(part => part.Id == q));
        var newR = afterReplace.Parts.Single(part => part.Cell == HollowShellRefit.Moved);
        Assert.AreEqual(r, newR.Id);
        Assert.AreEqual(150, newR.CurrentDurability);
        Assert.AreEqual(HollowShellRefit.ReplacementId, newR.DefinitionId);
        Assert.AreEqual(VoxelOrientation.Identity, newR.Orientation);
        Assert.AreEqual(p, afterReplace.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id);

        Assert.IsTrue(session.Refit(vehicleId, CompositeRefitOperation.Remove(r)).Succeeded);
        var final = session.QueryComposite(vehicleId)!;
        Assert.IsFalse(final.Parts.Any(part => part.Id == q || part.Id == r));
        Assert.AreEqual(169, final.Parts.Count);
        Assert.AreEqual(p, final.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id);
        Assert.AreEqual(75, final.Parts.Single(part => part.Id == p).CurrentDurability);
        Assert.AreEqual(HollowShellRefit.MarkerOrientation, final.Parts.Single(part => part.Id == p).Orientation);
        Assert.AreEqual(vehicleId, final.Id);
        Assert.AreEqual(coreId, final.Parts.Single(part => part.Cell == HollowShellFixture.CoreCell).Id);
        Assert.AreEqual(HollowShellFixture.CoreCell, final.Contacts[0].BoundCell);
        Assert.AreEqual(coreId, final.Contacts[0].BoundPartId);
        Assert.AreEqual(HollowShellFixture.RearBoundCell, final.Contacts[1].BoundCell);
        Assert.AreEqual(final.Parts.Single(part => part.Cell == HollowShellFixture.RearBoundCell).Id, final.Contacts[1].BoundPartId);
        Assert.AreEqual(0, session.World.VoxelCount);
        Assert.AreEqual(ConstructionStatus.Valid, final.Construction);
        Assert.AreEqual(blueprintRevision, world.Reloader.Composites.Current.Composites[HollowShellFixture.DefinitionId].SemanticRevision);
    }

    [TestMethod]
    public void Official_helper_matches_stepwise_success_and_does_not_hardcode_ids()
    {
        using var first = Open();
        using var second = Open();
        var a = first.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var b = second.Session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var left = HollowShellRefit.ApplySuccess(first.Session, a);
        var right = HollowShellRefit.ApplySuccess(second.Session, b);
        Assert.AreEqual(left.P.Value, right.P.Value);
        Assert.AreEqual(left.Q.Value, right.Q.Value);
        Assert.AreEqual(left.R.Value, right.R.Value);
        Assert.AreEqual(left.Success.Vehicles[0].Parts.Count, right.Success.Vehicles[0].Parts.Count);
        Assert.AreNotEqual(left.P, left.Q);
        Assert.AreNotEqual(left.P, left.R);
    }

    [TestMethod]
    public void Failed_batches_keep_full_authority_and_allow_later_legal_work()
    {
        using var world = Open();
        var session = world.Session;
        var vehicleId = session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var success = HollowShellRefit.ApplySuccess(session, vehicleId);
        var p = success.P;
        var next = session.NextEntityId;
        var transactions = session.TransactionCount;
        var changed = 0;
        session.Changed += () => changed++;

        var before = session.CaptureAuthority();
        var envelope = session.Refit(vehicleId, new[]
        {
            CompositeRefitOperation.Rotate(p, VoxelOrientation.Identity),
            CompositeRefitOperation.Add(HollowShellRefit.OutOfEnvelope, HollowShellRefit.MarkerId, VoxelOrientation.Identity)
        });
        Assert.AreEqual(ConstructionStatus.EnvelopeExceeded, envelope.Status);
        Assert.IsFalse(envelope.Succeeded);
        Assert.AreEqual(before, session.CaptureAuthority());
        Assert.AreEqual(next, session.NextEntityId);
        Assert.AreEqual(transactions, session.TransactionCount);
        Assert.AreEqual(0, changed);
        Assert.AreEqual(HollowShellRefit.MarkerOrientation, session.QueryComposite(vehicleId)!.Parts.Single(part => part.Id == p).Orientation);

        Assert.AreEqual(ConstructionStatus.DuplicateOccupancy, session.Refit(vehicleId, CompositeRefitOperation.Move(p, HollowShellRefit.Occupied)).Status);
        Assert.AreEqual(before, session.CaptureAuthority());

        Assert.AreEqual(ConstructionStatus.Disconnected, session.Refit(vehicleId, CompositeRefitOperation.Move(p, HollowShellRefit.Disconnected)).Status);
        Assert.AreEqual(before, session.CaptureAuthority());
        Assert.AreEqual(0, session.World.VoxelCount);
        Assert.AreEqual(1, session.QueryComposites().Count);

        Assert.IsTrue(session.Refit(vehicleId, CompositeRefitOperation.Move(p, HollowShellRefit.Moved)).Succeeded);
        Assert.AreEqual(p, session.QueryComposite(vehicleId)!.Parts.Single(part => part.Cell == HollowShellRefit.Moved).Id);
        Assert.AreEqual(75, session.QueryComposite(vehicleId)!.Parts.Single(part => part.Id == p).CurrentDurability);
        Assert.IsTrue(session.Refit(vehicleId, CompositeRefitOperation.Move(p, HollowShellRefit.SecondAdd)).Succeeded);
        Assert.AreEqual(before.Vehicles[0].Id, session.QueryComposite(vehicleId)!.Id);
    }

    [TestMethod]
    public void Locked_orientation_rotate_is_rejected_from_the_official_success_result()
    {
        using var world = Open();
        var session = world.Session;
        var vehicleId = session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var success = HollowShellRefit.ApplySuccess(session, vehicleId);
        var added = session.Refit(vehicleId, CompositeRefitOperation.Add(HollowShellRefit.LockedCell, HollowShellRefit.LockedId, VoxelOrientation.Identity));
        Assert.IsTrue(added.Succeeded);
        var locked = added.AllocatedIds!.Single();
        var before = session.CaptureAuthority();
        var next = session.NextEntityId;
        var transactions = session.TransactionCount;

        var rejected = session.Refit(vehicleId, CompositeRefitOperation.Rotate(locked, HollowShellRefit.MarkerOrientation));
        Assert.AreEqual(ConstructionStatus.DisallowedOrientation, rejected.Status);
        Assert.AreEqual(before, session.CaptureAuthority());
        Assert.AreEqual(next, session.NextEntityId);
        Assert.AreEqual(transactions, session.TransactionCount);
        Assert.AreEqual(VoxelOrientation.Identity, session.QueryComposite(vehicleId)!.Parts.Single(part => part.Id == locked).Orientation);
        Assert.AreEqual(success.P, session.QueryComposite(vehicleId)!.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id);
        Assert.AreEqual(HollowShellRefit.MarkerOrientation, session.QueryComposite(vehicleId)!.Parts.Single(part => part.Id == success.P).Orientation);

        Assert.IsTrue(session.Refit(vehicleId, CompositeRefitOperation.Remove(locked)).Succeeded);
        Assert.IsFalse(session.QueryComposite(vehicleId)!.Parts.Any(part => part.Id == locked));
        Assert.IsTrue(session.Refit(vehicleId, CompositeRefitOperation.Move(success.P, HollowShellRefit.Moved)).Succeeded);
        Assert.AreEqual(75, session.QueryComposite(vehicleId)!.Parts.Single(part => part.Id == success.P).CurrentDurability);
    }

    [TestMethod]
    public void Batch_swap_is_not_sequential_apply_and_failed_add_does_not_allocate()
    {
        using var world = Open();
        var session = world.Session;
        var vehicleId = session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var p = session.Refit(vehicleId, CompositeRefitOperation.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId, VoxelOrientation.Identity)).AllocatedIds!.Single();
        session.Damage(vehicleId, HollowShellRefit.FirstAdd, 25);
        session.Refit(vehicleId, CompositeRefitOperation.Move(p, HollowShellRefit.Moved));
        session.Refit(vehicleId, CompositeRefitOperation.Rotate(p, HollowShellRefit.MarkerOrientation));
        var q = session.Refit(vehicleId, CompositeRefitOperation.Add(HollowShellRefit.SecondAdd, HollowShellRefit.MarkerId, VoxelOrientation.Identity)).AllocatedIds!.Single();

        var sequential = session.Refit(vehicleId, CompositeRefitOperation.Move(p, HollowShellRefit.SecondAdd));
        Assert.AreEqual(ConstructionStatus.DuplicateOccupancy, sequential.Status);
        Assert.AreEqual(p, session.QueryComposite(vehicleId)!.Parts.Single(part => part.Cell == HollowShellRefit.Moved).Id);
        Assert.AreEqual(q, session.QueryComposite(vehicleId)!.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id);

        Assert.IsTrue(session.Refit(vehicleId, new[]
        {
            CompositeRefitOperation.Move(p, HollowShellRefit.SecondAdd),
            CompositeRefitOperation.Move(q, HollowShellRefit.Moved)
        }).Succeeded);

        var before = session.CaptureAuthority();
        var rejected = session.Refit(vehicleId, CompositeRefitOperation.Add(HollowShellRefit.OutOfEnvelope, HollowShellRefit.MarkerId, VoxelOrientation.Identity));
        Assert.AreEqual(ConstructionStatus.EnvelopeExceeded, rejected.Status);
        Assert.IsTrue(rejected.AllocatedIds is null || rejected.AllocatedIds.Count == 0);
        Assert.AreEqual(before.NextEntityId, session.NextEntityId);
        Assert.AreEqual(before, session.CaptureAuthority());
    }

    [TestMethod]
    public void Damage_after_refit_reads_actual_layout_and_same_identity()
    {
        using var world = Open();
        var session = world.Session;
        var vehicleId = session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var trace = HollowShellRefit.ApplySuccess(session, vehicleId);
        Assert.AreEqual(CompositeDamageStatus.Empty, session.Damage(vehicleId, HollowShellRefit.FirstAdd, 10).Status);
        var damaged = session.Damage(vehicleId, HollowShellRefit.SecondAdd, 25);
        Assert.IsTrue(damaged.Succeeded);
        var part = session.QueryComposite(vehicleId)!.Parts.Single(item => item.Id == trace.P);
        Assert.AreEqual(50, part.CurrentDurability);
        Assert.AreEqual(HollowShellRefit.SecondAdd, part.Cell);
        Assert.AreEqual(HollowShellRefit.MarkerOrientation, part.Orientation);
        Assert.AreEqual(0, session.World.VoxelCount);
        Assert.AreEqual(1, session.QueryComposites().Count);
    }

    [TestMethod]
    public void Fatal_delete_after_refit_without_split_updates_save_local()
    {
        using var world = Open();
        var session = world.Session;
        var vehicleId = session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var trace = HollowShellRefit.ApplySuccess(session, vehicleId);
        var before = session.Vehicles.Single();
        Assert.AreEqual(169, before.Parts.Count);
        Assert.AreEqual(169, before.SaveLocalDefinition!.Voxels.Count);
        var staleRevision = before.SaveLocalRevision;
        Assert.IsFalse(string.IsNullOrEmpty(staleRevision));

        var damaged = session.Damage(vehicleId, HollowShellRefit.Occupied, 100);
        Assert.AreEqual(CompositeDamageStatus.Damaged, damaged.Status);
        Assert.AreEqual(1, session.QueryComposites().Count);
        Assert.AreEqual(vehicleId, session.QueryComposites()[0].Id);
        Assert.AreEqual(168, session.QueryComposites()[0].Parts.Count);
        Assert.IsFalse(session.QueryComposites()[0].Parts.Any(part => part.Cell == HollowShellRefit.Occupied));
        var p = session.QueryComposite(vehicleId)!.Parts.Single(part => part.Id == trace.P);
        Assert.AreEqual(HollowShellRefit.SecondAdd, p.Cell);
        Assert.AreEqual(75, p.CurrentDurability);
        Assert.AreEqual(HollowShellRefit.MarkerOrientation, p.Orientation);
        Assert.AreEqual(0, session.World.VoxelCount);
        AssertSaveLocalMatchesSuccessor(session.Vehicles.Single(), staleRevision!);
        AssertUniquePartIds(session);
    }

    [TestMethod]
    public void Dual_bridge_same_event_after_second_bridge_updates_each_successor_save_local()
    {
        using var world = Open();
        var session = world.Session;
        var vehicleId = session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        Assert.IsNull(session.Vehicles.Single().SaveLocalDefinition);

        var added = session.Refit(
            vehicleId,
            CompositeRefitOperation.Add(BridgeFixture.SecondBridgeCell, HollowShellRefit.MarkerId, VoxelOrientation.Identity));
        Assert.IsTrue(added.Succeeded);
        var refitted = session.Vehicles.Single();
        Assert.AreEqual(56, refitted.Parts.Count);
        Assert.AreEqual(56, refitted.SaveLocalDefinition!.Voxels.Count);
        Assert.AreEqual(2, refitted.SaveLocalDefinition.Contacts.Count);
        var staleRevision = refitted.SaveLocalRevision;
        Assert.IsFalse(string.IsNullOrEmpty(staleRevision));
        Assert.AreEqual(staleRevision, refitted.LastUsedRevision);

        var damaged = session.Damage(new[]
        {
            new CompositeDamageTarget(vehicleId, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(vehicleId, BridgeFixture.SecondBridgeCell, 100)
        });
        Assert.AreEqual(CompositeDamageStatus.Damaged, damaged.Status);
        Assert.AreEqual(2, session.Vehicles.Count);
        Assert.AreEqual(0, session.World.VoxelCount);

        foreach (var successor in session.Vehicles)
        {
            Assert.AreEqual(27, successor.Parts.Count);
            Assert.AreEqual(1, successor.Contacts.Count);
            AssertSaveLocalMatchesSuccessor(successor, staleRevision!);
        }

        Assert.AreNotEqual(session.Vehicles[0].SaveLocalRevision, session.Vehicles[1].SaveLocalRevision);
        Assert.IsFalse(session.Vehicles[0].Parts.Select(part => part.Cell).Intersect(session.Vehicles[1].Parts.Select(part => part.Cell)).Any());
        AssertUniquePartIds(session);
    }

    [TestMethod]
    public void Unrefitted_fracture_does_not_invent_save_local()
    {
        using var world = Open();
        var session = world.Session;
        var vehicleId = session.Manufacture(BridgeFixture.DefinitionId).VehicleId!.Value;
        Assert.IsNull(session.Vehicles.Single().SaveLocalDefinition);
        Assert.IsTrue(session.Damage(vehicleId, BridgeFixture.BridgeCell, 100).Succeeded);
        Assert.AreEqual(2, session.Vehicles.Count);
        foreach (var successor in session.Vehicles)
        {
            Assert.IsNull(successor.SaveLocalDefinition);
            Assert.IsNull(successor.SaveLocalRevision);
            Assert.AreEqual(27, successor.Parts.Count);
        }
    }

    [TestMethod]
    public void Added_and_replaced_definitions_enter_candidate_content_protection()
    {
        using var session = LoggerSession.Create();
        var source = CompositeFixtures.FormalFixturePackage();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var voxelSession = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites);
        var vehicleId = voxelSession.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        HollowShellRefit.ApplySuccess(voxelSession, vehicleId);
        reloader.SetReloadParticipant(VoxelWorldReloadAdapter.For(voxelSession));
        var before = voxelSession.CaptureAuthority();
        var catalogGeneration = reloader.Catalog.Current!.Generation;
        var blueprintRevision = reloader.Composites.Current.Composites[HollowShellFixture.DefinitionId].SemanticRevision;

        var missingMarker = CompositeFixtures.FormalFixturePackage();
        missingMarker.AddVoxelFile("core", "voxels/refit-marker.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("other-marker", "其他", "其他说明", value: 10, tagsJson: "[]")));
        var stripped = ContentFixtures.Create(missingMarker, session.Logger, catalog: reloader.Catalog, composites: reloader.Composites);
        stripped.SetReloadParticipant(VoxelWorldReloadAdapter.For(voxelSession));
        var missing = stripped.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, missing.Status);
        Assert.AreEqual(before, voxelSession.CaptureAuthority());
        Assert.AreEqual(catalogGeneration, reloader.Catalog.Current.Generation);
        Assert.AreEqual(blueprintRevision, reloader.Composites.Current.Composites[HollowShellFixture.DefinitionId].SemanticRevision);

        source.AddCompositeFile(
            "core",
            "composites/dev-hollow-shell.composites.json",
            CompositeFixtures.FixtureFileText().Replace("开发空心壳", "显示名更新", StringComparison.Ordinal));
        var display = ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog, composites: reloader.Composites);
        display.SetReloadParticipant(VoxelWorldReloadAdapter.For(voxelSession));
        var displayReload = display.Reload();
        Assert.IsTrue(displayReload.IsCommitted);
        Assert.AreEqual(vehicleId, voxelSession.QueryComposites()[0].Id);
        Assert.AreEqual(traceP(voxelSession), voxelSession.QueryComposites()[0].Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id);
        Assert.AreEqual(75, voxelSession.QueryComposites()[0].Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).CurrentDurability);

        static EntityId traceP(VoxelDevelopmentSession live) =>
            live.QueryComposites()[0].Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id;
    }

    [TestMethod]
    public void Save_writes_refit_vehicles_without_changing_live_authority()
    {
        var (reloader, logger) = CompositeFixtures.Load(CompositeFixtures.FormalFixturePackage());
        using (logger)
        {
            var store = new RecordingStore();
            var session = new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, store, reloader.Composites);
            var vehicleId = session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
            HollowShellRefit.ApplySuccess(session, vehicleId);
            var before = session.CaptureAuthority();
            Assert.AreEqual(WorldSaveStatus.Saved, session.Save("Should Persist").Status);
            Assert.IsTrue(store.Wrote);
            Assert.AreEqual(before, session.CaptureAuthority());
        }
    }

    [TestMethod]
    public void One_hundred_legal_move_pairs_do_not_drift()
    {
        using var world = Open();
        var session = world.Session;
        var vehicleId = session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var trace = HollowShellRefit.ApplySuccess(session, vehicleId);
        var before = session.CaptureAuthority();
        HollowShellRefit.RunMoveReturnPairs(session, vehicleId, trace.P, 100);
        var after = session.QueryComposite(vehicleId)!;
        Assert.AreEqual(trace.P, after.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id);
        Assert.AreEqual(75, after.Parts.Single(part => part.Id == trace.P).CurrentDurability);
        Assert.AreEqual(HollowShellRefit.MarkerOrientation, after.Parts.Single(part => part.Id == trace.P).Orientation);
        Assert.AreEqual(before.Vehicles[0].Parts.Count, after.Parts.Count);
        Assert.AreEqual(before.WorldVoxelCount, session.World.VoxelCount);
        Assert.AreEqual(before.Vehicles[0].Id, after.Id);
        Assert.AreEqual(before.NextEntityId, session.NextEntityId);
        CollectionAssert.AreEqual(
            before.Vehicles[0].Parts.Select(part => (part.Id, part.Cell, part.DefinitionId, part.Orientation, part.CurrentDurability)).ToArray(),
            after.Parts.Select(part => (part.Id, part.Cell, part.DefinitionId, part.Orientation, part.CurrentDurability)).ToArray());
    }

    [TestMethod]
    public void Recorded_refit_metrics_are_measured_not_invented()
    {
        using var world = Open();
        var session = world.Session;
        var vehicleId = session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var trace = HollowShellRefit.ApplySuccess(session, vehicleId);
        HollowShellRefit.RunMoveReturnPairs(session, vehicleId, trace.P, 100);
        for (var run = 1; run <= 3; run++)
        {
            var memoryBefore = GC.GetTotalMemory(true);
            var clock = Stopwatch.StartNew();
            HollowShellRefit.RunMoveReturnPairs(session, vehicleId, trace.P, 100);
            clock.Stop();
            var memoryAfter = GC.GetTotalMemory(false);
            Console.WriteLine(
                $"REFIT_METRICS run={run} parts={session.QueryComposite(vehicleId)!.Parts.Count} world={session.World.VoxelCount} mutation_ms={clock.Elapsed.TotalMilliseconds:F3} memory_bytes={memoryAfter} memory_delta={memoryAfter - memoryBefore}");
            Assert.AreEqual(trace.P, session.QueryComposite(vehicleId)!.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id);
        }
    }

    [TestMethod]
    public void Unknown_vehicle_and_part_do_not_change_authority()
    {
        using var world = Open();
        var session = world.Session;
        var vehicleId = session.Manufacture(HollowShellFixture.DefinitionId).VehicleId!.Value;
        var before = session.CaptureAuthority();
        Assert.AreEqual(ConstructionStatus.UnknownVehicle, session.Refit(new EntityId(99), CompositeRefitOperation.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId, VoxelOrientation.Identity)).Status);
        Assert.AreEqual(before, session.CaptureAuthority());
        Assert.AreEqual(ConstructionStatus.UnknownPart, session.Refit(vehicleId, CompositeRefitOperation.Move(new EntityId(session.NextEntityId), HollowShellRefit.Moved)).Status);
        Assert.AreEqual(before, session.CaptureAuthority());
        Assert.AreEqual(ConstructionStatus.UnknownVoxel, session.Refit(vehicleId, CompositeRefitOperation.Add(HollowShellRefit.FirstAdd, new VoxelDefinitionId("core", "missing"), VoxelOrientation.Identity)).Status);
        Assert.AreEqual(before, session.CaptureAuthority());
    }

    private static void AssertSaveLocalMatchesSuccessor(CompositeInstance vehicle, string staleRevision)
    {
        var saved = vehicle.SaveLocalDefinition;
        Assert.IsNotNull(saved);
        Assert.AreEqual(vehicle.Parts.Count, saved.Voxels.Count);
        Assert.AreEqual(vehicle.Contacts.Count, saved.Contacts.Count);
        var actualCells = vehicle.Parts.Select(part => part.Cell).ToHashSet();
        Assert.AreEqual(0, saved.Voxels.Count(spec => !actualCells.Contains(spec.Cell)));
        foreach (var part in vehicle.Parts)
        {
            var spec = saved.Voxels.Single(item => item.Cell == part.Cell);
            Assert.AreEqual(part.DefinitionId, spec.DefinitionId);
            Assert.AreEqual(part.Orientation, spec.Orientation);
        }

        foreach (var contact in vehicle.Contacts)
        {
            Assert.IsTrue(saved.Contacts.Any(item =>
                item.BoundCell == contact.BoundCell &&
                item.X == contact.X &&
                item.Y == contact.Y &&
                item.Z == contact.Z));
        }

        Assert.AreEqual(vehicle.SaveLocalRevision, vehicle.LastUsedRevision);
        Assert.AreNotEqual(staleRevision, vehicle.SaveLocalRevision);
        Assert.AreNotEqual(staleRevision, vehicle.LastUsedRevision);
    }

    private static void AssertUniquePartIds(VoxelDevelopmentSession session)
    {
        var ids = session.Vehicles.SelectMany(vehicle => vehicle.Parts).Select(part => part.Id.Value).ToArray();
        Assert.AreEqual(ids.Length, ids.Distinct().Count());
    }

    private static SessionHandle Open()
    {
        var (reloader, logger) = CompositeFixtures.Load(CompositeFixtures.FormalFixturePackage());
        return new SessionHandle(
            new VoxelDevelopmentSession(reloader.Catalog, VoxelWorldIdentity.Slice3, composites: reloader.Composites),
            reloader,
            logger);
    }

    private sealed class SessionHandle : IDisposable
    {
        public VoxelDevelopmentSession Session { get; }
        public ContentReloader Reloader { get; }
        private readonly IDisposable _logger;

        public SessionHandle(VoxelDevelopmentSession session, ContentReloader reloader, IDisposable logger)
        {
            Session = session;
            Reloader = reloader;
            _logger = logger;
        }

        public void Dispose()
        {
            Session.Detach();
            _logger.Dispose();
        }
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
