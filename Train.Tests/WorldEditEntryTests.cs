using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;
using Train.Logging;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:world-edit")]
public sealed class WorldEditEntryTests
{
    [TestMethod]
    public void Console_panel_and_automatic_sequence_submit_the_same_payloads_at_the_same_positions()
    {
        var sequence = WorldEditorSequences.Find(WorldEditorSequences.StraightRailOrderedEditsId)!;

        using var automatic = WorldEditorHarness.Establish(sequence.ConfigurationId);
        var report = WorldEditorValidation.Execute(sequence, automatic.Session);
        Assert.IsTrue(report.Passed, string.Join(Environment.NewLine, report.Lines()));

        using var panel = WorldEditorHarness.Establish(sequence.ConfigurationId);
        foreach (var step in sequence.Steps)
            Assert.AreEqual(step.ExpectedStatus, step.Operation.Apply(panel.Session), step.Operation.Describe());

        using var console = WorldEditorHarness.Establish(sequence.ConfigurationId);
        using var logger = LoggerSession.Create();
        var session = console.Session;
        var dispatcher = Dispatcher(logger, session);
        var identity = VoxelOrientation.Identity;
        var probe = WorldEditorSequences.EditProbe;
        var pose = WorldEditorSequences.EditShellPose;
        var probeText = $"{probe.X} {probe.Y} {probe.Z}";
        dispatcher.Execute("rail start");
        dispatcher.Execute($"voxel add {probeText} {WorldEditorSequences.EditVoxelId.Value} {identity}");
        dispatcher.Execute($"voxel damage {probeText} 25");
        dispatcher.Execute($"composite manufacture {HollowShellFixture.DefinitionId.Value} pose {pose.OriginX} {pose.OriginY} {pose.OriginZ}");
        var shell = session.QueryComposites().Max(item => item.Id.Value);
        string Part(LocalVoxelCoord cell) =>
            session.QueryComposite(new EntityId(shell))!.Parts.Single(part => part.Cell == cell).Id.ToString();
        string Cell(LocalVoxelCoord cell) => $"{cell.X} {cell.Y} {cell.Z}";
        var marker = HollowShellRefit.MarkerId.Value;
        dispatcher.Execute($"composite add {shell} {Cell(HollowShellRefit.FirstAdd)} {marker} {identity}");
        dispatcher.Execute($"composite damage {shell} {Cell(HollowShellRefit.FirstAdd)} 25");
        dispatcher.Execute($"composite move {shell} {Part(HollowShellRefit.FirstAdd)} {Cell(HollowShellRefit.Moved)}");
        dispatcher.Execute($"composite rotate {shell} {Part(HollowShellRefit.Moved)} {HollowShellRefit.MarkerOrientation}");
        dispatcher.Execute($"composite replace {shell} {Cell(HollowShellRefit.Moved)} {HollowShellRefit.ReplacementId.Value} {identity}");
        dispatcher.Execute($"composite remove {shell} {Part(HollowShellRefit.Moved)}");
        dispatcher.Execute($"composite batch {shell} add {Cell(HollowShellRefit.FirstAdd)} {marker} {identity} add {Cell(HollowShellRefit.SecondAdd)} {marker} {identity}");
        dispatcher.Execute($"voxel remove {probeText} at 3");
        dispatcher.Execute($"voxel add {probeText} {WorldEditorSequences.EditVoxelId.Value} {identity} at 3");
        dispatcher.Execute($"composite add {shell} {Cell(HollowShellRefit.OutOfEnvelope)} {marker} {identity} at 3");
        dispatcher.Execute("voxel edits pending");
        dispatcher.Execute("rail step 3");
        dispatcher.Execute($"voxel add {probeText} {WorldEditorSequences.EditVoxelId.Value} {identity}");
        dispatcher.Execute("rail step");
        dispatcher.Execute("voxel edits log 3");
        dispatcher.Execute("voxel edits");

        string[] Records(VoxelDevelopmentSession target) => target.QueryEditLog().Select(WorldEditCodec.EncodeRecord).ToArray();
        CollectionAssert.AreEqual(Records(automatic.Session), Records(panel.Session));
        CollectionAssert.AreEqual(Records(automatic.Session), Records(session));
        Assert.AreEqual(automatic.Session.EditLogDigest, session.EditLogDigest);
        Assert.AreEqual(report.FinalFingerprint, WorldSaveAuthority.Fingerprint(panel.Session));
        Assert.AreEqual(report.FinalFingerprint, WorldSaveAuthority.Fingerprint(session));

        var text = Messages(logger);
        StringAssert.Contains(text, "Rail Started edit 1");
        StringAssert.Contains(text, "Edit 12 voxel.remove pending");
        StringAssert.Contains(text, "accepted 0:11 target 3");
        StringAssert.Contains(text, "Edits pending 3");
        StringAssert.Contains(text, "edit 14 vehicle.refit accepted 0:13 target 3");
        StringAssert.Contains(text, "edit 14 vehicle.refit rejected EnvelopeExceeded at 3:2 accepted 0:13");
        StringAssert.Contains(text, "edit 15 voxel.add rejected Occupied at 3:3 accepted 3:3");
        StringAssert.Contains(text, "Edits boundary 4:0 next 16");
        StringAssert.Contains(text, $"digest {session.EditLogDigest}");
        StringAssert.Contains(text, "fault none");
    }

