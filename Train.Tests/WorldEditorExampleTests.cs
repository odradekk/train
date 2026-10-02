using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Commands;
using Train.Domain.Voxels;
using Train.Domain.WorldEditor;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:world-editor")]
[TestCategory("scope:persistence")]
public sealed class WorldEditorExampleTests
{
    [TestMethod]
    public void Distributed_example_restores_the_world_boundary_and_pending_input_made_by_formal_edits()
    {
        using var harness = WorldEditorHarness.Content(ExampleContent());
        var workspace = Workspace(harness);
        var entry = workspace.ListExamples().Single();
        Assert.IsTrue(entry.Recovery.Succeeded);
        Assert.IsNotNull(entry.Document);
        Assert.IsTrue(entry.Example.Purpose.Length > 0);
        Assert.IsTrue(Version.TryParse(entry.Example.ApplicableSinceVersion, out _));
        Assert.IsTrue(entry.Document.ExternalDefinitions.Count > 0);
        foreach (var dependency in entry.Document.ExternalDefinitions)
        {
            Assert.IsTrue(harness.Catalog.Current!.TryGet(dependency.DefinitionId, out var definition));
            Assert.AreEqual(definition.SemanticRevision, dependency.SemanticRevision);
        }

        // This independent construction proves the distributed file represents formal edits of a blank source.
        BlankWorldSource.TryIdentity(16, 8, 16, 42, out var identity);
        var made = workspace.NewBlank(identity);
        var restored = workspace.NewBlank(identity);
        try
        {
            var voxel = new VoxelDefinitionId("core", "ferrous-ore");
            for (var x = 2; x <= 4; x++)
                Assert.IsTrue(made.Add(new(x, 1, 2), voxel, VoxelOrientation.Identity).Succeeded);
            Assert.IsTrue(made.SubmitEdit(new VoxelAddEdit(new(5, 1, 2), voxel, VoxelOrientation.Identity), 3).Accepted);
            Assert.IsTrue(made.AdvanceSimulation(1).Succeeded);
            Assert.IsTrue(restored.LoadDocument(entry.Document).Succeeded);
            Assert.AreEqual(identity, restored.Identity);
            Assert.AreEqual(WorldSaveAuthority.Fingerprint(made), WorldSaveAuthority.Fingerprint(restored));
            Assert.AreEqual(1UL, restored.SimulationStep);
            Assert.AreEqual(1, restored.QueryPendingEdits().Count);
            Assert.IsTrue(restored.SimulationPaused);
        }
        finally { made.Detach(); restored.Detach(); }
    }

    [TestMethod]
    public void Example_copy_is_unsaved_until_first_save_and_never_modifies_distribution()
    {
        using var harness = WorldEditorHarness.Content(ExampleContent());
        var workspace = Workspace(harness);
        var entry = workspace.ListExamples().Single();
        var original = ReadExample(entry.Example.Path);
        Assert.IsTrue(workspace.TryCreateExampleCopy(entry.Example, out var copy).Succeeded);
        try
        {
            Assert.IsNotNull(copy);
            Assert.IsNull(copy.SaveId);
            Assert.IsTrue(copy.SimulationPaused && copy.HasUnsavedChanges);
            Assert.AreEqual(0, workspace.ListSaves().Count);
            var fingerprint = WorldSaveAuthority.Fingerprint(copy);
            var saved = copy.Save();
            Assert.IsTrue(saved.Succeeded);
            Assert.IsNotNull(saved.SaveId);
            Assert.AreNotEqual(entry.Document!.Id, saved.SaveId.Value);
            Assert.IsFalse(copy.HasUnsavedChanges);
            Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(copy));
            Assert.AreEqual(saved.SaveId, workspace.ListSaves().Single().Id);
            Assert.AreEqual(original, ReadExample(entry.Example.Path));
            Assert.IsTrue(copy.AdvanceSimulation(2).Succeeded);
            Assert.AreEqual(4, copy.World.VoxelCount);
            Assert.AreEqual(0, copy.QueryPendingEdits().Count);
            Assert.IsTrue(workspace.TryCreateExampleCopy(entry.Example, out var again).Succeeded);
            try
            {
                Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(again!));
                Assert.IsNull(again!.SaveId);
            }
            finally { again?.Detach(); }
        }
        finally { copy?.Detach(); }
    }

    [TestMethod]
    public void Separate_distribution_list_never_discovers_user_saves_or_fault_fixtures()
    {
        using var harness = WorldEditorHarness.Content(ExampleContent());
        var workspace = Workspace(harness);
        var examples = workspace.ListExamples();
        Assert.IsTrue(examples.Count > 0);
        Assert.AreEqual(0, workspace.ListSaves().Count);
        var corruptId = Guid.NewGuid();
        Directory.CreateDirectory(harness.Store.DirectoryFor(corruptId));
        File.WriteAllText(harness.Store.PathFor(corruptId), "{");
        var unchanged = workspace.ListExamples();
        CollectionAssert.AreEqual(examples.Select(item => item.Example).ToArray(), unchanged.Select(item => item.Example).ToArray());
        Assert.IsTrue(unchanged.All(item => item.Recovery.Succeeded));
        Assert.IsFalse(workspace.ListSaves().Single().Recovery.Succeeded);
    }

    [TestMethod]
    public void Missing_dependency_rejects_example_without_publishing_a_copy()
    {
        // A valid catalog without the example's required definition must use the same restore rejection as saves.
        var source = ContentFixtures.CorePackage();
        source.AddVoxelFile("core", "voxels/stone.voxels.json", ContentFixtures.GoodsFile(ContentFixtures.Good("stone", "Stone", "Stone")));
        ContentFixtures.AddSquareImages(source, "iron.png");
        ContentFixtures.AddExampleCity(source, resources: new[] { "core:stone" });
        using var missing = WorldEditorHarness.Content(source);
        var workspace = Workspace(missing);
        var entry = workspace.ListExamples().Single();
        Assert.AreEqual(WorldSaveStatus.MissingDefinition, entry.Recovery.Status);
        Assert.IsNotNull(entry.Recovery.Diagnostic);
        Assert.AreEqual(WorldSaveStatus.MissingDefinition, workspace.TryCreateExampleCopy(entry.Example, out var copy).Status);
        Assert.IsNull(copy);
        Assert.AreEqual(0, workspace.ListSaves().Count);
    }

    private static WorldEditorWorkspace Workspace(WorldEditorHarness harness) =>
        new(harness.Catalog, harness.Composites, harness.Store, ReadExample);

    internal static string ReadExample(string path)
    {
        return File.ReadAllText(Path.Combine(CompositeFixtures.RepositoryRoot(), path["res://".Length..]));
    }

    internal static MemoryContentSource ExampleContent()
    {
        var source = ContentFixtures.ValidFerrousOre();
        source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", CompositeFixtures.BuiltinText("voxels", "ferrous-ore.voxels.json"));
        ContentFixtures.AddSquareImages(source, "voxels/ferrous-ore/icon.png", "voxels/ferrous-ore/top.png",
            "voxels/ferrous-ore/side.png", "voxels/ferrous-ore/bottom.png");
        return source;
    }
}
