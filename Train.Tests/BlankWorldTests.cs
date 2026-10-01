using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Commands;
using Train.Domain.Playground;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:playground")]
[TestCategory("scope:persistence")]
public sealed class BlankWorldTests
{
    [TestMethod]
    public void New_blank_world_is_empty_paused_unsaved_and_gets_identity_only_after_saving()
    {
        using var harness = PlaygroundHarness.Establish("straight-rail-train");
        var session = harness.Session;
        var commands = new VoxelConsoleCommands(session);
        StringAssert.StartsWith(commands.Execute(new[] { "new", "33", "17", "19", "42", "discard" }), "Voxel blank world.");
        Assert.AreEqual(new VoxelWorldBounds(33, 17, 19), session.Identity.Bounds);
        Assert.AreEqual(42UL, session.Identity.Seed);
        Assert.AreEqual("blank", session.WorldSource!.Kind);
        Assert.AreEqual(0, session.World.VoxelCount);
        Assert.AreEqual(0, session.Vehicles.Count);
        Assert.AreEqual(0, session.Rails.Segments.Count);
        Assert.AreEqual(0, session.QueryTrainAssemblies().Count);
        Assert.IsTrue(session.SimulationPaused);
        Assert.IsTrue(session.HasUnsavedChanges);
        Assert.IsNull(session.SaveId);
        Assert.AreEqual(WorldSaveStatus.InvalidName, session.Save("").Status);
        Assert.IsNull(session.SaveId);
        var saved = session.Save("Blank");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        Assert.IsNotNull(saved.SaveId);
        Assert.IsFalse(session.HasUnsavedChanges);
    }

