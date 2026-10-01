using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Commands;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:world-editor")]
[TestCategory("scope:persistence")]
public sealed class WorldEditorWorkspaceTests
{
    [TestMethod]
    public void Same_name_saves_remain_distinguishable_with_save_time_versions_and_dependencies()
    {
        using var harness = WorldEditorHarness.Content();
        var workspace = Workspace(harness);
        Assert.AreEqual(0, workspace.ListSaves().Count);
        var first = SaveBlank(workspace, "Same name", seed: 7);
        var second = SaveBlank(workspace, "Same name", seed: 8);
        var older = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var newer = older.AddHours(1);
        File.SetLastWriteTimeUtc(harness.Store.PathFor(first), older);
        File.SetLastWriteTimeUtc(harness.Store.PathFor(second), newer);
        var entries = workspace.ListSaves();
        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual(second, entries[0].Id);
        Assert.AreEqual(first, entries[1].Id);
        Assert.AreEqual(newer, entries[0].LastSavedUtc);
        Assert.AreEqual(older, entries[1].LastSavedUtc);
        foreach (var entry in entries)
        {
            Assert.AreEqual("Same name", entry.Name);
            Assert.AreEqual(WorldSaveDocument.CurrentFormatVersion, entry.FormatVersion);
            Assert.IsTrue(entry.SourceVersion.Length > 0);
            Assert.IsTrue(entry.Dependencies.Any(item => item.Contains("core:ferrous-ore", StringComparison.Ordinal)));
            Assert.AreEqual(WorldSaveStatus.Loaded, entry.Recovery.Status);
        }
        Assert.IsNull(workspace.ListFailure);
    }

    [DataTestMethod]
    [DataRow("corrupt", WorldSaveStatus.Truncated)]
    [DataRow("dependency", WorldSaveStatus.MissingDefinition)]
    [DataRow("source", WorldSaveStatus.UnknownSource)]
    [DataRow("version", WorldSaveStatus.UnknownFormat)]
    [DataRow("invalid-version", WorldSaveStatus.UnknownFormat)]
    [DataRow("id", WorldSaveStatus.MismatchedId)]
    public void Rejected_save_stays_listed_with_reason_and_open_returns_no_session(string fault, WorldSaveStatus expected)
    {
        using var harness = WorldEditorHarness.Content();
        var workspace = Workspace(harness);
        var good = SaveBlank(workspace, "Good");
        var rejected = SaveBlank(workspace, "Rejected");
        var path = harness.Store.PathFor(rejected);
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        switch (fault)
        {
            case "corrupt": File.WriteAllText(path, "{"); break;
            case "dependency":
                node["external_definitions"]![0]!["id"] = "missing:voxel";
                break;
            case "source": node["world_source"]!["kind"] = "unsupported"; break;
            case "version": node["save_format_version"] = 999; break;
            case "invalid-version": node["save_format_version"] = "invalid"; break;
            case "id": node["id"] = Guid.NewGuid().ToString("D"); break;
        }
        if (fault != "corrupt")
            File.WriteAllText(path, node.ToJsonString());
        var original = File.ReadAllBytes(path);
        var entries = workspace.ListSaves();
        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual(WorldSaveStatus.Loaded, entries.Single(item => item.Id == good).Recovery.Status);
        var bad = entries.Single(item => item.Id == rejected);
        Assert.AreEqual(expected, bad.Recovery.Status);
        Assert.IsNotNull(bad.Recovery.Diagnostic);
        if (fault == "version")
        {
            Assert.AreEqual("Rejected", bad.Name);
            Assert.AreEqual(999, bad.FormatVersion);
        }
        var result = workspace.TryOpen(rejected, out var session);
        Assert.AreEqual(expected, result.Status);
        Assert.IsNotNull(result.Diagnostic);
        Assert.IsNull(session);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.AreEqual(WorldSaveStatus.Loaded, workspace.TryOpen(good, out var opened).Status);
        try
        {
            Assert.IsNotNull(opened);
            Assert.AreEqual(good, opened.SaveId);
            Assert.IsTrue(opened.SimulationPaused);
            Assert.AreEqual(1, opened.World.VoxelCount);
        }
        finally { opened?.Detach(); }
    }

