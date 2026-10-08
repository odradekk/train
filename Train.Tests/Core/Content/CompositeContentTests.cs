// Core 内容模块测试：验证 schema 2 蓝图、设计属性、引用隔离与原子发布。
// 输入由最小内存夹具提供，期望属性均由夹具字段手工计算。
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;
using Train.Tests.Core.Logging;

namespace Train.Tests.Core.Content;

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
        // 通用发现会列出这些文件，只有蓝图格式可以解释 UUID 同名的根级文档。
        foreach (var ignored in new[] { "extra.voxels.json", "extra.composites.json", "nested/" + uuid + ".composites.json" })
            source.AddDefinitionFile(uuid, ignored, "invalid JSON", playerPackage.ResourcesRoot);
        var reloader = ContentFixtures.Create(source, session.Logger);

        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        Assert.IsTrue(reloader.Current!.Blueprints.TryGetValue(new CompositeDefinitionId(uuid, "player-car"), out var entry));
        Assert.AreEqual("我的手写蓝图", entry.FileDisplayName);
        Assert.AreEqual(3d, entry.Attributes.Mass);
        Assert.IsTrue(entry.Attributes.HasControl);
        Assert.IsFalse(reloader.Current.Voxels.Keys.Any(id => id.Namespace == uuid));
    }

    [TestMethod]
    public void Missing_required_fields_reject_built_in_and_keep_both_snapshots()
    {
        var switchable = new SwitchableContentSource(CompositeFixtures.VehiclePackage());
        using var logger = LoggerSession.Create();
        var baseline = ContentFixtures.Create(switchable, logger.Logger);
        Assert.IsTrue(baseline.Reload().IsCommitted);
        using (logger)
        {
            var voxelSnapshot = baseline.Current;
            var blueprintSnapshot = baseline.Current;
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
                Assert.AreSame(voxelSnapshot, baseline.Current, field);
                Assert.AreSame(blueprintSnapshot, baseline.Current, field);
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
            Assert.AreEqual(before.Attributes.BomValue, display.Attributes.BomValue);

            var blueprintSnapshot = reloader.Current;
            source.AddImage("core", "iron.png", ContentFixtures.Square(16, 9), rawBytes: new byte[] { 9, 9, 9 });
            Assert.IsTrue(reloader.Reload().Changed);
            Assert.AreNotSame(blueprintSnapshot, reloader.Current);
            Assert.AreEqual(blueprintSnapshot.Generation + 1, reloader.Current!.Generation);
        }
    }

    [TestMethod]
    public void Attribute_overflow_rejects_reload_and_keeps_both_snapshots()
    {
        var source = CompositeFixtures.VehiclePackage();
        source.AddDefinitionFile("core", "composites/dev-train.composites.json", CompositeFixtures.File(null,
            CompositeFixtures.Composite(voxels: string.Join(",",
                CompositeFixtures.Voxel(0, 0, 0, CompositeFixtures.PropulsionId),
                CompositeFixtures.Voxel(1, 0, 0, CompositeFixtures.PropulsionId)))));
        var (reloader, logger) = CompositeFixtures.Load(source);
        using (logger)
        {
            var voxelSnapshot = reloader.Current;
            var blueprintSnapshot = reloader.Current;
            CompositeFixtures.AddVoxel(source, CompositeFixtures.PropulsionId, "动力", 30, 3,
                behaviorsJson: "[{\"type\":\"propulsion\",\"traction\":1.7976931348623157E+308,\"rated_speed\":20}]");

            Assert.AreEqual(ContentReloadStatus.Rejected, reloader.Reload().Status);
            Assert.AreSame(voxelSnapshot, reloader.Current);
            Assert.AreSame(blueprintSnapshot, reloader.Current);
        }
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
