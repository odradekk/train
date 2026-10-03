using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Voxels;
using Train.Domain.WorldEditor;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:world-edit")]
public sealed class VoxelCellSetTests
{
    private static readonly VoxelDefinitionId Ore = new("core", "ferrous-ore");
    private static readonly VoxelCellContent OreIdentity = new(Ore, VoxelOrientation.Identity);

    [TestMethod]
    public void Add_remove_and_replace_apply_as_one_formal_edit()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var session = harness.Session;
        var first = new WorldVoxelCoord(4, 4, 4);
        var second = new WorldVoxelCoord(5, 4, 4);
        Assert.AreEqual("Applied", session.SubmitEdit(Set(Add(first))).Status);
        Assert.AreEqual("Applied", session.SubmitEdit(Set(
            Replace(first, new VoxelCellContent(Ore, new VoxelOrientation(VoxelAxis.PositiveZ, VoxelAxis.PositiveY))),
            Add(second))).Status);
        Assert.AreEqual(VoxelAxis.PositiveZ, session.Query(first).Voxel!.Orientation.Right);
        Assert.IsTrue(session.Query(second).IsOccupied);
        Assert.AreEqual("Applied", session.SubmitEdit(Set(Remove(first))).Status);
        Assert.IsFalse(session.Query(first).IsOccupied);
    }

    [TestMethod]
    public void Rejected_submissions_report_specific_conflicts_and_never_partially_write()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var session = harness.Session;
        var occupied = new WorldVoxelCoord(4, 15, 4);
        var first = new WorldVoxelCoord(5, 15, 4);
        var untouched = new WorldVoxelCoord(6, 15, 4);
        Assert.AreEqual("Applied", session.SubmitEdit(Set(Add(occupied))).Status);

        var cases = new[]
        {
            (Set(Add(first), Add(occupied)), VoxelCellSetStatus.Occupied),
            (Set(Add(first), Remove(untouched)), VoxelCellSetStatus.Empty),
            (Set(Add(first), Add(new WorldVoxelCoord(-1, 0, 0))), VoxelCellSetStatus.OutOfBounds),
            (Set(Add(first), new VoxelCellTarget(untouched, VoxelCellOperation.Add,
                new VoxelCellContent(new VoxelDefinitionId("core", "missing"), VoxelOrientation.Identity))), VoxelCellSetStatus.UnknownDefinition),
            (Set(Add(first), new VoxelCellTarget(untouched, VoxelCellOperation.Add, null)), VoxelCellSetStatus.InvalidTarget),
            (Set(Add(first), new VoxelCellTarget(untouched, VoxelCellOperation.Remove, OreIdentity)), VoxelCellSetStatus.InvalidTarget),
            (Set(Add(first), Add(first)), VoxelCellSetStatus.DuplicateTarget)
        };
        foreach (var (edit, expected) in cases)
            AssertRejectedUnchanged(session, edit, expected);
        AssertRejectedUnchanged(session, Set(), VoxelCellSetStatus.NoTargets);
    }

    [TestMethod]
    public void Disallowed_orientation_is_rejected_before_any_target_is_written()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var target = new WorldVoxelCoord(10, 10, 10);
        var before = harness.Session.World.VoxelCount;
        var locked = new VoxelCellContent(new VoxelDefinitionId("core", "fixed"),
            new VoxelOrientation(VoxelAxis.PositiveZ, VoxelAxis.PositiveY));
        AssertRejectedUnchanged(harness.Session, Set(Add(new WorldVoxelCoord(9, 10, 10)),
            new VoxelCellTarget(target, VoxelCellOperation.Add, locked)),
            VoxelCellSetStatus.DisallowedOrientation);
        Assert.AreEqual(before, harness.Session.World.VoxelCount);
    }

    [TestMethod]
    public void Single_cell_applicability_precedes_definition_and_orientation_validation()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var session = harness.Session;
        var occupied = new WorldVoxelCoord(8, 10, 8);
        var empty = new WorldVoxelCoord(9, 10, 8);
        Assert.AreEqual("Applied", session.SubmitEdit(Set(Add(occupied))).Status);
        var unknown = new VoxelCellContent(new VoxelDefinitionId("core", "missing"), VoxelOrientation.Identity);
        var disallowed = new VoxelCellContent(new VoxelDefinitionId("core", "fixed"), VoxelOrientation.Yaw90);

        foreach (var content in new[] { unknown, disallowed })
        {
            AssertRejectedUnchanged(session,
                Set(new VoxelCellTarget(occupied, VoxelCellOperation.Add, content)), VoxelCellSetStatus.Occupied);
            AssertRejectedUnchanged(session,
                Set(new VoxelCellTarget(empty, VoxelCellOperation.Replace, content)), VoxelCellSetStatus.Empty);
        }
    }

    [TestMethod]
    public void Rail_and_vehicle_ownership_are_reported_with_stable_ids()
    {
        using var rail = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var railCell = StraightRailFixture.FirstSegmentDamageCell;
        foreach (var edit in new[] { Set(Add(railCell)), Set(Remove(railCell)), Set(Replace(railCell, OreIdentity)) })
        {
            var railResult = AssertRejectedUnchanged(rail.Session,
                Set(Add(new(5, 15, 4)), edit.Targets[0]), VoxelCellSetStatus.RailOccupied);
            Assert.AreEqual(VoxelCellOwnerKind.RailSegment, railResult.OwnerKind);
            Assert.AreEqual(rail.Session.Rails.Segments[0].Id, railResult.OwnerId);
        }

        using var vehicle = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var manufactured = vehicle.Session.Manufacture(HollowShellFixture.DefinitionId, CompositeTransform.Translation(20, 8, 20));
        var vehicleId = manufactured.VehicleId!.Value;
        var part = vehicle.Session.QueryComposite(vehicleId)!.Parts.First();
        var cell = new WorldVoxelCoord(20 + part.Cell.X, 8 + part.Cell.Y, 20 + part.Cell.Z);
        foreach (var edit in new[] { Set(Add(cell)), Set(Remove(cell)), Set(Replace(cell, OreIdentity)) })
        {
            var vehicleResult = AssertRejectedUnchanged(vehicle.Session,
                Set(Add(new(5, 15, 4)), edit.Targets[0]), VoxelCellSetStatus.VehicleOccupied);
            Assert.AreEqual(vehicleId, vehicleResult.OwnerId);
            Assert.AreEqual(part.Id, vehicleResult.PartId);
        }
    }

    [TestMethod]
    public void Live_rail_definition_is_protected_even_without_a_registered_segment()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var session = harness.Session;
        var cell = new WorldVoxelCoord(5, 15, 5);
        Assert.IsTrue(session.Add(cell, StraightRailFixture.RailDefinitionId, VoxelOrientation.Identity).Succeeded);
        Assert.IsFalse(session.Rails.TryGetOccupancy(cell, out _));
        Assert.AreEqual(ObservedObject.WorldCell(cell), WorldEditorInspector.WorldTarget(session, cell));
        foreach (var target in new[] { Add(cell), Remove(cell), Replace(cell, OreIdentity) })
        {
            var result = AssertRejectedUnchanged(session, Set(Add(new(6, 15, 5)), target), VoxelCellSetStatus.RailOccupied);
            Assert.AreEqual(VoxelCellOwnerKind.None, result.OwnerKind);
            Assert.AreEqual(default(EntityId), result.OwnerId);
        }
    }

    [TestMethod]
    public void Vehicle_actual_occupancy_uses_positive_volume_for_rotated_part_and_excludes_touching_cell()
    {
        using var rotatedWorld = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        Assert.IsTrue(CompositeTransform.TryCreate(10, 10, 10,
            Math.Sqrt(0.5), 0, Math.Sqrt(0.5), 0, 1, 0, out var rotated));
        Assert.IsTrue(rotatedWorld.Session.Manufacture(HollowShellFixture.DefinitionId, rotated).Succeeded);
        Assert.AreEqual(VoxelCellSetStatus.VehicleOccupied,
            rotatedWorld.Session.ValidateCells(Set(Add(new(9, 10, 10)))).Status);
        Assert.AreEqual(VoxelCellSetStatus.VehicleOccupied,
            rotatedWorld.Session.ValidateCells(Set(Add(new(10, 10, 10)))).Status);
        using var touchingWorld = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        Assert.IsTrue(touchingWorld.Session.Manufacture(HollowShellFixture.DefinitionId,
            CompositeTransform.Translation(10, 10, 10)).Succeeded);
        Assert.AreEqual(VoxelCellSetStatus.Valid,
            touchingWorld.Session.ValidateCells(Set(Add(new(9, 10, 10)))).Status);
    }

    [TestMethod]
    public void Replace_rebuilds_collision_without_destroying_rail_state()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var session = harness.Session;
        var cell = new WorldVoxelCoord(4, 4, 4);
        Assert.AreEqual("Applied", session.SubmitEdit(Set(Add(cell))).Status);
        session.TakePendingCollisionDirtyChunks();

        Assert.AreEqual("Applied", session.SubmitEdit(Set(Replace(cell,
            new VoxelCellContent(Ore, VoxelOrientation.Yaw90)))).Status);

        CollectionAssert.Contains(session.TakePendingCollisionDirtyChunks().ToArray(),
            VoxelChunks.Of(cell, session.World.ChunkSize));
        Assert.IsFalse(session.Rails.IsDestroyed(cell));
        Assert.AreEqual(VoxelOrientation.Yaw90, session.Query(cell).Voxel!.Orientation);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, session.QueryCollision(cell).Status);
    }

    [TestMethod]
    public void Replace_rejects_no_change_and_other_replacements_create_a_fresh_full_durability_voxel()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var session = harness.Session;
        var manufactured = session.Manufacture(BridgeFixture.DefinitionId);
        var vehicleId = manufactured.VehicleId!.Value;
        var droppedPart = manufactured.Parts!.Single(part => part.Cell.Equals(new LocalVoxelCoord(0, 0, 0)));
        Assert.IsTrue(session.Damage(new[]
        {
            new CompositeDamageTarget(vehicleId, BridgeFixture.BridgeCell, 100),
            new CompositeDamageTarget(vehicleId, BridgeFixture.LeftCoreCell, 100)
        }).Succeeded);
        var cell = new WorldVoxelCoord(8, 8, 8);
        var untouched = new WorldVoxelCoord(4, 4, 4);
        Assert.AreEqual(droppedPart.Id, session.Query(cell).Voxel!.RuntimeId);
        Assert.AreEqual(WorldCommandStatus.Damaged, session.Damage(cell, 25).Status);
        var damaged = session.Query(cell).Voxel!;
        Assert.AreEqual(75, damaged.CurrentDurability);

        AssertRejectedUnchanged(session, Set(Add(untouched), Replace(cell,
            new VoxelCellContent(damaged.DefinitionId, damaged.Orientation))), VoxelCellSetStatus.NoChange);

        Assert.AreEqual("Applied", session.SubmitEdit(Set(Replace(cell, OreIdentity))).Status);
        var replaced = session.Query(cell);
        Assert.AreEqual(replaced.MaxDurability, replaced.Voxel!.CurrentDurability);
        Assert.IsNull(replaced.Voxel.RuntimeId);
    }

    [TestMethod]
    public void Pending_cell_sets_restore_in_input_order_and_execute_on_the_running_queue()
    {
        using var writer = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var cell = new WorldVoxelCoord(40, 4, 20);
        var edits = new[]
        {
            Set(Add(cell)),
            Set(Replace(cell, new VoxelCellContent(Ore, new VoxelOrientation(VoxelAxis.PositiveZ, VoxelAxis.PositiveY)))),
            Set(Remove(cell))
        };
        var submissions = edits.Select(edit => writer.Session.SubmitEdit(edit, 3)).ToArray();
        CollectionAssert.AreEqual(new[] { "Pending", "Pending", "Pending" }, submissions.Select(item => item.Status).ToArray());
        CollectionAssert.AreEqual(new ulong[] { 1, 2, 3 }, submissions.Select(item => item.Record!.Id).ToArray());
        CollectionAssert.AreEqual(new[] { new SimulationPosition(0, 0), new SimulationPosition(0, 1), new SimulationPosition(0, 2) },
            submissions.Select(item => item.Record!.AcceptedAt).ToArray());
        var saved = writer.Session.Save("Cell set pending");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);

        using var restarted = WorldEditorHarness.Content(userData: writer.UserData);
        var reader = new VoxelDevelopmentSession(restarted.Catalog, VoxelWorldIdentity.Slice4, restarted.Store, restarted.Composites);
        restarted.Attach(reader);
        Assert.AreEqual(WorldSaveStatus.Loaded, reader.Load(saved.SaveId!.Value).Status);
        var restored = reader.QueryPendingEdits();
        CollectionAssert.AreEqual(new ulong[] { 1, 2, 3 }, restored.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(edits.Cast<WorldEdit>().ToArray(), restored.Select(item => item.Edit).ToArray());
        Assert.IsTrue(reader.ResumeSimulation().Succeeded);
        Assert.IsTrue(reader.AdvanceSimulation(3).Succeeded);
        var log = reader.QueryEditLog();
        CollectionAssert.AreEqual(new ulong[] { 1, 2, 3 }, log.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(new SimulationPosition?[] { new(3, 0), new(3, 1), new(3, 2) }, log.Select(item => item.ExecutedAt).ToArray());
        CollectionAssert.AreEqual(new[] { "Applied", "Applied", "Applied" }, log.Select(item => item.Status).ToArray());
        Assert.IsFalse(reader.Query(cell).IsOccupied);
    }

    private static VoxelCellSetEdit Set(params VoxelCellTarget[] targets) => new(targets);
    private static VoxelCellTarget Add(WorldVoxelCoord position) =>
        new(position, VoxelCellOperation.Add, OreIdentity);
    private static VoxelCellTarget Remove(WorldVoxelCoord position) =>
        new(position, VoxelCellOperation.Remove, null);
    private static VoxelCellTarget Replace(WorldVoxelCoord position, VoxelCellContent content) =>
        new(position, VoxelCellOperation.Replace, content);

    private static VoxelCellSetResult AssertRejectedUnchanged(VoxelDevelopmentSession session, VoxelCellSetEdit edit, VoxelCellSetStatus expected)
    {
        var voxels = session.World.CaptureVoxels();
        var nextEntityId = session.NextEntityId;
        var transactions = session.TransactionCount;
        var submission = session.SubmitEdit(edit);
        Assert.AreEqual(WorldEditState.Rejected, submission.Record!.State);
        Assert.AreEqual(expected.ToString(), submission.Status);
        Assert.AreEqual(expected, ((VoxelCellSetResult)submission.Result!).Status);
        CollectionAssert.AreEqual(voxels, session.World.CaptureVoxels());
        Assert.AreEqual(nextEntityId, session.NextEntityId);
        Assert.AreEqual(transactions, session.TransactionCount);
        return (VoxelCellSetResult)submission.Result!;
    }

}
