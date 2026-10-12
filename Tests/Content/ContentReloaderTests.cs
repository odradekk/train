using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Content.Behaviors;
using Train.Content.Voxels;

using Train.Content.Tests;

namespace Train.Content.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:content")]
public sealed class ContentReloaderTests
{
    [DataTestMethod]
    [DataRow("voxels")]
    [DataRow("composites")]
    [DataRow("market_tables")]
    [DataRow("settlement_types")]
    public void Definition_id_whitespace_rejects_every_format_and_preserves_the_snapshot(string format)
    {
        var session = new RecordingContentLogger();
        var source = MarketSettlementFixtures.BaseSource();
        var (file, json, _) = DefinitionFile(format);
        if (format == "settlement_types")
            json = MarketSettlementFixtures.Edit(json, "/settlement_types/0/preferred_terrain", "[]");
        source.AddDefinitionFile("core", file, json);
        var reloader = ContentFixtures.Create(source, session);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        var previous = reloader.Current;
        var notifications = 0;
        reloader.Changed += _ => notifications++;
        using var document = JsonDocument.Parse(json);
        var localId = document.RootElement.GetProperty(format)[0].GetProperty("id").GetString()!;
        var path = $"/{format}/0/id";
        foreach (var invalidId in new[] { " " + localId, localId + "\t" })
        {
            source.AddDefinitionFile("core", file,
                MarketSettlementFixtures.Edit(json, path, JsonSerializer.Serialize(invalidId)));

            var result = reloader.Reload();

            Assert.AreEqual(ContentReloadStatus.Rejected, result.Status, invalidId);
            Assert.IsFalse(result.Changed);
            Assert.AreSame(previous, reloader.Current);
            Assert.AreEqual(0, notifications);
            Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.LocalIdInvalid &&
                item.Source == file && item.JsonPath == path), invalidId);
        }
    }

    [DataTestMethod]
    [DataRow("voxels", true)]
    [DataRow("voxels", false)]
    [DataRow("composites", true)]
    [DataRow("composites", false)]
    [DataRow("market_tables", true)]
    [DataRow("market_tables", false)]
    [DataRow("settlement_types", true)]
    [DataRow("settlement_types", false)]
    public void Definition_array_errors_report_the_field_path_in_every_format(string format, bool missing)
    {
        var session = new RecordingContentLogger();
        var source = MarketSettlementFixtures.CompleteSource();
        var reloader = ContentFixtures.Create(source, session);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        var previous = reloader.Current;
        var (file, json, _) = DefinitionFile(format);
        var path = "/" + format;
        source.AddDefinitionFile("core", file, MarketSettlementFixtures.Edit(json, path, missing ? null : "null"));

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.AreSame(previous, reloader.Current);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.FieldInvalid &&
            item.Source == file && item.JsonPath == path && item.Severity == ContentDiagnosticSeverity.Error));
    }

    [DataTestMethod]
    [DataRow("voxels", true)]
    [DataRow("voxels", false)]
    [DataRow("composites", true)]
    [DataRow("composites", false)]
    [DataRow("market_tables", true)]
    [DataRow("market_tables", false)]
    [DataRow("settlement_types", true)]
    [DataRow("settlement_types", false)]
    public void Parse_rejection_severity_follows_the_package_requiredness(string format, bool required)
    {
        var session = new RecordingContentLogger();
        var source = MarketSettlementFixtures.CompleteSource();
        var reloader = ContentFixtures.Create(source, session);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        var previous = reloader.Current;
        const string packageId = "cccccccc-dddd-4eee-8fff-000000000002";
        const string resources = "memory://mods/incoming/resources";
        if (required)
            source.AddPackage(packageId, resources);
        else
            source.AddMod("incoming", packageId);
        source.AddImage(packageId, "iron.png", ContentFixtures.Square(16), resourcesRoot: resources);
        var (relativeFile, json, schema) = DefinitionFile(format);
        var file = "incoming/" + relativeFile;
        if (format is "market_tables" or "settlement_types")
            json = json.Replace("\"grain\"", "\"core:grain\"", StringComparison.Ordinal)
                .Replace("\"coal\"", "\"core:coal\"", StringComparison.Ordinal);
        var cases = new (string Json, string Code, string? Path)[]
        {
            ("{/* comment */" + json[1..], ContentDiagnosticCodes.InvalidJson, null),
            ($"{{\"schema_version\":{schema}," + json[1..], ContentDiagnosticCodes.DuplicateProperty, "/schema_version"),
            (MarketSettlementFixtures.Edit(json, "/schema_version", "0"), ContentDiagnosticCodes.SchemaVersionUnsupported, "/schema_version"),
            (MarketSettlementFixtures.Edit(json, "/extra", "true"), ContentDiagnosticCodes.UnknownField, "/extra")
        };
        foreach (var (invalid, code, path) in cases)
        {
            source.AddDefinitionFile(packageId, file, invalid, resources);
            var result = reloader.Reload();
            Assert.AreEqual(required ? ContentReloadStatus.Rejected : ContentReloadStatus.CommittedWithWarnings, result.Status, code);
            Assert.AreSame(previous, reloader.Current, code);
            var diagnostic = result.Diagnostics.Single(item => item.Code == code && item.Source == file && item.JsonPath == path);
            Assert.AreEqual(required ? ContentDiagnosticSeverity.Error : ContentDiagnosticSeverity.Warning, diagnostic.Severity, code);
            var logged = session.Snapshot().Last(item => item.EventId == ContentLog.InvalidEventId &&
                item.Attributes["code"] is string loggedCode && loggedCode == code && item.Attributes["source"] is string loggedFile && loggedFile == file);
            Assert.AreEqual(required ? LogLevel.Error : LogLevel.Warning, logged.Level, code);
        }
    }

    [DataTestMethod]
    [DataRow("voxels", true)]
    [DataRow("composites", false)]
    [DataRow("market_tables", true)]
    [DataRow("settlement_types", true)]
    public void Empty_definition_arrays_preserve_each_formats_rule(string format, bool accepted)
    {
        var session = new RecordingContentLogger();
        var source = MarketSettlementFixtures.CompleteSource();
        var (file, _, schema) = DefinitionFile(format);
        source.AddDefinitionFile("core", "empty/" + file, $"{{\"schema_version\":{schema},\"{format}\":[]}}");
        var result = ContentFixtures.Create(source, session).Reload();
        Assert.AreEqual(accepted, result.IsCommitted);
        if (!accepted)
            Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.FieldInvalid &&
                item.Source == "empty/" + file && item.JsonPath == "/composites"));
    }

    [TestMethod]
    public void Invalid_voxel_entry_rejects_the_entire_file_without_partial_acceptance()
    {
        var session = new RecordingContentLogger();
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(source, session);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        var previous = reloader.Current;
        const string file = "bad.voxels.json";
        source.AddDefinitionFile("core", file, ContentFixtures.GoodsFile(
            ContentFixtures.Good("first", "有效", "说明"), ContentFixtures.Good("second", " ", "说明")));

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.AreSame(previous, reloader.Current);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.NameInvalid &&
            item.Source == file && item.JsonPath == "/voxels/1/name"));
        var scanned = session.Snapshot().Single(item => item.EventId == ContentLog.FileScannedEventId &&
            item.Attributes["source"] is string path && path == file);
        Assert.AreEqual(0, scanned.Attributes["accepted"]);
    }

    private static (string File, string Json, int Schema) DefinitionFile(string format) => format switch
    {
        "voxels" => ("goods.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("grain", "粮食", "说明"), ContentFixtures.Good("coal", "煤", "说明")), 2),
        "composites" => ("pair.composites.json", ContentReloadExamples.BlueprintFile.Replace("core:structure", "core:grain", StringComparison.Ordinal), 2),
        "market_tables" => (MarketSettlementFixtures.MarketFile, MarketSettlementExamples.PricesFile, 1),
        "settlement_types" => (MarketSettlementFixtures.TypeFile, MarketSettlementExamples.DepotFile, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    [TestMethod]
    public void Combined_behaviors_load_as_typed_data_and_affect_semantic_revision()
    {
        var session = new RecordingContentLogger();
        var source = ContentFixtures.ValidFerrousOre();
        source.AddDefinitionFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明", behaviorsJson:
                """[{"type":"propulsion","traction":12.5,"rated_speed":20},{"type":"control"},{"type":"cargo","capacity":4}]""")));
        var reloader = ContentFixtures.Create(source, session);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        var snapshot = reloader.Current!;
        var entry = snapshot.Voxels[new VoxelDefinitionId("core", "ferrous-ore")];
        CollectionAssert.AreEqual(
            new VoxelBehavior[] { new CargoBehavior(4), new ControlBehavior(), new PropulsionBehavior(12.5, 20) },
            entry.Definition.Behaviors.ToArray());

        source.AddDefinitionFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明", behaviorsJson:
                """[{"type":"propulsion","traction":12.5,"rated_speed":21},{"type":"control"},{"type":"cargo","capacity":4}]""")));
        var result = reloader.Reload();

        Assert.IsTrue(result.IsCommitted);
        Assert.IsTrue(result.Changed);
        Assert.AreNotEqual(entry.SemanticRevision,
            reloader.Current!.Voxels[entry.Definition.Id].SemanticRevision);

        var revised = reloader.Current.Voxels[entry.Definition.Id];
        var previous = reloader.Current;
        var notifications = 0;
        reloader.Changed += _ => notifications++;
        source.AddDefinitionFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明", behaviorsJson:
                """[{"type":"cargo","capacity":4},{"type":"control"},{"type":"propulsion","traction":12.5,"rated_speed":21}]""")));
        var reorder = reloader.Reload();
        Assert.IsTrue(reorder.IsCommitted);
        Assert.IsFalse(reorder.Changed);
        Assert.AreSame(previous, reloader.Current);
        Assert.AreEqual(0, notifications);

        var fresh = ContentFixtures.Create(source, session);
        Assert.IsTrue(fresh.Reload().IsCommitted);
        var reordered = fresh.Current!.Voxels[entry.Definition.Id];
        Assert.AreEqual(revised.SemanticRevision, reordered.SemanticRevision);
        Assert.AreEqual(revised.PublicationFingerprint, reordered.PublicationFingerprint);
        CollectionAssert.AreEqual(revised.Definition.Behaviors.ToArray(), reordered.Definition.Behaviors.ToArray());
    }

    [TestMethod]
    public void Behavior_parameter_boundaries_are_accepted()
    {
        var session = new RecordingContentLogger();
        var source = ContentFixtures.CorePackage();
        source.AddDefinitionFile("core", "bounds.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("minimum", "最小", "说明", behaviorsJson:
                """[{"type":"propulsion","traction":5E-324,"rated_speed":5E-324},{"type":"cargo","capacity":1}]"""),
            ContentFixtures.Good("maximum", "最大", "说明", icon: "max.png", voxel: "max.png", behaviorsJson:
                """[{"type":"cargo","capacity":2147483647}]""")));
        ContentFixtures.AddSquareImages(source, "iron.png", "max.png");

        var reloader = ContentFixtures.Create(source, session);
        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.AreEqual(new CargoBehavior(1),
            reloader.Current!.Voxels[new VoxelDefinitionId("core", "minimum")].Definition.Behaviors.OfType<CargoBehavior>().Single());
        Assert.AreEqual(new CargoBehavior(int.MaxValue),
            reloader.Current.Voxels[new VoxelDefinitionId("core", "maximum")].Definition.Behaviors.Single());
    }

    [TestMethod]
    public void Initial_valid_voxel_catalog_commits()
    {
        var session = new RecordingContentLogger();
        var source = ContentFixtures.CorePackage();
        source.AddDefinitionFile("core", "voxels/ores.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "Ferrous Ore 说明"),
            ContentFixtures.Good("coal", "煤", "煤说明", icon: "coal.png", voxel: "coal.png")));
        ContentFixtures.AddSquareImages(source, "iron.png", "coal.png");
        var reloader = ContentFixtures.Create(source, session);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(1, result.Snapshot!.Generation);
        var snapshot = reloader.Current!;
        Assert.AreEqual(2, snapshot.Voxels.Count);
        Assert.IsTrue(snapshot.Voxels.TryGetValue(new VoxelDefinitionId("core", "ferrous-ore"), out var iron));
        Assert.AreEqual("Ferrous Ore", iron.Definition.Name);
        Assert.AreEqual("core", iron.Origin.Namespace);
        Assert.AreEqual("voxels/ores.voxels.json", iron.Origin.SourceFile);
        Assert.AreEqual(100, iron.Definition.Value);
        Assert.AreEqual(1, iron.Definition.Mass);
        Assert.AreEqual(100, iron.Definition.MaxDurability);
        Assert.IsTrue(iron.Definition.HasRawGoodTag);
        var entries = (ICollection<KeyValuePair<VoxelDefinitionId, VoxelContentEntry>>)snapshot.Voxels;
        Assert.IsTrue(entries.IsReadOnly);
        Assert.ThrowsException<NotSupportedException>(() =>
            entries.Add(new KeyValuePair<VoxelDefinitionId, VoxelContentEntry>(new VoxelDefinitionId("core", "x"), iron)));
    }

    [TestMethod]
    public void Strict_schema_violations_reject_the_package()
    {
        var valid = ContentFixtures.GoodsFile(ContentFixtures.Good("iron", "铁矿", "说明"));
        var cases = new (string Json, string Code, string? Path)[]
        {
            ("{ /* comment */ " + valid[1..], ContentDiagnosticCodes.InvalidJson, null),
            (valid[..^1] + ",}", ContentDiagnosticCodes.InvalidJson, null),
            (valid[..^1] + ",\"extra\":1}", ContentDiagnosticCodes.UnknownField, "/extra"),
            ("{\"schema_version\":2,\"voxels\":[1]}", ContentDiagnosticCodes.FieldInvalid, "/voxels/0"),
            (valid.Replace("\"schema_version\":2", "\"schema_version\":2,\"schema_version\":2", StringComparison.Ordinal),
                ContentDiagnosticCodes.DuplicateProperty, "/schema_version"),
            (valid.Replace("schema_version", "Schema_version", StringComparison.Ordinal),
                ContentDiagnosticCodes.UnknownField, "/Schema_version"),
            (valid.Replace("\"schema_version\":2", "\"schema_version\":1", StringComparison.Ordinal),
                ContentDiagnosticCodes.SchemaVersionUnsupported, "/schema_version"),
            (valid.Replace("\"schema_version\":2", "\"schema_version\":1.5", StringComparison.Ordinal),
                ContentDiagnosticCodes.SchemaVersionUnsupported, "/schema_version"),
            (valid.Replace("\"schema_version\":2,", "", StringComparison.Ordinal),
                ContentDiagnosticCodes.SchemaVersionUnsupported, "/schema_version")
        };

        foreach (var (json, code, path) in cases)
        {
            var session = new RecordingContentLogger();
            var source = ContentFixtures.CorePackage();
            source.AddDefinitionFile("core", "bad.voxels.json", json);
            ContentFixtures.AddSquareImages(source, "iron.png");
            var reloader = ContentFixtures.Create(source, session);
            var result = reloader.Reload();
            Assert.AreEqual(ContentReloadStatus.Rejected, result.Status, json);
            Assert.IsNull(reloader.Current);
            Assert.IsTrue(result.Diagnostics.Any(item => item.Code == code && item.Source == "bad.voxels.json" && item.JsonPath == path),
                string.Join(Environment.NewLine, result.Diagnostics.Select(item => $"{item.Code} {item.JsonPath}: {item.Message}")));
        }
    }

    [TestMethod]
    public void Field_and_value_rules_reject_invalid_definitions()
    {
        var invalids = new (string Json, string Code, string Path)[]
        {
            (ContentFixtures.Good("Iron-Ore", "铁矿", "说明"), ContentDiagnosticCodes.LocalIdInvalid, "/voxels/0/id"),
            (ContentFixtures.Good("_iron", "铁矿", "说明"), ContentDiagnosticCodes.LocalIdInvalid, "/voxels/0/id"),
            (ContentFixtures.Good(new string('a', 65), "铁矿", "说明"), ContentDiagnosticCodes.LocalIdInvalid, "/voxels/0/id"),
            (ContentFixtures.Good("iron", " ", "说明"), ContentDiagnosticCodes.NameInvalid, "/voxels/0/name"),
            (ContentFixtures.Good("iron", "铁矿", ""), ContentDiagnosticCodes.DescriptionInvalid, "/voxels/0/description"),
            (ContentFixtures.Good("iron", "铁矿", "说明", value: 0), ContentDiagnosticCodes.ValueInvalid, "/voxels/0/value"),
            (ContentFixtures.Good("iron", "铁矿", "说明", value: 1_000_000_001), ContentDiagnosticCodes.ValueInvalid, "/voxels/0/value"),
            (ContentFixtures.Good("iron", "铁矿", "说明", tagsJson: "[\"Raw_Good\"]"), ContentDiagnosticCodes.TagInvalid, "/voxels/0/tags/0"),
            (ContentFixtures.Good("iron", "铁矿", "说明", behaviorsJson: "[{\"type\":\"future\"}]"), ContentDiagnosticCodes.BehaviorUnregistered, "/voxels/0/behaviors/0/type"),
            (ContentFixtures.Good("iron", "铁矿", "说明", behaviorsJson: """[{"type":"propulsion"}]"""), ContentDiagnosticCodes.BehaviorUnregistered, "/voxels/0/behaviors/0"),
            (ContentFixtures.Good("iron", "铁矿", "说明", behaviorsJson: """[{"type":"propulsion","traction":1}]"""), ContentDiagnosticCodes.BehaviorUnregistered, "/voxels/0/behaviors/0"),
            (ContentFixtures.Good("iron", "铁矿", "说明", behaviorsJson: """[{"type":"propulsion","traction":0,"rated_speed":20}]"""), ContentDiagnosticCodes.BehaviorUnregistered, "/voxels/0/behaviors/0"),
            (ContentFixtures.Good("iron", "铁矿", "说明", behaviorsJson: """[{"type":"propulsion","traction":-1,"rated_speed":20}]"""), ContentDiagnosticCodes.BehaviorUnregistered, "/voxels/0/behaviors/0"),
            (ContentFixtures.Good("iron", "铁矿", "说明", behaviorsJson: """[{"type":"propulsion","traction":1e309,"rated_speed":20}]"""), ContentDiagnosticCodes.BehaviorUnregistered, "/voxels/0/behaviors/0/traction"),
            (ContentFixtures.Good("iron", "铁矿", "说明", behaviorsJson: """[{"type":"propulsion","traction":1,"rated_speed":0}]"""), ContentDiagnosticCodes.BehaviorUnregistered, "/voxels/0/behaviors/0"),
            (ContentFixtures.Good("iron", "铁矿", "说明", behaviorsJson: """[{"type":"propulsion","traction":1,"rated_speed":-1}]"""), ContentDiagnosticCodes.BehaviorUnregistered, "/voxels/0/behaviors/0"),
            (ContentFixtures.Good("iron", "铁矿", "说明", behaviorsJson: """[{"type":"propulsion","traction":1,"rated_speed":1e309}]"""), ContentDiagnosticCodes.BehaviorUnregistered, "/voxels/0/behaviors/0/rated_speed"),
            (ContentFixtures.Good("iron", "铁矿", "说明", behaviorsJson: """[{"type":"cargo"}]"""), ContentDiagnosticCodes.BehaviorUnregistered, "/voxels/0/behaviors/0/capacity"),
            (ContentFixtures.Good("iron", "铁矿", "说明", behaviorsJson: """[{"type":"cargo","capacity":0}]"""), ContentDiagnosticCodes.BehaviorUnregistered, "/voxels/0/behaviors/0/capacity"),
            (ContentFixtures.Good("iron", "铁矿", "说明", behaviorsJson: """[{"type":"cargo","capacity":2147483648}]"""), ContentDiagnosticCodes.BehaviorUnregistered, "/voxels/0/behaviors/0/capacity"),
            (ContentFixtures.Good("iron", "铁矿", "说明", behaviorsJson: """[{"type":"control"},{"type":"control"}]"""), ContentDiagnosticCodes.BehaviorUnregistered, "/voxels/0/behaviors/1"),
            (ContentFixtures.Good("iron", "铁矿", "说明", behaviorsJson: """[{"type":"control","capacity":1}]"""), ContentDiagnosticCodes.UnknownField, "/voxels/0/behaviors/0/capacity"),
            (ContentFixtures.Good("iron", "铁矿", "说明", behaviorsJson: """[{"type":"cargo","capacity":4},{"type":"cargo","capacity":4}]"""), ContentDiagnosticCodes.BehaviorUnregistered, "/voxels/0/behaviors/1"),
            (ContentFixtures.Good("iron", "铁矿", "说明", behaviorsJson: """[{"type":"track_contact"}]"""), ContentDiagnosticCodes.BehaviorUnregistered, "/voxels/0/behaviors/0/type"),
            (ContentFixtures.Good("iron", "铁矿", "说明", orientations: "free"), ContentDiagnosticCodes.OrientationInvalid, "/voxels/0/allowed_orientations"),
            (ContentFixtures.Good("iron", "铁矿", "说明", mass: 0), ContentDiagnosticCodes.MassInvalid, "/voxels/0/mass"),
            (ContentFixtures.Good("iron", "铁矿", "说明").Replace("\"mass\":1,", "", StringComparison.Ordinal), ContentDiagnosticCodes.MassInvalid, "/voxels/0/mass")
        };

        foreach (var (good, code, path) in invalids)
        {
            var session = new RecordingContentLogger();
            var source = ContentFixtures.ValidFerrousOre();
            source.AddDefinitionFile("core", "bad.voxels.json", ContentFixtures.GoodsFile(good));
            var reloader = ContentFixtures.Create(source, session);
            var result = reloader.Reload();
            Assert.AreEqual(ContentReloadStatus.Rejected, result.Status, good);
            Assert.IsNull(reloader.Current);
            Assert.IsNull(result.Snapshot);
            Assert.IsTrue(result.Diagnostics.Any(item => item.Code == code && item.Source == "bad.voxels.json" && item.JsonPath == path),
                string.Join(Environment.NewLine, result.Diagnostics.Select(item => $"{item.Code} {item.JsonPath}: {item.Message}")));
        }
    }

    [TestMethod]
    public void Text_is_trimmed_and_boundaries_are_accepted()
    {
        var session = new RecordingContentLogger();
        var source = ContentFixtures.CorePackage();
        source.AddDefinitionFile("core", "bounds.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("1ferrous-ore", "  铁矿  ", "  说明  ", value: 1),
            ContentFixtures.Good("coal", new string('名', 100), new string('说', 2000), value: 1_000_000_000, icon: "coal.png", voxel: "coal.png")));
        ContentFixtures.AddSquareImages(source, "iron.png", "coal.png");
        var reloader = ContentFixtures.Create(source, session);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        Assert.AreEqual("铁矿", reloader.Current!.Voxels[new VoxelDefinitionId("core", "1ferrous-ore")].Definition.Name);
        Assert.AreEqual(1_000_000_000, reloader.Current.Voxels[new VoxelDefinitionId("core", "coal")].Definition.Value);
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
            var session = new RecordingContentLogger();
            var source = ContentFixtures.CorePackage();
            source.AddDefinitionFile("core", "path.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("iron", "铁矿", "说明", icon: path, voxel: path.Length == 0 ? "iron.png" : path)));
            ContentFixtures.AddSquareImages(source, "iron.png");
            var result = ContentFixtures.Create(source, session).Reload();
            Assert.AreEqual(ContentReloadStatus.Rejected, result.Status, path);
            Assert.IsTrue(result.Diagnostics.Any(item => item.Code is ContentDiagnosticCodes.PathInvalid or ContentDiagnosticCodes.PresentationRequired), path);
        }
    }

    [TestMethod]
    public void Image_shape_and_size_rules_reject_incompatible_visuals()
    {
        var session = new RecordingContentLogger();
        Assert.AreEqual(ContentReloadStatus.Rejected, ReloadWithImage(session, "icon.png", ContentFixtures.Rectangle(8, 4)).Status);
        var session2 = new RecordingContentLogger();
        Assert.AreEqual(ContentReloadStatus.Rejected, ReloadWithFaces(session2, 6, 6, 6).Status);
        var session3 = new RecordingContentLogger();
        Assert.AreEqual(ContentReloadStatus.Rejected, ReloadWithFaces(session3, 16, 16, 32).Status);
        var session4 = new RecordingContentLogger();
        var missing = ContentFixtures.CorePackage();
        missing.AddDefinitionFile("core", "n.voxels.json", ContentFixtures.GoodsFile(ContentFixtures.Good("iron", "铁矿", "说明")));
        missing.AddMissingImage("core", "iron.png");
        Assert.AreEqual(ContentReloadStatus.Rejected, ContentFixtures.Create(missing, session4).Reload().Status);
        var session5 = new RecordingContentLogger();
        var huge = ContentFixtures.CorePackage();
        huge.AddDefinitionFile("core", "n.voxels.json", ContentFixtures.GoodsFile(ContentFixtures.Good("iron", "铁矿", "说明")));
        huge.AddOversizedImageBytes("core", "iron.png");
        Assert.AreEqual(ContentReloadStatus.Rejected, ContentFixtures.Create(huge, session5).Reload().Status);
    }

    [TestMethod]
    public void Duplicate_local_ids_invalidate_the_whole_package()
    {
        var session = new RecordingContentLogger();
        var source = ContentFixtures.CorePackage();
        source.AddDefinitionFile("core", "a.voxels.json", ContentFixtures.GoodsFile(ContentFixtures.Good("iron", "铁矿", "说明")));
        source.AddDefinitionFile("core", "b.voxels.json", ContentFixtures.GoodsFile(ContentFixtures.Good("iron", "另一铁矿", "说明")));
        ContentFixtures.AddSquareImages(source, "iron.png");
        var reloader = ContentFixtures.Create(source, session);
        var result = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.DuplicateLocalId));
        Assert.IsNull(reloader.Current);
    }

    [TestMethod]
    public void File_and_global_limits_reject_oversized_candidates()
    {
        var session = new RecordingContentLogger();
        var oversizedFile = ContentFixtures.CorePackage();
        oversizedFile.AddDefinitionFile("core", "big.voxels.json", new byte[ContentLimits.JsonFileMaxBytes + 1]);
        Assert.AreEqual(ContentReloadStatus.Rejected, ContentFixtures.Create(oversizedFile, session).Reload().Status);

        var session2 = new RecordingContentLogger();
        var source = ContentFixtures.CorePackage();
        ContentFixtures.AddSquareImages(source, "iron.png");
        for (var start = 1; start <= 10001; start += 1000)
        {
            var end = Math.Min(start + 999, 10001);
            source.AddDefinitionFile("core", $"batch-{start}.voxels.json", ContentFixtures.ManyGoods(start, end));
        }

        var result = ContentFixtures.Create(source, session2).Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.GlobalVoxelLimitExceeded &&
            item.Severity == ContentDiagnosticSeverity.Error));
    }

    [TestMethod]
    public void Diagnostics_are_capped_per_package_and_still_reject()
    {
        var session = new RecordingContentLogger();
        var source = ContentFixtures.CorePackage();
        for (var i = 0; i < ContentLimits.MaxDiagnosticsPerPackage + 1; i++)
            source.AddDefinitionFile("core", $"bad-{i}.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good($"item-{i}", " ", "说明")));
        var result = ContentFixtures.Create(source, session).Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.AreEqual(ContentLimits.MaxDiagnosticsPerPackage, result.Diagnostics.Count);
        Assert.IsTrue(result.Diagnostics.All(item => item.Code == ContentDiagnosticCodes.NameInvalid &&
            item.Severity == ContentDiagnosticSeverity.Error));
    }

    [TestMethod]
    public void Initial_empty_available_package_fails_initialization()
    {
        var session = new RecordingContentLogger();
        var source = ContentFixtures.CorePackage();
        var reloader = ContentFixtures.Create(source, session);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsFalse(result.IsCommitted);
        Assert.IsNull(reloader.Current);
        Assert.IsNull(result.Snapshot);
        Assert.IsTrue(session.Snapshot().Any(item =>
            item.Category == ContentLog.Category &&
            item.EventId == ContentLog.ReloadFailedEventId &&
            item.Level == LogLevel.Error));
    }

    [TestMethod]
    public void Later_empty_reload_can_publish_when_a_catalog_already_exists()
    {
        var session = new RecordingContentLogger();
        var source = new SwitchableContentSource(ContentFixtures.ValidFerrousOre());
        var reloader = ContentFixtures.Create(source, session);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        Assert.IsNotNull(reloader.Current);

        var empty = ContentFixtures.CorePackage();
        source.Current = empty;
        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.IsTrue(result.Changed);
        Assert.IsNotNull(reloader.Current);
        Assert.AreEqual(0, reloader.Current.Voxels.Count);
        Assert.AreEqual(2, reloader.Current.Generation);
    }

    [TestMethod]
    public void Scheme_prefixed_visual_paths_reject_while_colon_free_relative_pngs_commit()
    {
        foreach (var path in new[] { "file:secret.png", "https:secret.png" })
        {
            var session = new RecordingContentLogger();
            var source = ContentFixtures.CorePackage();
            source.AddDefinitionFile("core", "path.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("iron", "铁矿", "说明", icon: path, voxel: path)));
            ContentFixtures.AddSquareImages(source, "iron.png");
            var reloader = ContentFixtures.Create(source, session);
            var result = reloader.Reload();
            Assert.AreEqual(ContentReloadStatus.Rejected, result.Status, path);
            Assert.IsNull(reloader.Current);
            Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.PathInvalid), path);
        }

        var ok = new RecordingContentLogger();
        var valid = ContentFixtures.CorePackage();
        valid.AddDefinitionFile("core", "voxels/ores.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("iron", "铁矿", "说明", icon: "voxels/iron.png", voxel: "voxels/iron.png")));
        ContentFixtures.AddSquareImages(valid, "voxels/iron.png");
        var validReloader = ContentFixtures.Create(valid, ok);
        var committed = validReloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, committed.Status);
        Assert.IsNotNull(validReloader.Current);
        Assert.IsTrue(validReloader.Current.Voxels.TryGetValue(new VoxelDefinitionId("core", "iron"), out _));
    }

    [TestMethod]
    public void Invalid_discovery_rejects_instead_of_committing_partial_files()
    {
        var session = new RecordingContentLogger();
        var source = new InvalidDiscoverySource(ContentFixtures.ValidFerrousOre());
        var reloader = new ContentReloader(source, session);

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsFalse(result.IsCommitted);
        Assert.IsNull(reloader.Current);
        Assert.IsNull(result.Snapshot);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UnsafeLinkedPath));
    }

    [TestMethod]
    public void Initial_failure_does_not_publish_an_empty_catalog()
    {
        var session = new RecordingContentLogger();
        var source = ContentFixtures.CorePackage(available: false);
        var reloader = ContentFixtures.Create(source, session);
        var result = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsNull(reloader.Current);
        Assert.IsNull(result.Snapshot);
        Assert.IsTrue(session.Snapshot().Any(item =>
            item.Category == ContentLog.Category &&
            item.EventId == ContentLog.ReloadFailedEventId &&
            item.Level == LogLevel.Error));
    }

    [TestMethod]
    public void Rejection_keeps_the_previous_snapshot()
    {
        var session = new RecordingContentLogger();
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(source, session);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var previous = reloader.Current!;
        source.AddDefinitionFile("core", "bad.voxels.json", "{ \"schema_version\": 3, \"voxels\": [] }");
        var rejected = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status);
        Assert.AreSame(previous, reloader.Current);
        Assert.AreEqual(1, previous.Generation);
        Assert.IsTrue(previous.Voxels.TryGetValue(new VoxelDefinitionId("core", "ferrous-ore"), out _));
        Assert.AreSame(previous, rejected.Snapshot);
    }

    [TestMethod]
    public void Shared_voxel_faces_can_commit()
    {
        var session = new RecordingContentLogger();
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(source, session);
        var result = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.IsTrue(reloader.Current!.Voxels.TryGetValue(new VoxelDefinitionId("core", "ferrous-ore"), out _));
    }

    [TestMethod]
    public void Concurrent_reload_is_rejected_immediately()
    {
        var session = new RecordingContentLogger();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var source = new BlockingContentSource(ContentFixtures.ValidFerrousOre(), entered, release);
        var reloader = ContentFixtures.Create(source, session);
        var worker = Task.Run(reloader.Reload);
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            var second = reloader.Reload();
            Assert.AreEqual(ContentReloadStatus.Rejected, second.Status);
            Assert.IsTrue(second.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.ReloadAlreadyInProgress));
        }
        finally
        {
            release.Set();
            Assert.IsTrue(worker.Wait(TimeSpan.FromSeconds(5)));
        }
        Assert.IsTrue(worker.Result.IsCommitted);
        Assert.IsNotNull(reloader.Current);
    }

    [TestMethod]
    public void Unchanged_reload_keeps_version_and_does_not_notify()
    {
        var session = new RecordingContentLogger();
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(source, session);
        var first = reloader.Reload();
        var snapshot = reloader.Current;
        var notifications = 0;
        reloader.Changed += _ => notifications++;
        var second = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, second.Status);
        Assert.IsFalse(second.Changed);
        Assert.AreSame(first.Snapshot, second.Snapshot);
        Assert.AreSame(snapshot, reloader.Current);
        Assert.AreEqual(0, notifications);
        Assert.IsTrue(session.Snapshot().Any(item =>
            item.EventId == ContentLog.ReloadSummaryEventId &&
            item.Attributes["changed"] is false));
    }

    [TestMethod]
    public void Hash_matrix_separates_semantic_asset_and_catalog_generation()
    {
        var session = new RecordingContentLogger();
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(source, session);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var baseline = reloader.Current!.Voxels[new VoxelDefinitionId("core", "ferrous-ore")];
        var baselineSemantic = baseline.SemanticRevision;
        var baselineAsset = baseline.AssetRevisions["iron.png"];
        var baselineGeneration = reloader.Current.Generation;

        source.AddDefinitionFile("core", "voxels/ferrous-ore.voxels.json", """
            {"schema_version":2,"voxels":[{"allowed_orientations":"all_24","behaviors":[],"collision":{"kind":"full_box"},"description":"说明","id":"ferrous-ore","mass":1,"max_durability":100,"name":"Ferrous Ore","presentation":{"icon":"iron.png","kind":"cube","voxel_bottom":"iron.png","voxel_side":"iron.png","voxel_top":"iron.png"},"tags":["raw_good"],"value":100}]}
            """);
        var whitespace = reloader.Reload();
        Assert.IsFalse(whitespace.Changed);
        Assert.AreEqual(baselineGeneration, whitespace.Snapshot!.Generation);
        Assert.AreEqual(baselineSemantic, reloader.Current!.Voxels[new VoxelDefinitionId("core", "ferrous-ore")].SemanticRevision);

        source.AddDefinitionFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明", tagsJson: "[\"raw_good\",\"stone\"]")));
        var tagged = reloader.Reload();
        Assert.IsTrue(tagged.Changed);
        var afterTags = reloader.Current!.Voxels[new VoxelDefinitionId("core", "ferrous-ore")];
        Assert.AreNotEqual(baselineSemantic, afterTags.SemanticRevision);
        var taggedSemantic = afterTags.SemanticRevision;
        var taggedGeneration = reloader.Current.Generation;

        source.AddDefinitionFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明", tagsJson: "[\"stone\",\"raw_good\"]")));
        var reorderedTags = reloader.Reload();
        Assert.IsFalse(reorderedTags.Changed);
        Assert.AreEqual(taggedSemantic, reloader.Current!.Voxels[new VoxelDefinitionId("core", "ferrous-ore")].SemanticRevision);

        source.AddDefinitionFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "新亚铁矿", "新说明", tagsJson: "[\"stone\",\"raw_good\"]")));
        var display = reloader.Reload();
        Assert.IsTrue(display.Changed);
        var afterDisplay = reloader.Current!.Voxels[new VoxelDefinitionId("core", "ferrous-ore")];
        Assert.AreEqual(taggedSemantic, afterDisplay.SemanticRevision);
        Assert.AreEqual("新亚铁矿", afterDisplay.Definition.Name);
        Assert.AreEqual(taggedGeneration + 1, display.Snapshot!.Generation);

        var sameBytes = new byte[] { 7, 7, 7, 7 };
        var expectedAsset = Convert.ToHexString(SHA256.HashData(sameBytes));
        source.AddDefinitionFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "新亚铁矿", "新说明", tagsJson: "[\"stone\",\"raw_good\"]", icon: "copy.png", voxel: "copy.png")));
        source.AddImage("core", "copy.png", ContentFixtures.Square(16), rawBytes: sameBytes);
        source.AddImage("core", "iron.png", ContentFixtures.Square(16), rawBytes: sameBytes);
        var rebound = reloader.Reload();
        Assert.IsTrue(rebound.Changed);
        var afterBind = reloader.Current!.Voxels[new VoxelDefinitionId("core", "ferrous-ore")];
        Assert.AreEqual(taggedSemantic, afterBind.SemanticRevision);
        Assert.AreEqual(expectedAsset, afterBind.AssetRevisions["copy.png"]);

        source.AddDefinitionFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "新亚铁矿", "新说明", tagsJson: "[\"stone\",\"raw_good\"]")));
        var reboundSameBytes = reloader.Reload();
        Assert.IsTrue(reboundSameBytes.Changed);
        var afterSameBytes = reloader.Current!.Voxels[new VoxelDefinitionId("core", "ferrous-ore")];
        Assert.AreEqual(taggedSemantic, afterSameBytes.SemanticRevision);
        Assert.AreEqual(expectedAsset, afterSameBytes.AssetRevisions["iron.png"]);

        source.AddImage("core", "iron.png", ContentFixtures.Square(16, 9), rawBytes: new byte[] { 9, 9, 9, 9 });
        var pixels = reloader.Reload();
        Assert.IsTrue(pixels.Changed);
        var afterPixels = reloader.Current!.Voxels[new VoxelDefinitionId("core", "ferrous-ore")];
        Assert.AreEqual(taggedSemantic, afterPixels.SemanticRevision);
        Assert.AreNotEqual(expectedAsset, afterPixels.AssetRevisions["iron.png"]);
        Assert.AreNotEqual(baselineAsset, afterPixels.AssetRevisions["iron.png"]);

        ContentReloadResult ReloadGood(string voxel)
        {
            source.AddDefinitionFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(voxel));
            return reloader.Reload();
        }

        var previousSemantic = taggedSemantic;
        foreach (var voxel in new[]
                 {
                     ContentFixtures.Good("ferrous-ore", "新亚铁矿", "新说明", value: 250, tagsJson: "[\"stone\",\"raw_good\"]"),
                     ContentFixtures.Good("ferrous-ore", "新亚铁矿", "新说明", value: 250, mass: 2, tagsJson: "[\"stone\",\"raw_good\"]"),
                     ContentFixtures.Good("ferrous-ore", "新亚铁矿", "新说明", value: 250, mass: 2, maxDurability: 80, tagsJson: "[\"stone\",\"raw_good\"]"),
                     ContentFixtures.Good("ferrous-ore", "新亚铁矿", "新说明", value: 250, mass: 2, maxDurability: 80, tagsJson: "[\"stone\",\"raw_good\"]", orientations: "four_way")
                 })
        {
            var result = ReloadGood(voxel);
            Assert.IsTrue(result.Changed);
            var currentSemantic = reloader.Current!.Voxels[new VoxelDefinitionId("core", "ferrous-ore")].SemanticRevision;
            Assert.AreNotEqual(previousSemantic, currentSemantic);
            previousSemantic = currentSemantic;
        }
    }

    [TestMethod]
    public void Successful_reload_reports_added_removed_and_pixel_changes()
    {
        var session = new RecordingContentLogger();
        var initial = ContentFixtures.ValidFerrousOre();
        initial.AddDefinitionFile("core", "coal.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("coal", "煤", "煤说明", icon: "coal.png", voxel: "coal.png")));
        ContentFixtures.AddSquareImages(initial, "coal.png");
        var source = new SwitchableContentSource(initial);
        var reloader = ContentFixtures.Create(source, session);
        reloader.Reload();

        var notifications = new List<ContentChange>();
        reloader.Changed += notifications.Add;
        var previous = reloader.Current!;
        var updated = ContentFixtures.ValidFerrousOre();
        updated.AddDefinitionFile("core", "stone.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("stone", "石", "石说明", icon: "stone.png", voxel: "stone.png")));
        ContentFixtures.AddSquareImages(updated, "stone.png");
        updated.AddImage("core", "iron.png", ContentFixtures.Square(16, 9));
        source.Current = updated;
        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.AreEqual(1, notifications.Count);
        Assert.AreSame(previous, notifications[0].Previous);
        Assert.AreSame(result.Snapshot, notifications[0].Current);
        Assert.IsTrue(previous.Voxels.TryGetValue(new VoxelDefinitionId("core", "coal"), out _));
        Assert.IsFalse(reloader.Current!.Voxels.TryGetValue(new VoxelDefinitionId("core", "coal"), out _));
        Assert.AreNotSame(previous, reloader.Current);
    }

    [TestMethod]
    public void Rejected_content_file_still_emits_a_zero_accept_scan_event()
    {
        var session = new RecordingContentLogger();
        var source = ContentFixtures.ValidFerrousOre();
        source.AddDefinitionFile("core", "bad.voxels.json", "{ \"schema_version\": 3, \"voxels\": [] }");
        var reloader = ContentFixtures.Create(source, session);
        reloader.Reload();

        var bad = session.Snapshot().Single(item =>
            item.EventId == ContentLog.FileScannedEventId &&
            item.Attributes["source"] is "bad.voxels.json");
        Assert.AreEqual(LogLevel.Debug, bad.Level);
        Assert.AreEqual("voxels", bad.Attributes["kind"]);
        Assert.AreEqual("core", bad.Attributes["package"]);
        Assert.AreEqual(0, bad.Attributes["accepted"]);
        Assert.IsTrue(session.Snapshot().Any(item =>
            item.EventId == ContentLog.InvalidEventId &&
            item.Level == LogLevel.Error));
    }

    [TestMethod]
    public void Notify_exception_keeps_the_commit_and_allows_a_later_reload()
    {
        var session = new RecordingContentLogger();
        var source = ContentFixtures.ValidFerrousOre();
        var reloader = ContentFixtures.Create(source, session);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        source.AddDefinitionFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明", value: 101)));
        var secondSawCommit = false;
        reloader.Changed += _ => throw new InvalidOperationException("observer failure");
        reloader.Changed += _ => secondSawCommit = true;

        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.IsTrue(secondSawCommit);
        Assert.AreEqual(101, reloader.Current!.Voxels[new VoxelDefinitionId("core", "ferrous-ore")].Definition.Value);
        Assert.IsTrue(session.Snapshot().Any(item =>
            item.EventId == ContentLog.NotifyFailedEventId &&
            item.Level == LogLevel.Error));

        source.AddDefinitionFile("core", "voxels/ferrous-ore.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("ferrous-ore", "Ferrous Ore", "说明", value: 102)));
        var later = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, later.Status);
        Assert.AreEqual(102, reloader.Current!.Voxels[new VoxelDefinitionId("core", "ferrous-ore")].Definition.Value);
    }

    [TestMethod]
    public void Asset_hash_uses_the_same_bytes_that_passed_png_validation()
    {
        var session = new RecordingContentLogger();
        var inner = ContentFixtures.ValidFerrousOre();
        var source = new FlipAfterReadSource(inner);
        var reloader = ContentFixtures.Create(source, session);

        var first = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, first.Status);
        var accepted = reloader.Current!.Voxels[new VoxelDefinitionId("core", "ferrous-ore")].AssetRevisions["iron.png"];
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(FlipAfterReadSource.GoodBytes)), accepted);
        Assert.AreNotEqual(Convert.ToHexString(SHA256.HashData(FlipAfterReadSource.BadBytes)), accepted);

        var second = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, second.Status);
    }

    [TestMethod]
    public void Same_pixels_different_raw_png_bytes_change_only_the_asset_revision()
    {
        var session = new RecordingContentLogger();
        var firstBytes = new byte[] { 1, 2, 3, 4 };
        var secondBytes = new byte[] { 9, 8, 7, 6 };
        var source = ContentFixtures.ValidFerrousOre();
        source.AddImage("core", "iron.png", ContentFixtures.Square(16), rawBytes: firstBytes);
        var reloader = ContentFixtures.Create(source, session);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var baseline = reloader.Current!.Voxels[new VoxelDefinitionId("core", "ferrous-ore")];

        source.AddImage("core", "iron.png", ContentFixtures.Square(16), rawBytes: secondBytes);
        var result = reloader.Reload();

        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        Assert.IsTrue(result.Changed);
        var after = reloader.Current!.Voxels[new VoxelDefinitionId("core", "ferrous-ore")];
        Assert.AreEqual(baseline.SemanticRevision, after.SemanticRevision);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(firstBytes)), baseline.AssetRevisions["iron.png"]);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(secondBytes)), after.AssetRevisions["iron.png"]);
        Assert.AreNotEqual(baseline.AssetRevisions["iron.png"], after.AssetRevisions["iron.png"]);
    }

    private static ContentReloadResult ReloadWithImage(RecordingContentLogger session, string path, DecodedImage image)
    {
        var source = ContentFixtures.CorePackage();
        source.AddDefinitionFile("core", "n.voxels.json", ContentFixtures.GoodsFile(ContentFixtures.Good("iron", "铁矿", "说明")));
        source.AddImage("core", path, image);
        return ContentFixtures.Create(source, session).Reload();
    }

    private static ContentReloadResult ReloadWithFaces(RecordingContentLogger session, int top, int side, int bottom)
    {
        var source = ContentFixtures.CorePackage();
        source.AddDefinitionFile("core", "n.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("iron", "铁矿", "说明", icon: "icon.png", voxel: null, top: "top.png", side: "side.png", bottom: "bottom.png")));
        source.AddImage("core", "icon.png", ContentFixtures.Square(16));
        source.AddImage("core", "top.png", ContentFixtures.Square(top, 2));
        source.AddImage("core", "side.png", ContentFixtures.Square(side, 3));
        source.AddImage("core", "bottom.png", ContentFixtures.Square(bottom, 4));
        return ContentFixtures.Create(source, session).Reload();
    }
}

