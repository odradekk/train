using Train.Core.Content.Voxels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Voxels;
using Train.Domain.WorldEditor;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:world-edit")]
[TestCategory("scope:world-editor")]
public sealed class VoxelRegionTests
{
    private static readonly VoxelDefinitionId Ore = WorldEditorSequences.EditVoxelId;
    private static readonly VoxelCellContent OreIdentity = new(Ore, VoxelOrientation.Identity);

    [TestMethod]
    public void Boxes_normalize_reverse_corners_height_and_report_world_boundary_conflicts()
    {
        var forward = VoxelCellRegion.Box(new(1, 2, 3), new(3, 2, 4), 2);
        var reverse = VoxelCellRegion.Box(new(3, 2, 4), new(1, 2, 3), 2);
        Assert.AreEqual(12L, forward.Count);
        Assert.AreEqual(new WorldVoxelCoord(1, 2, 3), reverse.Min);
        Assert.AreEqual(new WorldVoxelCoord(3, 3, 4), reverse.Max);
        CollectionAssert.AreEqual(forward.Cells.ToArray(), reverse.Cells.ToArray());

        using var harness = Blank();
        var preview = harness.Session.PreviewRegion(
            VoxelCellRegion.Box(new(14, 7, 14), new(16, 7, 15), 2), VoxelCellOperation.Add, OreIdentity);
        Assert.AreEqual(12L, preview.Region.Count);
        Assert.AreEqual(4, preview.AffectedCount);
        Assert.AreEqual(0, preview.SkippedCount);
        Assert.AreEqual(VoxelCellSetStatus.OutOfBounds, preview.Validation.Status);
        Assert.AreEqual(8, preview.Validation.Conflicts.Count);
    }

    [TestMethod]
    public void Region_operations_filter_only_inapplicable_cells_and_still_reject_bad_content()
    {
        using var harness = Blank();
        var session = harness.Session;
        var range = VoxelCellRegion.Box(new(1, 1, 1), new(2, 1, 2), 1);
        Assert.AreEqual("Applied", session.SubmitRegion(range, VoxelCellOperation.Add, OreIdentity).Status);

        var add = session.PreviewRegion(range, VoxelCellOperation.Add, OreIdentity);
        AssertPreview(add, affected: 0, skipped: 4, VoxelCellSetStatus.NoTargets);
        var next = session.NextEditId;
        var noTargets = session.SubmitRegion(range, VoxelCellOperation.Add, OreIdentity);
        Assert.AreEqual(WorldEditRefusal.RegionRejected, noTargets.Refusal);
        Assert.AreEqual(VoxelCellSetStatus.NoTargets, noTargets.RegionPreview!.Validation.Status);
        Assert.AreEqual(next, session.NextEditId);
        var same = session.PreviewRegion(range, VoxelCellOperation.Replace, OreIdentity);
        AssertPreview(same, affected: 0, skipped: 4, VoxelCellSetStatus.NoTargets);

        var yaw = new VoxelCellContent(Ore, VoxelOrientation.Yaw90);
        var replace = session.PreviewRegion(range, VoxelCellOperation.Replace, yaw);
        AssertPreview(replace, affected: 4, skipped: 0, VoxelCellSetStatus.Valid);
        Assert.AreEqual("Applied", session.SubmitRegion(range, VoxelCellOperation.Replace, yaw).Status);

        var mixedRemove = VoxelCellRegion.Box(new(1, 1, 1), new(3, 1, 2), 1);
        var remove = session.PreviewRegion(mixedRemove, VoxelCellOperation.Remove, null);
        AssertPreview(remove, affected: 4, skipped: 2, VoxelCellSetStatus.Valid);
        Assert.AreEqual("Applied", session.SubmitRegion(mixedRemove, VoxelCellOperation.Remove, null).Status);
        Assert.AreEqual(0, session.World.VoxelCount);

        var unknown = new VoxelCellContent(new VoxelDefinitionId("core", "missing"), VoxelOrientation.Identity);
        Assert.AreEqual(VoxelCellSetStatus.UnknownDefinition,
            session.PreviewRegion(range, VoxelCellOperation.Replace, unknown).Validation.Status);
        var illegal = new VoxelCellContent(new VoxelDefinitionId("core", "fixed"), VoxelOrientation.Yaw90);
        Assert.AreEqual(VoxelCellSetStatus.DisallowedOrientation,
            session.PreviewRegion(range, VoxelCellOperation.Replace, illegal).Validation.Status);
    }

