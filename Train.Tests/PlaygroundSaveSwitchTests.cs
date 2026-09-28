using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Playground;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
public sealed class PlaygroundSaveSwitchTests
{
    [TestMethod]
    public void Established_base_fingerprint_is_stable_across_culture_and_is_not_a_generator_claim()
    {
        using var first = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var fingerprint = first.Session.WorldSource!.BaseFingerprint;
        Assert.AreEqual(64, fingerprint.Length);
        Assert.IsFalse(first.Session.HasUnsavedChanges);

        var culture = CultureInfo.CurrentCulture;
        var ui = CultureInfo.CurrentUICulture;
        try
        {
            var french = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentCulture = french;
            CultureInfo.CurrentUICulture = french;
            using var second = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
            Assert.AreEqual(fingerprint, second.Session.WorldSource!.BaseFingerprint);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = ui;
        }

        var saved = first.Session.Save("Fingerprint");
        var json = File.ReadAllText(first.Store.PathFor(saved.SaveId!.Value));
        StringAssert.Contains(json, "\"save_format_version\":5");
        StringAssert.Contains(json, $"\"base_fingerprint\":\"{fingerprint}\"");
        Assert.IsFalse(json.Contains("\"generation\"", StringComparison.Ordinal));
        Assert.IsFalse(first.Session.HasUnsavedChanges);
    }

    [TestMethod]
    public void Unsaved_changes_cover_edits_steps_and_pending_input_but_not_observation()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        Assert.IsFalse(session.HasUnsavedChanges);
        session.ResumeSimulation();
        session.PauseSimulation();
        Assert.IsFalse(session.HasUnsavedChanges);
        using (var observer = new PlaygroundObserver(session, new ObservationVector(10, 20, 30), new ObservationVector(0, 1, 0)))
        {
            Assert.AreEqual(PlaygroundObservationStatus.Selected, observer.Select(ObservedObject.Segment(session.Rails.Segments[0].Id)));
            Assert.IsFalse(session.HasUnsavedChanges);
        }

        Assert.AreEqual(WorldCommandStatus.Added, session.Add(new WorldVoxelCoord(1, 2, 1), new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Identity).Status);
        Assert.IsTrue(session.HasUnsavedChanges);
        Assert.AreEqual(WorldSaveStatus.Saved, session.Save("Edited").Status);
        Assert.IsFalse(session.HasUnsavedChanges);

        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        var step = session.SimulationStep;
        Assert.IsTrue(session.HasUnsavedChanges);
        var saved = session.Save("Stepped");
        Assert.IsFalse(session.HasUnsavedChanges);
        Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        Assert.IsTrue(session.HasUnsavedChanges);

        var reader = new VoxelDevelopmentSession(playground.Catalog, session.Identity, playground.Store, playground.Composites);
        try
        {
            Assert.AreEqual(WorldSaveStatus.Loaded, reader.Load(saved.SaveId!.Value).Status);
            Assert.AreEqual(step, reader.SimulationStep);
            Assert.IsTrue(reader.SimulationPaused);
            Assert.AreEqual(session.WorldSource, reader.WorldSource);
            Assert.IsFalse(reader.HasUnsavedChanges);
        }
        finally
        {
            reader.Detach();
        }