    [TestMethod]
    public void Console_scheduling_refuses_past_steps_and_reports_faults_without_consuming_positions()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailEmptyId);
        using var logger = LoggerSession.Create();
        var session = worldEditor.Session;
        var dispatcher = Dispatcher(logger, session);
        Assert.IsTrue(session.AdvanceSimulation(4).Succeeded);
        var probe = WorldEditorSequences.EditProbe;

        dispatcher.Execute($"voxel remove {probe.X} {probe.Y} {probe.Z} at 3");
        dispatcher.Execute($"composite manufacture at 2");
        dispatcher.Execute($"voxel remove {probe.X} {probe.Y} {probe.Z} at soon");
        Assert.AreEqual(1UL, session.NextEditId);
        Assert.AreEqual(new SimulationPosition(4, 0), session.EditBoundary);

        dispatcher.Execute($"voxel remove {probe.X} {probe.Y} {probe.Z} at 4");
        Assert.AreEqual("Empty", session.QueryEditLog().Single().Status);
        dispatcher.Execute("composite manufacture pose 60 6 24 at 6");
        Assert.AreEqual(CompositeTransform.Translation(60, 6, 24), ((VehicleManufactureEdit)session.QueryPendingEdits().Single().Edit).Transform);
        dispatcher.Execute("voxel edits fault");

        session.EditFaultProbe = _ => throw new InvalidOperationException("injected");
        dispatcher.Execute("rail step 2");
        session.EditFaultProbe = null;
        dispatcher.Execute("voxel edits fault");
        dispatcher.Execute($"voxel add {probe.X} {probe.Y} {probe.Z} {WorldEditorSequences.EditVoxelId.Value} {VoxelOrientation.Identity} at 9");

        var text = Messages(logger);
        StringAssert.Contains(text, "Edit PastStep.");
        StringAssert.Contains(text, "Edit 1 voxel.remove rejected Empty");
        StringAssert.Contains(text, "accepted 4:0 executed 4:0");
        StringAssert.Contains(text, "Edit 2 vehicle.manufacture pending");
        StringAssert.Contains(text, "Edits fault none.");
        StringAssert.Contains(text, "Edits fault edit 2 vehicle.manufacture at 6:0");
        StringAssert.Contains(text, "exception InvalidOperationException");
        StringAssert.Contains(text, "Edit SimulationFaulted.");
        StringAssert.Contains(text, "Usage: voxel reset");
        Assert.AreEqual(0, session.QueryComposites().Count);
    }

    [TestMethod]
    public void Console_cell_conflicts_report_the_owning_rail_and_vehicle_part()
    {
        using var worldEditor = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        using var logger = LoggerSession.Create();
        var session = worldEditor.Session;
        var dispatcher = Dispatcher(logger, session);

        var railCell = StraightRailFixture.FirstSegmentDamageCell;
        Assert.IsTrue(session.Rails.TryGetOccupancy(railCell, out var railOccupancy));
        var railId = railOccupancy.Supports.MinBy(support => support.SegmentId.Value)!.SegmentId;
        dispatcher.Execute($"voxel cell remove {railCell.X} {railCell.Y} {railCell.Z}");

        var vehicle = session.QueryComposites().First();
        WorldVoxelCoord vehicleCell = default;
        EntityId partId = default;
        foreach (var part in vehicle.Parts)
        {
            var center = vehicle.Transform.TransformPoint(part.Cell.X + 0.5, part.Cell.Y + 0.5, part.Cell.Z + 0.5);
            var candidate = new WorldVoxelCoord((int)Math.Floor(center.X), (int)Math.Floor(center.Y), (int)Math.Floor(center.Z));
            if (!session.World.Bounds.Contains(candidate) ||
                session.Query(candidate).IsOccupied ||
                session.Rails.TryGetOccupancy(candidate, out _))
                continue;
            vehicleCell = candidate;
            partId = part.Id;
            break;
        }
        Assert.AreNotEqual(default, partId, "Fixture must expose a vehicle-owned cell away from railway occupancy.");
        dispatcher.Execute($"voxel cell add {vehicleCell.X} {vehicleCell.Y} {vehicleCell.Z} {WorldEditorSequences.EditVoxelId.Value} {VoxelOrientation.Identity}");

        var text = Messages(logger);
        StringAssert.Contains(text, $"Edit 1 voxel.cell-set rejected RailOccupied");
        StringAssert.Contains(text, $"conflict rail-segment {railId} at ({railCell.X}, {railCell.Y}, {railCell.Z})");
        StringAssert.Contains(text, $"Edit 2 voxel.cell-set rejected VehicleOccupied");
        StringAssert.Contains(text, $"conflict vehicle {vehicle.Id} part {partId} at ({vehicleCell.X}, {vehicleCell.Y}, {vehicleCell.Z})");
    }

    internal static ConsoleCommandDispatcher Dispatcher(LoggerSession logger, VoxelDevelopmentSession session) =>
        new(
            logger.Logger,
            static () => { },
            static () => { },
            static () => { },
            allowReload: false,
            new VoxelConsoleCommands(session),
            new CompositeConsoleCommands(session),
            new RailConsoleCommands(session));

    internal static string Messages(LoggerSession logger) =>
        string.Join('\n', logger.Memory.Snapshot().Select(item => item.Message));
}