    [TestMethod]
    public void Oversized_and_invalid_ranges_are_refused_before_allocating_an_input()
    {
        using var harness = Blank(32, 16, 32);
        var range = VoxelCellRegion.Box(new(0, 0, 0), new(31, 0, 31), 5);
        Assert.AreEqual(5120L, range.Count);
        Assert.AreEqual(VoxelCellSetStatus.RegionTooLarge, range.Status);
        Assert.AreEqual(WorldEditRefusal.RegionRejected,
            harness.Session.SubmitRegion(range, VoxelCellOperation.Add, OreIdentity, confirmLargeEdit: true).Refusal);
        Assert.AreEqual(VoxelCellSetStatus.InvalidRegion,
            VoxelCellRegion.Box(new(0, int.MaxValue, 0), new(0, 0, 0), 2).Status);
        Assert.AreEqual(VoxelCellSetStatus.InvalidRegion, VoxelCellRegion.Box(default, default, 0).Status);
        Assert.AreEqual(1UL, harness.Session.NextEditId);
        Assert.AreEqual(0, harness.Session.World.VoxelCount);
    }

    [TestMethod]
    public void Draft_preview_and_commit_revalidate_the_whole_covered_range_after_world_changes()
    {
        using var harness = Blank();
        using var tool = new WorldEditorVoxelTool(harness.Session);
        tool.SetMode(WorldEditorVoxelMode.Box);
        tool.SetBoxExact(new(2, 1, 2), new(3, 1, 3), 1, Ore, VoxelOrientation.Identity, VoxelCellOperation.Add);
        Assert.AreEqual(4, tool.RegionPreview.AffectedCount);
        harness.Session.Add(new(2, 1, 2), StraightRailFixture.RailDefinitionId, VoxelOrientation.Identity);
        Assert.AreEqual(3, tool.RegionPreview.AffectedCount);
        Assert.AreEqual(VoxelCellSetStatus.RailOccupied, tool.Preview.Status);
        var before = WorldSaveAuthority.Fingerprint(harness.Session);
        Assert.AreEqual(WorldEditRefusal.RegionRejected, tool.Commit().Refusal);
        Assert.AreEqual(before, WorldSaveAuthority.Fingerprint(harness.Session));
        Assert.IsTrue(tool.HasDraft);
    }

