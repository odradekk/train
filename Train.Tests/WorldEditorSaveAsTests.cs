using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:world-editor")]
[TestCategory("scope:persistence")]
public sealed class WorldEditorSaveAsTests
{
    [TestMethod]
    public void Save_as_publishes_a_new_file_before_switching_the_current_target()
    {
        using var harness = WorldEditorHarness.Content();
        var store = new ObservedStore(harness.Store);
        Assert.AreEqual(WorldEditorEstablishStatus.Established,
            WorldEditorSession.TryEstablish(WorldEditorConfigurations.StraightRailTrainId,
                harness.Catalog, harness.Composites, store, out var session));
        harness.Attach(session!);
        var original = session!.Save("Original").SaveId!.Value;
        var bytes = File.ReadAllBytes(harness.Store.PathFor(original));
        Assert.IsTrue(session.SubmitEdit(new VoxelAddEdit(new WorldVoxelCoord(1, 2, 1),
            WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity), 3).Accepted);
        Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        session.ResumeSimulation();
        var fingerprint = WorldSaveAuthority.Fingerprint(session);
        var world = session.World;
        store.BeforeWrite = id =>
        {
            Assert.AreNotEqual(original, id);
            Assert.AreEqual(original, session.SaveId);
            Assert.AreEqual("Original", session.DisplayName);
            Assert.IsTrue(session.HasUnsavedChanges);
        };
        store.AfterWrite = id =>
        {
            Assert.IsTrue(harness.Store.TryRead(id, out var json, out _));
            Assert.IsTrue(WorldSaveCodec.TryDeserialize(json, out var document, out _));
            Assert.AreEqual(id, document.Id);
            Assert.AreEqual(original, session.SaveId);
            Assert.IsTrue(session.HasUnsavedChanges);
        };
        var saved = session.SaveAs("Copy");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        Assert.AreNotEqual(original, saved.SaveId);
        Assert.AreEqual(saved.SaveId, session.SaveId);
        Assert.AreEqual("Copy", session.DisplayName);
        Assert.IsFalse(session.HasUnsavedChanges);
        Assert.IsFalse(session.SimulationPaused);
        Assert.AreSame(world, session.World);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(harness.Store.PathFor(original)));
        var workspace = new WorldEditorWorkspace(harness.Catalog, harness.Composites, harness.Store);
        var entries = workspace.ListSaves();
        CollectionAssert.AreEquivalent(new[] { original, saved.SaveId!.Value }, entries.Select(item => item.Id).ToArray());
        Assert.IsTrue(entries.All(item => item.Recovery.Succeeded));
        Assert.AreEqual(WorldSaveStatus.Loaded, workspace.TryOpen(saved.SaveId.Value, out var reopened).Status);
        try
        {
            Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(reopened!));
            Assert.AreEqual(session.WorldSource, reopened!.WorldSource);
            Assert.AreEqual(1UL, reopened.SimulationStep);
            Assert.AreEqual(1, reopened.QueryPendingEdits().Count);
        }
        finally { reopened?.Detach(); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Failed_publication_keeps_identity_name_dirty_state_and_existing_files(bool dirty)
    {
        using var harness = WorldEditorHarness.Content();
        var store = new ObservedStore(harness.Store);
        Assert.AreEqual(WorldEditorEstablishStatus.Established,
            WorldEditorSession.TryEstablish(WorldEditorConfigurations.StraightRailTrainId,
                harness.Catalog, harness.Composites, store, out var session));
        harness.Attach(session!);
        var original = session!.Save("Original").SaveId!.Value;
        var bytes = File.ReadAllBytes(harness.Store.PathFor(original));
        if (dirty) Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        var fingerprint = WorldSaveAuthority.Fingerprint(session);
        Guid target = default;
        store.BeforeWrite = id =>
        {
            target = id;
            Directory.CreateDirectory(harness.Store.PathFor(id)); // A directory blocks atomic publication after candidate writing.
        };
        var failed = session.SaveAs("Copy");
        Assert.AreEqual(WorldSaveStatus.WriteFailed, failed.Status);
        Assert.AreEqual(original, session.SaveId);
        Assert.AreEqual("Original", session.DisplayName);
        Assert.AreEqual(dirty, session.HasUnsavedChanges);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(harness.Store.PathFor(original)));
        Assert.IsFalse(File.Exists(Path.Combine(harness.Store.DirectoryFor(target), FileWorldSaveStore.CandidateFileName)));
        var workspace = new WorldEditorWorkspace(harness.Catalog, harness.Composites, harness.Store);
        Assert.AreEqual(original, workspace.ListSaves().Single().Id);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("First save")]
    public void Save_as_on_a_never_saved_world_behaves_like_first_save(string? name)
    {
        using var harness = WorldEditorHarness.Content();
        var workspace = new WorldEditorWorkspace(harness.Catalog, harness.Composites, harness.Store);
        BlankWorldSource.TryIdentity(32, 16, 16, 42, out var identity);
        var session = workspace.NewBlank(identity);
        harness.Attach(session);
        var expectedName = name ?? session.DisplayName;
        var fingerprint = WorldSaveAuthority.Fingerprint(session);
        Assert.IsNull(session.SaveId);
        Assert.IsTrue(session.HasUnsavedChanges);
        var saved = session.SaveAs(name);
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        Assert.IsNotNull(session.SaveId);
        Assert.AreEqual(expectedName, session.DisplayName);
        Assert.IsFalse(session.HasUnsavedChanges);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(session));
        Assert.AreEqual(session.SaveId, workspace.ListSaves().Single().Id);
        Assert.AreEqual(saved.SaveId, session.Save().SaveId);
    }

    [TestMethod]
    public void Copies_preserve_entity_and_embedded_content_identities_and_evolve_independently()
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
        Assert.IsNotNull(local);
        Assert.IsTrue(session.SubmitEdit(new VoxelAddEdit(new WorldVoxelCoord(1, 2, 1),
            WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity), 3).Accepted);
        Assert.IsTrue(session.AdvanceSimulation(1).Succeeded);
        var fingerprint = WorldSaveAuthority.Fingerprint(session);
        var original = session.Save("Original").SaveId!.Value;
        var bytes = File.ReadAllBytes(harness.Store.PathFor(original));
        var copy = session.SaveAs().SaveId!.Value;
        Assert.AreNotEqual(original, copy);
        Assert.AreEqual("Original", session.DisplayName);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(harness.Store.PathFor(original)));
        Assert.AreEqual(WorldSaveStatus.Loaded, workspace.TryOpen(original, out var first).Status);
        Assert.AreEqual(WorldSaveStatus.Loaded, workspace.TryOpen(copy, out var second).Status);
        try
        {
            foreach (var opened in new[] { first!, second! })
            {
                Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(opened));
                Assert.AreEqual(vehicle, opened.Vehicles.Single().Id);
                var embedded = opened.Vehicles.Single().SaveLocalDefinition!;
                Assert.AreEqual(local.Id, embedded.Id);
                AssertEmbeddedLayout(local, embedded);
                Assert.AreEqual(local.ArchetypeId, embedded.ArchetypeId);
            }
            Assert.IsTrue(first!.Refit(vehicle, CompositeRefitOperation.Add(HollowShellRefit.Moved,
                HollowShellRefit.MarkerId, VoxelOrientation.Identity)).Succeeded);
            var firstFingerprint = WorldSaveAuthority.Fingerprint(first);
            var firstLocal = first.Vehicles.Single().SaveLocalDefinition!;
            Assert.AreEqual(original, first.Save().SaveId);
            Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(second!));
            Assert.IsTrue(second!.Refit(vehicle, CompositeRefitOperation.Rotate(
                second.QueryComposite(vehicle)!.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id,
                VoxelOrientation.Identity)).Succeeded);
            Assert.IsTrue(second.AdvanceSimulation(3).Succeeded);
            var secondFingerprint = WorldSaveAuthority.Fingerprint(second);
            var secondLocal = second.Vehicles.Single().SaveLocalDefinition!;
            var originalAfterEdit = File.ReadAllBytes(harness.Store.PathFor(original));
            Assert.AreEqual(copy, second.Save().SaveId);
            CollectionAssert.AreEqual(originalAfterEdit, File.ReadAllBytes(harness.Store.PathFor(original)));
            Assert.AreEqual(firstFingerprint, WorldSaveAuthority.Fingerprint(first));
            Assert.AreNotEqual(firstFingerprint, secondFingerprint);
            foreach (var (id, expected, definition) in new[] { (original, firstFingerprint, firstLocal), (copy, secondFingerprint, secondLocal) })
            {
                Assert.AreEqual(WorldSaveStatus.Loaded, workspace.TryOpen(id, out var reopened).Status);
                try
                {
                    Assert.AreEqual(expected, WorldSaveAuthority.Fingerprint(reopened!));
                    Assert.AreEqual(local.Id, reopened!.Vehicles.Single().SaveLocalDefinition!.Id);
                    AssertEmbeddedLayout(definition, reopened.Vehicles.Single().SaveLocalDefinition!);
                }
                finally { reopened?.Detach(); }
            }
        }
        finally { first?.Detach(); second?.Detach(); }
    }

    private static void AssertEmbeddedLayout(CompositeDefinition expected, CompositeDefinition actual) =>
        CollectionAssert.AreEqual(expected.Voxels.Select(voxel => (voxel.Cell, voxel.DefinitionId, voxel.Orientation)).ToArray(),
            actual.Voxels.Select(voxel => (voxel.Cell, voxel.DefinitionId, voxel.Orientation)).ToArray());

    private sealed class ObservedStore(FileWorldSaveStore files) : IWorldSaveStore
    {
        public Action<Guid>? BeforeWrite { get; set; }
        public Action<Guid>? AfterWrite { get; set; }
        public bool TryRead(Guid id, out string json, out WorldSaveStatus status) => files.TryRead(id, out json, out status);
        public bool TryWrite(Guid id, string json, out WorldSaveStatus status)
        {
            BeforeWrite?.Invoke(id);
            var written = files.TryWrite(id, json, out status);
            if (written) AfterWrite?.Invoke(id);
            return written;
        }
    }
}
