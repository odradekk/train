// Core 内容模块测试：验证 schema 2 蓝图、设计属性、引用隔离与原子发布。
// 输入由最小内存夹具提供，期望属性均由夹具字段手工计算。
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Content.Composites;
using Train.Content.Voxels;
using Train.Content.Tests;

namespace Train.Content.Tests;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:composites")]
public sealed class CompositeContentTests
{
    private static readonly CompositeDefinitionId TestDefinitionId = new("core", "dev-train");

    [TestMethod]
    public void Built_in_blueprint_publishes_layout_and_hand_calculated_attributes()
    {
        var (reloader, logger) = CompositeFixtures.Load();
        using (logger)
        {
            Assert.IsTrue(reloader.Current!.Blueprints.TryGetValue(TestDefinitionId, out var entry));
            Assert.AreEqual(VehicleSizeClass.Small, entry.Definition.SizeClass);
            Assert.AreEqual(4, entry.Definition.Voxels.Count);
            Assert.AreEqual(10d, entry.Attributes.Mass);
            Assert.AreEqual(12.5d, entry.Attributes.Traction);
            Assert.AreEqual(20d, entry.Attributes.RatedSpeed);
            Assert.AreEqual(4L, entry.Attributes.CargoCapacity);
            Assert.AreEqual(100L, entry.Attributes.StructuralDurabilityMax);
            Assert.IsTrue(entry.Attributes.HasControl);
            Assert.AreEqual(100L, entry.Attributes.BomValue);
            Assert.AreEqual(1, entry.Attributes.Bom[new VoxelDefinitionId("core", CompositeFixtures.StructureId)]);
            Assert.AreEqual(1, entry.Attributes.Bom[new VoxelDefinitionId("core", CompositeFixtures.PropulsionId)]);
            Assert.AreEqual(1, entry.Attributes.Bom[new VoxelDefinitionId("core", CompositeFixtures.ControlId)]);
            Assert.AreEqual(1, entry.Attributes.Bom[new VoxelDefinitionId("core", CompositeFixtures.CargoId)]);
        }
    }

