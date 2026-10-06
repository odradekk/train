using Train.Core.Content.Voxels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Commands;
using Train.Domain.Rails;
using Train.Domain.Voxels;
using Train.Domain.WorldEditor;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:world-edit")]
public sealed class VoxelRegionConsoleTests
{
    [TestMethod]
    public void Region_commands_apply_only_relevant_cells_and_report_counts()
    {
        using var harness = BlankWorld();
        var session = harness.Session;
        var commands = new VoxelConsoleCommands(session);
        var id = WorldEditorSequences.EditVoxelId.Value;

        session.Add(new WorldVoxelCoord(1, 0, 0), WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity);
        var added = commands.Execute(["region", "add", "0", "0", "0", "1", "0", "1", id, "px_py"]);
        StringAssert.Contains(added, nameof(VoxelCellSetStatus.Valid));
        StringAssert.Contains(added, "affected 1");
        StringAssert.Contains(added, "skipped 1");
        Assert.AreEqual(2, session.World.VoxelCount);

        var replaced = commands.Execute(["region", "replace", "0", "0", "0", "1", "0", "1", id, "pz_py"]);
        StringAssert.Contains(replaced, nameof(VoxelCellSetStatus.Valid));
        StringAssert.Contains(replaced, "affected 2");
        StringAssert.Contains(replaced, "skipped 0");
        Assert.IsTrue(new[] { new WorldVoxelCoord(0, 0, 0), new WorldVoxelCoord(1, 0, 0) }
            .All(cell => session.Query(cell).Voxel!.Orientation == VoxelOrientation.Yaw90));

        var removed = commands.Execute(["region", "remove", "0", "0", "0", "2", "0", "1"]);
        StringAssert.Contains(removed, nameof(VoxelCellSetStatus.Valid));
        StringAssert.Contains(removed, "affected 2");
        StringAssert.Contains(removed, "skipped 1");
        Assert.AreEqual(0, session.World.VoxelCount);
    }

    [TestMethod]
    public void Region_conflict_reports_status_position_and_owner()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        var session = harness.Session;
        var commands = new VoxelConsoleCommands(session);
        var cell = StraightRailFixture.FirstSegmentDamageCell;

        var result = commands.Execute(["region", "remove", cell.X.ToString(), cell.Y.ToString(), cell.Z.ToString(),
            cell.X.ToString(), cell.Z.ToString(), "1"]);

        StringAssert.Contains(result, nameof(WorldEditRefusal.RegionRejected));
        StringAssert.Contains(result, nameof(VoxelCellSetStatus.RailOccupied));
        StringAssert.Contains(result, "affected 0");
        StringAssert.Contains(result, "skipped 0");
        Assert.IsNotNull(session.Query(cell).Voxel);
        Assert.AreEqual(1UL, session.NextEditId);
    }

    [TestMethod]
    public void Region_conflict_reports_non_owner_validation_status()
    {
        using var harness = BlankWorld();
        var commands = new VoxelConsoleCommands(harness.Session);

        var result = commands.Execute(["region", "add", "15", "0", "0", "16", "0", "1",
            WorldEditorSequences.EditVoxelId.Value, "px_py"]);

        StringAssert.Contains(result, nameof(VoxelCellSetStatus.OutOfBounds));
        StringAssert.Contains(result, "affected 1");
        StringAssert.Contains(result, "skipped 0");
        Assert.AreEqual(0, harness.Session.World.VoxelCount);
        Assert.AreEqual(1UL, harness.Session.NextEditId);
    }

    [TestMethod]
    public void Large_region_confirmation_does_not_consume_the_refused_edit_id()
    {
        using var harness = BlankWorld();
        var session = harness.Session;
        var commands = new VoxelConsoleCommands(session);
        var args = new[] { "region", "add", "0", "0", "0", "15", "4", "8", WorldEditorSequences.EditVoxelId.Value, "px_py" };

        var refused = commands.Execute(args);
        StringAssert.Contains(refused, nameof(WorldEditRefusal.LargeEditConfirmationRequired));
        StringAssert.Contains(refused, "affected 640");
        StringAssert.Contains(refused, "skipped 0");
        Assert.AreEqual(1UL, session.NextEditId);
        Assert.AreEqual(0, session.World.VoxelCount);

        var accepted = commands.Execute(args.Append("confirm-large-edit").ToArray());
        StringAssert.Contains(accepted, nameof(VoxelCellSetStatus.Applied));
        StringAssert.Contains(accepted, "affected 640");
        StringAssert.Contains(accepted, "skipped 0");
        Assert.AreEqual(640, session.World.VoxelCount);
        Assert.AreEqual(2UL, session.NextEditId);
    }

    [TestMethod]
    public void Scheduled_region_stores_the_previewed_cells_explicitly()
    {
        using var harness = BlankWorld();
        var session = harness.Session;
        var commands = new VoxelConsoleCommands(session);

        var result = commands.Execute(["region", "add", "1", "1", "2", "2", "3", "2",
            WorldEditorSequences.EditVoxelId.Value, "px_py", "at", "4"]);

        StringAssert.Contains(result, nameof(WorldEditState.Pending).ToLowerInvariant());
        StringAssert.Contains(result, "affected 8");
        StringAssert.Contains(result, "skipped 0");
        var edit = (VoxelCellSetEdit)session.QueryPendingEdits().Single().Edit;
        Assert.AreEqual(8, edit.Targets.Count);
        CollectionAssert.AreEquivalent(
            new[]
            {
                new WorldVoxelCoord(1, 1, 2), new WorldVoxelCoord(1, 1, 3),
                new WorldVoxelCoord(1, 2, 2), new WorldVoxelCoord(1, 2, 3),
                new WorldVoxelCoord(2, 1, 2), new WorldVoxelCoord(2, 1, 3),
                new WorldVoxelCoord(2, 2, 2), new WorldVoxelCoord(2, 2, 3)
            },
            edit.Targets.Select(target => target.Position).ToArray());
    }

    private static WorldEditorHarness BlankWorld()
    {
        var harness = WorldEditorHarness.Content();
        Assert.IsTrue(BlankWorldSource.TryIdentity(16, 8, 16, 184, out var identity));
        harness.Attach(BlankWorldSource.Establish(identity, harness.Catalog, harness.Composites, harness.Store));
        return harness;
    }
}
