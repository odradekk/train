// Core 内容发布的功能例子：从两格蓝图的输入验证一个完整批次。
// 预期属性来自 ContentReloadExamples 的手算数据。
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Tests.Core.Logging;

namespace Train.Tests.Core.Content;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:content")]
[TestCategory("scope:composites")]
public sealed class ContentPublicationTests
{
    [TestMethod]
    public void Publication_updates_dependencies_together_and_preserves_captured_snapshots()
    {
        using var session = LoggerSession.Create();
        var source = PairSource();
        var reloader = new ContentReloader(source, session.Logger);
        var changes = new List<ContentChange>();
        reloader.Changed += change =>
        {
            Assert.AreSame(change.Current, reloader.Current);
            changes.Add(change);
        };

        AssertResult(ContentReloadExamples.FirstPublish, reloader.Reload());
        var original = reloader.Current!;
        Assert.AreEqual(1, changes.Count);
        Assert.IsNull(changes[0].Previous);
        Assert.AreSame(original, changes[0].Current);

        AssertResult(ContentReloadExamples.Unchanged, reloader.Reload());
        Assert.AreSame(original, reloader.Current);
        Assert.AreEqual(1, changes.Count);

        source.AddDefinitionFile("core", "structure.voxels.json", ContentReloadExamples.UpdatedStructureFile);
        AssertResult(ContentReloadExamples.DependencyUpdated, reloader.Reload());
        var updated = reloader.Current!;
        Assert.AreEqual(2, changes.Count);
        Assert.AreSame(original, changes[1].Previous);
        Assert.AreSame(updated, changes[1].Current);
        Assert.AreEqual(3d, updated.Voxels[ContentReloadExamples.StructureId].Definition.Mass);
        AssertSnapshot(ContentReloadExamples.InitialContent, original);
        Assert.AreEqual(2d, original.Voxels[ContentReloadExamples.StructureId].Definition.Mass);
        Assert.AreEqual(original.Blueprints[ContentReloadExamples.BlueprintId].SemanticRevision,
            updated.Blueprints[ContentReloadExamples.BlueprintId].SemanticRevision);
        Assert.AreNotEqual(original.Blueprints[ContentReloadExamples.BlueprintId].PublicationFingerprint,
            updated.Blueprints[ContentReloadExamples.BlueprintId].PublicationFingerprint);

        source.AddDefinitionFile("core", "structure.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("structure", "结构", "无效质量", mass: 0)));
        AssertResult(ContentReloadExamples.InvalidBuiltIn with { Snapshot = ContentReloadExamples.UpdatedContent },
            reloader.Reload());
        Assert.AreSame(updated, reloader.Current);
        Assert.AreEqual(2, changes.Count);
    }

    [TestMethod]
    public void Missing_required_package_rejects_without_a_first_snapshot()
    {
        using var session = LoggerSession.Create();
        var reloader = new ContentReloader(new MemoryContentSource(), session.Logger);
        AssertResult(ContentReloadExamples.InitialRejected, reloader.Reload());
        Assert.IsNull(reloader.Current);
    }

    [TestMethod]
    public void Removing_all_files_publishes_one_empty_batch_after_initialization()
    {
        using var session = LoggerSession.Create();
        var source = new SwitchableContentSource(PairSource());
        var reloader = new ContentReloader(source, session.Logger);
        AssertResult(ContentReloadExamples.FirstPublish, reloader.Reload());
        source.Current = ContentFixtures.CorePackage();

        AssertResult(ContentReloadExamples.AllDefinitionsRemoved, reloader.Reload());
        AssertResult(ContentReloadExamples.Unchanged with { Snapshot = ContentReloadExamples.EmptyContent },
            reloader.Reload());
    }

    [TestMethod]
    public void Collision_changes_publish_without_changing_core_semantics_or_blueprint_attributes()
    {
        using var session = LoggerSession.Create();
        var source = PairSource();
        var reloader = new ContentReloader(source, session.Logger);
        AssertResult(ContentReloadExamples.FirstPublish, reloader.Reload());
        var before = reloader.Current!;
        source.AddDefinitionFile("core", "structure.voxels.json", ContentReloadExamples.CollisionStructureFile);

        AssertResult(ContentReloadExamples.CollisionUpdated, reloader.Reload());
        var after = reloader.Current!;
        var oldVoxel = before.Voxels[ContentReloadExamples.StructureId];
        var newVoxel = after.Voxels[ContentReloadExamples.StructureId];
        var oldBlueprint = before.Blueprints[ContentReloadExamples.BlueprintId];
        var newBlueprint = after.Blueprints[ContentReloadExamples.BlueprintId];
        Assert.AreEqual(ContentReloadExamples.CollisionRevisionChanges,
            (oldVoxel.SemanticRevision != newVoxel.SemanticRevision,
             oldVoxel.PublicationFingerprint != newVoxel.PublicationFingerprint,
             oldBlueprint.SemanticRevision != newBlueprint.SemanticRevision,
             oldBlueprint.PublicationFingerprint != newBlueprint.PublicationFingerprint));
    }

    [TestMethod]
    public void Invalid_optional_package_is_removed_as_a_whole()
    {
        using var session = LoggerSession.Create();
        const string mod = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
        const string root = "memory://mods/broken/resources";
        var source = PairSource();
        source.AddMod("broken", mod);
        source.AddDefinitionFile(mod, "bad.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("bad", "无效", "无效质量", mass: 0)), root);
        source.AddDefinitionFile(mod, "valid.composites.json", ContentReloadExamples.BlueprintFile, root);
        var reloader = new ContentReloader(source, session.Logger);

        AssertResult(ContentReloadExamples.OptionalPackageIsolated, reloader.Reload());
        Assert.IsFalse(reloader.Current!.Blueprints.Keys.Any(id => id.Namespace == mod));
    }

    [TestMethod]
    public void Required_package_failure_is_determined_by_ownership_instead_of_namespace()
    {
        using var session = LoggerSession.Create();
        var source = PairSource();
        var reloader = new ContentReloader(source, session.Logger);
        AssertResult(ContentReloadExamples.FirstPublish, reloader.Reload());
        var previous = reloader.Current;
        const string requiredNamespace = "additional-builtin";
        const string root = "memory://additional-builtin/resources";
        source.AddPackage(requiredNamespace, root);
        source.AddDefinitionFile(requiredNamespace, "bad.composites.json",
            ContentReloadExamples.BlueprintFile.Replace("core:structure", "core:missing", StringComparison.Ordinal), root);

        var rejected = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status);
        Assert.AreSame(previous, rejected.Snapshot);
        Assert.AreSame(previous, reloader.Current);
        Assert.IsTrue(rejected.Diagnostics.Any(item =>
            item.Code == ContentDiagnosticCodes.UnresolvedVoxelReference && item.Severity == ContentDiagnosticSeverity.Error));
    }

    [TestMethod]
    public void Reload_during_publication_notification_is_rejected_with_the_published_snapshot()
    {
        using var session = LoggerSession.Create();
        var reloader = new ContentReloader(PairSource(), session.Logger);
        ContentReloadResult? reentrant = null;
        reloader.Changed += _ => reentrant = reloader.Reload();

        var published = reloader.Reload();

        AssertResult(ContentReloadExamples.FirstPublish, published);
        Assert.IsNotNull(reentrant);
        AssertResult(ContentReloadExamples.ConcurrentOrReentrant, reentrant);
        Assert.AreSame(published.Snapshot, reentrant.Snapshot);
        Assert.AreSame(published.Snapshot, reloader.Current);
        AssertResult(ContentReloadExamples.Unchanged, reloader.Reload());
    }

    [TestMethod]
    public void Unexpected_source_exception_preserves_content_and_releases_reload_ownership()
    {
        using var session = LoggerSession.Create();
        var source = new SwitchableContentSource(PairSource());
        var reloader = new ContentReloader(source, session.Logger);
        AssertResult(ContentReloadExamples.FirstPublish, reloader.Reload());
        var previous = reloader.Current;
        source.Current = new ThrowingContentSource();

        Assert.ThrowsException<IOException>(() => reloader.Reload());
        Assert.AreSame(previous, reloader.Current);
        source.Current = PairSource();
        AssertResult(ContentReloadExamples.Unchanged, reloader.Reload());
    }

    private sealed class ThrowingContentSource : IContentSource
    {
        public IReadOnlyList<ContentPackage> ListPackages() => throw new IOException("Source failed.");
        public IReadOnlyList<ContentDiagnostic> ListScanDiagnostics() => throw new AssertFailedException("Not reached.");
        public ContentFileDiscovery ListDefinitionFiles(ContentPackage package) => throw new AssertFailedException("Not reached.");
        public ContentBytesResult ReadFile(ContentPackage package, string path, int maxBytes) => throw new AssertFailedException("Not reached.");
        public ImageDecodeResult DecodePng(ContentPackage package, string path) => throw new AssertFailedException("Not reached.");
        public ImageDecodeResult DecodePng(ContentPackage package, string path, byte[] bytes) => throw new AssertFailedException("Not reached.");
    }

    private static MemoryContentSource PairSource()
    {
        var source = ContentFixtures.CorePackage();
        source.AddDefinitionFile("core", "structure.voxels.json", ContentReloadExamples.StructureFile);
        source.AddDefinitionFile("core", "pair.composites.json", ContentReloadExamples.BlueprintFile);
        ContentFixtures.AddSquareImages(source, "iron.png");
        return source;
    }

    private static void AssertResult(ReloadResultExample expected, ContentReloadResult actual)
    {
        Assert.AreEqual(expected.Status, actual.Status);
        Assert.AreEqual(expected.Changed, actual.Changed);
        if (expected.DiagnosticCode is not null)
            Assert.IsTrue(actual.Diagnostics.Any(item => item.Code == expected.DiagnosticCode));
        if (expected.Snapshot is null)
            Assert.IsNull(actual.Snapshot);
        else
            AssertSnapshot(expected.Snapshot, actual.Snapshot!);
    }

    private static void AssertSnapshot(PublishedContentExample expected, ContentSnapshot actual)
    {
        Assert.AreEqual(expected.Generation, actual.Generation);
        Assert.AreEqual(expected.VoxelCount, actual.Voxels.Count);
        Assert.AreEqual(expected.BlueprintCount, actual.Blueprints.Count);
        if (expected.Blueprint is not { } blueprint)
            return;
        var attributes = actual.Blueprints[ContentReloadExamples.BlueprintId].Attributes;
        Assert.AreEqual(blueprint.Mass, attributes.Mass);
        Assert.AreEqual(blueprint.BomValue, attributes.BomValue);
        Assert.AreEqual(blueprint.BomUnits, attributes.Bom[ContentReloadExamples.StructureId]);
        Assert.AreEqual(blueprint.StructuralDurabilityMax, attributes.StructuralDurabilityMax);
    }
}
