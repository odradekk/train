using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Goods;
using Train.Domain.Settlements;
using Train.Logging;

namespace Train.Tests;

[TestClass]
public sealed class ContentReloaderTests
{
    [TestMethod]
    public void Valid_multi_definition_file_commits_an_immutable_catalog()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.CorePackage();
        source.AddGoodsFile("core", "natural/ores.goods.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("iron-ore", "铁矿", "铁矿说明"),
            ContentFixtures.Good("coal", "煤", "煤说明", icon: "coal.png", voxel: "coal.png")));
        ContentFixtures.AddSquareImages(source, "iron.png", "coal.png");
        ContentFixtures.AddExampleCity(source);
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(1, result.CatalogVersion);
        Assert.AreEqual(2, result.AcceptedDefinitionCount);
        var snapshot = reloader.Catalog.Current!;
        Assert.AreEqual(2, snapshot.Entries.Count);
        Assert.IsTrue(snapshot.TryGet(new GoodId("core", "iron-ore"), out var iron));
        Assert.AreEqual("铁矿", iron.Definition.Name);
        Assert.AreEqual("core", iron.Origin.Namespace);
        Assert.AreEqual("natural/ores.goods.json", iron.Origin.SourceFile);
        Assert.AreEqual(100, iron.Definition.BaseMonetaryValue);
        var entries = (ICollection<KeyValuePair<GoodId, GoodCatalogEntry>>)snapshot.Entries;
        Assert.IsTrue(entries.IsReadOnly);
        Assert.ThrowsException<NotSupportedException>(() =>
            entries.Add(new KeyValuePair<GoodId, GoodCatalogEntry>(new GoodId("core", "x"), iron)));
    }

    [TestMethod]
    public void Strict_schema_violations_reject_the_package()
    {
        var cases = new[]
        {
            "{ /* comment */ \"schema_version\": 1, \"goods\": [] }",
            "{ \"schema_version\": 1, \"goods\": [], }",
            "{ \"schema_version\": 1, \"goods\": [], \"extra\": 1 }",
            "{ \"schema_version\": 1, \"schema_version\": 1, \"goods\": [] }",
            "{ \"Schema_version\": 1, \"goods\": [] }",
            "{ \"schema_version\": 2, \"goods\": [] }",
            "{ \"schema_version\": 1.5, \"goods\": [] }",
            "{ \"goods\": [] }"
        };

        foreach (var json in cases)
        {
            using var session = LoggerSession.Create();
            var source = ContentFixtures.CorePackage();
            source.AddGoodsFile("core", "bad.goods.json", json);
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
            ContentFixtures.Good("iron", "铁矿", "说明", category: "Natural_Resource"),
            ContentFixtures.Good("iron", "铁矿", "说明", category: "processed"),
            "{\"id\":\"iron\",\"category\":\"natural_resource\",\"name\":\"铁矿\",\"description\":\"说明\",\"base_monetary_value\":100}"
        };

        foreach (var good in invalids)
        {
            using var session = LoggerSession.Create();
            var source = ContentFixtures.CorePackage();
            source.AddGoodsFile("core", "bad.goods.json", ContentFixtures.GoodsFile(good));
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
        source.AddGoodsFile("core", "bounds.goods.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("1iron-ore", "  铁矿  ", "  说明  ", value: 1),
            ContentFixtures.Good("coal", new string('名', 100), new string('说', 2000), value: 1_000_000_000, icon: "coal.png", voxel: "coal.png")));
        ContentFixtures.AddSquareImages(source, "iron.png", "coal.png");
        ContentFixtures.AddExampleCity(source, resources: new[] { "core:1iron-ore" });
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        Assert.AreEqual("铁矿", reloader.Catalog.Current!.Entries[new GoodId("core", "1iron-ore")].Definition.Name);
        Assert.AreEqual(1_000_000_000, reloader.Catalog.Current.Entries[new GoodId("core", "coal")].Definition.BaseMonetaryValue);
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
            source.AddGoodsFile("core", "path.goods.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("iron", "铁矿", "说明", icon: path, voxel: path.Length == 0 ? "iron.png" : path)));
            ContentFixtures.AddSquareImages(source, "iron.png");
            var result = ContentFixtures.Create(source, session.Logger).Reload();
            Assert.AreEqual(ContentReloadStatus.Rejected, result.Status, path);
            Assert.IsTrue(result.Diagnostics.Any(item => item.Code is ContentDiagnosticCodes.PathInvalid or ContentDiagnosticCodes.VisualRequired), path);
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
        missing.AddGoodsFile("core", "n.goods.json", ContentFixtures.GoodsFile(ContentFixtures.Good("iron", "铁矿", "说明")));
        missing.AddMissingImage("core", "iron.png");
        Assert.AreEqual(ContentReloadStatus.Rejected, ContentFixtures.Create(missing, session4.Logger).Reload().Status);
        using var session5 = LoggerSession.Create();
        var huge = ContentFixtures.CorePackage();
        huge.AddGoodsFile("core", "n.goods.json", ContentFixtures.GoodsFile(ContentFixtures.Good("iron", "铁矿", "说明")));
        huge.AddOversizedImageBytes("core", "iron.png");
        Assert.AreEqual(ContentReloadStatus.Rejected, ContentFixtures.Create(huge, session5.Logger).Reload().Status);
    }

    [TestMethod]
    public void Duplicate_local_ids_invalidate_the_whole_package()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.CorePackage();
        source.AddGoodsFile("core", "a.goods.json", ContentFixtures.GoodsFile(ContentFixtures.Good("iron", "铁矿", "说明")));
        source.AddGoodsFile("core", "b.goods.json", ContentFixtures.GoodsFile(ContentFixtures.Good("iron", "另一铁矿", "说明")));
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
        oversizedFile.AddGoodsFile("core", "big.goods.json", new byte[ContentLimits.GoodsFileMaxBytes + 1]);
        Assert.AreEqual(ContentReloadStatus.Rejected, ContentFixtures.Create(oversizedFile, session.Logger).Reload().Status);

        using var session2 = LoggerSession.Create();
        var source = ContentFixtures.CorePackage();
        ContentFixtures.AddSquareImages(source, "iron.png");
        for (var start = 1; start <= 10001; start += 1000)
        {
            var end = Math.Min(start + 999, 10001);
            source.AddGoodsFile("core", $"batch-{start}.goods.json", ContentFixtures.ManyGoods(start, end));
        }

        var result = ContentFixtures.Create(source, session2.Logger).Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.GlobalGoodLimitExceeded));
    }

    [TestMethod]
    public void Diagnostics_are_capped_per_package_and_still_reject()
    {
        using var session = LoggerSession.Create();
        var goods = string.Join(",", Enumerable.Range(0, 150).Select(i =>
            ContentFixtures.Good($"item-{i}", " ", "说明")));
        var source = ContentFixtures.CorePackage();
        source.AddGoodsFile("core", "many.goods.json", $"{{\"schema_version\":1,\"goods\":[{goods}]}}");
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
        var source = ContentFixtures.ValidIron();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        Assert.IsNotNull(reloader.Catalog.Current);

        var empty = ContentFixtures.CorePackage();
        var later = ContentFixtures.Create(empty, session.Logger, catalog: reloader.Catalog, settlements: reloader.Settlements);
        var result = later.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.IsTrue(result.Changed);
        Assert.IsNotNull(later.Catalog.Current);
        Assert.AreEqual(0, later.Catalog.Current.Entries.Count);
        Assert.IsFalse(later.InitializationFailed);
        Assert.IsTrue(result.Removed.Contains(new GoodId("core", "iron-ore")));
    }

    [TestMethod]
    public void Scheme_prefixed_visual_paths_reject_while_colon_free_relative_pngs_commit()
    {
        foreach (var path in new[] { "file:secret.png", "https:secret.png" })
        {
            using var session = LoggerSession.Create();
            var source = ContentFixtures.CorePackage();
            source.AddGoodsFile("core", "path.goods.json", ContentFixtures.GoodsFile(
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
        valid.AddGoodsFile("core", "natural/ores.goods.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("iron", "铁矿", "说明", icon: "natural/iron.png", voxel: "natural/iron.png")));
        ContentFixtures.AddSquareImages(valid, "natural/iron.png");
        ContentFixtures.AddExampleCity(valid, resources: new[] { "core:iron" });
        var validReloader = ContentFixtures.Create(valid, ok.Logger);
        var committed = validReloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, committed.Status);
        Assert.IsNotNull(validReloader.Catalog.Current);
        Assert.IsTrue(validReloader.Catalog.Current.TryGet(new GoodId("core", "iron"), out _));
    }

    [TestMethod]
    public void Invalid_discovery_rejects_instead_of_committing_partial_files()
    {
        using var session = LoggerSession.Create();
        var source = new InvalidDiscoverySource(ContentFixtures.ValidIron());
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
        var source = ContentFixtures.ValidIron();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var previous = reloader.Catalog.Current!;
        source.AddGoodsFile("core", "bad.goods.json", "{ \"schema_version\": 2, \"goods\": [] }");
        var rejected = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status);
        Assert.AreSame(previous, reloader.Catalog.Current);
        Assert.AreEqual(1, previous.Version);
        Assert.IsTrue(previous.TryGet(new GoodId("core", "iron-ore"), out _));
        Assert.IsFalse(reloader.InitializationFailed);
    }

    [TestMethod]
    public void Shared_voxel_faces_and_validator_warnings_can_commit()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidIron();
        var reloader = ContentFixtures.Create(
            source,
            session.Logger,
            new WarningValidator());
        var result = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.CommittedWithWarnings, result.Status);
        Assert.IsTrue(result.WarningCount > 0);
        Assert.IsTrue(reloader.Catalog.Current!.TryGet(new GoodId("core", "iron-ore"), out _));
    }

    [TestMethod]
    public void Pre_commit_validation_rejects_unresolved_references()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidIron();
        var reloader = ContentFixtures.Create(
            source,
            session.Logger,
            new ReferencedGoodIdValidator(() => new HashSet<GoodId> { new("core", "coal") }));
        var result = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UnresolvedGoodReference));
        Assert.IsNull(reloader.Catalog.Current);
    }

    [TestMethod]
    public void Concurrent_reload_is_rejected_immediately()
    {
        using var session = LoggerSession.Create();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var source = ContentFixtures.ValidIron();
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
        var source = ContentFixtures.ValidIron();
        var reloader = ContentFixtures.Create(source, session.Logger);
        var first = reloader.Reload();
        var snapshot = reloader.Catalog.Current;
        var notifications = 0;
        reloader.Catalog.Changed += _ => notifications++;
        source.AddImage("core", "iron.png", ContentFixtures.Square(16), rawBytes: new byte[] { 9, 8, 7 });
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
    public void Successful_reload_reports_added_removed_and_pixel_changes()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidIron();
        source.AddGoodsFile("core", "coal.goods.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("coal", "煤", "煤说明", icon: "coal.png", voxel: "coal.png")));
        ContentFixtures.AddSquareImages(source, "coal.png");
        var reloader = ContentFixtures.Create(source, session.Logger);
        reloader.Reload();

        var notifications = new List<GoodCatalogChange>();
        reloader.Catalog.Changed += notifications.Add;
        var previous = reloader.Catalog.Current!;
        source = ContentFixtures.ValidIron();
        source.AddGoodsFile("core", "stone.goods.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("stone", "石", "石说明", icon: "stone.png", voxel: "stone.png")));
        ContentFixtures.AddSquareImages(source, "stone.png");
        source.AddImage("core", "iron.png", ContentFixtures.Square(16, 9));
        var rebuilt = ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog, settlements: reloader.Settlements);
        var result = rebuilt.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        CollectionAssert.AreEquivalent(new[] { new GoodId("core", "stone") }, result.Added.ToArray());
        CollectionAssert.AreEquivalent(new[] { new GoodId("core", "coal") }, result.Removed.ToArray());
        CollectionAssert.AreEquivalent(new[] { new GoodId("core", "iron-ore") }, result.ChangedIds.ToArray());
        Assert.AreEqual(1, notifications.Count);
        Assert.AreEqual(result.CatalogVersion, notifications[0].Version);
        Assert.IsTrue(previous.TryGet(new GoodId("core", "coal"), out _));
        Assert.IsFalse(reloader.Catalog.Current!.TryGet(new GoodId("core", "coal"), out _));
        Assert.AreNotSame(previous, reloader.Catalog.Current);
    }

    [TestMethod]
    public void First_load_commits_built_in_city_into_the_live_registry()
    {
        using var session = LoggerSession.Create();
        var reloader = ContentFixtures.Create(ContentFixtures.ValidIron(), session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.AreEqual(1, result.AcceptedSettlementCount);
        Assert.IsTrue(result.AddedSettlements.Contains(new SettlementId("core:example-city")));
        Assert.IsTrue(reloader.Settlements.TryGet(new SettlementId("core:example-city"), out var city));
        Assert.AreEqual("示例城市", city.Name);
        Assert.AreEqual(new MapPosition(0, 0), city.Position);
        Assert.AreEqual(100, city.Population);
        Assert.IsTrue(city.NaturalResourceIds.Contains(new GoodId("core", "iron-ore")));
        Assert.IsTrue(session.Memory.Snapshot().Any(item =>
            item.EventId == ContentLog.ReloadSummaryEventId &&
            item.Attributes["settlements_accepted"] is 1));
    }

    [TestMethod]
    public void Debug_reload_emits_per_file_scan_and_settlement_accept_events()
    {
        using var session = LoggerSession.Create();
        var reloader = ContentFixtures.Create(ContentFixtures.ValidIron(), session.Logger);
        var result = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);

        var scanned = session.Memory.Snapshot()
            .Where(item => item.EventId == ContentLog.FileScannedEventId)
            .ToArray();
        Assert.AreEqual(2, scanned.Length);
        Assert.IsTrue(scanned.All(item =>
            item.Level == LogLevel.Debug && item.Category == ContentLog.Category));

        var goods = scanned.Single(item => item.Attributes["kind"] is "goods");
        Assert.AreEqual("natural/iron-ore.goods.json", goods.Attributes["source"]);
        Assert.AreEqual("core", goods.Attributes["package"]);
        Assert.AreEqual(1, goods.Attributes["accepted"]);

        var settlements = scanned.Single(item => item.Attributes["kind"] is "settlements");
        Assert.AreEqual("settlements/example-city.settlements.json", settlements.Attributes["source"]);
        Assert.AreEqual("core", settlements.Attributes["package"]);
        Assert.AreEqual(1, settlements.Attributes["accepted"]);

        Assert.IsTrue(session.Memory.Snapshot().Any(item =>
            item.EventId == ContentLog.DefinitionAcceptedEventId &&
            item.Level == LogLevel.Debug &&
            item.Attributes.TryGetValue("settlement_id", out var settlementId) &&
            settlementId is "core:example-city" &&
            item.Attributes.ContainsKey("source")));

        var summary = session.Memory.Snapshot().Single(item => item.EventId == ContentLog.ReloadSummaryEventId);
        StringAssert.Contains(summary.Message, result.ScannedFileCount.ToString());
        StringAssert.Contains(summary.Message, result.AcceptedDefinitionCount.ToString());
        StringAssert.Contains(summary.Message, (result.WarningCount + result.ErrorCount).ToString());
    }

    [TestMethod]
    public void Rejected_content_file_still_emits_a_zero_accept_scan_event()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidIron();
        source.AddGoodsFile("core", "bad.goods.json", "{ \"schema_version\": 2, \"goods\": [] }");
        var reloader = ContentFixtures.Create(source, session.Logger);
        reloader.Reload();

        var bad = session.Memory.Snapshot().Single(item =>
            item.EventId == ContentLog.FileScannedEventId &&
            item.Attributes["source"] is "bad.goods.json");
        Assert.AreEqual(LogLevel.Debug, bad.Level);
        Assert.AreEqual("goods", bad.Attributes["kind"]);
        Assert.AreEqual("core", bad.Attributes["package"]);
        Assert.AreEqual(0, bad.Attributes["accepted"]);
        Assert.IsTrue(session.Memory.Snapshot().Any(item =>
            item.EventId == ContentLog.InvalidEventId &&
            item.Level == LogLevel.Warning));
    }

    [TestMethod]
    public void First_load_without_settlements_fails_initialization()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.CorePackage();
        source.AddGoodsFile("core", "natural/iron-ore.goods.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("iron-ore", "铁矿", "说明")));
        ContentFixtures.AddSquareImages(source, "iron.png");
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsTrue(reloader.InitializationFailed);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.EmptyInitialSettlements));
        Assert.AreEqual(0, reloader.Settlements.Count);
        Assert.IsNull(reloader.Catalog.Current);
    }

    [TestMethod]
    public void Built_in_unresolved_natural_resource_rejects_goods_and_settlements()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidIron();
        source.AddSettlementsFile("core", "settlements/ghost.settlements.json", ContentFixtures.SettlementsFile(
            ContentFixtures.Settlement("ghost", "幽灵城", x: 4, y: 4, resources: new[] { "core:missing-ore" })));
        var previous = ContentFixtures.Create(ContentFixtures.ValidIron(), session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, previous.Reload().Status);
        var priorCatalog = previous.Catalog.Current;
        var rebuilt = ContentFixtures.Create(source, session.Logger, catalog: previous.Catalog, settlements: previous.Settlements);

        var result = rebuilt.Reload();

        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.AreSame(priorCatalog, rebuilt.Catalog.Current);
        Assert.IsTrue(previous.Settlements.TryGet(new SettlementId("core:example-city"), out _));
        Assert.IsFalse(previous.Settlements.TryGet(new SettlementId("core:ghost"), out _));
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UnresolvedGoodReference));
    }

    [TestMethod]
    public void Reload_reconciles_an_existing_city_in_place()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidIron();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        Assert.IsTrue(reloader.Settlements.TryGet(new SettlementId("core:example-city"), out var held));

        source.AddSettlementsFile("core", "settlements/example-city.settlements.json", ContentFixtures.SettlementsFile(
            ContentFixtures.Settlement("example-city", "港湾", x: 6, y: -2, population: 250)));
        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.IsTrue(result.ChangedSettlementIds.Contains(new SettlementId("core:example-city")));
        Assert.AreSame(held, reloader.Settlements.Items.Single());
        Assert.AreEqual("港湾", held.Name);
        Assert.AreEqual(new MapPosition(6, -2), held.Position);
        Assert.AreEqual(250, held.Population);
    }

    [TestMethod]
    public void New_good_and_city_that_references_it_commit_together()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidIron();
        source.AddGoodsFile("core", "natural/coal.goods.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("coal", "煤", "煤说明", icon: "coal.png", voxel: "coal.png")));
        ContentFixtures.AddSquareImages(source, "coal.png");
        source.AddSettlementsFile("core", "settlements/mine.settlements.json", ContentFixtures.SettlementsFile(
            ContentFixtures.Settlement("coal-town", "煤城", x: 2, y: 3, resources: new[] { "core:coal" })));
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.IsTrue(reloader.Catalog.Current!.TryGet(new GoodId("core", "coal"), out _));
        Assert.IsTrue(reloader.Settlements.TryGet(new SettlementId("core:coal-town"), out var town));
        Assert.IsTrue(town.NaturalResourceIds.Contains(new GoodId("core", "coal")));
        Assert.IsTrue(result.Added.Contains(new GoodId("core", "coal")));
        Assert.IsTrue(result.AddedSettlements.Contains(new SettlementId("core:coal-town")));
    }

    private static ContentReloadResult ReloadWithImage(LoggerSession session, string path, DecodedImage image)
    {
        var source = ContentFixtures.CorePackage();
        source.AddGoodsFile("core", "n.goods.json", ContentFixtures.GoodsFile(ContentFixtures.Good("iron", "铁矿", "说明")));
        source.AddImage("core", path, image);
        return ContentFixtures.Create(source, session.Logger).Reload();
    }

    private static ContentReloadResult ReloadWithFaces(LoggerSession session, int top, int side, int bottom)
    {
        var source = ContentFixtures.CorePackage();
        source.AddGoodsFile("core", "n.goods.json", ContentFixtures.GoodsFile(
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

    public ContentFileDiscovery ListGoodsFiles(ContentPackage package)
    {
        var inner = _inner.ListGoodsFiles(package);
        return new ContentFileDiscovery(
            inner.Files,
            new[] { ContentPackageWalk.LinkedPathDiagnostic("res://linked") },
            invalid: true);
    }

    public ContentBytesResult ReadFile(ContentPackage package, string normalizedRelativePath, int maxBytes) =>
        _inner.ReadFile(package, normalizedRelativePath, maxBytes);

    public ContentFileDiscovery ListSettlementFiles(ContentPackage package) =>
        _inner.ListSettlementFiles(package);

    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath) =>
        _inner.DecodePng(package, normalizedRelativePath);
}

internal sealed class WarningValidator : IContentPreCommitValidator
{
    public ContentPreCommitDecision Validate(GoodCatalogSnapshot candidate) =>
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

    public ContentPreCommitDecision Validate(GoodCatalogSnapshot candidate)
    {
        _entered.Set();
        _release.Wait();
        return ContentPreCommitDecision.Allow();
    }
}

internal static class ContentFixtures
{
    public static MemoryContentSource CorePackage(bool available = true)
    {
        var source = new MemoryContentSource();
        source.AddPackage("core", available: available);
        return source;
    }

    public static MemoryContentSource ValidIron()
    {
        var source = CorePackage();
        source.AddGoodsFile("core", "natural/iron-ore.goods.json", GoodsFile(Good("iron-ore", "铁矿", "说明")));
        AddSquareImages(source, "iron.png");
        AddExampleCity(source);
        return source;
    }

    public static void AddExampleCity(
        MemoryContentSource source,
        string @namespace = "core",
        string id = "example-city",
        string name = "示例城市",
        int x = 0,
        int y = 0,
        int population = 100,
        string[]? resources = null,
        string? resourcesRoot = null)
    {
        source.AddSettlementsFile(
            @namespace,
            $"settlements/{id}.settlements.json",
            SettlementsFile(Settlement(id, name, x: x, y: y, population: population, resources: resources)),
            resourcesRoot);
    }

    public static string SettlementsFile(params string[] settlements) =>
        $"{{\"schema_version\":1,\"settlements\":[{string.Join(",", settlements)}]}}";

    public static string Settlement(
        string id,
        string name = "示例城市",
        string type = "city",
        int x = 0,
        int y = 0,
        int population = 100,
        string[]? resources = null)
    {
        resources ??= new[] { "core:iron-ore" };
        var resourceJson = string.Join(",", resources.Select(item => $"\"{item}\""));
        return $"{{\"id\":\"{id}\",\"type\":\"{type}\",\"name\":\"{name}\",\"position\":{{\"x\":{x},\"y\":{y}}},\"population\":{population},\"natural_resources\":[{resourceJson}]}}";
    }

    public static ContentReloader Create(
        MemoryContentSource source,
        TrainLogger logger,
        IContentPreCommitValidator? validator = null,
        GoodCatalog? catalog = null,
        SettlementRegistry? settlements = null) =>
        new(source, logger, catalog, validator, settlements);

    public static void AddSquareImages(MemoryContentSource source, params string[] paths)
    {
        foreach (var path in paths)
            source.AddImage("core", path, Square(16));
    }

    public static DecodedImage Square(int size, byte value = 1) =>
        new(size, size, "rgba8", Enumerable.Repeat(value, size * size * 4).ToArray());

    public static DecodedImage Rectangle(int width, int height) =>
        new(width, height, "rgba8", new byte[width * height * 4]);

    public static string GoodsFile(params string[] goods) =>
        $"{{\"schema_version\":1,\"goods\":[{string.Join(",", goods)}]}}";

    public static string Good(
        string id,
        string name,
        string description,
        long value = 100,
        string category = "natural_resource",
        string? icon = "iron.png",
        string? voxel = "iron.png",
        string? top = null,
        string? side = null,
        string? bottom = null)
    {
        var visual = icon is null
            ? "null"
            : $"{{\"icon\":{Json(icon)},\"voxel_top\":{Json(top ?? voxel ?? icon)},\"voxel_side\":{Json(side ?? voxel ?? icon)},\"voxel_bottom\":{Json(bottom ?? voxel ?? icon)}}}";
        return $"{{\"id\":{Json(id)},\"category\":{Json(category)},\"name\":{Json(name)},\"description\":{Json(description)},\"base_monetary_value\":{value},\"visual\":{visual}}}";
    }

    public static string ManyGoods(int fromInclusive, int toInclusive)
    {
        var goods = Enumerable.Range(fromInclusive, toInclusive - fromInclusive + 1)
            .Select(i => Good($"g{i:D5}", "矿", "说明"));
        return GoodsFile(goods.ToArray());
    }

    private static string Json(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\0", "\\u0000");
        return $"\"{escaped}\"";
    }
}
