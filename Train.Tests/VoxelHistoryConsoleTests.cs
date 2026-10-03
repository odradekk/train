using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Commands;
using Train.Domain.Composites;
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
    public void Console_history_conflict_preserves_owner_identity_and_position()
    {
        var cell = new WorldVoxelCoord(7, 3, 5);
        var vehicle = new EntityId(41);
        var part = new EntityId(73);
        var result = new VoxelCellSetResult(
            VoxelCellSetStatus.VehicleOccupied,
            cell,
            VoxelCellOwnerKind.VehiclePart,
            vehicle,
            part);
        var record = new WorldEditRecord(
            9,
            new VoxelCellRestoreEdit(Array.Empty<VoxelCellRestoreTarget>()),
            default,
            0,
            WorldEditState.Rejected,
            default,
            nameof(VoxelCellSetStatus.VehicleOccupied));

        var output = WorldEditConsole.FormatSubmission(new WorldEditSubmission(WorldEditRefusal.None, record, result));

        StringAssert.Contains(output, nameof(VoxelCellSetStatus.VehicleOccupied));
        Assert.IsTrue(output.Split('\n').Any(line =>
            line.Contains(vehicle.ToString(), StringComparison.Ordinal) &&
            line.Contains(part.ToString(), StringComparison.Ordinal) &&
            line.Contains($"({cell.X}, {cell.Y}, {cell.Z})", StringComparison.Ordinal)));
    }

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
        Assert.AreEqual(0, session.RedoCount);
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
        var pending = session.QueryPendingEdits().Single();
        Assert.IsInstanceOfType<TrainStopEdit>(pending.Edit);
        Assert.AreEqual(4ul, pending.TargetStep);
        Assert.AreEqual(0, session.UndoCount);
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
        Assert.AreEqual(0, session.RedoCount);
        Assert.IsTrue(tool.HasDraft);
    }

    [TestMethod]
    public void Console_reports_when_history_capacity_evicts_the_oldest_record()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        using var logger = LoggerSession.Create();
        var dispatcher = WorldEditEntryTests.Dispatcher(logger, worldEditor.Session);

        for (var x = 0; x <= 64; x++)
            dispatcher.Execute($"voxel add {x} 5 0 {WorldEditorSequences.EditVoxelId.Value} {VoxelOrientation.Identity}");

        Assert.AreEqual(64, worldEditor.Session.UndoCount);
        Assert.AreEqual(1UL, worldEditor.Session.HistoryEvictionCount);
    }

    [TestMethod]
    public void Save_names_are_not_parsed_as_edit_submission_options()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var session = worldEditor.Session;
        var voxel = new VoxelConsoleCommands(session);
        var cell = WorldEditorSequences.EditProbe;
        session.Add(cell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity);

        voxel.Execute(["save", "Home", "at", "Station"]);

        Assert.IsNotNull(session.SaveId);
        Assert.AreEqual("Home at Station", session.DisplayName);
        Assert.IsFalse(session.HasUnsavedChanges);
        Assert.AreEqual(1, session.UndoCount);
        Assert.IsTrue(session.Query(cell).IsOccupied);
    }

    [TestMethod]
    public void Immediate_console_commands_keep_typed_output_when_history_exists_or_is_confirmed()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        var session = worldEditor.Session;
        var voxel = new VoxelConsoleCommands(session);
        var composite = new CompositeConsoleCommands(session);
        var first = WorldEditorSequences.EditProbe;
        var second = first with { X = first.X + 1 };

        voxel.Execute(["add", first.X.ToString(), first.Y.ToString(), first.Z.ToString(), WorldEditorSequences.EditVoxelId.Value, VoxelOrientation.Identity.ToString()]);
        var voxelOutput = voxel.Execute(["add", second.X.ToString(), second.Y.ToString(), second.Z.ToString(), WorldEditorSequences.EditVoxelId.Value, VoxelOrientation.Identity.ToString()]);

        Assert.IsTrue(session.Query(second).IsOccupied);
        StringAssert.Contains(voxelOutput, $"({second.X}, {second.Y}, {second.Z})");
        var count = System.Text.RegularExpressions.Regex.Match(voxelOutput, @"Voxel count:\s*(\d+)");
        Assert.IsTrue(count.Success);
        Assert.AreEqual(session.World.VoxelCount, int.Parse(count.Groups[1].Value));

        var manufactureOutput = composite.Execute(["manufacture", "confirm-end-history"]);
        var vehicle = session.QueryComposites().Single();

        Assert.AreEqual(0, session.UndoCount);
        StringAssert.Contains(manufactureOutput, $"vehicle {vehicle.Id}");
        StringAssert.Contains(manufactureOutput, $"parts {vehicle.Parts.Count}");
    }
}