    [DataTestMethod]
    [DataRow(" " + CompositeFixtures.StructureId)]
    [DataRow("core:" + CompositeFixtures.StructureId + " ")]
    public void Voxel_reference_whitespace_rejects_the_blueprint(string reference)
    {
        using var session = LoggerSession.Create();
        var source = CompositeFixtures.VehiclePackage();
        const string file = "composites/dev-train.composites.json";
        source.AddDefinitionFile("core", file, CompositeFixtures.File(null,
            CompositeFixtures.Composite(voxels: CompositeFixtures.Voxel(0, 0, 0, reference))));
        var result = ContentFixtures.Create(source, session.Logger).Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.LocalIdInvalid &&
                item.Source == file && item.JsonPath == "/composites/0/voxels/0/definition"),
            string.Join(Environment.NewLine, result.Diagnostics.Select(item => $"{item.Code} {item.JsonPath}: {item.Message}")));
    }

    [TestMethod]
    public void Unknown_fields_and_old_schema_reject_built_in_blueprints()
    {
        var composite = CompositeFixtures.Composite();
        var valid = CompositeFixtures.File(null, composite);
        var cases = new (string Json, string Code, string Path)[]
        {
            (valid.Replace("\"schema_version\":2", "\"schema_version\":1", StringComparison.Ordinal),
                ContentDiagnosticCodes.SchemaVersionUnsupported, "/schema_version"),
            (valid[..^1] + ",\"archetypes\":[]}", ContentDiagnosticCodes.UnknownField, "/archetypes"),
            (valid[..^1] + ",\"extra\":true}", ContentDiagnosticCodes.UnknownField, "/extra"),
            ("{\"schema_version\":2,\"composites\":[1]}", ContentDiagnosticCodes.FieldInvalid, "/composites/0"),
            (CompositeFixtures.File(null, CompositeFixtures.Composite(voxels: "{\"x\":0,\"y\":0,\"z\":0,\"definition\":1,\"orientation\":\"px_py\"}")),
                ContentDiagnosticCodes.LocalIdInvalid, "/composites/0/voxels/0/definition"),
            (CompositeFixtures.File(null, composite[..^1] + ",\"contacts\":[]}"),
                ContentDiagnosticCodes.UnknownField, "/composites/0/contacts"),
            (CompositeFixtures.File(null, composite[..^1] + ",\"archetype\":\"vehicle\"}"),
                ContentDiagnosticCodes.UnknownField, "/composites/0/archetype")
        };
        foreach (var (json, code, path) in cases)
        {
            using var session = LoggerSession.Create();
            var source = CompositeFixtures.VehiclePackage();
            const string file = "composites/dev-train.composites.json";
            source.AddDefinitionFile("core", file, json);
            var result = ContentFixtures.Create(source, session.Logger).Reload();
            Assert.AreEqual(ContentReloadStatus.Rejected, result.Status, json);
            Assert.IsTrue(result.Diagnostics.Any(item => item.Code == code && item.Source == file && item.JsonPath == path),
                string.Join(Environment.NewLine, result.Diagnostics.Select(item => $"{item.Code} {item.JsonPath}: {item.Message}")));
        }
    }

    [TestMethod]
    public void Duplicate_composite_ids_invalidate_the_owning_package()
    {
        using var session = LoggerSession.Create();
        var source = CompositeFixtures.VehiclePackage();
        source.AddDefinitionFile("core", "other.composites.json", CompositeFixtures.File(null, CompositeFixtures.Composite()));
        Assert.AreEqual(ContentReloadStatus.Rejected, ContentFixtures.Create(source, session.Logger).Reload().Status);
    }

    [TestMethod]
    public void Invalid_mod_blueprint_is_isolated_without_rejecting_built_in()
    {
        using var session = LoggerSession.Create();
        const string broken = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
        var source = CompositeFixtures.VehiclePackage();
        source.AddMod("broken", broken);
        source.AddDefinitionFile(broken, "composites/bad.composites.json",
            CompositeFixtures.File(null, CompositeFixtures.Composite(id: "bad", voxels: CompositeFixtures.Voxel(0, 0, 0, "missing-part"))),
            "memory://mods/broken/resources");

        var result = ContentFixtures.Create(source, session.Logger).Reload();
        Assert.IsTrue(result.IsCommitted);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Severity == ContentDiagnosticSeverity.Warning));
    }

    [TestMethod]
    public void Removing_an_invalid_mod_cascades_to_a_mod_that_references_its_voxel()
    {
        using var session = LoggerSession.Create();
        const string broken = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
        const string dependent = "bbbbbbbb-cccc-4ddd-8eee-ffffffffffff";
        var source = CompositeFixtures.VehiclePackage();
        source.AddMod("broken", broken);
        source.AddMod("dependent", dependent);
        source.AddDefinitionFile(broken, "voxels/shared-part.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("shared-part", "共享部件", "说明", tagsJson: "[]")),
            "memory://mods/broken/resources");
        source.AddImage(broken, "iron.png", ContentFixtures.Square(16), resourcesRoot: "memory://mods/broken/resources");
        source.AddDefinitionFile(broken, "composites/bad.composites.json", "{\"schema_version\":2,\"extra\":true}",
            "memory://mods/broken/resources");
        source.AddDefinitionFile(dependent, "composites/dependent.composites.json", CompositeFixtures.File(null,
            CompositeFixtures.Composite(id: "dependent", voxels: CompositeFixtures.Voxel(0, 0, 0, broken + ":shared-part"))),
            "memory://mods/dependent/resources");

        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        Assert.IsTrue(reloader.Current!.Blueprints.TryGetValue(TestDefinitionId, out _));
        Assert.IsFalse(reloader.Current!.Voxels.TryGetValue(new VoxelDefinitionId(broken, "shared-part"), out _));
        Assert.IsFalse(reloader.Current.Blueprints.TryGetValue(new CompositeDefinitionId(dependent, "dependent"), out _));
    }

    [TestMethod]
    public void Player_blueprint_uses_file_uuid_namespace_and_display_name()
    {
        using var session = LoggerSession.Create();
        const string uuid = "cccccccc-dddd-4eee-8fff-000000000001";
        var source = CompositeFixtures.VehiclePackage();
        source.AddBlueprint(uuid, CompositeFixtures.File("我的手写蓝图", CompositeFixtures.Composite(
            id: "player-car",
            voxels: string.Join(",",
                CompositeFixtures.Voxel(0, 0, 0, "core:vehicle-shell"),
                CompositeFixtures.Voxel(1, 0, 0, "core:control-unit")))));
        var playerPackage = source.ListPackages().Single(package => package.Kind == ContentPackageKind.PlayerBlueprint);
        // 通用发现会列出这些文件，加载器只读取 UUID 同名的根级蓝图。
        foreach (var ignored in new[]
                 {
                     "extra.voxels.json", "extra.composites.json", "extra.market-tables.json",
                     "extra.settlement-types.json", "nested/" + uuid + ".composites.json"
                 })
            source.AddDefinitionFile(uuid, ignored, "invalid JSON", playerPackage.ResourcesRoot);
        var reloader = ContentFixtures.Create(source, session.Logger);

        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        Assert.IsTrue(reloader.Current!.Blueprints.TryGetValue(new CompositeDefinitionId(uuid, "player-car"), out var entry));
        Assert.AreEqual("我的手写蓝图", entry.FileDisplayName);
        Assert.AreEqual(3d, entry.Attributes.Mass);
        Assert.IsTrue(entry.Attributes.HasControl);
        Assert.IsFalse(reloader.Current.Voxels.Keys.Any(id => id.Namespace == uuid));
        var scanned = session.Memory.Snapshot().Where(item => item.EventId == ContentLog.FileScannedEventId &&
            item.Attributes["package"] is string packageId && packageId == uuid).ToArray();
        Assert.AreEqual(1, scanned.Length);
        Assert.AreEqual(uuid + ".composites.json", scanned.Single().Attributes["source"]);
    }

    [TestMethod]
    public void Missing_required_fields_reject_built_in_and_preserve_current_snapshot()
    {
        var switchable = new SwitchableContentSource(CompositeFixtures.VehiclePackage());
        using var logger = LoggerSession.Create();
        var baseline = ContentFixtures.Create(switchable, logger.Logger);
        Assert.IsTrue(baseline.Reload().IsCommitted);
        using (logger)
        {
            var snapshot = baseline.Current;
            var cases = new[]
            {
                ("id", ContentDiagnosticCodes.LocalIdInvalid),
                ("name", ContentDiagnosticCodes.NameInvalid),
                ("description", ContentDiagnosticCodes.DescriptionInvalid),
                ("size_class", ContentDiagnosticCodes.SizeClassInvalid),
                ("voxels", ContentDiagnosticCodes.LayoutInvalid)
            };
            foreach (var (field, code) in cases)
            {
                var source = CompositeFixtures.VehiclePackage();
                source.AddDefinitionFile("core", "composites/dev-train.composites.json",
                    CompositeFixtures.File(null, CompositeWithout(field)));
                switchable.Current = source;
                var result = baseline.Reload();
                Assert.AreEqual(ContentReloadStatus.Rejected, result.Status, field);
                Assert.AreSame(snapshot, baseline.Current, field);
                Assert.IsTrue(result.Diagnostics.Any(item => item.Code == code &&
                    item.Source == "composites/dev-train.composites.json" && item.JsonPath == $"/composites/0/{field}"),
                    string.Join(Environment.NewLine, result.Diagnostics.Select(item => $"{item.Code} {item.JsonPath}: {item.Message}")));
            }
        }
    }

    [TestMethod]
    public void Layout_change_recalculates_attributes_and_semantic_revision()
    {
        var source = CompositeFixtures.VehiclePackage();
        var (reloader, logger) = CompositeFixtures.Load(source);
        using (logger)
        {
            var before = reloader.Current!.Blueprints[TestDefinitionId];
            source.AddDefinitionFile("core", "composites/dev-train.composites.json", CompositeFixtures.File(null,
                CompositeFixtures.Composite(voxels: string.Join(",",
                    CompositeFixtures.Voxel(0, 0, 0),
                    CompositeFixtures.Voxel(1, 0, 0, CompositeFixtures.ControlId)))));

            Assert.IsTrue(reloader.Reload().Changed);
            var after = reloader.Current.Blueprints[TestDefinitionId];
            Assert.AreNotEqual(before.SemanticRevision, after.SemanticRevision);
            Assert.AreEqual(3d, after.Attributes.Mass);
            Assert.AreEqual(30L, after.Attributes.BomValue);
            Assert.AreEqual(100L, after.Attributes.StructuralDurabilityMax);
            Assert.IsTrue(after.Attributes.HasControl);
        }
    }

    [TestMethod]
    public void Dependency_values_and_behaviors_recalculate_attributes_without_changing_blueprint_semantics()
    {
        var source = CompositeFixtures.VehiclePackage();
        var (reloader, logger) = CompositeFixtures.Load(source);
        using (logger)
        {
            var before = reloader.Current!.Blueprints[TestDefinitionId];
            CompositeFixtures.AddVoxel(source, CompositeFixtures.PropulsionId, "动力", 35, 3.5,
                behaviorsJson: "[{\"type\":\"propulsion\",\"traction\":25,\"rated_speed\":30}]");
            CompositeFixtures.AddVoxel(source, CompositeFixtures.CargoId, "货舱", 45, 4.5,
                behaviorsJson: "[{\"type\":\"cargo\",\"capacity\":6}]");

            Assert.IsTrue(reloader.Reload().Changed);
            var after = reloader.Current.Blueprints[TestDefinitionId];
            Assert.AreEqual(before.SemanticRevision, after.SemanticRevision);
            Assert.AreEqual(11d, after.Attributes.Mass);
            Assert.AreEqual(25d, after.Attributes.Traction);
            Assert.AreEqual(30d, after.Attributes.RatedSpeed);
            Assert.AreEqual(6L, after.Attributes.CargoCapacity);
            Assert.AreEqual(110L, after.Attributes.BomValue);
        }
    }

    [TestMethod]
    public void Display_and_texture_changes_stay_out_of_blueprint_semantics_and_attributes()
    {
        var source = CompositeFixtures.VehiclePackage();
        var (reloader, logger) = CompositeFixtures.Load(source);
        using (logger)
        {
            var before = reloader.Current!.Blueprints[TestDefinitionId];
            source.AddDefinitionFile("core", "composites/dev-train.composites.json",
                CompositeFixtures.File("展示名", CompositeFixtures.Composite(name: "新名称", description: "新说明")));
            Assert.IsTrue(reloader.Reload().Changed);
            var display = reloader.Current.Blueprints[TestDefinitionId];
            Assert.AreEqual(before.SemanticRevision, display.SemanticRevision);
            Assert.AreNotEqual(before.PublicationFingerprint, display.PublicationFingerprint);
            AssertAttributesEqual(before.Attributes, display.Attributes);

            var snapshot = reloader.Current;
            var voxelId = new VoxelDefinitionId("core", CompositeFixtures.StructureId);
            source.AddImage("core", "iron.png", ContentFixtures.Square(16, 9), rawBytes: new byte[] { 9, 9, 9 });
            Assert.IsTrue(reloader.Reload().Changed);
            Assert.AreNotSame(snapshot, reloader.Current);
            Assert.AreEqual(snapshot.Generation + 1, reloader.Current!.Generation);
            Assert.AreNotEqual(snapshot.Voxels[voxelId].AssetRevisions["iron.png"],
                reloader.Current.Voxels[voxelId].AssetRevisions["iron.png"]);
            var texture = reloader.Current.Blueprints[TestDefinitionId];
            Assert.AreEqual(display.SemanticRevision, texture.SemanticRevision);
            Assert.AreEqual(display.PublicationFingerprint, texture.PublicationFingerprint);
            AssertAttributesEqual(display.Attributes, texture.Attributes);
        }
    }

    [TestMethod]
    public void Attribute_overflow_rejects_reload_and_preserves_current_snapshot()
    {
        var source = CompositeFixtures.VehiclePackage();
        source.AddDefinitionFile("core", "composites/dev-train.composites.json", CompositeFixtures.File(null,
            CompositeFixtures.Composite(voxels: string.Join(",",
                CompositeFixtures.Voxel(0, 0, 0, CompositeFixtures.PropulsionId),
                CompositeFixtures.Voxel(1, 0, 0, CompositeFixtures.PropulsionId)))));
        var (reloader, logger) = CompositeFixtures.Load(source);
        using (logger)
        {
            var snapshot = reloader.Current;
            CompositeFixtures.AddVoxel(source, CompositeFixtures.PropulsionId, "动力", 30, 3,
                behaviorsJson: "[{\"type\":\"propulsion\",\"traction\":1.7976931348623157E+308,\"rated_speed\":20}]");

            Assert.AreEqual(ContentReloadStatus.Rejected, reloader.Reload().Status);
            Assert.AreSame(snapshot, reloader.Current);
        }
    }

    /// <summary>比较全部设计属性和 BOM，不依赖对象实例或映射的枚举顺序。</summary>
    private static void AssertAttributesEqual(DesignAttributes expected, DesignAttributes actual)
    {
        Assert.AreEqual(expected.Mass, actual.Mass, nameof(DesignAttributes.Mass));
        Assert.AreEqual(expected.Traction, actual.Traction, nameof(DesignAttributes.Traction));
        Assert.AreEqual(expected.RatedSpeed, actual.RatedSpeed, nameof(DesignAttributes.RatedSpeed));
        Assert.AreEqual(expected.CargoCapacity, actual.CargoCapacity, nameof(DesignAttributes.CargoCapacity));
        Assert.AreEqual(expected.StructuralDurabilityMax, actual.StructuralDurabilityMax, nameof(DesignAttributes.StructuralDurabilityMax));
        Assert.AreEqual(expected.HasControl, actual.HasControl, nameof(DesignAttributes.HasControl));
        Assert.AreEqual(expected.BomValue, actual.BomValue, nameof(DesignAttributes.BomValue));
        CollectionAssert.AreEquivalent(expected.Bom.ToArray(), actual.Bom.ToArray(), nameof(DesignAttributes.Bom));
    }

    private static string CompositeWithout(string field)
    {
        var json = CompositeFixtures.Composite();
        return field switch
        {
            "id" => json.Replace("\"id\":\"dev-train\",", "", StringComparison.Ordinal),
            "name" => json.Replace("\"name\":\"开发列车\",", "", StringComparison.Ordinal),
            "description" => json.Replace("\"description\":\"测试车辆\",", "", StringComparison.Ordinal),
            "size_class" => json.Replace("\"size_class\":\"small\",", "", StringComparison.Ordinal),
            "voxels" => json[..json.IndexOf(",\"voxels\"", StringComparison.Ordinal)] + "}",
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
    }
}
