using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Voxels;
using Train.Domain.Settlements;
using Train.Logging;

namespace Train.Tests;

[TestClass]
public sealed class ModLoadingTests
{
    [TestMethod]
    public void Valid_zero_goods_mod_commits_with_built_in_content()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidIron();
        source.AddMod("empty-mod", ModFixtures.SampleUuid);
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.IsTrue(reloader.Catalog.Current!.TryGet(new VoxelDefinitionId("core", "iron-ore"), out _));
        Assert.AreEqual(1, reloader.Catalog.Current.Entries.Count);
        Assert.IsTrue(session.Memory.Snapshot().Any(item =>
            item.EventId == ContentLog.PackageEmptyEventId &&
            item.Level == LogLevel.Debug &&
            item.Attributes["package"] as string == ModFixtures.SampleUuid));
    }

    [TestMethod]
    public void Local_ids_expand_to_the_mod_uuid_namespace()
    {
        using var session = LoggerSession.Create();
        var source = ModFixtures.CorePlusCopper();
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.IsTrue(reloader.Catalog.Current!.TryGet(new VoxelDefinitionId(ModFixtures.SampleUuid, "copper"), out var copper));
        Assert.AreEqual($"{ModFixtures.SampleUuid}:copper", copper.Definition.Id.Value);
        Assert.AreEqual(ModFixtures.SampleUuid, copper.Origin.Namespace);
        Assert.IsFalse(reloader.Catalog.Current.TryGet(new VoxelDefinitionId("core", "copper"), out _));
        Assert.IsFalse(reloader.Catalog.Current.TryGet(new VoxelDefinitionId("pretty-name", "copper"), out _));
    }

    [TestMethod]
    public void Invalid_mod_is_isolated_while_unrelated_valid_packages_commit()
    {
        using var session = LoggerSession.Create();
        var source = ModFixtures.CorePlusCopper();
        source.AddMod("broken", ModFixtures.OtherUuid);
        source.AddVoxelFile(ModFixtures.OtherUuid, "bad.voxels.json", "{ \"schema_version\": 2, \"voxels\": [] }");
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.CommittedWithWarnings, result.Status);
        Assert.IsTrue(reloader.Catalog.Current!.TryGet(new VoxelDefinitionId("core", "iron-ore"), out _));
        Assert.IsTrue(reloader.Catalog.Current.TryGet(new VoxelDefinitionId(ModFixtures.SampleUuid, "copper"), out _));
        Assert.IsFalse(reloader.Catalog.Current.TryGet(new VoxelDefinitionId(ModFixtures.OtherUuid, "copper"), out _));
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.SchemaVersionUnsupported));
        Assert.IsTrue(session.Memory.Snapshot().Any(item =>
            item.EventId == ContentLog.ModsScanSummaryEventId &&
            item.Level == LogLevel.Info));
    }

    [TestMethod]
    public void Duplicate_mod_uuids_are_all_disabled_without_choosing_a_winner()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidIron();
        source.AddMod("alpha", ModFixtures.SampleUuid);
        source.AddVoxelFile(ModFixtures.SampleUuid, "a.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("copper", "铜", "铜说明", icon: "copper.png", voxel: "copper.png")),
            "memory://mods/alpha/resources");
        source.AddImage(ModFixtures.SampleUuid, "copper.png", ContentFixtures.Square(16), resourcesRoot: "memory://mods/alpha/resources");
        source.AddMod("beta", ModFixtures.SampleUuid);
        source.AddVoxelFile(ModFixtures.SampleUuid, "b.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("tin", "锡", "锡说明", icon: "tin.png", voxel: "tin.png")),
            "memory://mods/beta/resources");
        source.AddImage(ModFixtures.SampleUuid, "tin.png", ContentFixtures.Square(16), resourcesRoot: "memory://mods/beta/resources");
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.CommittedWithWarnings, result.Status);
        Assert.IsTrue(reloader.Catalog.Current!.TryGet(new VoxelDefinitionId("core", "iron-ore"), out _));
        Assert.IsFalse(reloader.Catalog.Current.TryGet(new VoxelDefinitionId(ModFixtures.SampleUuid, "copper"), out _));
        Assert.IsFalse(reloader.Catalog.Current.TryGet(new VoxelDefinitionId(ModFixtures.SampleUuid, "tin"), out _));
        Assert.AreEqual(2, result.Diagnostics.Count(item => item.Code == ContentDiagnosticCodes.DuplicateModUuid));
    }

    [TestMethod]
    public void Successful_reload_removes_stale_definitions_from_missing_or_invalid_mods()
    {
        using var session = LoggerSession.Create();
        var source = ModFixtures.CorePlusCopper();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        Assert.IsTrue(reloader.Catalog.Current!.TryGet(new VoxelDefinitionId(ModFixtures.SampleUuid, "copper"), out _));

        var later = ContentFixtures.ValidIron();
        later.AddMod("broken", ModFixtures.SampleUuid);
        later.AddVoxelFile(ModFixtures.SampleUuid, "bad.voxels.json", "{ \"schema_version\": 2, \"voxels\": [] }");
        var rebuilt = ContentFixtures.Create(later, session.Logger, catalog: reloader.Catalog, settlements: reloader.Settlements);
        var result = rebuilt.Reload();

        Assert.AreEqual(ContentReloadStatus.CommittedWithWarnings, result.Status);
        Assert.IsTrue(result.Removed.Contains(new VoxelDefinitionId(ModFixtures.SampleUuid, "copper")));
        Assert.IsFalse(reloader.Catalog.Current.TryGet(new VoxelDefinitionId(ModFixtures.SampleUuid, "copper"), out _));
        Assert.IsTrue(reloader.Catalog.Current.TryGet(new VoxelDefinitionId("core", "iron-ore"), out _));
    }

    [TestMethod]
    public void Pre_commit_rejection_retains_the_previous_snapshot_when_mod_removal_breaks_references()
    {
        using var session = LoggerSession.Create();
        var source = ModFixtures.CorePlusCopper();
        var referenced = new HashSet<VoxelDefinitionId> { new(ModFixtures.SampleUuid, "copper") };
        var reloader = ContentFixtures.Create(
            source,
            session.Logger,
            new ReferencedVoxelIdValidator(() => referenced));
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var previous = reloader.Catalog.Current!;

        var later = ContentFixtures.ValidIron();
        var rebuilt = ContentFixtures.Create(
            later,
            session.Logger,
            new ReferencedVoxelIdValidator(() => referenced),
            reloader.Catalog,
            reloader.Settlements);
        var rejected = rebuilt.Reload();

        Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status);
        Assert.AreSame(previous, reloader.Catalog.Current);
        Assert.IsTrue(previous.TryGet(new VoxelDefinitionId(ModFixtures.SampleUuid, "copper"), out _));
        Assert.IsTrue(rejected.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UnresolvedVoxelReference));
    }

    [TestMethod]
    public void Mods_root_failure_does_not_fail_built_in_content()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidIron();
        source.AddScanDiagnostic(ExternalContent.ModsRootUnavailable(
            ExternalContent.ModsGodotPath,
            "Mods directory could not be created."));
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.CommittedWithWarnings, result.Status);
        Assert.IsTrue(reloader.Catalog.Current!.TryGet(new VoxelDefinitionId("core", "iron-ore"), out _));
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.ModsRootUnavailable));
        Assert.IsTrue(session.Memory.Snapshot().Any(item =>
            item.EventId == ContentLog.InvalidEventId &&
            item.Level == LogLevel.Warning &&
            item.Attributes["code"] as string == ContentDiagnosticCodes.ModsRootUnavailable));
    }

    [TestMethod]
    public void Per_mod_good_limit_isolates_only_that_package()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidIron();
        source.AddMod("huge", ModFixtures.SampleUuid);
        for (var start = 1; start <= 1001; start += 250)
        {
            var end = Math.Min(start + 249, 1001);
            source.AddVoxelFile(
                ModFixtures.SampleUuid,
                $"batch-{start}.voxels.json",
                ContentFixtures.ManyGoods(start, end));
        }

        source.AddImage(ModFixtures.SampleUuid, "iron.png", ContentFixtures.Square(16));
        var reloader = ContentFixtures.Create(source, session.Logger);
        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.CommittedWithWarnings, result.Status);
        Assert.IsTrue(reloader.Catalog.Current!.TryGet(new VoxelDefinitionId("core", "iron-ore"), out _));
        Assert.AreEqual(1, reloader.Catalog.Current.Entries.Count);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.ModVoxelLimitExceeded));
    }

    [TestMethod]
    public void Diagnostic_cap_still_isolates_an_invalid_mod()
    {
        using var session = LoggerSession.Create();
        var goods = string.Join(",", Enumerable.Range(0, 150).Select(i =>
            ContentFixtures.Good($"item-{i}", " ", "说明")));
        var source = ContentFixtures.ValidIron();
        source.AddMod("noisy", ModFixtures.SampleUuid);
        source.AddVoxelFile(ModFixtures.SampleUuid, "many.voxels.json", $"{{\"schema_version\":1,\"voxels\":[{goods}]}}");
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.CommittedWithWarnings, result.Status);
        Assert.IsTrue(reloader.Catalog.Current!.TryGet(new VoxelDefinitionId("core", "iron-ore"), out _));
        Assert.AreEqual(ContentLimits.MaxDiagnosticsPerPackage, result.Diagnostics.Count(item => item.Code == ContentDiagnosticCodes.NameInvalid));
        Assert.IsFalse(reloader.Catalog.Current.Entries.Keys.Any(id => id.Namespace == ModFixtures.SampleUuid));
    }

    [TestMethod]
    public void Data_only_source_refuses_to_read_non_json_or_png_bytes()
    {
        var source = new MemoryContentSource();
        source.AddMod("code", ModFixtures.SampleUuid);
        var package = source.ListPackages().Single(item => item.Kind == ContentPackageKind.Mod);

        Assert.AreEqual(null, source.ReadFile(package, "plugin.dll", ContentLimits.JsonFileMaxBytes).Bytes);
        Assert.AreEqual(null, source.ReadFile(package, "script.cs", ContentLimits.JsonFileMaxBytes).Bytes);
        Assert.AreEqual(null, source.ReadFile(package, "hook.gd", ContentLimits.JsonFileMaxBytes).Bytes);
        Assert.AreEqual(null, source.ReadFile(package, "pack.pck", ContentLimits.JsonFileMaxBytes).Bytes);
        Assert.IsTrue(source.DecodePng(package, "plugin.dll").Failed);
    }

    [TestMethod]
    public void Built_in_packages_do_not_carry_mod_metadata()
    {
        var source = ContentFixtures.ValidIron();
        Assert.IsNull(source.ListPackages().Single(item => item.Kind == ContentPackageKind.BuiltIn).Mod);
        Assert.IsTrue(ModManifest.TryParse(
            System.Text.Encoding.UTF8.GetBytes(ModFixtures.Manifest(ModFixtures.SampleUuid)),
            "pretty/mod.json",
            out var manifest,
            out _));
        Assert.ThrowsException<ArgumentException>(() =>
            new ContentPackage("core", "memory://resources", true, ContentPackageKind.BuiltIn, null, new ModPackageMetadata(manifest!)));
        Assert.ThrowsException<ArgumentException>(() =>
            new ContentPackage(
                ModFixtures.OtherUuid,
                "memory://mods/x/resources",
                true,
                ContentPackageKind.Mod,
                "x/mod.json",
                new ModPackageMetadata(manifest!)));
    }

    [TestMethod]
    public void Missing_required_manifest_fields_commit_built_in_content_with_warnings()
    {
        var cases = new[]
        {
            ("{ \"schema_version\": 1, \"name\": \"A\", \"version\": \"1.0.0\", \"authors\": [\"A\"] }", ContentDiagnosticCodes.UuidInvalid, "/id"),
            ("{ \"schema_version\": 1, \"id\": \"" + ModFixtures.SampleUuid + "\", \"version\": \"1.0.0\", \"authors\": [\"A\"] }", ContentDiagnosticCodes.NameInvalid, "/name"),
            ("{ \"schema_version\": 1, \"id\": \"" + ModFixtures.SampleUuid + "\", \"name\": \"A\", \"authors\": [\"A\"] }", ContentDiagnosticCodes.VersionInvalid, "/version"),
            ("{ \"schema_version\": 1, \"id\": \"" + ModFixtures.SampleUuid + "\", \"name\": \"A\", \"version\": \"1.0.0\" }", ContentDiagnosticCodes.AuthorsInvalid, "/authors")
        };

        foreach (var (json, code, jsonPath) in cases)
        {
            using var session = LoggerSession.Create();
            var source = ContentFixtures.ValidIron();
            var scan = ModDiscovery.Scan(new[]
            {
                new ModFolderCandidate(
                    "broken",
                    "memory://mods/broken",
                    isDirectory: true,
                    manifestBytes: System.Text.Encoding.UTF8.GetBytes(json))
            });
            foreach (var diagnostic in scan.Diagnostics)
                source.AddScanDiagnostic(diagnostic);
            var reloader = ContentFixtures.Create(source, session.Logger);

            var result = reloader.Reload();

            Assert.AreEqual(ContentReloadStatus.CommittedWithWarnings, result.Status, json);
            Assert.AreEqual(0, scan.AcceptedCount, json);
            Assert.IsTrue(scan.Diagnostics.Any(item => item.Code == code && item.JsonPath == jsonPath), json);
            Assert.IsTrue(reloader.Catalog.Current!.TryGet(new VoxelDefinitionId("core", "iron-ore"), out _), json);
            Assert.IsFalse(reloader.Catalog.Current.Entries.Keys.Any(id => id.Namespace == ModFixtures.SampleUuid), json);
        }
    }

    [TestMethod]
    public void Mod_city_expands_to_the_package_uuid_namespace()
    {
        using var session = LoggerSession.Create();
        var source = ModFixtures.CorePlusCopper();
        ContentFixtures.AddExampleCity(
            source,
            ModFixtures.SampleUuid,
            "copper-town",
            "铜城",
            x: 5,
            y: 1,
            resources: new[] { $"{ModFixtures.SampleUuid}:copper" },
            resourcesRoot: "memory://mods/pretty-name/resources");
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.IsTrue(reloader.Settlements.TryGet(new SettlementId($"{ModFixtures.SampleUuid}:copper-town"), out var town));
        Assert.AreEqual("铜城", town.Name);
        Assert.IsFalse(reloader.Settlements.TryGet(new SettlementId("core:copper-town"), out _));
    }

    [TestMethod]
    public void Mod_city_with_unresolved_good_is_dropped_while_the_package_survives()
    {
        using var session = LoggerSession.Create();
        var source = ModFixtures.CorePlusCopper();
        ContentFixtures.AddExampleCity(
            source,
            ModFixtures.SampleUuid,
            "ok-town",
            "铜城",
            x: 5,
            y: 1,
            resources: new[] { $"{ModFixtures.SampleUuid}:copper" },
            resourcesRoot: "memory://mods/pretty-name/resources");
        source.AddSettlementsFile(
            ModFixtures.SampleUuid,
            "settlements/ghost.settlements.json",
            ContentFixtures.SettlementsFile(
                ContentFixtures.Settlement("ghost", "幽灵城", x: 8, y: 8, resources: new[] { "core:missing-ore" })),
            "memory://mods/pretty-name/resources");
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.CommittedWithWarnings, result.Status);
        Assert.IsTrue(reloader.Catalog.Current!.TryGet(new VoxelDefinitionId(ModFixtures.SampleUuid, "copper"), out _));
        Assert.IsTrue(reloader.Settlements.TryGet(new SettlementId($"{ModFixtures.SampleUuid}:ok-town"), out _));
        Assert.IsFalse(reloader.Settlements.TryGet(new SettlementId($"{ModFixtures.SampleUuid}:ghost"), out _));
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UnresolvedVoxelReference));
    }

    [TestMethod]
    public void Invalid_mod_settlements_file_isolates_that_mod_goods_as_well()
    {
        using var session = LoggerSession.Create();
        var source = ModFixtures.CorePlusCopper();
        source.AddSettlementsFile(
            ModFixtures.SampleUuid,
            "settlements/bad.settlements.json",
            "{ \"schema_version\": 2, \"settlements\": [] }",
            "memory://mods/pretty-name/resources");
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.CommittedWithWarnings, result.Status);
        Assert.IsTrue(reloader.Catalog.Current!.TryGet(new VoxelDefinitionId("core", "iron-ore"), out _));
        Assert.IsFalse(reloader.Catalog.Current.TryGet(new VoxelDefinitionId(ModFixtures.SampleUuid, "copper"), out _));
        Assert.IsTrue(reloader.Settlements.TryGet(new SettlementId("core:example-city"), out _));
        Assert.IsFalse(reloader.Settlements.Items.Any(item => item.Id.Value.StartsWith(ModFixtures.SampleUuid, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Duplicate_local_ids_and_positions_keep_the_first_survivor()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidIron();
        source.AddMod("towns", ModFixtures.SampleUuid);
        source.AddSettlementsFile(
            ModFixtures.SampleUuid,
            "settlements/dup.settlements.json",
            ContentFixtures.SettlementsFile(
                ContentFixtures.Settlement("twin", "甲", x: 3, y: 3),
                ContentFixtures.Settlement("twin", "乙", x: 4, y: 4),
                ContentFixtures.Settlement("first", "先到", x: 7, y: 7),
                ContentFixtures.Settlement("second", "后到", x: 7, y: 7)));
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.CommittedWithWarnings, result.Status);
        Assert.IsFalse(reloader.Settlements.TryGet(new SettlementId($"{ModFixtures.SampleUuid}:twin"), out _));
        Assert.IsTrue(reloader.Settlements.TryGet(new SettlementId($"{ModFixtures.SampleUuid}:first"), out var first));
        Assert.AreEqual("先到", first.Name);
        Assert.IsFalse(reloader.Settlements.TryGet(new SettlementId($"{ModFixtures.SampleUuid}:second"), out _));
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.DuplicateLocalId));
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.DuplicatePosition));
    }

    [TestMethod]
    public void Later_mod_cannot_take_a_built_in_city_position()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidIron();
        source.AddMod("overlap", ModFixtures.SampleUuid);
        ContentFixtures.AddExampleCity(
            source,
            ModFixtures.SampleUuid,
            "intruder",
            "入侵",
            x: 0,
            y: 0,
            resourcesRoot: "memory://mods/overlap/resources");
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.CommittedWithWarnings, result.Status);
        Assert.IsTrue(reloader.Settlements.TryGet(new SettlementId("core:example-city"), out var builtIn));
        Assert.AreEqual("示例城市", builtIn.Name);
        Assert.IsFalse(reloader.Settlements.TryGet(new SettlementId($"{ModFixtures.SampleUuid}:intruder"), out _));
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.DuplicatePosition));
    }

    [TestMethod]
    public void Invalid_built_in_package_still_rejects_even_when_a_mod_is_valid()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.CorePackage();
        source.AddVoxelFile("core", "bad.voxels.json", "{ \"schema_version\": 2, \"voxels\": [] }");
        source.AddMod("ok", ModFixtures.SampleUuid);
        source.AddVoxelFile(ModFixtures.SampleUuid, "copper.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("copper", "铜", "铜说明", icon: "copper.png", voxel: "copper.png")));
        source.AddImage(ModFixtures.SampleUuid, "copper.png", ContentFixtures.Square(16));
        var reloader = ContentFixtures.Create(source, session.Logger);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsNull(reloader.Catalog.Current);
        Assert.IsTrue(reloader.InitializationFailed);
    }
}

internal static class ModFixtures
{
    public const string SampleUuid = "550e8400-e29b-41d4-a716-446655440000";
    public const string OtherUuid = "11111111-2222-4333-8444-555555555555";

    public static MemoryContentSource CorePlusCopper()
    {
        var source = ContentFixtures.ValidIron();
        source.AddMod("pretty-name", SampleUuid);
        source.AddVoxelFile(SampleUuid, "natural/copper.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("copper", "铜", "铜说明", icon: "copper.png", voxel: "copper.png")));
        source.AddImage(SampleUuid, "copper.png", ContentFixtures.Square(16));
        return source;
    }

    public static string Manifest(
        string id,
        string name = "铜矿扩展",
        string version = "1.0.0",
        string[]? authors = null,
        string? description = "可选说明")
    {
        authors ??= new[] { "作者甲" };
        var authorJson = string.Join(",", authors.Select(Json));
        var descriptionJson = description is null ? "" : $",\"description\":{Json(description)}";
        return $"{{\"schema_version\":1,\"id\":{Json(id)},\"name\":{Json(name)},\"version\":{Json(version)},\"authors\":[{authorJson}]{descriptionJson}}}";
    }

    private static string Json(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }
}