internal sealed class InvalidDiscoverySource : IContentSource
{
    private readonly IContentSource _inner;

    public InvalidDiscoverySource(IContentSource inner) => _inner = inner;

    public IReadOnlyList<ContentPackage> ListPackages() => _inner.ListPackages();

    public IReadOnlyList<ContentDiagnostic> ListScanDiagnostics() => _inner.ListScanDiagnostics();

    public ContentFileDiscovery ListDefinitionFiles(ContentPackage package)
    {
        var inner = _inner.ListDefinitionFiles(package);
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

    public ContentFileDiscovery ListDefinitionFiles(ContentPackage package) => _inner.ListDefinitionFiles(package);


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

internal sealed class BlockingContentSource(
    IContentSource inner,
    ManualResetEventSlim entered,
    ManualResetEventSlim release) : IContentSource
{
    public IReadOnlyList<ContentPackage> ListPackages()
    {
        entered.Set();
        release.Wait();
        return inner.ListPackages();
    }

    public IReadOnlyList<ContentDiagnostic> ListScanDiagnostics() => inner.ListScanDiagnostics();
    public ContentFileDiscovery ListDefinitionFiles(ContentPackage package) => inner.ListDefinitionFiles(package);
    public ContentBytesResult ReadFile(ContentPackage package, string normalizedRelativePath, int maxBytes) =>
        inner.ReadFile(package, normalizedRelativePath, maxBytes);
    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath) =>
        inner.DecodePng(package, normalizedRelativePath);
    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath, byte[] rawBytes) =>
        inner.DecodePng(package, normalizedRelativePath, rawBytes);
}