    [TestMethod]
    public void Unreadable_save_does_not_hide_other_saves_and_candidates_are_not_published()
    {
        using var harness = WorldEditorHarness.Content();
        var workspace = Workspace(harness);
        var good = SaveBlank(workspace, "Readable");
        var locked = SaveBlank(workspace, "Locked");
        var unpublished = Guid.NewGuid();
        Directory.CreateDirectory(harness.Store.DirectoryFor(unpublished));
        File.WriteAllText(Path.Combine(harness.Store.DirectoryFor(unpublished), FileWorldSaveStore.CandidateFileName), "{");
        using var handle = new FileStream(harness.Store.PathFor(locked), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var entries = workspace.ListSaves();
        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual(WorldSaveStatus.Loaded, entries.Single(item => item.Id == good).Recovery.Status);
        var bad = entries.Single(item => item.Id == locked);
        Assert.AreEqual(WorldSaveStatus.ReadFailed, bad.Recovery.Status);
        Assert.IsNotNull(bad.Recovery.Diagnostic);
        Assert.AreEqual(WorldSaveStatus.ReadFailed, workspace.TryOpen(locked, out var session).Status);
        Assert.IsNull(session);
    }

    [TestMethod]
    public void Return_requires_a_choice_and_failed_save_or_cancel_keeps_current_world()
    {
        using var harness = WorldEditorHarness.Content();
        var workspace = Workspace(harness);
        BlankWorldSource.TryIdentity(9, 8, 7, 6, out var identity);
        var session = workspace.NewBlank(identity);
        try
        {
            var world = session.World;
            Assert.AreEqual(WorldChangeStatus.ChoiceRequired, session.TryChangeWorld(new(WorldChangeKind.ReturnToWorkspace, null)).Status);
            Assert.AreEqual(WorldChangeStatus.Cancelled, session.TryChangeWorld(new(WorldChangeKind.ReturnToWorkspace, WorldChangeChoice.Cancel)).Status);
            Assert.AreSame(world, session.World);
            File.WriteAllText(Path.Combine(harness.UserData, "saves"), "block writes");
            Assert.AreEqual(WorldChangeStatus.SaveFailed, session.TryChangeWorld(new(WorldChangeKind.ReturnToWorkspace, WorldChangeChoice.SaveThenSwitch)).Status);
            Assert.AreSame(world, session.World);
            Assert.IsNull(session.SaveId);
            Assert.IsTrue(session.HasUnsavedChanges);
            File.Delete(Path.Combine(harness.UserData, "saves"));
            Assert.AreEqual(WorldChangeStatus.Completed, session.TryChangeWorld(new(WorldChangeKind.ReturnToWorkspace, WorldChangeChoice.SaveThenSwitch, SaveDisplayName: "Named on return")).Status);
            Assert.IsNotNull(session.SaveId);
            Assert.AreEqual("Named on return", workspace.ListSaves().Single().Name);
            Assert.IsFalse(session.HasUnsavedChanges);
        }
        finally { session.Detach(); }
    }

    private static WorldEditorWorkspace Workspace(WorldEditorHarness harness) => new(harness.Catalog, harness.Composites, harness.Store);

    private static Guid SaveBlank(WorldEditorWorkspace workspace, string name, ulong seed = 42)
    {
        BlankWorldSource.TryIdentity(9, 8, 7, seed, out var identity);
        var session = workspace.NewBlank(identity);
        try
        {
            Assert.IsTrue(session.Add(new WorldVoxelCoord(2, 2, 2), WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity).Succeeded);
            var result = session.Save(name);
            Assert.AreEqual(WorldSaveStatus.Saved, result.Status);
            return result.SaveId!.Value;
        }
        finally { session.Detach(); }
    }
}