    [TestMethod]
    public void Blank_source_round_trips_without_resource_definitions_and_fingerprint_covers_inputs()
    {
        using var harness = PlaygroundHarness.Content();
        var empty = new VoxelCatalog();
        empty.Publish(new VoxelCatalogSnapshot(1, new Dictionary<VoxelDefinitionId, VoxelCatalogEntry>()));
        Assert.IsTrue(BlankWorldSource.TryIdentity(1, 1, 1, ulong.MaxValue, out var identity));
        var session = BlankWorldSource.Establish(identity, empty, harness.Composites, harness.Store);
        harness.Attach(session);
        var saved = session.Save("Content independent");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        var bytes = File.ReadAllText(harness.Store.PathFor(saved.SaveId!.Value));
        Assert.IsTrue(WorldSaveCodec.TryDeserialize(bytes, out var decoded, out _));
        Assert.AreEqual("blank", decoded.Source!.Kind);
        Assert.AreEqual(1, decoded.Source.ConfigurationVersion);
        Assert.AreEqual(0, decoded.Source.ContentDependencies.Count);
        Assert.AreEqual(0, decoded.ExternalDefinitions.Count);
        Assert.AreEqual(session.WorldSource, decoded.Source);
        StringAssert.Contains(bytes, "\"source_version\":1");
        Assert.IsFalse(bytes.Contains("configuration_id", StringComparison.Ordinal));
        Assert.AreEqual(WorldSaveStatus.Loaded, session.Load(saved.SaveId.Value).Status);
        Assert.AreEqual(bytes, File.ReadAllText(harness.Store.PathFor(saved.SaveId.Value)));

        var fingerprint = session.WorldSource!.BaseFingerprint;
        Assert.AreEqual(64, fingerprint.Length);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            foreach (var variant in new[] { identity, identity with { Seed = 0 }, identity with { Bounds = new VoxelWorldBounds(2, 1, 1) } })
            {
                var candidate = BlankWorldSource.Establish(variant, harness.Catalog, harness.Composites, null);
                try
                {
                    if (variant == identity)
                        Assert.AreEqual(fingerprint, candidate.WorldSource!.BaseFingerprint);
                    else
                        Assert.AreNotEqual(fingerprint, candidate.WorldSource!.BaseFingerprint);
                }
                finally { candidate.Detach(); }
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [TestMethod]
    public void Blank_edits_steps_and_pending_input_mark_unsaved_and_resume_at_the_saved_boundary()
    {
        using var harness = PlaygroundHarness.Establish("straight-rail-train");
        var session = harness.Session;
        NewBlank(session);
        var saved = session.Save("Blank edits");
        session.ResumeSimulation();
        session.PauseSimulation();
        Assert.IsFalse(session.HasUnsavedChanges);
        var cell = new WorldVoxelCoord(17, 1, 1);
        Assert.IsTrue(session.Add(cell, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Succeeded);
        Assert.IsTrue(session.HasUnsavedChanges);
        Assert.IsTrue(session.Damage(new[] { new VoxelDamageTarget(cell, 25) }).Succeeded);
        Assert.AreEqual(WorldSaveStatus.Saved, session.Save().Status);
        Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        Assert.IsTrue(session.HasUnsavedChanges);
        Assert.AreEqual(WorldSaveStatus.Saved, session.Save().Status);
        Assert.AreEqual("Pending", session.SubmitEdit(new VoxelRemoveEdit(cell), 3).Status);
        Assert.IsTrue(session.HasUnsavedChanges);
        Assert.AreEqual(WorldSaveStatus.Saved, session.Save().Status);
        var source = session.WorldSource;
        var queue = session.CaptureEditQueue();
        Assert.IsTrue(session.AdvanceSimulation(2).Succeeded);
        Assert.IsFalse(session.Query(cell).IsOccupied);
        Assert.AreEqual(WorldSaveStatus.Loaded, session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(source, session.WorldSource);
        Assert.AreEqual(1UL, session.SimulationStep);
        Assert.IsTrue(session.SimulationPaused);
        Assert.IsFalse(session.HasUnsavedChanges);
        Assert.AreEqual(75, session.Query(cell).Voxel!.CurrentDurability);
        Assert.AreEqual(queue.NextEditId, session.CaptureEditQueue().NextEditId);
        Assert.AreEqual(queue.BoundarySequence, session.CaptureEditQueue().BoundarySequence);
        CollectionAssert.AreEqual(queue.Pending.ToArray(), session.CaptureEditQueue().Pending.ToArray());
        Assert.IsTrue(session.AdvanceSimulation(2).Succeeded);
        Assert.IsFalse(session.Query(cell).IsOccupied);
        Assert.AreEqual(0, session.QueryPendingEdits().Count);
    }

    [TestMethod]
    public void Non_editor_session_rejects_blank_creation_and_configuration_switch_without_saving()
    {
        using var harness = PlaygroundHarness.Content();
        var session = new VoxelDevelopmentSession(harness.Catalog, VoxelWorldIdentity.Slice3, harness.Store, harness.Composites);
        harness.Attach(session);
        Assert.AreEqual(WorldCommandStatus.Added, session.Add(
            new WorldVoxelCoord(1, 1, 1), VoxelDefinitionId.Parse("core:ferrous-ore"), VoxelOrientation.Identity).Status);
        var world = session.World;
        var commands = new VoxelConsoleCommands(session);
        foreach (var command in new[] { "new 8 8 8 1", "use straight-rail-empty" })
        foreach (var choice in new[] { " discard", " save", "", " cancel" })
        {
            var output = commands.Execute((command + choice).Split(' ', StringSplitOptions.RemoveEmptyEntries));
            StringAssert.StartsWith(output, "Voxel IncompatibleBase.");
            StringAssert.Contains(output, "stage base subject session relationship world_editor");
            Assert.AreSame(world, session.World);
            Assert.AreEqual(VoxelWorldIdentity.Slice3, session.Identity);
            Assert.IsNull(session.WorldSource);
            Assert.IsNull(session.SaveId);
            Assert.IsTrue(session.HasUnsavedChanges);
            Assert.IsFalse(session.SimulationPaused);
        }
        Assert.AreEqual(WorldChangeStatus.Completed, session.TryChangeWorld(
            new WorldChangeRequest(WorldChangeKind.Reset, WorldChangeChoice.Discard)).Status);
        Assert.AreEqual(WorldSaveStatus.Saved, session.Save("Slice after rejected change").Status);
        Assert.AreEqual(WorldSaveStatus.Loaded, session.Load(session.SaveId!.Value).Status);
    }

    [TestMethod]
    public void Invalid_blank_parameters_keep_world_identity_observation_and_unsaved_state_without_saving()
    {
        using var harness = PlaygroundHarness.Establish("straight-rail-train");
        var session = harness.Session;
        using var observer = ObserveVehicle(session);
        Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        session.ResumeSimulation();
        var world = session.World;
        var identity = session.Identity;
        var selection = observer.Selection;
        var target = observer.Target;
        var pose = observer.Pose;
        var commands = new VoxelConsoleCommands(session);
        foreach (var invalid in new[] { "0 17 19 42", "321 17 19 42", "33 -1 19 42", "33 33 19 42", "33 17 33 42", "33 17 19 -1", "33 17 19 18446744073709551616", "33.5 17 19 42", "33 17 19", "33 17 19 42 extra" })
        {
            StringAssert.Contains(commands.Execute(("new " + invalid + " save").Split(' ')), "relationship parameters");
            Assert.AreSame(world, session.World);
            Assert.AreEqual(identity, session.Identity);
            Assert.IsNull(session.SaveId);
            Assert.IsTrue(session.HasUnsavedChanges);
            Assert.IsFalse(session.SimulationPaused);
            Assert.AreEqual(selection, observer.Selection);
            Assert.AreEqual(target, observer.Target);
            Assert.AreEqual(pose, observer.Pose);
        }
        var badIdentity = identity with { GeneratorVersion = BlankWorldSource.GeneratorVersion, Bounds = new VoxelWorldBounds(321, 17, 19) };
        Assert.AreEqual(WorldChangeStatus.CandidateFailed, session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.NewBlank, WorldChangeChoice.SaveThenSwitch, BlankIdentity: badIdentity)).Status);
        Assert.IsNull(session.SaveId);
        Assert.AreSame(world, session.World);
        NewBlank(session, 320, 32, 32, 0);
        Assert.AreEqual(new VoxelWorldBounds(320, 32, 32), session.Identity.Bounds);
    }

    [TestMethod]
    public void Altered_legacy_slice_identity_is_rejected_before_world_replacement()
    {
        using var harness = PlaygroundHarness.Establish("straight-rail-empty");
        var legacy = new VoxelDevelopmentSession(harness.Catalog, VoxelWorldIdentity.Slice3, harness.Store, harness.Composites);
        WorldSaveResult saved;
        try { saved = legacy.Save("Canonical slice"); }
        finally { legacy.Detach(); }
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        var path = harness.Store.PathFor(saved.SaveId!.Value);
        var original = File.ReadAllText(path);
        var session = harness.Session;
        var world = session.World;
        var identity = session.Identity;
        var source = session.WorldSource;
        foreach (var mutation in new Action<JsonObject>[]
        {
            root => root["bounds"]!["x"] = 33,
            root => root["chunk_size"] = 8,
            root => root["seed"] = 43
        })
        {
            var root = JsonNode.Parse(original)!.AsObject();
            mutation(root);
            var invalid = root.ToJsonString();
            File.WriteAllText(path, invalid);
            var rejected = session.Load(saved.SaveId.Value);
            Assert.AreEqual(WorldSaveStatus.IncompatibleBase, rejected.Status);
            Assert.AreEqual("base", rejected.Diagnostic!.Stage);
            Assert.AreEqual("identity", rejected.Diagnostic.Subject);
            Assert.AreEqual("seed_bounds", rejected.Diagnostic.Relationship);
            Assert.AreSame(world, session.World);
            Assert.AreEqual(identity, session.Identity);
            Assert.AreEqual(source, session.WorldSource);
            Assert.IsNull(session.SaveId);
            Assert.IsTrue(session.SimulationPaused);
            Assert.AreEqual(invalid, File.ReadAllText(path));
        }
        File.WriteAllText(path, original);
        Assert.AreEqual(WorldSaveStatus.Loaded, session.Load(saved.SaveId.Value).Status);
        Assert.AreEqual(VoxelWorldIdentity.Slice3, session.Identity);
        Assert.IsNull(session.WorldSource);
    }

    [TestMethod]
    public void Unsupported_or_inconsistent_blank_source_keeps_the_current_world_and_observation()
    {
        using var harness = PlaygroundHarness.Establish("straight-rail-train");
        var session = harness.Session;
        var fixedSave = session.Save("Fixed");
        NewBlank(session);
        Assert.IsTrue(session.Add(new WorldVoxelCoord(17, 1, 1), VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Succeeded);
        var saved = session.Save("Blank");
        var path = harness.Store.PathFor(saved.SaveId!.Value);
        var original = File.ReadAllText(path);
        Assert.AreEqual(WorldSaveStatus.Loaded, session.Load(fixedSave.SaveId!.Value).Status);
        using var observer = ObserveVehicle(session);
        session.ResumeSimulation();
        var world = session.World;
        var identity = session.Identity;
        var source = session.WorldSource;
        var selection = observer.Selection;
        var target = observer.Target;
        var pose = observer.Pose;
        foreach (var mutation in new Action<JsonObject>[]
        {
            root => root["world_source"]!["source_version"] = 99,
            root => root["world_source"]!["kind"] = "unsupported",
            root => root["world_source"]!.AsObject().Remove("base_fingerprint"),
            root => root["world_source"]!["base_fingerprint"] = new string('0', 64),
            root => root["world_source"]!["content_dependencies"] = new JsonArray(JsonNode.Parse("{\"kind\":\"voxel\",\"id\":\"core:ferrous-ore\",\"semantic_revision\":\"ignored\"}")),
            root => root["bounds"]!["x"] = 321,
            root => root["seed"] = 43,
            root => root["chunk_size"] = 8,
            root => root["delta"]!["added"]![0]!["x"] = 33
        })
        {
            var root = JsonNode.Parse(original)!.AsObject();
            mutation(root);
            var invalid = root.ToJsonString();
            File.WriteAllText(path, invalid);
            var failure = session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.Load, WorldChangeChoice.Discard, SaveId: saved.SaveId));
            Assert.AreEqual(WorldChangeStatus.CandidateFailed, failure.Status);
            Assert.IsNotNull(failure.Diagnostic);
            Assert.AreSame(world, session.World);
            Assert.AreEqual(identity, session.Identity);
            Assert.AreEqual(source, session.WorldSource);
            Assert.AreEqual(fixedSave.SaveId, session.SaveId);
            Assert.IsFalse(session.SimulationPaused);
            Assert.AreEqual(selection, observer.Selection);
            Assert.AreEqual(target, observer.Target);
            Assert.AreEqual(pose, observer.Pose);
            Assert.AreEqual(invalid, File.ReadAllText(path));
        }
        File.WriteAllText(path, original);
        Assert.AreEqual(WorldSaveStatus.Loaded, session.Load(saved.SaveId.Value).Status);
        Assert.IsNull(observer.Selection);
        Assert.IsNull(observer.Target);
        Assert.AreEqual(PlaygroundCameraMode.Free, observer.Mode);
        Assert.AreEqual(pose, observer.Pose);
    }

    [TestMethod]
    public void Failed_first_write_keeps_blank_world_without_uuid_and_unsaved()
    {
        using var harness = PlaygroundHarness.Establish("straight-rail-train");
        NewBlank(harness.Session);
        var world = harness.Session.World;
        File.WriteAllText(Path.Combine(harness.UserData, "saves"), "block the save directory");
        Assert.AreEqual(WorldSaveStatus.WriteFailed, harness.Session.Save("First write").Status);
        Assert.IsNull(harness.Session.SaveId);
        Assert.IsTrue(harness.Session.HasUnsavedChanges);
        Assert.AreSame(world, harness.Session.World);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void Blank_fixed_and_legacy_saves_switch_both_ways_without_rewriting_files(int slice)
    {
        using var harness = PlaygroundHarness.Establish("straight-rail-train");
        var session = harness.Session;
        var fixedSave = session.Save("Fixed");
        var fixedIdentity = session.Identity;
        var fixedSource = session.WorldSource;
        var legacyIdentity = new[] { VoxelWorldIdentity.Slice1, VoxelWorldIdentity.Slice2, VoxelWorldIdentity.Slice3, VoxelWorldIdentity.Slice4 }[slice - 1];
        var legacy = new VoxelDevelopmentSession(harness.Catalog, legacyIdentity, harness.Store, harness.Composites);
        WorldSaveResult legacySave;
        try { legacySave = legacy.Save("Legacy"); }
        finally { legacy.Detach(); }
        Assert.AreEqual(WorldSaveStatus.Saved, legacySave.Status);
        NewBlank(session);
        var blankIdentity = session.Identity;
        var blankSource = session.WorldSource;
        var blankSave = session.Save("Blank");
        var files = new[] { blankSave.SaveId!.Value, fixedSave.SaveId!.Value, legacySave.SaveId!.Value }
            .ToDictionary(id => id, id => File.ReadAllText(harness.Store.PathFor(id)));
        foreach (var target in new[] { (fixedSave, fixedIdentity, fixedSource), (blankSave, blankIdentity, blankSource), (legacySave, legacyIdentity, (WorldSource?)null), (blankSave, blankIdentity, blankSource), (fixedSave, fixedIdentity, fixedSource), (legacySave, legacyIdentity, (WorldSource?)null) })
        {
            var changed = session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.Load, WorldChangeChoice.Discard, SaveId: target.Item1.SaveId));
            Assert.AreEqual(WorldChangeStatus.Completed, changed.Status);
            Assert.AreEqual(target.Item2, session.Identity);
            Assert.AreEqual(target.Item2, session.World.Identity);
            Assert.AreEqual(target.Item3, session.WorldSource);
            Assert.AreEqual(target.Item1.SaveId, session.SaveId);
            Assert.IsTrue(session.SimulationPaused);
            Assert.IsFalse(session.HasUnsavedChanges);
        }
        foreach (var file in files)
            Assert.AreEqual(file.Value, File.ReadAllText(harness.Store.PathFor(file.Key)));
    }

    private static void NewBlank(VoxelDevelopmentSession session, int x = 33, int y = 17, int z = 19, ulong seed = 42)
    {
        Assert.IsTrue(BlankWorldSource.TryIdentity(x, y, z, seed, out var identity));
        Assert.AreEqual(WorldChangeStatus.Completed, session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.NewBlank, WorldChangeChoice.Discard, BlankIdentity: identity)).Status);
    }

    private static PlaygroundObserver ObserveVehicle(VoxelDevelopmentSession session)
    {
        var observer = new PlaygroundObserver(session, new ObservationVector(10, 20, 30), new ObservationVector(0, 1, 0));
        Assert.AreEqual(PlaygroundObservationStatus.Selected, observer.Select(ObservedObject.Vehicle(session.Vehicles[0].Id)));
        Assert.AreEqual(PlaygroundObservationStatus.Following, observer.FollowSelection());
        return observer;
    }
}
