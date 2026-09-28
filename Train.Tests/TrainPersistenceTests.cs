using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Rails;
using Train.Domain.Voxels;
using Train.Logging;

namespace Train.Tests;

[TestClass]
public sealed class TrainPersistenceTests
{
    private const double Tolerance = 1e-8;

    [TestMethod]
    public void Railway_and_parked_assembly_round_trip_through_a_new_session_file_load()
    {
        using var writer = Parked();
        var before = writer.Session.CaptureTrainAuthority();
        var fingerprint = WorldSaveAuthority.Fingerprint(writer.Session);
        var saved = writer.Session.Save("Slice 4 Parked");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        var json = File.ReadAllText(writer.Store.PathFor(saved.SaveId!.Value));
        StringAssert.Contains(json, "\"save_format_version\":5");
        StringAssert.Contains(json, "\"railway\"");
        StringAssert.Contains(json, "\"assemblies\"");
        StringAssert.Contains(json, "\"world_edits\"");
        Assert.IsFalse(json.Contains("\"scheduled_commands\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("MeshInstance", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("UnsupportedRailway", StringComparison.Ordinal));

        using var reader = StaticTrainHarness.Create(writer.UserData, writer.Source);
        Assert.IsFalse(reader.Session.HasRailway);
        Assert.AreEqual(WorldSaveStatus.Loaded, reader.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(before, reader.Session.CaptureTrainAuthority());
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(reader.Session));
        Assert.AreEqual(257, reader.Session.QueryRails().SleeperCount);
        Assert.AreEqual(2, reader.Session.Rails.Segments.Count);
        Assert.AreEqual(1, reader.Session.Rails.Connections.Count);
        Assert.IsTrue(reader.Session.Rails.AreConnected(
            reader.Session.Rails.Segments[0].Id,
            reader.Session.Rails.Segments[1].Id));
        Assert.IsTrue(reader.Session.Rails.Occupies(StraightRailFixture.EdgeCell));
        var seam = StraightRailFixture.SharedSeamCell;
        Assert.IsTrue(reader.Session.Rails.TryGetOccupancy(seam, out var occupancy));
        Assert.IsTrue(occupancy.Supports.Select(item => item.SegmentId).Distinct().Count() >= 2);
        var parked = reader.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(80.0, parked.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(0.0, parked.Speed, Tolerance);
        Assert.IsFalse(parked.Running);
        Assert.AreEqual(0UL, reader.Session.SimulationStep);
        Assert.AreEqual(saved.SaveId, reader.Session.SaveId);
        Assert.AreEqual(WorldSaveStatus.Saved, reader.Session.Save("Slice 4 Parked").Status);
        Assert.AreEqual(saved.SaveId, reader.Session.SaveId);
    }

    [TestMethod]
    public void Start_at_zero_save_at_900_new_session_load_matches_uninterrupted_checkpoints()
    {
        using var live = Parked();
        Assert.IsTrue(live.Session.StartTrain(RailHeading.AlongAlignment, 0).Succeeded);
        var liveHashes = new Dictionary<string, string>();
        var liveStates = new Dictionary<ulong, TrainAuthoritySnapshot>();
        Record(live.Session, 0, liveHashes, liveStates);
        AdvanceTo(live.Session, 300);
        Record(live.Session, 300, liveHashes, liveStates);
        AdvanceTo(live.Session, 600);
        Record(live.Session, 600, liveHashes, liveStates);
        AdvanceTo(live.Session, 900);
        Record(live.Session, 900, liveHashes, liveStates);
        var saved = live.Session.Save("Slice 4 Step 900");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        AdvanceTo(live.Session, 1200);
        Record(live.Session, 1200, liveHashes, liveStates);
        AdvanceTo(live.Session, 1500);
        Record(live.Session, 1500, liveHashes, liveStates);
        AdvanceTo(live.Session, 1800);
        Record(live.Session, 1800, liveHashes, liveStates);
        Assert.IsTrue(live.Session.StopTrain().Succeeded);
        Record(live.Session, 1800, liveHashes, liveStates, suffix: "stopped");

        using var writer = Parked();
        Assert.IsTrue(writer.Session.StartTrain(RailHeading.AlongAlignment, 0).Succeeded);
        AdvanceTo(writer.Session, 900);
        Assert.AreEqual(liveStates[900], writer.Session.CaptureTrainAuthority());
        Assert.AreEqual(liveHashes["900"], WorldSaveAuthority.Fingerprint(writer.Session));
        var branch = writer.Session.Save("Slice 4 Branch 900");
        Assert.AreEqual(WorldSaveStatus.Saved, branch.Status);

        using var resumed = StaticTrainHarness.Create(writer.UserData, writer.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, resumed.Session.Load(branch.SaveId!.Value).Status);
        Assert.AreEqual(900UL, resumed.Session.SimulationStep);
        Assert.AreEqual(liveStates[900], resumed.Session.CaptureTrainAuthority());
        Assert.AreEqual(liveHashes["900"], WorldSaveAuthority.Fingerprint(resumed.Session));
        Assert.AreEqual(136.0, resumed.Session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(4.0, resumed.Session.QueryTrainAssemblies().Single().Speed, Tolerance);
        Assert.IsTrue(resumed.Session.QueryTrainAssemblies().Single().Running);

        AdvanceTo(resumed.Session, 1200);
        Assert.AreEqual(liveStates[1200], resumed.Session.CaptureTrainAuthority());
        Assert.AreEqual(liveHashes["1200"], WorldSaveAuthority.Fingerprint(resumed.Session));
        AdvanceTo(resumed.Session, 1500);
        Assert.AreEqual(liveStates[1500], resumed.Session.CaptureTrainAuthority());
        Assert.AreEqual(liveHashes["1500"], WorldSaveAuthority.Fingerprint(resumed.Session));
        AdvanceTo(resumed.Session, 1800);
        Assert.AreEqual(liveStates[1800], resumed.Session.CaptureTrainAuthority());
        Assert.AreEqual(liveHashes["1800"], WorldSaveAuthority.Fingerprint(resumed.Session));
        Assert.IsTrue(resumed.Session.StopTrain().Succeeded);
        Assert.AreEqual(liveHashes["1800-stopped"], WorldSaveAuthority.Fingerprint(resumed.Session));
        Assert.AreEqual(0.0, resumed.Session.QueryTrainAssemblies().Single().Speed, Tolerance);
        Assert.IsFalse(resumed.Session.QueryTrainAssemblies().Single().Running);
        Assert.AreEqual(WorldSaveStatus.Saved, resumed.Session.Save("Slice 4 Branch 1800").Status);
        Assert.AreEqual(branch.SaveId, resumed.Session.SaveId);
    }

    [TestMethod]
    public void Pending_same_step_commands_survive_file_load_without_loss_or_replay()
    {
        using var writer = Parked();
        Assert.IsTrue(writer.Session.StartTrain(RailHeading.AlongAlignment, 0).Succeeded);
        Assert.IsTrue(writer.Session.StopTrain(101).Succeeded);
        Assert.IsTrue(writer.Session.StartTrain(RailHeading.AlongAlignment, 101).Succeeded);
        var pending = writer.Session.QueryScheduledTrainCommands();
        Assert.AreEqual(2, pending.Count);
        Assert.IsFalse(pending[0].Start);
        Assert.IsTrue(pending[1].Start);
        Assert.AreEqual(101UL, pending[0].AtStep);
        Assert.AreEqual(101UL, pending[1].AtStep);
        var before = writer.Session.CaptureTrainAuthority();
        var fingerprint = WorldSaveAuthority.Fingerprint(writer.Session);
        var saved = writer.Session.Save("Queued");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);

        using var reader = StaticTrainHarness.Create(writer.UserData, writer.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, reader.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(before, reader.Session.CaptureTrainAuthority());
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(reader.Session));
        CollectionAssert.AreEqual(
            pending.ToArray(),
            reader.Session.QueryScheduledTrainCommands().ToArray());

        Assert.IsTrue(reader.Session.AdvanceSimulation(102).Succeeded);
        var resumed = reader.Session.CaptureTrainAuthority();
        Assert.AreEqual(102UL, reader.Session.SimulationStep);
        Assert.AreEqual(0, reader.Session.QueryScheduledTrainCommands().Count);
        Assert.AreEqual(82.833888888888893, reader.Session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(0.033333333333333333, reader.Session.QueryTrainAssemblies().Single().Speed, Tolerance);
        Assert.IsTrue(reader.Session.QueryTrainAssemblies().Single().Running);

        using var live = Parked();
        Assert.IsTrue(live.Session.StartTrain(RailHeading.AlongAlignment, 0).Succeeded);
        Assert.IsTrue(live.Session.StopTrain(101).Succeeded);
        Assert.IsTrue(live.Session.StartTrain(RailHeading.AlongAlignment, 101).Succeeded);
        Assert.IsTrue(live.Session.AdvanceSimulation(102).Succeeded);
        Assert.AreEqual(live.Session.CaptureTrainAuthority(), resumed);
        Assert.AreEqual(WorldSaveAuthority.Fingerprint(live.Session), WorldSaveAuthority.Fingerprint(reader.Session));
    }

    [TestMethod]
    public void Development_and_end_of_line_stops_reload_at_zero_and_require_formal_start()
    {
        using var stopped = Parked();
        Assert.IsTrue(stopped.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(stopped.Session.AdvanceSimulation(300).Succeeded);
        Assert.IsTrue(stopped.Session.StopTrain().Succeeded);
        var stoppedSave = stopped.Session.Save("Dev Stop");
        Assert.AreEqual(WorldSaveStatus.Saved, stoppedSave.Status);

        using var stoppedReader = StaticTrainHarness.Create(stopped.UserData, stopped.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, stoppedReader.Session.Load(stoppedSave.SaveId!.Value).Status);
        var afterStop = stoppedReader.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(0.0, afterStop.Speed, Tolerance);
        Assert.IsFalse(afterStop.Running);
        Assert.AreEqual(96.0, afterStop.Vehicles[0].FrontContact.X, Tolerance);
        Assert.IsTrue(stoppedReader.Session.AdvanceSimulation(60).Succeeded);
        var still = stoppedReader.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(0.0, still.Speed, Tolerance);
        Assert.IsFalse(still.Running);
        Assert.AreEqual(96.0, still.Vehicles[0].FrontContact.X, Tolerance);
        Assert.IsTrue(stoppedReader.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(stoppedReader.Session.QueryTrainAssemblies().Single().Running);

        using var ended = Parked();
        Assert.IsTrue(ended.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(ended.Session.AdvanceSimulation(4000).Succeeded);
        var endSave = ended.Session.Save("End Stop");
        Assert.AreEqual(WorldSaveStatus.Saved, endSave.Status);

        using var endReader = StaticTrainHarness.Create(ended.UserData, ended.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, endReader.Session.Load(endSave.SaveId!.Value).Status);
        var endState = endReader.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(272.0, endState.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(0.0, endState.Speed, Tolerance);
        Assert.IsFalse(endState.Running);
        Assert.IsTrue(endReader.Session.AdvanceSimulation(30).Succeeded);
        Assert.AreEqual(272.0, endReader.Session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(0.0, endReader.Session.QueryTrainAssemblies().Single().Speed, Tolerance);
        Assert.IsTrue(endReader.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
    }

    [TestMethod]
    public void Damaged_occupancy_and_pending_graph_rebuild_do_not_refit_geometry()
    {
        using var writer = Parked();
        Assert.AreEqual(WorldCommandStatus.Removed, writer.Session.Damage(StraightRailFixture.FirstSegmentDamageCell, 100).Status);
        var first = writer.Session.Rails.TraversableWorldX(writer.Session.Rails.Segments[0].Id, writer.Session.World);
        Assert.IsFalse(first.Any(interval => interval.Start <= 110 && interval.End >= 111));
        var fingerprint = WorldSaveAuthority.Fingerprint(writer.Session);
        var saved = writer.Session.Save("Damaged Rail");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);

        using var reader = StaticTrainHarness.Create(writer.UserData, writer.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, reader.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(reader.Session));
        Assert.AreEqual(writer.Session.Rails.Segments[0].Id, reader.Session.Rails.Segments[0].Id);
        Assert.AreEqual(writer.Session.Rails.Segments[0].Alignment, reader.Session.Rails.Segments[0].Alignment);
        CollectionAssert.AreEqual(
            writer.Session.Rails.Segments[0].SleeperVolumes.ToArray(),
            reader.Session.Rails.Segments[0].SleeperVolumes.ToArray());
        var restored = reader.Session.Rails.TraversableWorldX(reader.Session.Rails.Segments[0].Id, reader.Session.World);
        Assert.IsFalse(restored.Any(interval => interval.Start <= 110 && interval.End >= 111));
        Assert.IsFalse(reader.Session.Query(StraightRailFixture.FirstSegmentDamageCell).IsOccupied);
        Assert.AreEqual(257, reader.Session.QueryRails().SleeperCount);
        reader.Session.RebuildDerivedRailGraph();
        Assert.AreEqual(80.0, reader.Session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact.X, Tolerance);
    }

    [TestMethod]
    public void Double_round_trip_keeps_exact_bits_and_rejects_non_finite_motion()
    {
        using var writer = Parked();
        Assert.IsTrue(writer.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(writer.Session.AdvanceSimulation(300).Succeeded);
        var saved = writer.Session.Save("Bits");
        var path = writer.Store.PathFor(saved.SaveId!.Value);
        var json = File.ReadAllText(path);
        var root = JsonNode.Parse(json)!.AsObject();
        var speed = root["assemblies"]![0]!["speed"]!.GetValue<double>();
        var distance = root["assemblies"]![0]!["distance"]!.GetValue<double>();
        Assert.AreEqual(writer.Session.QueryTrainAssemblies().Single().Speed, speed);
        Assert.AreEqual(writer.Session.QueryTrainAssemblies().Single().Reference.Distance, distance);

        using var reader = StaticTrainHarness.Create(writer.UserData, writer.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, reader.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(speed, reader.Session.QueryTrainAssemblies().Single().Speed);
        Assert.AreEqual(distance, reader.Session.QueryTrainAssemblies().Single().Reference.Distance);

        var before = reader.Session.CaptureTrainAuthority();
        File.WriteAllText(path, json.Replace(
            "\"speed\":" + speed.ToString("G17", CultureInfo.InvariantCulture),
            "\"speed\":NaN",
            StringComparison.Ordinal));
        Assert.AreEqual(WorldSaveStatus.InvalidDelta, reader.Session.Load(saved.SaveId.Value).Status);
        Assert.AreEqual(before, reader.Session.CaptureTrainAuthority());
    }

    [TestMethod]
    public void Illegal_railway_and_assembly_files_are_rejected_before_replacing_the_session()
    {
        using var harness = Parked();
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(harness.Session.AdvanceSimulation(300).Succeeded);
        var saved = harness.Session.Save("Review Baseline");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        var path = harness.Store.PathFor(saved.SaveId!.Value);
        var previous = File.ReadAllText(path);
        var before = harness.Session.CaptureTrainAuthority();
        var fingerprint = WorldSaveAuthority.Fingerprint(harness.Session);
        var sequence = harness.Session.NextEditId;
        Assert.AreEqual(4.0, harness.Session.QueryTrainAssemblies().Single().Speed, Tolerance);
        Assert.IsTrue(harness.Session.QueryTrainAssemblies().Single().Running);

        AssertLoadKeeps(harness, Mutate(previous, root => root["assemblies"]![0]!["distance"] = -1d), before, fingerprint, sequence);
        AssertLoadKeeps(harness, Mutate(previous, root => root["assemblies"]![0]!["connections"] = new JsonArray()), before, fingerprint, sequence);
        AssertLoadKeeps(harness, Mutate(previous, root =>
        {
            var assembly = root["assemblies"]![0]!;
            assembly["connections"] = new JsonArray();
        }), before, fingerprint, sequence);
        AssertLoadKeeps(harness, Mutate(previous, root => root["assemblies"]![0]!["running"] = false), before, fingerprint, sequence);
        AssertLoadKeeps(harness, Mutate(previous, root => root["railway"]!["occupancy"] = new JsonArray()), before, fingerprint, sequence);
        AssertLoadKeeps(harness, Mutate(previous, root =>
        {
            var occupancy = root["railway"]!["occupancy"]!.AsArray();
            occupancy[1] = occupancy[0]!.DeepClone();
        }), before, fingerprint, sequence);
        AssertLoadKeeps(harness, Mutate(previous, root => root["railway"]!["sleeper_count"] = 0), before, fingerprint, sequence);
        AssertLoadKeeps(harness, Mutate(previous, DuplicateVehiclesAcrossAssemblies), before, fingerprint, sequence);
        File.WriteAllText(path, Mutate(previous, AddSecondAssembly));
        using var multi = StaticTrainHarness.Create(harness.UserData, harness.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, multi.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(2, multi.Session.QueryTrainAssemblies().Count);
        File.WriteAllText(path, previous);
        Assert.AreEqual(saved.SaveId, harness.Session.SaveId);

        Assert.AreEqual(previous, File.ReadAllText(path));
        Assert.AreEqual(WorldSaveStatus.Loaded, harness.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(before, harness.Session.CaptureTrainAuthority());
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(harness.Session));
    }

    [TestMethod]
    public void Legacy_train_queue_fields_are_refused_and_same_step_train_edits_restore_in_acceptance_order()
    {
        using var harness = Parked();
        Assert.IsTrue(harness.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(harness.Session.AdvanceSimulation(300).Succeeded);
        Assert.AreEqual(TrainMotionStatus.Pending, harness.Session.StartTrain(RailHeading.AlongAlignment, 301).Status);
        Assert.AreEqual(TrainMotionStatus.Pending, harness.Session.StopTrain(301).Status);
        var saved = harness.Session.Save("Sequence Baseline");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        var path = harness.Store.PathFor(saved.SaveId!.Value);
        var previous = File.ReadAllText(path);
        var before = harness.Session.CaptureTrainAuthority();
        var fingerprint = WorldSaveAuthority.Fingerprint(harness.Session);
        var nextEdit = harness.Session.NextEditId;
        var root = JsonNode.Parse(previous)!.AsObject();
        Assert.IsFalse(root.ContainsKey("scheduled_commands"));
        Assert.IsFalse(root.ContainsKey("scheduled_sequence"));
        CollectionAssert.AreEqual(
            new[] { "train.start", "train.stop" },
            root["world_edits"]!["pending"]!.AsArray().Select(item => item!["edit"]!["kind"]!.GetValue<string>()).ToArray());

        AssertLoadKeeps(harness, Mutate(previous, node => node["scheduled_sequence"] = 0), before, fingerprint, nextEdit, WorldSaveStatus.UnknownFormat);
        AssertLoadKeeps(harness, Mutate(previous, node => node["scheduled_commands"] = new JsonArray()), before, fingerprint, nextEdit, WorldSaveStatus.UnknownFormat);
        AssertLoadKeeps(harness, Mutate(previous, node =>
        {
            var list = node["world_edits"]!["pending"]!.AsArray();
            var head = list[0]!.DeepClone();
            list.RemoveAt(0);
            list.Add(head);
        }), before, fingerprint, nextEdit);
        AssertLoadKeeps(harness, Mutate(previous, node =>
            node["world_edits"]!["pending"]![0]!["edit"]!["assembly_id"] = node["next_entity_id"]!.GetValue<ulong>()), before, fingerprint, nextEdit);

        using var resumed = StaticTrainHarness.Create(harness.UserData, harness.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, resumed.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(nextEdit, resumed.Session.NextEditId);
        var pending = resumed.Session.QueryScheduledTrainCommands();
        Assert.AreEqual(2, pending.Count);
        Assert.IsTrue(pending[0].Start);
        Assert.IsFalse(pending[1].Start);
        Assert.AreEqual(pending[0].Sequence + 1, pending[1].Sequence);
        Assert.IsTrue(resumed.Session.AdvanceSimulation(2).Succeeded);
        Assert.IsFalse(resumed.Session.QueryTrainAssemblies().Single().Running);
        Assert.AreEqual(0.0, resumed.Session.QueryTrainAssemblies().Single().Speed, Tolerance);

        using var live = Parked();
        Assert.IsTrue(live.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(live.Session.AdvanceSimulation(300).Succeeded);
        Assert.IsTrue(live.Session.StartTrain(RailHeading.AlongAlignment, 301).Succeeded);
        Assert.IsTrue(live.Session.StopTrain(301).Succeeded);
        Assert.IsTrue(live.Session.AdvanceSimulation(2).Succeeded);
        Assert.AreEqual(live.Session.CaptureTrainAuthority(), resumed.Session.CaptureTrainAuthority());
        Assert.AreEqual(WorldSaveAuthority.Fingerprint(live.Session), WorldSaveAuthority.Fingerprint(resumed.Session));
    }

    [TestMethod]
    public void Missing_segment_unknown_format_and_io_failures_keep_the_old_session_and_file()
    {
        using var harness = Parked();
        var good = harness.Session.Save("Good Railway");
        var path = harness.Store.PathFor(good.SaveId!.Value);
        var previous = File.ReadAllText(path);
        var before = harness.Session.CaptureTrainAuthority();

        AssertLoadKeeps(harness, previous.Replace("\"save_format_version\":5", "\"save_format_version\":1", StringComparison.Ordinal), WorldSaveStatus.UnknownFormat, before);
        AssertLoadKeeps(harness, previous.Replace("\"save_format_version\":5", "\"save_format_version\":99", StringComparison.Ordinal), WorldSaveStatus.UnknownFormat, before);
        AssertLoadKeeps(harness, previous.Replace("slice-4-straight-rail-v1", "future-rail-v9", StringComparison.Ordinal), WorldSaveStatus.UnknownGenerator, before);
        AssertLoadKeeps(harness, previous[..80], WorldSaveStatus.Truncated, before);

        var missingSegment = previous.Replace(
            "\"reference_segment_id\":" + harness.Session.Rails.Segments[0].Id.Value,
            "\"reference_segment_id\":999999",
            StringComparison.Ordinal);
        AssertLoadKeeps(harness, missingSegment, WorldSaveStatus.InvalidDelta, before);

        JsonObject Legacy(JsonArray commands, ulong sequence)
        {
            var node = JsonNode.Parse(previous)!.AsObject();
            node["save_format_version"] = WorldSaveDocument.PredecessorFormatVersion;
            node["scheduled_commands"] = commands;
            node["scheduled_sequence"] = sequence;
            return node;
        }

        var assemblyId = harness.Session.QueryTrainAssemblies().Single().Id.Value;
        var legacyCommand = new JsonArray(new JsonObject
        {
            ["at_step"] = 10,
            ["start"] = true,
            ["sequence"] = 0,
            ["assembly_id"] = assemblyId
        });
        AssertLoadKeeps(harness, Legacy(legacyCommand, 1).ToJsonString(), WorldSaveStatus.UnsupportedRailway, before);
        AssertLoadKeeps(harness, Legacy(new JsonArray(), harness.Session.TransactionCount + 1).ToJsonString(), WorldSaveStatus.InvalidDelta, before);

        var slice2Logger = LoggerSession.Create();
        var slice2Reloader = ContentFixtures.Create(ModFixtures.CorePlusCopper(), slice2Logger.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, slice2Reloader.Reload().Status);
        var slice2 = new VoxelDevelopmentSession(slice2Reloader.Catalog, VoxelWorldIdentity.Slice2, harness.Store);
        try
        {
            Assert.AreEqual(WorldSaveStatus.IncompatibleBase, slice2.Load(good.SaveId!.Value).Status);
        }
        finally
        {
            slice2.Detach();
            slice2Logger.Dispose();
        }

        File.WriteAllText(path, Legacy(new JsonArray(), 0).ToJsonString());
        Assert.AreEqual(WorldSaveStatus.Loaded, harness.Session.Load(good.SaveId!.Value).Status);
        Assert.AreEqual(before, harness.Session.CaptureTrainAuthority());
        File.WriteAllText(path, previous);

        Assert.AreEqual(previous, File.ReadAllText(path));
        Assert.AreEqual(WorldSaveStatus.Loaded, harness.Session.Load(good.SaveId!.Value).Status);
    }

    [TestMethod]
    public void Format_two_predecessor_without_railway_still_loads()
    {
        using var writer = Parked();
        var saved = writer.Session.Save("Format 2 Predecessor");
        var path = writer.Store.PathFor(saved.SaveId!.Value);
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["save_format_version"] = 2;
        root.Remove("railway");
        root.Remove("assemblies");
        root.Remove("scheduled_commands");
        root.Remove("scheduled_sequence");
        root.Remove("simulation_step");
        root.Remove("world_edits");
        File.WriteAllText(path, root.ToJsonString());

        using var reader = StaticTrainHarness.Create(writer.UserData, writer.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, reader.Session.Load(saved.SaveId!.Value).Status);
        Assert.IsFalse(reader.Session.HasRailway);
        Assert.AreEqual(0, reader.Session.QueryTrainAssemblies().Count);
        Assert.AreEqual(0UL, reader.Session.SimulationStep);
        Assert.IsTrue(reader.Session.World.VoxelCount > 0);
    }

    [TestMethod]
    public void Current_propulsion_adapts_saved_speed_and_rejects_illegal_content()
    {
        using var writer = Parked();
        Assert.IsTrue(writer.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(writer.Session.AdvanceSimulation(300).Succeeded);
        var saved = writer.Session.Save("Adapt");
        Assert.AreEqual(4.0, writer.Session.QueryTrainAssemblies().Single().Speed, Tolerance);

        WritePropulsion(writer, targetSpeed: 2, acceleration: 2);
        Assert.AreEqual(ContentReloadStatus.Committed, writer.Reloader.Reload().Status);
        using var adapted = StaticTrainHarness.Create(writer.UserData, writer.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, adapted.Session.Load(saved.SaveId!.Value).Status);
        var after = adapted.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(2.0, after.Speed, Tolerance);
        Assert.IsTrue(after.Running);
        Assert.AreEqual(96.0, after.Vehicles[0].FrontContact.X, Tolerance);

        var beforeBad = adapted.Session.CaptureTrainAuthority();
        WritePropulsion(adapted, targetSpeed: -1, acceleration: 2);
        Assert.AreEqual(ContentReloadStatus.Rejected, adapted.Reloader.Reload().Status);
        Assert.AreEqual(beforeBad, adapted.Session.CaptureTrainAuthority());
    }

    [TestMethod]
    public void High_speed_fixtures_load_only_through_formal_files()
    {
        using var catalog = HighSpeed();
        var x80 = TrainPersistenceFixtures.WriteHighSpeed(
            catalog.Session,
            catalog.Store,
            StaticTrainFixture.LocomotiveFrontContactX);
        var x140 = TrainPersistenceFixtures.WriteHighSpeed(catalog.Session, catalog.Store, 140);

        using var load80 = StaticTrainHarness.Create(catalog.UserData, catalog.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, load80.Session.Load(x80).Status);
        var at80 = load80.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(80.0, at80.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(600.0, at80.Speed, Tolerance);
        Assert.AreEqual(600.0, CurrentTarget(load80), Tolerance);
        Assert.IsTrue(at80.Running);
        Assert.AreEqual(catalog.Session.QueryTrainAssemblies().Single().Id, at80.Id);

        using var load140 = StaticTrainHarness.Create(catalog.UserData, catalog.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, load140.Session.Load(x140).Status);
        var at140 = load140.Session.QueryTrainAssemblies().Single();
        Assert.AreEqual(140.0, at140.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(600.0, at140.Speed, Tolerance);
        Assert.AreEqual(600.0, CurrentTarget(load140), Tolerance);
        Assert.IsTrue(at140.Running);
        Assert.AreEqual(at80.Id, at140.Id);
        Assert.AreEqual(at80.VehicleIds[0], at140.VehicleIds[0]);
        Assert.AreEqual(at80.VehicleIds[1], at140.VehicleIds[1]);

        using var defaultCatalog = StaticTrainHarness.Create(catalog.UserData, StaticTrainHarness.Package());
        Assert.AreEqual(WorldSaveStatus.Loaded, defaultCatalog.Session.Load(x80).Status);
        Assert.AreEqual(4.0, defaultCatalog.Session.QueryTrainAssemblies().Single().Speed, Tolerance);
        Assert.AreEqual(80.0, defaultCatalog.Session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact.X, Tolerance);
    }

    [TestMethod]
    public void Host_remainder_is_not_authoritative_and_reload_binds_the_restored_session()
    {
        using var writer = Parked();
        Assert.IsTrue(writer.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.AreEqual(0UL, writer.Session.CollectHostSteps(1.0 / 144.0));
        var fingerprint = WorldSaveAuthority.Fingerprint(writer.Session);
        var saved = writer.Session.Save("Host");
        using var reader = StaticTrainHarness.Create(writer.UserData, writer.Source);
        Assert.AreEqual(WorldSaveStatus.Loaded, reader.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(reader.Session));
        Assert.AreEqual(0UL, reader.Session.CollectHostSteps(0));
        reader.Reloader.SetWorldReloadAdapter(VoxelWorldReloadAdapter.For(reader.Session));
        WritePropulsion(reader, targetSpeed: 4, acceleration: 2);
        Assert.AreEqual(ContentReloadStatus.Committed, reader.Reloader.Reload().Status);
        Assert.AreEqual(saved.SaveId, reader.Session.SaveId);
        Assert.IsTrue(reader.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
    }

    private static void AdvanceTo(VoxelDevelopmentSession session, ulong step)
    {
        Assert.IsTrue(session.SimulationStep <= step);
        if (session.SimulationStep < step)
            Assert.IsTrue(session.AdvanceSimulation(step - session.SimulationStep).Succeeded);
        Assert.AreEqual(step, session.SimulationStep);
    }

    private static void Record(
        VoxelDevelopmentSession session,
        ulong step,
        Dictionary<string, string> hashes,
        Dictionary<ulong, TrainAuthoritySnapshot> states,
        string? suffix = null)
    {
        if (suffix is null)
        {
            hashes[step.ToString()] = WorldSaveAuthority.Fingerprint(session);
            states[step] = session.CaptureTrainAuthority();
        }
        else
            hashes[$"{step}-{suffix}"] = WorldSaveAuthority.Fingerprint(session);
    }

    private static void AssertLoadKeeps(
        StaticTrainHarness harness,
        string json,
        WorldSaveStatus expected,
        TrainAuthoritySnapshot before)
    {
        AssertLoadKeeps(harness, json, before, fingerprint: null, sequence: null, expected);
    }

    private static void AssertLoadKeeps(
        StaticTrainHarness harness,
        string json,
        TrainAuthoritySnapshot before,
        string? fingerprint,
        ulong? sequence,
        WorldSaveStatus expected = WorldSaveStatus.InvalidDelta)
    {
        var path = harness.Store.PathFor(harness.Session.SaveId!.Value);
        var previous = File.ReadAllText(path);
        File.WriteAllText(path, json);
        Assert.AreEqual(expected, harness.Session.Load(harness.Session.SaveId.Value).Status);
        Assert.AreEqual(before, harness.Session.CaptureTrainAuthority());
        if (fingerprint is not null)
            Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(harness.Session));
        if (sequence is not null)
            Assert.AreEqual(sequence.Value, harness.Session.NextEditId);
        File.WriteAllText(path, previous);
    }

    private static string Mutate(string json, Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        mutate(root);
        return root.ToJsonString();
    }

    private static void DuplicateVehiclesAcrossAssemblies(JsonObject root)
    {
        var first = root["assemblies"]![0]!.AsObject();
        var second = first.DeepClone().AsObject();
        second["id"] = first["id"]!.GetValue<ulong>() + 1000;
        root["assemblies"]!.AsArray().Add(second);
        var next = Math.Max(root["next_entity_id"]!.GetValue<ulong>(), second["id"]!.GetValue<ulong>() + 1);
        root["next_entity_id"] = next;
    }

    private static void AddSecondAssembly(JsonObject root)
    {
        const ulong offset = 1000;
        ulong Shift(JsonNode node) => node.GetValue<ulong>() + offset;
        var vehicles = root["vehicles"]!.AsArray();
        var clones = vehicles.Select(vehicle => vehicle!.DeepClone().AsObject()).ToArray();
        foreach (var vehicle in clones)
        {
            vehicle["id"] = Shift(vehicle["id"]!);
            vehicle["transform"]!["origin_x"] = vehicle["transform"]!["origin_x"]!.GetValue<double>() + 40.0;
            foreach (var part in vehicle["parts"]!.AsArray())
                part!["id"] = Shift(part["id"]!);
            vehicles.Add(vehicle);
        }

        var first = root["assemblies"]![0]!.AsObject();
        var second = first.DeepClone().AsObject();
        second["id"] = Shift(second["id"]!);
        second["distance"] = second["distance"]!.GetValue<double>() + 40.0;
        second["active_locomotive_id"] = Shift(second["active_locomotive_id"]!);
        var memberIds = second["vehicle_ids"]!.AsArray();
        for (var index = 0; index < memberIds.Count; index++)
            memberIds[index] = Shift(memberIds[index]!);
        foreach (var connection in second["connections"]!.AsArray())
        {
            connection!["from_vehicle_id"] = Shift(connection["from_vehicle_id"]!);
            connection["to_vehicle_id"] = Shift(connection["to_vehicle_id"]!);
            connection["from_part_id"] = Shift(connection["from_part_id"]!);
            connection["to_part_id"] = Shift(connection["to_part_id"]!);
        }

        root["assemblies"]!.AsArray().Add(second);
        root["next_entity_id"] = 2000;
    }

    private static StaticTrainHarness Parked()
    {
        var harness = StaticTrainHarness.Create();
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        return harness;
    }

    private static StaticTrainHarness HighSpeed()
    {
        var harness = StaticTrainHarness.Create();
        WritePropulsion(harness, targetSpeed: 600, acceleration: 2);
        Assert.AreEqual(ContentReloadStatus.Committed, harness.Reloader.Reload().Status);
        Assert.IsTrue(harness.Session.ConstructRails().Succeeded);
        Assert.IsTrue(harness.Session.AssembleStaticTrain().Succeeded);
        return harness;
    }

    private static double CurrentTarget(StaticTrainHarness harness)
    {
        Assert.IsTrue(harness.Reloader.Catalog.Current!.TryGet(StaticTrainFixture.PropulsionId, out var entry));
        return entry.Definition.Propulsion!.TargetSpeed;
    }

    private static void WritePropulsion(StaticTrainHarness harness, double targetSpeed, double acceleration)
    {
        harness.Source.AddVoxelFile("core", "voxels/propulsion.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good(
                "propulsion",
                "开发动力件",
                "切片 4.2 最小动力行为零件。能力由已注册 propulsion 行为推导，不表示燃料或通用资源。",
                value: 10,
                tagsJson: "[]",
                behaviorsJson:
                $"[{{ \"type\":\"propulsion\",\"target_speed\":{targetSpeed.ToString(CultureInfo.InvariantCulture)},\"acceleration\":{acceleration.ToString(CultureInfo.InvariantCulture)} }}]")));
    }
}
