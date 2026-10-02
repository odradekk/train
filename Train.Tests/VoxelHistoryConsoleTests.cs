using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Commands;
using Train.Domain.Voxels;
using Train.Domain.WorldEditor;
using Train.Logging;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:world-edit")]
public sealed class VoxelHistoryConsoleTests
{
    [TestMethod]
    public void Panel_and_console_share_history_and_irreversible_edits_require_explicit_confirmation()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        using var logger = LoggerSession.Create();
        using var tool = new WorldEditorVoxelTool(worldEditor.Session);
        var session = worldEditor.Session;
        var dispatcher = WorldEditEntryTests.Dispatcher(logger, session);
        var cell = WorldEditorSequences.EditProbe;
        var voxel = WorldEditorSequences.EditVoxelId;

        tool.SetExact(cell, voxel, VoxelOrientation.Identity, VoxelCellOperation.Add);
        Assert.IsTrue(tool.Commit().Accepted);
        Assert.AreEqual(1, session.UndoCount);

        dispatcher.Execute("voxel undo");
        Assert.IsFalse(session.Query(cell).IsOccupied);
        Assert.AreEqual(1, session.RedoCount);

        dispatcher.Execute("voxel redo");
        Assert.IsTrue(session.Query(cell).IsOccupied);
        Assert.AreEqual(1, session.UndoCount);

        dispatcher.Execute($"voxel damage {cell.X} {cell.Y} {cell.Z} 25");
        Assert.AreEqual(100, session.Query(cell).Voxel!.CurrentDurability);
        Assert.AreEqual(1, session.UndoCount);

        dispatcher.Execute($"voxel damage {cell.X} {cell.Y} {cell.Z} 25 confirm-end-history");
        Assert.AreEqual(75, session.Query(cell).Voxel!.CurrentDurability);
        Assert.AreEqual(0, session.UndoCount);
        StringAssert.Contains(
            WorldEditEntryTests.Messages(logger),
            "This irreversible edit will clear undo and redo history; resubmit with trailing confirm-end-history.");
    }

    [TestMethod]
    public void Confirmation_can_follow_at_step()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        using var logger = LoggerSession.Create();
        var session = worldEditor.Session;
        var dispatcher = WorldEditEntryTests.Dispatcher(logger, session);
        var cell = WorldEditorSequences.EditProbe;

        dispatcher.Execute($"voxel add {cell.X} {cell.Y} {cell.Z} {WorldEditorSequences.EditVoxelId.Value} {VoxelOrientation.Identity}");
        dispatcher.Execute("composite manufacture at 3 confirm-end-history");

        Assert.AreEqual(0, session.UndoCount);
        Assert.AreEqual(1, session.QueryPendingEdits().Count);
        Assert.IsInstanceOfType<VehicleManufactureEdit>(session.QueryPendingEdits().Single().Edit);
    }

    [TestMethod]
    public void Rail_start_and_stop_keep_their_target_syntax_before_history_confirmation()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        using var logger = LoggerSession.Create();
        var session = worldEditor.Session;
        var dispatcher = WorldEditEntryTests.Dispatcher(logger, session);
        var assembly = session.QueryTrainAssemblies().Single().Id;
        var cell = WorldEditorSequences.EditProbe;

        dispatcher.Execute($"voxel add {cell.X} {cell.Y} {cell.Z} {WorldEditorSequences.EditVoxelId.Value} {VoxelOrientation.Identity}");
        var cursor = session.NextEditId;
        dispatcher.Execute($"rail start assembly {assembly} at 3");
        Assert.AreEqual(cursor, session.NextEditId);
        Assert.AreEqual(1, session.UndoCount);

        dispatcher.Execute($"rail start assembly {assembly} at 3 confirm-end-history");
        Assert.AreEqual(0, session.UndoCount);
        Assert.IsInstanceOfType<TrainStartEdit>(session.QueryPendingEdits().Single().Edit);
        Assert.IsTrue(session.AdvanceSimulation(3).Succeeded);

        var second = cell with { X = cell.X + 1 };
        dispatcher.Execute($"voxel add {second.X} {second.Y} {second.Z} {WorldEditorSequences.EditVoxelId.Value} {VoxelOrientation.Identity}");
        dispatcher.Execute($"rail stop assembly {assembly} 4 confirm-end-history");
        Assert.IsInstanceOfType<TrainStopEdit>(session.QueryPendingEdits().Single().Edit);
        StringAssert.Contains(
            WorldEditEntryTests.Messages(logger),
            "This irreversible edit will clear undo and redo history; resubmit with trailing confirm-end-history.");
    }

    [TestMethod]
    public void Redo_only_history_requires_confirmation_and_refusal_preserves_cursor()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        using var logger = LoggerSession.Create();
        var session = worldEditor.Session;
        var dispatcher = WorldEditEntryTests.Dispatcher(logger, session);
        var cell = WorldEditorSequences.EditProbe;

        dispatcher.Execute($"voxel add {cell.X} {cell.Y} {cell.Z} {WorldEditorSequences.EditVoxelId.Value} {VoxelOrientation.Identity}");
        dispatcher.Execute("voxel undo");
        var cursor = session.NextEditId;
        Assert.AreEqual(0, session.UndoCount);
        Assert.AreEqual(1, session.RedoCount);

        dispatcher.Execute("composite manufacture");
        Assert.AreEqual(cursor, session.NextEditId);
        Assert.AreEqual(1, session.RedoCount);

        dispatcher.Execute("composite manufacture confirm-end-history");
        Assert.AreEqual(0, session.RedoCount);
        Assert.AreEqual(1, session.QueryComposites().Count);
    }

    [TestMethod]
    public void Draft_refuses_console_undo_without_changing_history()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        using var logger = LoggerSession.Create();
        using var tool = new WorldEditorVoxelTool(worldEditor.Session);
        var session = worldEditor.Session;
        var dispatcher = WorldEditEntryTests.Dispatcher(logger, session);
        var cell = WorldEditorSequences.EditProbe;

        dispatcher.Execute($"voxel add {cell.X} {cell.Y} {cell.Z} {WorldEditorSequences.EditVoxelId.Value} {VoxelOrientation.Identity}");
        tool.SetExact(cell with { X = cell.X + 1 }, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity, VoxelCellOperation.Add);
        dispatcher.Execute("voxel undo");

        Assert.IsTrue(session.Query(cell).IsOccupied);
        Assert.AreEqual(1, session.UndoCount);
        StringAssert.Contains(WorldEditEntryTests.Messages(logger), "Edit VoxelDraftExists.");
    }

    [TestMethod]
    public void Console_reports_when_history_capacity_evicts_the_oldest_record()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        using var logger = LoggerSession.Create();
        var dispatcher = WorldEditEntryTests.Dispatcher(logger, worldEditor.Session);

        for (var x = 0; x <= 64; x++)
            dispatcher.Execute($"voxel cell add {x} 5 0 {WorldEditorSequences.EditVoxelId.Value} {VoxelOrientation.Identity}");

        Assert.AreEqual(64, worldEditor.Session.UndoCount);
        StringAssert.Contains(
            WorldEditEntryTests.Messages(logger),
            "History capacity reached; oldest undo record evicted.");
    }
}
