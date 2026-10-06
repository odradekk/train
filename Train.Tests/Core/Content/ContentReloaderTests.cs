// Core 内容模块测试：验证体素内容解析、发布和热重载契约。
// 测试输入不依赖 Godot 或已退役的聚居地内容。
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Core.Content.Voxels;
using Train.Core.Logging;
using Train.Tests;

namespace Train.Tests.Core.Content;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:content")]
public sealed class ContentReloaderTests
{
    [TestMethod]
    public void Initial_valid_voxel_catalog_commits_without_settlement_files()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.CorePackage();
        source.AddVoxelFile("core", "voxels/ores.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "Ferrous Ore 说明"),
            ContentFixtures.Good("coal", "煤", "煤说明", icon: "coal.png", voxel: "coal.png")));
        ContentFixtures.AddSquareImages(source, "iron.png", "coal.png");
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(1, result.CatalogVersion);
        Assert.AreEqual(2, result.AcceptedDefinitionCount);
        var snapshot = reloader.Catalog.Current!;
        Assert.AreEqual(2, snapshot.Entries.Count);
        Assert.IsTrue(snapshot.TryGet(new VoxelDefinitionId("core", "ferrous-ore"), out var iron));
        Assert.AreEqual("Ferrous Ore", iron.Definition.Name);
        Assert.AreEqual("core", iron.Origin.Namespace);
        Assert.AreEqual("voxels/ores.voxels.json", iron.Origin.SourceFile);
        Assert.AreEqual(100, iron.Definition.Value);
        Assert.AreEqual(1, iron.Definition.Mass);
        Assert.AreEqual(100, iron.Definition.MaxDurability);
        Assert.IsTrue(iron.Definition.HasRawGoodTag);
        var entries = (ICollection<KeyValuePair<VoxelDefinitionId, VoxelCatalogEntry>>)snapshot.Entries;
        Assert.IsTrue(entries.IsReadOnly);
        Assert.ThrowsException<NotSupportedException>(() =>
            entries.Add(new KeyValuePair<VoxelDefinitionId, VoxelCatalogEntry>(new VoxelDefinitionId("core", "x"), iron)));
    }

    [TestMethod]
    public void Strict_schema_violations_reject_the_package()
    {
        var cases = new[]
        {
            "{ /* comment */ \"schema_version\": 1, \"voxels\": [] }",
            "{ \"schema_version\": 1, \"voxels\": [], }",
            "{ \"schema_version\": 1, \"voxels\": [], \"extra\": 1 }",
            "{ \"schema_version\": 1, \"schema_version\": 1, \"voxels\": [] }",
            "{ \"Schema_version\": 1, \"voxels\": [] }",
            "{ \"schema_version\": 2, \"voxels\": [] }",
            "{ \"schema_version\": 1.5, \"voxels\": [] }",
            "{ \"voxels\": [] }"
        };

        foreach (var json in cases)
        {
            using var session = LoggerSession.Create();
            var source = ContentFixtures.CorePackage();
            source.AddVoxelFile("core", "bad.voxels.json", json);
            var reloader = ContentFixtures.Create(source, session.Logger);
            var result = reloader.Reload();
            Assert.AreEqual(ContentReloadStatus.Rejected, result.Status, json);
            Assert.IsNull(reloader.Catalog.Current);
        }
    }

    [TestMethod]
    public void Field_and_value_rules_reject_invalid_definitions()
    {
        var invalids = new[]
        {
            ContentFixtures.Good("Iron-Ore", "铁矿", "说明"),
            ContentFixtures.Good("_iron", "铁矿", "说明"),
            ContentFixtures.Good(new string('a', 65), "铁矿", "说明"),
            ContentFixtures.Good("iron", " ", "说明"),
            ContentFixtures.Good("iron", "铁矿", ""),
            ContentFixtures.Good("iron", "铁矿", "说明", value: 0),
            ContentFixtures.Good("iron", "铁矿", "说明", value: 1_000_000_001),
            ContentFixtures.Good("iron", "铁矿", "说明", tagsJson: "[\"Raw_Good\"]"),
            ContentFixtures.Good("iron", "铁矿", "说明", behaviorsJson: "[{\"type\":\"future\"}]"),
            ContentFixtures.Good("iron", "铁矿", "说明", orientations: "free"),
            ContentFixtures.Good("iron", "铁矿", "说明", mass: 0),
            "{\"id\":\"iron\",\"name\":\"铁矿\",\"description\":\"说明\",\"value\":100}"
        };

        foreach (var good in invalids)
        {
            using var session = LoggerSession.Create();
            var source = ContentFixtures.CorePackage();
            source.AddVoxelFile("core", "bad.voxels.json", ContentFixtures.GoodsFile(good));
            ContentFixtures.AddSquareImages(source, "iron.png");
            var reloader = ContentFixtures.Create(source, session.Logger);
            var result = reloader.Reload();
            Assert.AreEqual(ContentReloadStatus.Rejected, result.Status, good);
            Assert.IsNull(reloader.Catalog.Current);
            Assert.IsTrue(reloader.InitializationFailed);
        }
    }

    [TestMethod]
    public void Text_is_trimmed_and_boundaries_are_accepted()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.CorePackage();
        source.AddVoxelFile("core", "bounds.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("1ferrous-ore", "  铁矿  ", "  说明  ", value: 1),
            ContentFixtures.Good("coal", new string('名', 100), new string('说', 2000), value: 1_000_000_000, icon: "coal.png", voxel: "coal.png")));
        ContentFixtures.AddSquareImages(source, "iron.png", "coal.png");
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        Assert.AreEqual("铁矿", reloader.Catalog.Current!.Entries[new VoxelDefinitionId("core", "1ferrous-ore")].Definition.Name);
        Assert.AreEqual(1_000_000_000, reloader.Catalog.Current.Entries[new VoxelDefinitionId("core", "coal")].Definition.Value);
    }

    [TestMethod]
    public void Unsafe_visual_paths_reject_the_package()
    {
        var paths = new[]
        {
            "../secret.png",
            "/abs.png",
            "C:/abs.png",
            "file://x.png",
            "res://x.png",
            "a\\b.png",
            "a/./b.png",
            "a/../b.png",
            "",
            "a\0b.png",
            "icon.jpg"
        };

        foreach (var path in paths)
        {
            using var session = LoggerSession.Create();
            var source = ContentFixtures.CorePackage();
            source.AddVoxelFile("core", "path.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("iron", "铁矿", "说明", icon: path, voxel: path.Length == 0 ? "iron.png" : path)));
            ContentFixtures.AddSquareImages(source, "iron.png");
            var result = ContentFixtures.Create(source, session.Logger).Reload();
            Assert.AreEqual(ContentReloadStatus.Rejected, result.Status, path);
            Assert.IsTrue(result.Diagnostics.Any(item => item.Code is ContentDiagnosticCodes.PathInvalid or ContentDiagnosticCodes.PresentationRequired), path);
        }
    }

    [TestMethod]
    public void Image_shape_and_size_rules_reject_incompatible_visuals()
    {
        using var session = LoggerSession.Create();
        Assert.AreEqual(ContentReloadStatus.Rejected, ReloadWithImage(session, "icon.png", ContentFixtures.Rectangle(8, 4)).Status);
        using var session2 = LoggerSession.Create();
        Assert.AreEqual(ContentReloadStatus.Rejected, ReloadWithFaces(session2, 6, 6, 6).Status);
        using var session3 = LoggerSession.Create();
        Assert.AreEqual(ContentReloadStatus.Rejected, ReloadWithFaces(session3, 16, 16, 32).Status);
        using var session4 = LoggerSession.Create();
        var missing = ContentFixtures.CorePackage();
        missing.AddVoxelFile("core", "n.voxels.json", ContentFixtures.GoodsFile(ContentFixtures.Good("iron", "铁矿", "说明")));
        missing.AddMissingImage("core", "iron.png");
        Assert.AreEqual(ContentReloadStatus.Rejected, ContentFixtures.Create(missing, session4.Logger).Reload().Status);
        using var session5 = LoggerSession.Create();
        var huge = ContentFixtures.CorePackage();
        huge.AddVoxelFile("core", "n.voxels.json", ContentFixtures.GoodsFile(ContentFixtures.Good("iron", "铁矿", "说明")));
        huge.AddOversizedImageBytes("core", "iron.png");
        Assert.AreEqual(ContentReloadStatus.Rejected, ContentFixtures.Create(huge, session5.Logger).Reload().Status);
    }

    [TestMethod]
    public void Duplicate_local_ids_invalidate_the_whole_package()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.CorePackage();
        source.AddVoxelFile("core", "a.voxels.json", ContentFixtures.GoodsFile(ContentFixtures.Good("iron", "铁矿", "说明")));
        source.AddVoxelFile("core", "b.voxels.json", ContentFixtures.GoodsFile(ContentFixtures.Good("iron", "另一铁矿", "说明")));
        ContentFixtures.AddSquareImages(source, "iron.png");
        var reloader = ContentFixtures.Create(source, session.Logger);
        var result = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.DuplicateLocalId));
        Assert.IsNull(reloader.Catalog.Current);
    }

    [TestMethod]
    public void File_and_global_limits_reject_oversized_candidates()
    {
        using var session = LoggerSession.Create();
        var oversizedFile = ContentFixtures.CorePackage();
        oversizedFile.AddVoxelFile("core", "big.voxels.json", new byte[ContentLimits.JsonFileMaxBytes + 1]);
        Assert.AreEqual(ContentReloadStatus.Rejected, ContentFixtures.Create(oversizedFile, session.Logger).Reload().Status);

        using var session2 = LoggerSession.Create();
        var source = ContentFixtures.CorePackage();
        ContentFixtures.AddSquareImages(source, "iron.png");
        for (var start = 1; start <= 10001; start += 1000)
        {
            var end = Math.Min(start + 999, 10001);
            source.AddVoxelFile("core", $"batch-{start}.voxels.json", ContentFixtures.ManyGoods(start, end));
        }

        var result = ContentFixtures.Create(source, session2.Logger).Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.GlobalVoxelLimitExceeded));
    }

    [TestMethod]
    public void Diagnostics_are_capped_per_package_and_still_reject()
    {
        using var session = LoggerSession.Create();
        var goods = string.Join(",", Enumerable.Range(0, 150).Select(i =>
            ContentFixtures.Good($"item-{i}", " ", "说明")));
        var source = ContentFixtures.CorePackage();
        source.AddVoxelFile("core", "many.voxels.json", $"{{\"schema_version\":1,\"voxels\":[{goods}]}}");
        var result = ContentFixtures.Create(source, session.Logger).Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.AreEqual(ContentLimits.MaxDiagnosticsPerPackage, result.Diagnostics.Count);
        Assert.IsTrue(result.Diagnostics.All(item => item.Code == ContentDiagnosticCodes.NameInvalid));
    }

    [TestMethod]
    public void Initial_empty_available_package_fails_initialization()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.CorePackage();
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsFalse(result.IsCommitted);
        Assert.AreEqual(0, result.CatalogVersion);
        Assert.IsNull(reloader.Catalog.Current);
        Assert.IsTrue(reloader.InitializationFailed);
        Assert.IsTrue(session.Memory.Snapshot().Any(item =>
            item.Category == ContentLog.Category &&
            item.EventId == ContentLog.ReloadFailedEventId &&
            item.Level == LogLevel.Error));
    }

    [TestMethod]
    public void Later_empty_reload_can_publish_when_a_catalog_already_exists()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        Assert.IsNotNull(reloader.Catalog.Current);

        var empty = ContentFixtures.CorePackage();
        var later = ContentFixtures.Create(empty, session.Logger, catalog: reloader.Catalog);
        var result = later.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.IsTrue(result.Changed);
        Assert.IsNotNull(later.Catalog.Current);
        Assert.AreEqual(0, later.Catalog.Current.Entries.Count);
        Assert.IsFalse(later.InitializationFailed);
        Assert.IsTrue(result.Removed.Contains(new VoxelDefinitionId("core", "ferrous-ore")));
    }

    [TestMethod]
    public void Scheme_prefixed_visual_paths_reject_while_colon_free_relative_pngs_commit()
    {
        foreach (var path in new[] { "file:secret.png", "https:secret.png" })
        {
            using var session = LoggerSession.Create();
            var source = ContentFixtures.CorePackage();
            source.AddVoxelFile("core", "path.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("iron", "铁矿", "说明", icon: path, voxel: path)));
            ContentFixtures.AddSquareImages(source, "iron.png");
            var reloader = ContentFixtures.Create(source, session.Logger);
            var result = reloader.Reload();
            Assert.AreEqual(ContentReloadStatus.Rejected, result.Status, path);
            Assert.IsNull(reloader.Catalog.Current);
            Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.PathInvalid), path);
        }

        using var ok = LoggerSession.Create();
        var valid = ContentFixtures.CorePackage();
        valid.AddVoxelFile("core", "voxels/ores.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("iron", "铁矿", "说明", icon: "voxels/iron.png", voxel: "voxels/iron.png")));
        ContentFixtures.AddSquareImages(valid, "voxels/iron.png");
        var validReloader = ContentFixtures.Create(valid, ok.Logger);
        var committed = validReloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, committed.Status);
        Assert.IsNotNull(validReloader.Catalog.Current);
        Assert.IsTrue(validReloader.Catalog.Current.TryGet(new VoxelDefinitionId("core", "iron"), out _));
    }

    [TestMethod]
    public void Invalid_discovery_rejects_instead_of_committing_partial_files()
    {
        using var session = LoggerSession.Create();
        var source = new InvalidDiscoverySource(ContentFixtures.ValidFerrousOre());
        var reloader = new ContentReloader(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsFalse(result.IsCommitted);
        Assert.IsNull(reloader.Catalog.Current);
        Assert.IsTrue(reloader.InitializationFailed);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UnsafeLinkedPath));
    }

    [TestMethod]
    public void Initial_failure_does_not_publish_an_empty_catalog()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.CorePackage(available: false);
        var reloader = ContentFixtures.Create(source, session.Logger);
        var result = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsNull(reloader.Catalog.Current);
        Assert.IsTrue(reloader.InitializationFailed);
        Assert.IsTrue(session.Memory.Snapshot().Any(item =>
            item.Category == ContentLog.Category &&
            item.EventId == ContentLog.ReloadFailedEventId &&
            item.Level == LogLevel.Error));
    }

    [TestMethod]
    public void Rejection_keeps_the_previous_snapshot()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var previous = reloader.Catalog.Current!;
        source.AddVoxelFile("core", "bad.voxels.json", "{ \"schema_version\": 2, \"voxels\": [] }");
        var rejected = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status);
        Assert.AreSame(previous, reloader.Catalog.Current);
        Assert.AreEqual(1, previous.Generation);
        Assert.IsTrue(previous.TryGet(new VoxelDefinitionId("core", "ferrous-ore"), out _));
        Assert.IsFalse(reloader.InitializationFailed);
    }

    [TestMethod]
    public void Shared_voxel_faces_and_validator_warnings_can_commit()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(
            source,
            session.Logger,
            new WarningValidator());
        var result = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.CommittedWithWarnings, result.Status);
        Assert.IsTrue(result.WarningCount > 0);
        Assert.IsTrue(reloader.Catalog.Current!.TryGet(new VoxelDefinitionId("core", "ferrous-ore"), out _));
    }

    [TestMethod]
    public void Pre_commit_validation_rejects_unresolved_references()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(
            source,
            session.Logger,
            new RequiredVoxelDefinitionsValidator([new("core", "coal")]));
        var result = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UnresolvedVoxelReference));
        Assert.IsNull(reloader.Catalog.Current);
    }

    [TestMethod]
    public void Concurrent_reload_is_rejected_immediately()
    {
        using var session = LoggerSession.Create();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(source, session.Logger, new BlockingValidator(entered, release));
        ContentReloadResult? first = null;
        var worker = new Thread(() => first = reloader.Reload());
        worker.Start();
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        var second = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, second.Status);
        Assert.IsTrue(second.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.ReloadAlreadyInProgress));
        release.Set();
        Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(first!.IsCommitted);
        Assert.IsNotNull(reloader.Catalog.Current);
    }

    [TestMethod]
    public void Unchanged_reload_keeps_version_and_does_not_notify()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(source, session.Logger);
        var first = reloader.Reload();
        var snapshot = reloader.Catalog.Current;
        var notifications = 0;
        reloader.Catalog.Changed += _ => notifications++;
        var second = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, second.Status);
        Assert.IsFalse(second.Changed);
        Assert.AreEqual(first.CatalogVersion, second.CatalogVersion);
        Assert.AreSame(snapshot, reloader.Catalog.Current);
        Assert.AreEqual(0, notifications);
        Assert.IsTrue(session.Memory.Snapshot().Any(item =>
            item.EventId == ContentLog.ReloadSummaryEventId &&
            item.Attributes["changed"] is false));
    }

    [TestMethod]
    public void Hash_matrix_separates_semantic_asset_and_catalog_generation()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var baseline = reloader.Catalog.Current!.Entries[new VoxelDefinitionId("core", "ferrous-ore")];
        var baselineSemantic = baseline.SemanticRevision;
        var baselineAsset = baseline.AssetRevisions["iron.png"];
        var baselineGeneration = reloader.Catalog.Current.Generation;

        source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", """
            {"schema_version":1,"voxels":[{"allowed_orientations":"all_24","behaviors":[],"collision":{"kind":"full_box"},"description":"说明","id":"ferrous-ore","mass":1,"max_durability":100,"name":"Ferrous Ore","presentation":{"icon":"iron.png","kind":"cube","voxel_bottom":"iron.png","voxel_side":"iron.png","voxel_top":"iron.png"},"tags":["raw_good"],"value":100}]}
            """);
        var whitespace = ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog).Reload();
        Assert.IsFalse(whitespace.Changed);
        Assert.AreEqual(baselineGeneration, whitespace.CatalogVersion);
        Assert.AreEqual(baselineSemantic, reloader.Catalog.Current!.Entries[new VoxelDefinitionId("core", "ferrous-ore")].SemanticRevision);

        source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明", tagsJson: "[\"raw_good\",\"stone\"]")));
        var tagged = ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog).Reload();
        Assert.IsTrue(tagged.Changed);
        var afterTags = reloader.Catalog.Current!.Entries[new VoxelDefinitionId("core", "ferrous-ore")];
        Assert.AreNotEqual(baselineSemantic, afterTags.SemanticRevision);
        var taggedSemantic = afterTags.SemanticRevision;
        var taggedGeneration = reloader.Catalog.Current.Generation;

        source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明", tagsJson: "[\"stone\",\"raw_good\"]")));
        var reorderedTags = ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog).Reload();
        Assert.IsFalse(reorderedTags.Changed);
        Assert.AreEqual(taggedSemantic, reloader.Catalog.Current!.Entries[new VoxelDefinitionId("core", "ferrous-ore")].SemanticRevision);

        source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "新亚铁矿", "新说明", tagsJson: "[\"stone\",\"raw_good\"]")));
        var display = ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog).Reload();
        Assert.IsTrue(display.Changed);
        Assert.IsTrue(display.ChangedIds.Contains(new VoxelDefinitionId("core", "ferrous-ore")));
        var afterDisplay = reloader.Catalog.Current!.Entries[new VoxelDefinitionId("core", "ferrous-ore")];
        Assert.AreEqual(taggedSemantic, afterDisplay.SemanticRevision);
        Assert.AreEqual("新亚铁矿", afterDisplay.Definition.Name);
        Assert.AreNotEqual(taggedGeneration, display.CatalogVersion);

        var sameBytes = new byte[] { 7, 7, 7, 7 };
        var expectedAsset = Convert.ToHexString(SHA256.HashData(sameBytes));
        source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "新亚铁矿", "新说明", tagsJson: "[\"stone\",\"raw_good\"]", icon: "copy.png", voxel: "copy.png")));
        source.AddImage("core", "copy.png", ContentFixtures.Square(16), rawBytes: sameBytes);
        source.AddImage("core", "iron.png", ContentFixtures.Square(16), rawBytes: sameBytes);
        var rebound = ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog).Reload();
        Assert.IsTrue(rebound.Changed);
        var afterBind = reloader.Catalog.Current!.Entries[new VoxelDefinitionId("core", "ferrous-ore")];
        Assert.AreEqual(taggedSemantic, afterBind.SemanticRevision);
        Assert.AreEqual(expectedAsset, afterBind.AssetRevisions["copy.png"]);

        source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "新亚铁矿", "新说明", tagsJson: "[\"stone\",\"raw_good\"]")));
        var reboundSameBytes = ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog).Reload();
        Assert.IsTrue(reboundSameBytes.Changed);
        var afterSameBytes = reloader.Catalog.Current!.Entries[new VoxelDefinitionId("core", "ferrous-ore")];
        Assert.AreEqual(taggedSemantic, afterSameBytes.SemanticRevision);
        Assert.AreEqual(expectedAsset, afterSameBytes.AssetRevisions["iron.png"]);

        source.AddImage("core", "iron.png", ContentFixtures.Square(16, 9), rawBytes: new byte[] { 9, 9, 9, 9 });
        var pixels = ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog).Reload();
        Assert.IsTrue(pixels.Changed);
        var afterPixels = reloader.Catalog.Current!.Entries[new VoxelDefinitionId("core", "ferrous-ore")];
        Assert.AreEqual(taggedSemantic, afterPixels.SemanticRevision);
        Assert.AreNotEqual(expectedAsset, afterPixels.AssetRevisions["iron.png"]);
        Assert.AreNotEqual(baselineAsset, afterPixels.AssetRevisions["iron.png"]);

        ContentReloadResult ReloadGood(string voxel)
        {
            source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(voxel));
            return ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog).Reload();
        }

        var previousSemantic = taggedSemantic;
        foreach (var voxel in new[]
                 {
                     ContentFixtures.Good("ferrous-ore", "新亚铁矿", "新说明", value: 250, tagsJson: "[\"stone\",\"raw_good\"]"),
                     ContentFixtures.Good("ferrous-ore", "新亚铁矿", "新说明", value: 250, mass: 2, tagsJson: "[\"stone\",\"raw_good\"]"),
                     ContentFixtures.Good("ferrous-ore", "新亚铁矿", "新说明", value: 250, mass: 2, maxDurability: 80, tagsJson: "[\"stone\",\"raw_good\"]"),
                     ContentFixtures.Good("ferrous-ore", "新亚铁矿", "新说明", value: 250, mass: 2, maxDurability: 80, tagsJson: "[\"stone\",\"raw_good\"]", orientations: "four_way"),
                     ContentFixtures.Good("ferrous-ore", "新亚铁矿", "新说明", value: 250, mass: 2, maxDurability: 80, tagsJson: "[\"stone\",\"raw_good\"]", orientations: "four_way", collisionJson: "{\"kind\":\"none\"}")
                 })
        {
            var result = ReloadGood(voxel);
            Assert.IsTrue(result.Changed);
            var currentSemantic = reloader.Catalog.Current!.Entries[new VoxelDefinitionId("core", "ferrous-ore")].SemanticRevision;
            Assert.AreNotEqual(previousSemantic, currentSemantic);
            previousSemantic = currentSemantic;
        }
    }

    [TestMethod]
    public void Successful_reload_reports_added_removed_and_pixel_changes()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        source.AddVoxelFile("core", "coal.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("coal", "煤", "煤说明", icon: "coal.png", voxel: "coal.png")));
        ContentFixtures.AddSquareImages(source, "coal.png");
        var reloader = ContentFixtures.Create(source, session.Logger);
        reloader.Reload();

        var notifications = new List<VoxelCatalogChange>();
        reloader.Catalog.Changed += notifications.Add;
        var previous = reloader.Catalog.Current!;
        source = ContentFixtures.ValidFerrousOre();
        source.AddVoxelFile("core", "stone.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("stone", "石", "石说明", icon: "stone.png", voxel: "stone.png")));
        ContentFixtures.AddSquareImages(source, "stone.png");
        source.AddImage("core", "iron.png", ContentFixtures.Square(16, 9));
        var rebuilt = ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog);
        var result = rebuilt.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        CollectionAssert.AreEquivalent(new[] { new VoxelDefinitionId("core", "stone") }, result.Added.ToArray());
        CollectionAssert.AreEquivalent(new[] { new VoxelDefinitionId("core", "coal") }, result.Removed.ToArray());
        CollectionAssert.AreEquivalent(new[] { new VoxelDefinitionId("core", "ferrous-ore") }, result.ChangedIds.ToArray());
        Assert.AreEqual(1, notifications.Count);
        Assert.AreEqual(result.CatalogVersion, notifications[0].Generation);
        Assert.IsTrue(previous.TryGet(new VoxelDefinitionId("core", "coal"), out _));
        Assert.IsFalse(reloader.Catalog.Current!.TryGet(new VoxelDefinitionId("core", "coal"), out _));
        Assert.AreNotSame(previous, reloader.Catalog.Current);
    }

    [TestMethod]
    public void Rejected_content_file_still_emits_a_zero_accept_scan_event()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        source.AddVoxelFile("core", "bad.voxels.json", "{ \"schema_version\": 2, \"voxels\": [] }");
        var reloader = ContentFixtures.Create(source, session.Logger);
        reloader.Reload();

        var bad = session.Memory.Snapshot().Single(item =>
            item.EventId == ContentLog.FileScannedEventId &&
            item.Attributes["source"] is "bad.voxels.json");
        Assert.AreEqual(LogLevel.Debug, bad.Level);
        Assert.AreEqual("voxels", bad.Attributes["kind"]);
        Assert.AreEqual("core", bad.Attributes["package"]);
        Assert.AreEqual(0, bad.Attributes["accepted"]);
        Assert.IsTrue(session.Memory.Snapshot().Any(item =>
            item.EventId == ContentLog.InvalidEventId &&
            item.Level == LogLevel.Warning));
    }

    [TestMethod]
    public void Notify_exception_keeps_the_commit_and_allows_a_later_reload()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明", value: 101)));
        var secondSawCommit = false;
        reloader.Catalog.Changed += _ => throw new InvalidOperationException("observer failure");
        reloader.Catalog.Changed += _ => secondSawCommit = true;

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.IsTrue(secondSawCommit);
        Assert.AreEqual(101, reloader.Catalog.Current!.Entries[new VoxelDefinitionId("core", "ferrous-ore")].Definition.Value);
        Assert.IsTrue(session.Memory.Snapshot().Any(item =>
            item.EventId == ContentLog.NotifyFailedEventId &&
            item.Level == LogLevel.Error));

        source.AddVoxelFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明", value: 102)));
        var later = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, later.Status);
        Assert.AreEqual(102, reloader.Catalog.Current!.Entries[new VoxelDefinitionId("core", "ferrous-ore")].Definition.Value);
    }

    [TestMethod]
    public void Asset_hash_uses_the_same_bytes_that_passed_png_validation()
    {
        using var session = LoggerSession.Create();
        var inner = ContentFixtures.ValidFerrousOre();
        var source = new FlipAfterReadSource(inner);
        var reloader = ContentFixtures.Create(source, session.Logger);

        var first = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, first.Status);
        var accepted = reloader.Catalog.Current!.Entries[new VoxelDefinitionId("core", "ferrous-ore")].AssetRevisions["iron.png"];
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(FlipAfterReadSource.GoodBytes)), accepted);
        Assert.AreNotEqual(Convert.ToHexString(SHA256.HashData(FlipAfterReadSource.BadBytes)), accepted);

        var second = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, second.Status);
    }

    [TestMethod]
    public void Same_pixels_different_raw_png_bytes_change_only_the_asset_revision()
    {
        using var session = LoggerSession.Create();
        var firstBytes = new byte[] { 1, 2, 3, 4 };
        var secondBytes = new byte[] { 9, 8, 7, 6 };
        var source = ContentFixtures.ValidFerrousOre();
        source.AddImage("core", "iron.png", ContentFixtures.Square(16), rawBytes: firstBytes);
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var baseline = reloader.Catalog.Current!.Entries[new VoxelDefinitionId("core", "ferrous-ore")];

        source.AddImage("core", "iron.png", ContentFixtures.Square(16), rawBytes: secondBytes);
        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.IsTrue(result.Changed);
        var after = reloader.Catalog.Current!.Entries[new VoxelDefinitionId("core", "ferrous-ore")];
        Assert.AreEqual(baseline.SemanticRevision, after.SemanticRevision);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(firstBytes)), baseline.AssetRevisions["iron.png"]);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(secondBytes)), after.AssetRevisions["iron.png"]);
        Assert.AreNotEqual(baseline.AssetRevisions["iron.png"], after.AssetRevisions["iron.png"]);
    }

    private static ContentReloadResult ReloadWithImage(LoggerSession session, string path, DecodedImage image)
    {
        var source = ContentFixtures.CorePackage();
        source.AddVoxelFile("core", "n.voxels.json", ContentFixtures.GoodsFile(ContentFixtures.Good("iron", "铁矿", "说明")));
        source.AddImage("core", path, image);
        return ContentFixtures.Create(source, session.Logger).Reload();
    }

    private static ContentReloadResult ReloadWithFaces(LoggerSession session, int top, int side, int bottom)
    {
        var source = ContentFixtures.CorePackage();
        source.AddVoxelFile("core", "n.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("iron", "铁矿", "说明", icon: "icon.png", voxel: null, top: "top.png", side: "side.png", bottom: "bottom.png")));
        source.AddImage("core", "icon.png", ContentFixtures.Square(16));
        source.AddImage("core", "top.png", ContentFixtures.Square(top, 2));
        source.AddImage("core", "side.png", ContentFixtures.Square(side, 3));
        source.AddImage("core", "bottom.png", ContentFixtures.Square(bottom, 4));
        return ContentFixtures.Create(source, session.Logger).Reload();
    }
}

internal sealed class InvalidDiscoverySource : IContentSource
{
    private readonly IContentSource _inner;

    public InvalidDiscoverySource(IContentSource inner) => _inner = inner;

    public IReadOnlyList<ContentPackage> ListPackages() => _inner.ListPackages();

    public IReadOnlyList<ContentDiagnostic> ListScanDiagnostics() => _inner.ListScanDiagnostics();

    public ContentFileDiscovery ListVoxelFiles(ContentPackage package)
    {
        var inner = _inner.ListVoxelFiles(package);
        return new ContentFileDiscovery(
            inner.Files,
            new[] { ContentPackageWalk.LinkedPathDiagnostic("res://linked") },
            invalid: true);
    }

    public ContentBytesResult ReadFile(ContentPackage package, string normalizedRelativePath, int maxBytes) =>
        _inner.ReadFile(package, normalizedRelativePath, maxBytes);


    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath) =>
        _inner.DecodePng(package, normalizedRelativePath);

    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath, byte[] rawBytes) =>
        _inner.DecodePng(package, normalizedRelativePath, rawBytes);
}

internal sealed class FlipAfterReadSource : IContentSource
{
    public static readonly byte[] BadBytes = "not a PNG"u8.ToArray();
    public static readonly byte[] GoodBytes = Enumerable.Repeat((byte)1, 16 * 16 * 4).ToArray();

    private readonly IContentSource _inner;
    private bool _pngConsumed;

    public FlipAfterReadSource(IContentSource inner) => _inner = inner;

    public IReadOnlyList<ContentPackage> ListPackages() => _inner.ListPackages();

    public IReadOnlyList<ContentDiagnostic> ListScanDiagnostics() => _inner.ListScanDiagnostics();

    public ContentFileDiscovery ListVoxelFiles(ContentPackage package) => _inner.ListVoxelFiles(package);


    public ContentBytesResult ReadFile(ContentPackage package, string normalizedRelativePath, int maxBytes)
    {
        if (normalizedRelativePath.EndsWith(".png", StringComparison.Ordinal) && _pngConsumed)
            return ContentBytesResult.Ok(BadBytes);
        var read = _inner.ReadFile(package, normalizedRelativePath, maxBytes);
        if (normalizedRelativePath.EndsWith(".png", StringComparison.Ordinal))
            _pngConsumed = true;
        return read;
    }

    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath)
    {
        var decoded = _inner.DecodePng(package, normalizedRelativePath);
        _pngConsumed = true;
        return decoded;
    }

    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath, byte[] rawBytes)
    {
        if (rawBytes.AsSpan().SequenceEqual(BadBytes))
            return ImageDecodeResult.Invalid();
        return _inner.DecodePng(package, normalizedRelativePath, rawBytes);
    }
}

internal sealed class WarningValidator : IContentPreCommitValidator
{
    public ContentPreCommitDecision Validate(VoxelCatalogSnapshot candidate) =>
        ContentPreCommitDecision.Allow(new[]
        {
            new ContentDiagnostic(ContentDiagnosticSeverity.Warning, "precommit_warning", "Advisory warning.")
        });
}

internal sealed class BlockingValidator : IContentPreCommitValidator
{
    private readonly ManualResetEventSlim _entered;
    private readonly ManualResetEventSlim _release;

    public BlockingValidator(ManualResetEventSlim entered, ManualResetEventSlim release)
    {
        _entered = entered;
        _release = release;
    }

    public ContentPreCommitDecision Validate(VoxelCatalogSnapshot candidate)
    {
        _entered.Set();
        _release.Wait();
        return ContentPreCommitDecision.Allow();
    }
}