    [TestMethod]
    public void Draft_hover_reuses_the_same_preview_until_world_or_catalog_state_changes()
    {
        using var harness = Blank();
        using var tool = new WorldEditorVoxelTool(harness.Session);
        tool.SetActive(true);
        tool.SetMode(WorldEditorVoxelMode.Box);
        tool.SetBoxExact(new(2, 1, 2), new(3, 1, 3), 1, Ore, VoxelOrientation.Identity, VoxelCellOperation.Add);
        var first = tool.RegionPreview;

        tool.Hover(new(12, 1, 12));
        Assert.AreSame(first, tool.RegionPreview);
        Assert.AreEqual(new WorldVoxelCoord(2, 1, 2), tool.Position);
        Assert.AreEqual(new WorldVoxelCoord(3, 1, 3), tool.BoxCorner);

        Assert.IsTrue(harness.Session.Add(new(10, 1, 10), Ore, VoxelOrientation.Identity).Succeeded);
        var afterWorldChange = tool.RegionPreview;
        Assert.AreNotSame(first, afterWorldChange);
        Assert.AreSame(afterWorldChange, tool.RegionPreview);

        harness.Source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", """
            {"schema_version":1,"voxels":[{"allowed_orientations":"all_24","behaviors":[],"collision":{"kind":"full_box"},"description":"说明","id":"ferrous-ore","mass":1,"max_durability":100,"name":"Ferrous Ore Reloaded","presentation":{"icon":"iron.png","kind":"cube","voxel_bottom":"iron.png","voxel_side":"iron.png","voxel_top":"iron.png"},"tags":["raw_good"],"value":100}]}
            """);
        Assert.AreEqual(ContentReloadStatus.Committed, harness.Reloader.Reload().Status);
        Assert.AreNotSame(afterWorldChange, tool.RegionPreview);
    }

    [TestMethod]
    public void Every_rail_or_vehicle_conflict_rejects_the_whole_region_without_changing_authority()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var session = harness.Session;
        var vehicle = session.Manufacture(HollowShellFixture.DefinitionId, CompositeTransform.Translation(200, 8, 20));
        var part = session.QueryComposite(vehicle.VehicleId!.Value)!.Parts.First();
        var vehicleCell = new WorldVoxelCoord(200 + part.Cell.X, 8 + part.Cell.Y, 20 + part.Cell.Z);
        var railCell = StraightRailFixture.FirstSegmentDamageCell;
        foreach (var cell in new[] { railCell, vehicleCell })
        foreach (var operation in Enum.GetValues<VoxelCellOperation>())
        {
            var before = WorldSaveAuthority.Fingerprint(session);
            var next = session.NextEditId;
            var content = operation == VoxelCellOperation.Remove ? null : OreIdentity;
            var result = session.SubmitRegion(VoxelCellRegion.Box(cell, cell, 1), operation, content);
            Assert.AreEqual(WorldEditRefusal.RegionRejected, result.Refusal);
            Assert.AreEqual(next, session.NextEditId);
            Assert.AreEqual(before, WorldSaveAuthority.Fingerprint(session));
            Assert.AreEqual(cell == railCell ? VoxelCellSetStatus.RailOccupied : VoxelCellSetStatus.VehicleOccupied,
                result.RegionPreview!.Validation.Status);
        }
        var otherPart = session.QueryComposite(vehicle.VehicleId.Value)!.Parts.Skip(1).First();
        var otherVehicleCell = new WorldVoxelCoord(200 + otherPart.Cell.X, 8 + otherPart.Cell.Y, 20 + otherPart.Cell.Z);
        var covered = new[] { railCell, StraightRailFixture.LeftRailSample, vehicleCell, otherVehicleCell, new WorldVoxelCoord(0, 8, 0) };
        var preview = session.PreviewRegion(VoxelCellRegion.FromCells(covered), VoxelCellOperation.Add, OreIdentity);
        Assert.AreEqual(4, preview.Validation.Conflicts.Count);
        CollectionAssert.AreEquivalent(covered.Take(4).ToArray(), preview.Validation.Conflicts.Select(conflict => conflict.Position).ToArray());
        Assert.AreEqual(1, preview.AffectedCount);
        var beforeMixed = WorldSaveAuthority.Fingerprint(session);
        Assert.AreEqual(WorldEditRefusal.RegionRejected,
            session.SubmitRegion(preview.Region, VoxelCellOperation.Add, OreIdentity).Refusal);
        Assert.AreEqual(beforeMixed, WorldSaveAuthority.Fingerprint(session));
    }

    [TestMethod]
    public void Large_confirmation_threshold_does_not_consume_an_input_before_confirmation()
    {
        using var harness = Blank(32, 8, 32);
        var session = harness.Session;
        foreach (var count in new[] { VoxelRegionLimits.ConfirmationThreshold, VoxelRegionLimits.ConfirmationThreshold + 1 })
        {
            session.Reset();
            var range = count == VoxelRegionLimits.ConfirmationThreshold
                ? VoxelCellRegion.Box(new(0, 0, 0), new(31, 0, 15), 1)
                : VoxelCellRegion.Box(new(0, 0, 0), new(26, 0, 18), 1);
            var preview = session.PreviewRegion(range, VoxelCellOperation.Add, OreIdentity);
            Assert.AreEqual(count > VoxelRegionLimits.ConfirmationThreshold, preview.RequiresConfirmation);
            var next = session.NextEditId;
            var first = session.SubmitRegion(range, VoxelCellOperation.Add, OreIdentity);
            if (preview.RequiresConfirmation)
            {
                Assert.AreEqual(WorldEditRefusal.LargeEditConfirmationRequired, first.Refusal);
                Assert.AreEqual(count, first.RegionPreview!.AffectedCount);
                Assert.AreEqual(next, session.NextEditId);
                Assert.AreEqual(0, session.World.VoxelCount);
                first = session.SubmitRegion(range, VoxelCellOperation.Add, OreIdentity, confirmLargeEdit: true);
            }
            Assert.AreEqual(WorldEditState.Executed, first.Record!.State);
            Assert.AreEqual(count, session.World.VoxelCount);
        }
    }

    [TestMethod]
    public void Brush_interpolates_and_deduplicates_without_submitting_until_end_then_undoes_as_one_record()
    {
        using var harness = Blank();
        using var tool = new WorldEditorVoxelTool(harness.Session);
        Assert.IsTrue(tool.SetActive(true));
        Assert.IsTrue(tool.SetMode(WorldEditorVoxelMode.Brush));
        Assert.IsTrue(tool.SetBrushSize(2));
        tool.SetSettings(Ore, VoxelOrientation.Identity, VoxelCellOperation.Add);
        Assert.IsTrue(tool.BeginStroke(new(2, 1, 2)));
        tool.ExtendStroke(new(5, 1, 2));
        tool.ExtendStroke(new(5, 1, 2));
        Assert.AreEqual(0, harness.Session.World.VoxelCount);
        Assert.AreEqual(1UL, harness.Session.NextEditId);
        Assert.AreEqual(10, tool.RegionPreview.AffectedCount);

        var submitted = tool.EndStroke()!;
        Assert.AreEqual(1UL, submitted.Record!.Id);
        Assert.AreEqual(10, harness.Session.World.VoxelCount);
        Assert.AreEqual(1, harness.Session.UndoCount);
        Assert.AreEqual("Applied", harness.Session.Undo().Status);
        Assert.AreEqual(0, harness.Session.World.VoxelCount);
        Assert.AreEqual("Applied", harness.Session.Redo().Status);
        Assert.AreEqual(10, harness.Session.World.VoxelCount);
    }

    [TestMethod]
    public void Exact_brush_edit_translates_the_whole_shape_and_settings_preserve_geometry_through_draft_history()
    {
        using var harness = Blank();
        using var tool = new WorldEditorVoxelTool(harness.Session);
        tool.SetActive(true);
        tool.SetMode(WorldEditorVoxelMode.Brush);
        tool.SetSettings(Ore, VoxelOrientation.Identity, VoxelCellOperation.Add);
        Assert.IsTrue(tool.BeginStroke(new(2, 1, 2)));
        tool.ExtendStroke(new(4, 1, 2));
        tool.InterruptDraftInput();
        var original = tool.CurrentEdit.Targets.Select(target => target.Position).ToArray();
        CollectionAssert.AreEqual(
            new[] { new WorldVoxelCoord(2, 1, 2), new WorldVoxelCoord(3, 1, 2), new WorldVoxelCoord(4, 1, 2) }, original);

        tool.SetExact(new(8, 2, 8), Ore, VoxelOrientation.Identity, VoxelCellOperation.Add);
        var translated = tool.CurrentEdit.Targets.Select(target => target.Position).ToArray();
        CollectionAssert.AreEqual(
            new[] { new WorldVoxelCoord(6, 2, 8), new WorldVoxelCoord(7, 2, 8), new WorldVoxelCoord(8, 2, 8) }, translated);
        Assert.AreEqual(3, tool.RegionPreview.AffectedCount);

        tool.SetSettings(Ore, VoxelOrientation.Yaw90, VoxelCellOperation.Add);
        CollectionAssert.AreEqual(translated, tool.CurrentEdit.Targets.Select(target => target.Position).ToArray());
        Assert.IsTrue(tool.CurrentEdit.Targets.All(target =>
            target.Operation == VoxelCellOperation.Add && target.Content!.Orientation == VoxelOrientation.Yaw90));
        Assert.IsTrue(tool.UndoDraft());
        CollectionAssert.AreEqual(translated, tool.CurrentEdit.Targets.Select(target => target.Position).ToArray());
        Assert.IsTrue(tool.CurrentEdit.Targets.All(target => target.Operation == VoxelCellOperation.Add));
        Assert.IsTrue(tool.UndoDraft());
        CollectionAssert.AreEqual(original, tool.CurrentEdit.Targets.Select(target => target.Position).ToArray());
        Assert.IsTrue(tool.RedoDraft());
        CollectionAssert.AreEqual(translated, tool.CurrentEdit.Targets.Select(target => target.Position).ToArray());
        Assert.IsTrue(tool.RedoDraft());
        CollectionAssert.AreEqual(translated, tool.CurrentEdit.Targets.Select(target => target.Position).ToArray());
        Assert.IsTrue(tool.CurrentEdit.Targets.All(target =>
            target.Operation == VoxelCellOperation.Add && target.Content!.Orientation == VoxelOrientation.Yaw90));
    }

    [TestMethod]
    public void Far_brush_jump_reports_region_too_large_without_input_or_world_change_and_undo_restores_valid_draft()
    {
        using var harness = Blank();
        using var tool = new WorldEditorVoxelTool(harness.Session);
        tool.SetActive(true);
        tool.SetMode(WorldEditorVoxelMode.Brush);
        tool.SetSettings(Ore, VoxelOrientation.Identity, VoxelCellOperation.Add);
        Assert.IsTrue(tool.BeginStroke(new(1, 1, 1)));
        tool.ExtendStroke(new(10_000, 1, 1));
        tool.InterruptDraftInput();
        Assert.AreEqual(VoxelRegionLimits.MaxCells + 1L, tool.RegionPreview.Region.Count);
        Assert.AreEqual(VoxelCellSetStatus.RegionTooLarge, tool.RegionPreview.Validation.Status);
        var nextEdit = harness.Session.NextEditId;
        var before = WorldSaveAuthority.Fingerprint(harness.Session);

        var refused = tool.Commit(confirmLargeEdit: true);
        Assert.AreEqual(WorldEditRefusal.RegionRejected, refused.Refusal);
        Assert.AreEqual(VoxelCellSetStatus.RegionTooLarge, refused.RegionPreview!.Validation.Status);
        Assert.AreEqual(nextEdit, harness.Session.NextEditId);
        Assert.AreEqual(before, WorldSaveAuthority.Fingerprint(harness.Session));
        Assert.IsTrue(tool.HasDraft);

        Assert.IsTrue(tool.UndoDraft());
        Assert.AreEqual(VoxelCellSetStatus.Valid, tool.RegionPreview.Validation.Status);
        Assert.AreEqual(1, tool.RegionPreview.AffectedCount);
        Assert.IsFalse(tool.UndoDraft());
    }

    [TestMethod]
    public void Interrupted_stroke_remains_a_protected_draft_with_draft_undo()
    {
        using var harness = Blank();
        using var tool = new WorldEditorVoxelTool(harness.Session);
        tool.SetActive(true);
        tool.SetMode(WorldEditorVoxelMode.Brush);
        tool.SetSettings(Ore, VoxelOrientation.Identity, VoxelCellOperation.Add);
        Assert.IsTrue(tool.BeginStroke(new(2, 1, 2)));
        tool.ExtendStroke(new(4, 1, 2));
        tool.InterruptDraftInput();

        Assert.IsTrue(tool.HasDraft);
        Assert.IsFalse(tool.StrokeInProgress);
        Assert.AreEqual(0, harness.Session.World.VoxelCount);
        Assert.IsTrue(tool.CanUndoDraft);
        Assert.IsTrue(tool.UndoDraft());
        Assert.AreEqual(1, tool.RegionPreview.AffectedCount);
        Assert.AreEqual(WorldChangeStatus.ChoiceRequired,
            harness.Session.BeginWorldChange(new(WorldChangeKind.Quit, null)).Status);
    }

    [TestMethod]
    public void Box_submits_and_undoes_as_one_record()
    {
        using var harness = Blank();
        using var tool = new WorldEditorVoxelTool(harness.Session);
        tool.SetActive(true);
        tool.SetMode(WorldEditorVoxelMode.Box);
        tool.SetBoxExact(new(2, 1, 3), new(4, 1, 4), 2, Ore, VoxelOrientation.Identity, VoxelCellOperation.Add);
        Assert.AreEqual(12, tool.RegionPreview.AffectedCount);
        var result = tool.Commit();
        Assert.AreEqual(1UL, result.Record!.Id);
        Assert.AreEqual(12, harness.Session.World.VoxelCount);
        Assert.AreEqual(1, harness.Session.UndoCount);
        harness.Session.Undo();
        Assert.AreEqual(0, harness.Session.World.VoxelCount);
        harness.Session.Redo();
        Assert.AreEqual(12, harness.Session.World.VoxelCount);
    }

    [TestMethod]
    public void Maximum_brush_stroke_keeps_bounded_draft_history_and_submits_once()
    {
        using var harness = Blank(128, 16, 32);
        using var tool = new WorldEditorVoxelTool(harness.Session);
        tool.SetActive(true);
        tool.SetMode(WorldEditorVoxelMode.Brush);
        tool.SetSettings(Ore, VoxelOrientation.Identity, VoxelCellOperation.Add);
        tool.BeginStroke(new(0, 8, 0));
        for (var z = 0; z < 32; z++)
        for (var index = 0; index < 128; index++)
            tool.ExtendStroke(new(z % 2 == 0 ? index : 127 - index, 8, z));
        tool.InterruptDraftInput();
        Assert.AreEqual(0, harness.Session.World.VoxelCount);
        Assert.AreEqual(4096, tool.RegionPreview.AffectedCount);
        for (var index = 0; index < VoxelDevelopmentSession.VoxelHistoryCapacity; index++)
            Assert.IsTrue(tool.UndoDraft(), $"undo {index}");
        Assert.IsFalse(tool.UndoDraft());
        for (var index = 0; index < VoxelDevelopmentSession.VoxelHistoryCapacity; index++)
            Assert.IsTrue(tool.RedoDraft(), $"redo {index}");
        Assert.IsFalse(tool.RedoDraft());
        Assert.AreEqual(4096, tool.RegionPreview.AffectedCount);
        Assert.AreEqual(WorldEditRefusal.LargeEditConfirmationRequired, tool.Commit().Refusal);
        var submitted = tool.Commit(confirmLargeEdit: true);
        Assert.AreEqual(WorldEditState.Executed, submitted.Record!.State);
        Assert.AreEqual(4096, harness.Session.World.VoxelCount);
        Assert.AreEqual(1, harness.Session.UndoCount);
    }

    private static void AssertPreview(VoxelRegionPreview preview, int affected, int skipped, VoxelCellSetStatus status)
    {
        Assert.AreEqual(affected, preview.AffectedCount);
        Assert.AreEqual(skipped, preview.SkippedCount);
        Assert.AreEqual(status, preview.Validation.Status);
    }

    private static WorldEditorHarness Blank(int x = 16, int y = 8, int z = 16)
    {
        var harness = WorldEditorHarness.Content();
        Assert.IsTrue(BlankWorldSource.TryIdentity(x, y, z, 184, out var identity));
        harness.Attach(BlankWorldSource.Establish(identity, harness.Catalog, harness.Composites, harness.Store));
        return harness;
    }
}
