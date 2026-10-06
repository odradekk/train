using Train.Core.Content.Voxels;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:world-editor")]
[TestCategory("scope:persistence")]
public sealed class WorldEditorTransferTests
{
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void Export_preserves_never_saved_clean_and_dirty_sessions_without_pausing(bool saved, bool editAfterSave)
    {
        using var harness = WorldEditorHarness.Content();
        var workspace = new WorldEditorWorkspace(harness.Catalog, harness.Composites, harness.Store);
        BlankWorldSource.TryIdentity(8, 8, 8, 7, out var identity);
        var session = workspace.NewBlank(identity);
        harness.Attach(session);
        if (saved) Assert.IsTrue(session.Save("Current").Succeeded);
        if (editAfterSave) Assert.IsTrue(session.Add(new WorldVoxelCoord(1, 1, 1),
            WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity).Succeeded);
        session.ResumeSimulation();
        var id = session.SaveId;
        var name = session.DisplayName;
        var dirty = session.HasUnsavedChanges;
        var fingerprint = WorldSaveAuthority.Fingerprint(session);
        var path = Path.Combine(harness.UserData, "world.json");
        Assert.IsTrue(workspace.Export(session, path).Succeeded);
        Assert.AreEqual(id, session.SaveId);
        Assert.AreEqual(name, session.DisplayName);
        Assert.AreEqual(dirty, session.HasUnsavedChanges);
        Assert.IsFalse(session.SimulationPaused);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session));
        Assert.AreEqual(saved ? 1 : 0, workspace.ListSaves().Count);
        var imported = workspace.Import(path);
        Assert.IsTrue(imported.Succeeded);
        Assert.IsTrue(workspace.TryOpen(imported.SaveId!.Value, out var opened).Succeeded);
        try { Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(opened!)); }
        finally { opened?.Detach(); }
    }

    [DataTestMethod]
    [DataRow("corrupt", WorldSaveStatus.Truncated)]
    [DataRow("format", WorldSaveStatus.UnknownFormat)]
    [DataRow("source", WorldSaveStatus.UnknownSource)]
    [DataRow("source-version", WorldSaveStatus.IncompatibleBase)]
    [DataRow("base", WorldSaveStatus.IncompatibleBase)]
    [DataRow("dependency", WorldSaveStatus.MissingDefinition)]
    [DataRow("state", WorldSaveStatus.InvalidDelta)]
    public void Rejected_import_has_a_locatable_reason_and_keeps_current_work_files_and_list(string fault, WorldSaveStatus status)
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        var session = harness.Session;
        var id = session.Save("Current").SaveId!.Value;
        var existing = File.ReadAllBytes(harness.Store.PathFor(id));
        Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        session.ResumeSimulation();
        var fingerprint = WorldSaveAuthority.Fingerprint(session);
        var world = session.World;
        var node = JsonNode.Parse(File.ReadAllText(harness.Store.PathFor(id)))!;
        switch (fault)
        {
            case "format": node["save_format_version"] = 999; break;
            case "source": node["world_source"]!["kind"] = "unsupported"; break;
            case "source-version": node["world_source"]!["configuration_version"] = 999; break;
            case "base": node["world_source"]!["base_fingerprint"] = new string('0', 64); break;
            case "dependency": node["world_source"]!["content_dependencies"]![0]!["id"] = "missing:definition"; break;
            case "state": node["next_entity_id"] = 1; break;
        }
        var path = Path.Combine(harness.UserData, "rejected.json");
        File.WriteAllText(path, fault == "corrupt" ? "{" : node.ToJsonString());
        var input = File.ReadAllBytes(path);
        var workspace = new WorldEditorWorkspace(harness.Catalog, harness.Composites, harness.Store);
        var rejected = workspace.Import(path);
        Assert.AreEqual(status, rejected.Status);
        Assert.IsNotNull(rejected.Diagnostic);
        Assert.IsFalse(string.IsNullOrEmpty(rejected.Diagnostic.Subject));
        Assert.IsFalse(string.IsNullOrEmpty(rejected.Diagnostic.Relationship));
        Assert.AreEqual(id, workspace.ListSaves().Single().Id);
        Assert.AreSame(world, session.World);
        Assert.AreEqual(id, session.SaveId);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session));
        Assert.IsTrue(session.HasUnsavedChanges);
        Assert.IsFalse(session.SimulationPaused);
        CollectionAssert.AreEqual(existing, File.ReadAllBytes(harness.Store.PathFor(id)));
        CollectionAssert.AreEqual(input, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void Incompatible_world_voxel_import_identifies_the_definition_and_saved_position()
    {
        var definition = new VoxelDefinitionId("core", "import-rotated");
        var position = new WorldVoxelCoord(1, 2, 3);
        var package = StaticTrainHarness.Package();
        package.AddVoxelFile("core", "voxels/import-rotated.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good(definition.LocalId, "Rotated", "Rotated")));
        using var writer = WorldEditorHarness.Content(package);
        var exporting = new WorldEditorWorkspace(writer.Catalog, writer.Composites, writer.Store);
        BlankWorldSource.TryIdentity(8, 8, 8, 42, out var identity);
        var source = exporting.NewBlank(identity);
        writer.Attach(source);
        Assert.IsTrue(source.Add(position, definition, VoxelOrientation.Yaw90).Succeeded);
        var path = Path.Combine(writer.UserData, "rotated.json");
        Assert.IsTrue(exporting.Export(source, path).Succeeded);
        var bytes = File.ReadAllBytes(path);
        var restricted = StaticTrainHarness.Package();
        restricted.AddVoxelFile("core", "voxels/import-rotated.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good(definition.LocalId, "Rotated", "Rotated", orientations: "none")));
        using var reader = WorldEditorHarness.Content(restricted);
        var workspace = new WorldEditorWorkspace(reader.Catalog, reader.Composites, reader.Store);
        var current = workspace.NewBlank(identity);
        reader.Attach(current);
        var fingerprint = WorldSaveAuthority.Fingerprint(current);

        var rejected = workspace.Import(path);

        Assert.AreEqual(WorldSaveStatus.InvalidDelta, rejected.Status);
        Assert.IsNotNull(rejected.Diagnostic);
        Assert.AreEqual(definition.Value, rejected.Diagnostic.Subject);
        StringAssert.Contains(rejected.Diagnostic.Relationship, position.ToString());
        Assert.AreEqual(0, workspace.ListSaves().Count);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(current));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void Incompatible_train_dependency_import_identifies_the_changed_definition_and_part()
    {
        using var writer = StaticTrainHarness.Create();
        Assert.IsTrue(writer.Session.ConstructRails().Succeeded);
        Assert.IsTrue(writer.Session.AssembleStaticTrain().Succeeded);
        Assert.IsTrue(writer.Session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(writer.Session.AdvanceSimulation(1).Succeeded);
        var definition = StaticTrainFixture.PropulsionId;
        var part = writer.Session.Vehicles.SelectMany(vehicle => vehicle.Parts)
            .First(item => item.DefinitionId == definition);
        var exporting = new WorldEditorWorkspace(writer.Reloader.Catalog, writer.Reloader.Composites, writer.Store);
        var path = Path.Combine(writer.UserData, "train.json");
        Assert.IsTrue(exporting.Export(writer.Session, path).Succeeded);
        Assert.IsTrue(exporting.Import(path).Succeeded, "The source-free moving save must be valid before its dependency changes.");
        var package = StaticTrainHarness.Package();
        var definitionJson = JsonNode.Parse(CompositeFixtures.BuiltinText("voxels", "propulsion.voxels.json"))!;
        definitionJson["voxels"]![0]!["behaviors"] = new JsonArray();
        package.AddVoxelFile("core", "voxels/propulsion.voxels.json", definitionJson.ToJsonString());
        using var reader = WorldEditorHarness.Content(package);
        var workspace = new WorldEditorWorkspace(reader.Catalog, reader.Composites, reader.Store);

        var rejected = workspace.Import(path);

        Assert.AreEqual(WorldSaveStatus.InvalidDelta, rejected.Status);
        Assert.IsNotNull(rejected.Diagnostic);
        Assert.AreEqual(definition.Value, rejected.Diagnostic.Subject);
        StringAssert.Contains(rejected.Diagnostic.Relationship, part.Cell.ToString());
        Assert.AreEqual(0, workspace.ListSaves().Count);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Missing_or_incompatible_installed_content_is_rejected_with_its_definition_location(bool missing)
    {
        using var writer = WorldEditorHarness.Content();
        var exporting = new WorldEditorWorkspace(writer.Catalog, writer.Composites, writer.Store);
        BlankWorldSource.TryIdentity(64, 32, 32, 42, out var identity);
        var source = exporting.NewBlank(identity);
        writer.Attach(source);
        var vehicle = source.Manufacture(HollowShellFixture.DefinitionId, CompositeTransform.Translation(10, 8, 8)).VehicleId!.Value;
        HollowShellRefit.ApplySuccess(source, vehicle);
        var path = Path.Combine(writer.UserData, "incompatible.json");
        Assert.IsTrue(exporting.Export(source, path).Succeeded);
        var package = StaticTrainHarness.Package();
        package.AddVoxelFile("core", "voxels/refit-marker.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good(missing ? "other-marker" : "refit-marker", "Marker", "Marker", value: 10, tagsJson: "[]", orientations: "none")));
        using var reader = WorldEditorHarness.Content(package);
        var workspace = new WorldEditorWorkspace(reader.Catalog, reader.Composites, reader.Store);
        var current = workspace.NewBlank(identity);
        reader.Attach(current);
        var world = current.World;
        var bytes = File.ReadAllBytes(path);
        var rejected = workspace.Import(path);
        Assert.AreEqual(missing ? WorldSaveStatus.MissingDefinition : WorldSaveStatus.InvalidDelta, rejected.Status);
        Assert.IsNotNull(rejected.Diagnostic);
        Assert.AreEqual(HollowShellRefit.MarkerId.Value, rejected.Diagnostic.Subject);
        Assert.AreEqual(0, workspace.ListSaves().Count);
        Assert.AreSame(world, current.World);
        Assert.IsNull(current.SaveId);
        Assert.IsTrue(current.HasUnsavedChanges);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void Export_overwrite_requires_permission_and_write_failure_keeps_existing_target_and_work()
    {
        using var harness = WorldEditorHarness.Establish(WorldEditorConfigurations.StraightRailTrainId);
        var session = harness.Session;
        var id = session.Save("Current").SaveId!.Value;
        var original = File.ReadAllBytes(harness.Store.PathFor(id));
        Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        session.ResumeSimulation();
        var fingerprint = WorldSaveAuthority.Fingerprint(session);
        var world = session.World;
        var workspace = new WorldEditorWorkspace(harness.Catalog, harness.Composites, harness.Store);
        var path = Path.Combine(harness.UserData, "existing.json");
        File.WriteAllText(path, "existing target");
        var bytes = File.ReadAllBytes(path);
        Assert.AreEqual(WorldSaveStatus.OverwriteConfirmationRequired, workspace.Export(session, path).Status);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        using (var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.AreEqual(WorldSaveStatus.WriteFailed, workspace.Export(session, path, overwrite: true).Status);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        Assert.AreEqual(WorldSaveStatus.WriteFailed, workspace.Export(session, harness.Store.PathFor(id), overwrite: true).Status);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(harness.Store.PathFor(id)));
        Assert.AreSame(world, session.World);
        Assert.AreEqual(id, session.SaveId);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session));
        Assert.IsTrue(session.HasUnsavedChanges);
        Assert.IsFalse(session.SimulationPaused);
        Assert.IsTrue(workspace.Export(session, path, overwrite: true).Succeeded);
        Assert.IsTrue(workspace.Import(path).Succeeded);
    }

    [TestMethod]
    public void Unreadable_input_and_failed_local_publication_leave_no_imported_entry()
    {
        using var harness = WorldEditorHarness.Content();
        var workspace = new WorldEditorWorkspace(harness.Catalog, harness.Composites, harness.Store);
        BlankWorldSource.TryIdentity(8, 8, 8, 7, out var identity);
        var session = workspace.NewBlank(identity);
        harness.Attach(session);
        var path = Path.Combine(harness.UserData, "input.json");
        Assert.IsTrue(workspace.Export(session, path).Succeeded);
        var bytes = File.ReadAllBytes(path);
        using (var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.AreEqual(WorldSaveStatus.ReadFailed, workspace.Import(path).Status);
        File.WriteAllText(Path.Combine(harness.UserData, FileWorldSaveStore.SavesFolder), "blocks local publication");
        Assert.AreEqual(WorldSaveStatus.WriteFailed, workspace.Import(path).Status);
        Assert.AreEqual(0, workspace.ListSaves().Count);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        Assert.IsNull(session.SaveId);
        Assert.IsTrue(session.HasUnsavedChanges);
    }

    [TestMethod]
    public void Export_then_import_preserves_committed_state_and_creates_independent_same_name_copies()
    {
        using var harness = WorldEditorHarness.Content();
        var workspace = new WorldEditorWorkspace(harness.Catalog, harness.Composites, harness.Store);
        BlankWorldSource.TryIdentity(64, 32, 32, 42, out var identity);
        var session = workspace.NewBlank(identity);
        harness.Attach(session);
        var manufactured = session.Manufacture(HollowShellFixture.DefinitionId, CompositeTransform.Translation(10, 8, 8));
        Assert.IsTrue(manufactured.Succeeded);
        var vehicle = manufactured.VehicleId!.Value;
        HollowShellRefit.ApplySuccess(session, vehicle);
        var local = session.Vehicles.Single().SaveLocalDefinition!;
        var original = session.Save("Same name").SaveId!.Value;
        var originalBytes = File.ReadAllBytes(harness.Store.PathFor(original));
        Assert.IsTrue(session.SubmitEdit(new VoxelAddEdit(new WorldVoxelCoord(1, 2, 1),
            WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity), 3).Accepted);
        Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        session.ResumeSimulation();
        var fingerprint = WorldSaveAuthority.Fingerprint(session);
        var world = session.World;
        var path = Path.Combine(harness.UserData, "export.json");

        Assert.IsTrue(workspace.Export(session, path).Succeeded);
        Assert.AreEqual(original, session.SaveId);
        Assert.AreEqual("Same name", session.DisplayName);
        Assert.IsTrue(session.HasUnsavedChanges);
        Assert.IsFalse(session.SimulationPaused);
        var exportedBytes = File.ReadAllBytes(path);
        var imported = workspace.Import(path);
        var another = workspace.Import(path);
        Assert.IsTrue(imported.Succeeded);
        Assert.IsTrue(another.Succeeded);
        CollectionAssert.AllItemsAreUnique(new[] { original, imported.SaveId!.Value, another.SaveId!.Value });
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(harness.Store.PathFor(original)));
        CollectionAssert.AreEqual(exportedBytes, File.ReadAllBytes(path));
        Assert.AreSame(world, session.World);
        Assert.AreEqual(original, session.SaveId);
        Assert.IsTrue(session.HasUnsavedChanges);
        Assert.IsFalse(session.SimulationPaused);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session));
        var entries = workspace.ListSaves();
        Assert.AreEqual(3, entries.Count);
        Assert.IsTrue(entries.All(entry => entry.Name == "Same name" && entry.Recovery.Succeeded));
        Assert.IsTrue(workspace.TryOpen(imported.SaveId.Value, out var copy).Succeeded);
        Assert.IsTrue(workspace.TryOpen(another.SaveId.Value, out var second).Succeeded);
        try
        {
            foreach (var opened in new[] { copy!, second! })
            {
                Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(opened));
                Assert.AreEqual(vehicle, opened.Vehicles.Single().Id);
                var embedded = opened.Vehicles.Single().SaveLocalDefinition!;
                Assert.AreEqual(local.Id, embedded.Id);
                Assert.AreEqual(local.ArchetypeId, embedded.ArchetypeId);
                CollectionAssert.AreEqual(local.Voxels.Select(item => (item.Cell, item.DefinitionId, item.Orientation)).ToArray(),
                    embedded.Voxels.Select(item => (item.Cell, item.DefinitionId, item.Orientation)).ToArray());
                Assert.AreEqual(1UL, opened.SimulationStep);
                Assert.AreEqual(1, opened.QueryPendingEdits().Count);
            }
            Assert.IsTrue(copy!.Refit(vehicle, CompositeRefitOperation.Add(HollowShellRefit.Moved,
                HollowShellRefit.MarkerId, VoxelOrientation.Identity)).Succeeded);
            var changed = WorldSaveAuthority.Fingerprint(copy);
            Assert.AreNotEqual(fingerprint, changed);
            Assert.IsTrue(copy.Save().Succeeded);
            Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(second!));
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(harness.Store.PathFor(original)));
            CollectionAssert.AreEqual(exportedBytes, File.ReadAllBytes(path));
            Assert.IsTrue(workspace.TryOpen(imported.SaveId.Value, out var reopened).Succeeded);
            try { Assert.AreEqual(changed, WorldSaveAuthority.Fingerprint(reopened!)); }
            finally { reopened?.Detach(); }
        }
        finally { copy?.Detach(); second?.Detach(); }
    }
}