        Assert.AreEqual("Pending", session.SubmitEdit(new VoxelRemoveEdit(new WorldVoxelCoord(1, 2, 1)), session.SimulationStep + 1).Status);
        Assert.IsTrue(session.HasUnsavedChanges);
    }

    [TestMethod]
    public void Protected_reset_keeps_the_configuration_and_requires_a_choice_while_dirty()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        var saved = session.Save("Keep");
        Assert.AreEqual(WorldCommandStatus.Added, session.Add(new WorldVoxelCoord(1, 2, 1), new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Identity).Status);
        var fingerprint = WorldSaveAuthority.Fingerprint(session);

        var required = session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.Reset, null));
        Assert.AreEqual(WorldChangeStatus.ChoiceRequired, required.Status);
        Assert.IsTrue(required.UnsavedChanges);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session));

        var cancelled = session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.Reset, WorldChangeChoice.Cancel));
        Assert.AreEqual(WorldChangeStatus.Cancelled, cancelled.Status);
        Assert.IsTrue(session.HasUnsavedChanges);
        Assert.AreEqual(saved.SaveId, session.SaveId);

        var discarded = session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.Reset, WorldChangeChoice.Discard));
        Assert.AreEqual(WorldChangeStatus.Completed, discarded.Status);
        Assert.AreEqual(PlaygroundConfigurations.StraightRailTrainId, session.WorldSource!.ConfigurationId);
        Assert.AreEqual(64, session.WorldSource.BaseFingerprint.Length);
        Assert.IsTrue(session.SimulationPaused);
        Assert.AreEqual(0UL, session.SimulationStep);
        Assert.AreEqual(saved.SaveId, session.SaveId);
        Assert.IsFalse(session.HasUnsavedChanges);
        Assert.IsFalse(session.Query(new WorldVoxelCoord(1, 2, 1)).IsOccupied);
    }

    [TestMethod]
    public void Switch_clears_save_identity_and_candidate_failure_keeps_the_session()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        session.Save("Train");
        Assert.AreEqual(WorldCommandStatus.Added, session.Add(new WorldVoxelCoord(1, 2, 1), new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Identity).Status);
        var fingerprint = WorldSaveAuthority.Fingerprint(session);

        var failed = session.TryChangeWorld(new WorldChangeRequest(
            WorldChangeKind.SwitchConfiguration,
            WorldChangeChoice.Discard,
            "not-a-config"));
        Assert.AreEqual(WorldChangeStatus.CandidateFailed, failed.Status);
        Assert.AreEqual(WorldSaveStatus.IncompatibleBase, failed.CandidateStatus);
        Assert.AreEqual("unknown_configuration", failed.Diagnostic!.Relationship);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session));
        Assert.AreEqual(PlaygroundConfigurations.StraightRailTrainId, session.WorldSource!.ConfigurationId);
        Assert.IsTrue(session.HasUnsavedChanges);

        var savedThenFailed = session.TryChangeWorld(new WorldChangeRequest(
            WorldChangeKind.SwitchConfiguration,
            WorldChangeChoice.SaveThenSwitch,
            "not-a-config"));
        Assert.AreEqual(WorldChangeStatus.CandidateFailed, savedThenFailed.Status);
        Assert.AreEqual(WorldSaveStatus.Saved, savedThenFailed.SaveStatus);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session));
        Assert.IsFalse(session.HasUnsavedChanges);

        var switched = session.TryChangeWorld(new WorldChangeRequest(
            WorldChangeKind.SwitchConfiguration,
            null,
            PlaygroundConfigurations.StraightRailEmptyId));
        Assert.AreEqual(WorldChangeStatus.Completed, switched.Status);
        Assert.AreEqual(PlaygroundConfigurations.StraightRailEmptyId, session.WorldSource!.ConfigurationId);
        Assert.IsTrue(session.SimulationPaused);
        Assert.AreEqual(0, session.QueryTrainAssemblies().Count);
        Assert.IsNull(session.SaveId);
        Assert.IsFalse(session.HasUnsavedChanges);
    }

    [TestMethod]
    public void Save_failure_cancels_the_switch_and_leaves_the_previous_file()
    {
        using var playground = PlaygroundHarness.Content();
        var files = new FileWorldSaveStore(playground.UserData);
        var store = new FailStore(files);
        Assert.AreEqual(
            PlaygroundEstablishStatus.Established,
            PlaygroundSession.TryEstablish(PlaygroundConfigurations.StraightRailTrainId, playground.Catalog, playground.Composites, store, out var session));
        playground.Attach(session!);
        var saved = session!.Save("Held");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        var path = files.PathFor(saved.SaveId!.Value);
        var bytes = File.ReadAllBytes(path);
        Assert.AreEqual(WorldCommandStatus.Added, session.Add(new WorldVoxelCoord(1, 2, 1), new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Identity).Status);
        store.Fail = true;

        var result = session.TryChangeWorld(new WorldChangeRequest(
            WorldChangeKind.SwitchConfiguration,
            WorldChangeChoice.SaveThenSwitch,
            PlaygroundConfigurations.StraightRailEmptyId));

        Assert.AreEqual(WorldChangeStatus.SaveFailed, result.Status);
        Assert.AreEqual(WorldSaveStatus.WriteFailed, result.SaveStatus);
        Assert.AreEqual("write", result.Diagnostic!.Relationship);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        Assert.AreEqual(PlaygroundConfigurations.StraightRailTrainId, session.WorldSource!.ConfigurationId);
        Assert.IsTrue(session.HasUnsavedChanges);
        Assert.IsTrue(session.Query(new WorldVoxelCoord(1, 2, 1)).IsOccupied);
    }

    [TestMethod]
    public void Faulted_save_then_switch_does_not_repair_or_replace_the_session()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        var probe = new WorldVoxelCoord(1, 2, 1);
        Assert.AreEqual(WorldCommandStatus.Added, session.Add(probe, new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Identity).Status);
        session.EditFaultProbe = record =>
        {
            if (record.Edit is VoxelRemoveEdit)
                throw new InvalidOperationException("injected invariant failure");
        };
        Assert.AreEqual("Pending", session.SubmitEdit(new VoxelRemoveEdit(probe), 3).Status);
        Assert.AreEqual(TrainMotionStatus.SimulationFaulted, session.AdvanceSimulation(10).Status);
        var fingerprint = WorldSaveAuthority.Fingerprint(session);
        Assert.IsNotNull(session.Fault);

        var saved = session.TryChangeWorld(new WorldChangeRequest(
            WorldChangeKind.SwitchConfiguration,
            WorldChangeChoice.SaveThenSwitch,
            PlaygroundConfigurations.StraightRailEmptyId));
        Assert.AreEqual(WorldChangeStatus.SaveFailed, saved.Status);
        Assert.AreEqual(WorldSaveStatus.SimulationFaulted, saved.SaveStatus);
        Assert.AreEqual("fault", saved.Diagnostic!.Relationship);
        Assert.IsNotNull(session.Fault);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session));
        Assert.AreEqual(PlaygroundConfigurations.StraightRailTrainId, session.WorldSource!.ConfigurationId);

        var discarded = session.TryChangeWorld(new WorldChangeRequest(
            WorldChangeKind.SwitchConfiguration,
            WorldChangeChoice.Discard,
            PlaygroundConfigurations.StraightRailEmptyId));
        Assert.AreEqual(WorldChangeStatus.Completed, discarded.Status);
        Assert.IsNull(session.Fault);
        Assert.AreEqual(PlaygroundConfigurations.StraightRailEmptyId, session.WorldSource!.ConfigurationId);
        Assert.IsTrue(session.SimulationPaused);
        Assert.IsFalse(session.HasUnsavedChanges);
    }

    [TestMethod]
    public void Load_reports_base_dependency_and_reference_failures_without_replacing_the_session()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        var assembly = session.QueryTrainAssemblies().Single();
        var coupler = assembly.Connections[0];
        Assert.AreEqual(
            "Pending",
            session.SubmitEdit(
                new VehicleDamageEdit(new[] { new CompositeDamageTarget(coupler.FromVehicleId, coupler.FromCell, 1) }),
                1).Status);
        var saved = session.Save("Diagnose");
        var path = playground.Store.PathFor(saved.SaveId!.Value);
        var original = File.ReadAllText(path);
        var before = WorldSaveAuthority.Fingerprint(session);

        File.WriteAllText(path, Mutate(original, root =>
        {
            var fingerprint = root["world_source"]!["base_fingerprint"]!.GetValue<string>();
            root["world_source"]!["base_fingerprint"] = fingerprint[..^1] + (fingerprint[^1] == 'A' ? "B" : "A");
        }));
        var tampered = session.Load(saved.SaveId!.Value);
        Assert.AreEqual(WorldSaveStatus.IncompatibleBase, tampered.Status);
        Assert.AreEqual("base", tampered.Diagnostic!.Stage);
        Assert.AreEqual("base_fingerprint", tampered.Diagnostic.Relationship);
        Assert.AreEqual(before, WorldSaveAuthority.Fingerprint(session));

        File.WriteAllText(path, Mutate(original, root => root["world_source"]!["configuration_version"] = 99));
        var version = session.Load(saved.SaveId.Value);
        Assert.AreEqual(WorldSaveStatus.IncompatibleBase, version.Status);
        Assert.AreEqual("configuration_version", version.Diagnostic!.Relationship);
        Assert.AreEqual(before, WorldSaveAuthority.Fingerprint(session));

        File.WriteAllText(path, Mutate(original, root => root["world_source"]!["configuration_id"] = "not-a-config"));
        var unknown = session.Load(saved.SaveId.Value);
        Assert.AreEqual(WorldSaveStatus.IncompatibleBase, unknown.Status);
        Assert.AreEqual("unknown_configuration", unknown.Diagnostic!.Relationship);
        Assert.AreEqual(before, WorldSaveAuthority.Fingerprint(session));

        File.WriteAllText(path, Mutate(original, root =>
            root["world_edits"]!["pending"]![0]!["edit"]!["targets"]![0]!["vehicle_id"] = 999_999));
        var reference = session.Load(saved.SaveId.Value);
        Assert.AreEqual(WorldSaveStatus.InvalidDelta, reference.Status);
        Assert.AreEqual("reference", reference.Diagnostic!.Stage);
        Assert.AreEqual("entity_id", reference.Diagnostic.Relationship);
        Assert.AreEqual(before, WorldSaveAuthority.Fingerprint(session));

        File.WriteAllText(path, original);
        Assert.AreEqual(WorldSaveStatus.Loaded, session.Load(saved.SaveId.Value).Status);
    }

    [TestMethod]
    public void Parameter_order_and_revision_text_adapt_without_rejecting_a_matching_base()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        var saved = session.Save("Order");
        var path = playground.Store.PathFor(saved.SaveId!.Value);
        var original = File.ReadAllText(path);

        File.WriteAllText(path, Mutate(original, root =>
        {
            var parameters = root["world_source"]!["effective_parameters"]!.AsArray();
            var copy = parameters.Select(item => item!.ToJsonString()).Reverse().ToArray();
            parameters.Clear();
            foreach (var item in copy)
                parameters.Add(JsonNode.Parse(item));
        }));
        var reordered = session.Load(saved.SaveId!.Value);
        Assert.AreEqual(WorldSaveStatus.Loaded, reordered.Status);
        Assert.IsFalse(session.HasUnsavedChanges);
        Assert.AreEqual(PlaygroundConfigurations.StraightRailTrainId, session.WorldSource!.ConfigurationId);

        File.WriteAllText(path, Mutate(original, root =>
        {
            var revision = root["world_source"]!["content_dependencies"]![0]!["semantic_revision"]!.GetValue<string>();
            root["world_source"]!["content_dependencies"]![0]!["semantic_revision"] = revision[..^1] + (revision[^1] == '0' ? "1" : "0");
        }));
        var adapted = session.Load(saved.SaveId.Value);
        Assert.AreEqual(WorldSaveStatus.Loaded, adapted.Status);
        var adaptations = adapted.Adaptations ?? Array.Empty<WorldSaveAdaptation>();
        Assert.IsTrue(adaptations.Any(item => item.Relationship == "semantic_revision"));
        Assert.IsFalse(adaptations.Any(item => item.ChangesPersistentState));
        Assert.IsFalse(session.HasUnsavedChanges);

        File.WriteAllText(path, Mutate(original, root => root["world_source"]!.AsObject().Remove("base_fingerprint")));
        var legacy = session.Load(saved.SaveId.Value);
        Assert.AreEqual(WorldSaveStatus.Loaded, legacy.Status);
        Assert.AreEqual("", session.WorldSource!.BaseFingerprint);
        Assert.IsTrue(session.SimulationPaused);
    }

    [TestMethod]
    public void Current_propulsion_clamps_saved_speed_and_leaves_the_world_unsaved()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(session.AdvanceSimulation(300).Succeeded);
        var contact = session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact.X;
        var saved = session.Save("Speed");
        playground.Source.AddVoxelFile("core", "voxels/propulsion.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good(
                "propulsion",
                "开发动力件",
                "切片 4.2 最小动力行为零件。能力由已注册 propulsion 行为推导，不表示燃料或通用资源。",
                value: 10,
                tagsJson: "[]",
                behaviorsJson: "[{ \"type\":\"propulsion\",\"target_speed\":2,\"acceleration\":2 }]")));
        Assert.AreEqual(ContentReloadStatus.Committed, playground.Reloader.Reload().Status);

        var reader = new VoxelDevelopmentSession(playground.Catalog, session.Identity, playground.Store, playground.Composites);
        try
        {
            var loaded = reader.Load(saved.SaveId!.Value);
            Assert.AreEqual(WorldSaveStatus.Loaded, loaded.Status);
            var assembly = reader.QueryTrainAssemblies().Single();
            Assert.AreEqual(2, assembly.Speed, 1e-8);
            Assert.IsTrue(assembly.Running);
            Assert.AreEqual(contact, assembly.Vehicles[0].FrontContact.X, 1e-8);
            Assert.IsTrue(loaded.Adaptations!.Any(item => item.Relationship == "speed"));
            Assert.IsTrue(reader.HasUnsavedChanges);
            Assert.IsNull(reader.Fault);
            Assert.AreEqual(TrainHaltReason.None, assembly.HaltReason);
        }
        finally
        {
            reader.Detach();
        }
    }

    [TestMethod]
    public void Console_reset_and_use_share_the_unsaved_choice()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        var commands = new VoxelConsoleCommands(session);
        Assert.AreEqual(WorldCommandStatus.Added, session.Add(new WorldVoxelCoord(1, 2, 1), new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Identity).Status);

        StringAssert.Contains(commands.Execute(new[] { "reset" }), "Unsaved changes");
        Assert.IsTrue(session.Query(new WorldVoxelCoord(1, 2, 1)).IsOccupied);
        StringAssert.Contains(commands.Execute(new[] { "reset", "cancel" }), "Cancelled");
        Assert.IsTrue(session.HasUnsavedChanges);
        StringAssert.StartsWith(commands.Execute(new[] { "reset", "discard" }), "Voxel world reset.");
        Assert.AreEqual(PlaygroundConfigurations.StraightRailTrainId, session.WorldSource!.ConfigurationId);
        Assert.IsFalse(session.HasUnsavedChanges);

        StringAssert.Contains(commands.Execute(new[] { "use", PlaygroundConfigurations.StraightRailEmptyId }), "straight-rail-empty");
        Assert.IsTrue(session.SimulationPaused);
        Assert.AreEqual(0, session.QueryTrainAssemblies().Count);
    }

    [TestMethod]
    public void Slice_reset_without_a_source_still_discards_immediately_once_chosen()
    {
        using var playground = PlaygroundHarness.Content();
        var session = new VoxelDevelopmentSession(playground.Catalog, VoxelWorldIdentity.Slice1, playground.Store, playground.Composites);
        playground.Attach(session);
        Assert.IsFalse(session.HasUnsavedChanges);
        Assert.AreEqual(WorldCommandStatus.Removed, session.Remove(new WorldVoxelCoord(4, 4, 4)).Status);
        var clean = session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.Reset, null));
        Assert.AreEqual(WorldChangeStatus.ChoiceRequired, clean.Status);
        var discarded = session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.Reset, WorldChangeChoice.Discard));
        Assert.AreEqual(WorldChangeStatus.Completed, discarded.Status);
        Assert.IsNull(session.WorldSource);
        Assert.IsFalse(session.SimulationPaused);
        Assert.IsTrue(session.Query(new WorldVoxelCoord(4, 4, 4)).IsOccupied);
        Assert.IsFalse(session.HasUnsavedChanges);
    }

    private static string Mutate(string json, Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        mutate(root);
        return root.ToJsonString();
    }

    private sealed class FailStore : IWorldSaveStore
    {
        private readonly FileWorldSaveStore _inner;

        public FailStore(FileWorldSaveStore inner) => _inner = inner;

        public bool Fail { get; set; }

        public bool TryRead(Guid id, out string json, out WorldSaveStatus status) =>
            _inner.TryRead(id, out json, out status);

        public bool TryWrite(Guid id, string json, out WorldSaveStatus status)
        {
            if (Fail)
            {
                status = WorldSaveStatus.WriteFailed;
                return false;
            }

            return _inner.TryWrite(id, json, out status);
        }
    }
}
