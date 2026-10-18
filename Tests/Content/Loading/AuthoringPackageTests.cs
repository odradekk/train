using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class AuthoringPackageTests
{
    [TestMethod]
    [TestCategory("integration")]
    public void Disk_source_is_fixed_before_edits_and_written_patch_is_read_by_the_production_loader()
    {
        var run = Path.Combine(Path.GetTempPath(), "authoring-" + Guid.NewGuid().ToString("N"));
        var root = new C.ContentRoot(Path.Combine(run, "core"));
        var mods = new C.ContentRoot(Path.Combine(run, "mods"));
        const string definition = "resources/voxels/block.voxel.json";
        try
        {
            Directory.CreateDirectory(Path.Combine(root.Key, "resources/voxels/block"));
            Directory.CreateDirectory(mods.Key);
            File.WriteAllText(Path.Combine(root.Key, definition), ContentFixture.VoxelJson);
            File.WriteAllBytes(Path.Combine(root.Key, "resources/voxels/block/tile.png"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root.Key, "resources/voxels/block/icon.png"), [4, 5, 6]);
            var files = new ContentDiskFiles();
            var factory = ContentFactory.Create();
            var inspector = new ContentFixture();
            inspector.AddCube();
            var installation = new C.ContentInstallation(root, mods, null, "0.2.0", []);
            using var initial = Success(factory.PrepareLoad(new(installation, new C.ContentLoadPurpose.Initial()), files, inspector));
            var package = Success(factory.OpenAuthoringPackage(root, new("core"), new("original disk"), files));
            File.WriteAllText(Path.Combine(root.Key, definition), "external edit");
            var voxel = (C.DefinitionDraft.Voxel)Read(package, C.DefinitionKind.Voxel, "block");
            Assert.AreEqual("方块", voxel.Value.Display.Name);
            var changed = voxel with { Value = voxel.Value with { Display = new("磁盘补丁", "普通重载") } };
            var plan = Plan(package, new C.PackageEdit.WriteDefinition(changed));
            Assert.AreEqual("external edit", File.ReadAllText(Path.Combine(root.Key, definition)));
            foreach (var change in plan.Changes) File.WriteAllBytes(Path.Combine(root.Key, change.RelativePath), change.Bytes!.Value.ToArray());
            using var reloaded = Success(factory.PrepareLoad(new(installation,
                new C.ContentLoadPurpose.Reload(initial.View, new([], [], [], [initial.View.Core.Identity]))), files, inspector));
            Assert.AreEqual(changed.Value.Display, WorldContentFixtures.Found(reloaded.View.FindVoxelDisplay(new("core:block"))).Display);
            Assert.AreEqual("方块", WorldContentFixtures.Found(initial.View.FindVoxelDisplay(new("core:block"))).Display.Name);
        }
        finally { if (Directory.Exists(run)) Directory.Delete(run, recursive: true); }
    }

    [TestMethod]
    public void Edited_voxel_patch_loads_through_the_ordinary_reload_boundary()
    {
        var fixture = DesignFixtures.Content();
        WorldContentFixtures.Change(fixture, "resources/voxels/block.voxel.json",
            json => json["appearance"]!["allowed_orientations"] = "all_24");
        using var initial = fixture.Load();
        var package = Open(fixture);
        var voxel = (C.DefinitionDraft.Voxel)Read(package, C.DefinitionKind.Voxel, "block");
        Assert.AreEqual(7L, voxel.Value.Simulation!.BaseValue);
        Assert.AreEqual(3d, voxel.Value.Simulation.MassTons);
        Assert.AreEqual(C.AllowedVoxelOrientations.All24, voxel.Value.Orientations);
        var presentation = (C.AuthoringVoxelPresentation.Cube)voxel.Value.Presentation!;
        Assert.AreEqual(new C.AuthoringAssetPath("tile.png"), presentation.Top);
        CollectionAssert.AreEqual(new[] { new C.VoxelShape("cube"), new C.VoxelShape("wedge") }, presentation.Shapes.ToArray());
        var edited = voxel with { Value = voxel.Value with { Display = new("重载后的方块", "说明") } };
        var plan = Plan(package, new C.PackageEdit.WriteDefinition(edited));
        var bytes = plan.Changes.Single().Bytes!.Value;
        Assert.IsTrue(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(edited.Value.Display.Name)) >= 0);
        Assert.AreEqual((byte)'\n', bytes[^1]);
        Apply(fixture, plan);
        using var reloaded = Success(ContentFactory.Create().PrepareLoad(new(fixture.Installation,
            new C.ContentLoadPurpose.Reload(initial.View, new([], [], [], [initial.View.Core.Identity]))), fixture, fixture));
        var display = WorldContentFixtures.Found(reloaded.View.FindVoxelDisplay(new("core:block")));
        Assert.AreEqual(edited.Value.Display, display.Display);
        Assert.AreEqual(presentation.Top, ((C.AuthoringVoxelPresentation.Cube)
            ((C.DefinitionDraft.Voxel)Read(Open(fixture), C.DefinitionKind.Voxel, "block")).Value.Presentation!).Top);
    }

    [TestMethod]
    public void Voxel_forms_preserve_front_mesh_collision_and_visual_behaviors()
    {
        var fixture = DesignFixtures.Content();
        var value = ((C.DefinitionDraft.Voxel)Read(Open(fixture), C.DefinitionKind.Voxel, "block")).Value;
        var collision = new C.VoxelCollision.LocalBox(new(0.1, 0.2, 0.3), new(0.5, 0.5, 0.5));
        var visual = new C.VoxelAppearanceBehaviors(true, new(new("#123456"), 7));
        foreach (var presentation in new C.AuthoringVoxelPresentation[]
        {
            ((C.AuthoringVoxelPresentation.Cube)value.Presentation!) with { Front = new("front.png") },
            new C.AuthoringVoxelPresentation.Mesh(new("model.glb"), [C.MaterialCategory.Glass, C.MaterialCategory.BareMetal])
        })
        {
            var definition = new C.DefinitionDraft.Voxel(value with { Collision = collision, AppearanceBehaviors = visual, Presentation = presentation });
            Apply(fixture, Plan(Open(fixture), new C.PackageEdit.WriteDefinition(definition)));
            var actual = ((C.DefinitionDraft.Voxel)Read(Open(fixture), C.DefinitionKind.Voxel, "block")).Value;
            Assert.AreEqual(collision, actual.Collision);
            Assert.AreEqual(visual, actual.AppearanceBehaviors);
            if (presentation is C.AuthoringVoxelPresentation.Cube cube)
            {
                var written = (C.AuthoringVoxelPresentation.Cube)actual.Presentation!;
                Assert.AreEqual(cube.Front, written.Front);
                Assert.AreEqual(cube.TileSpan, written.TileSpan);
                CollectionAssert.AreEqual(cube.Shapes.ToArray(), written.Shapes.ToArray());
            }
            else
            {
                var mesh = (C.AuthoringVoxelPresentation.Mesh)presentation;
                var written = (C.AuthoringVoxelPresentation.Mesh)actual.Presentation!;
                Assert.AreEqual(mesh.Model, written.Model);
                CollectionAssert.AreEqual(mesh.SurfaceMaterials.ToArray(), written.SurfaceMaterials.ToArray());
            }
        }
    }

    [TestMethod]
    public void Snapshot_is_independent_of_source_and_preserves_opaque_version_and_template_display()
    {
        var fixture = WorldContentFixtures.Content();
        var files = new ClosableFiles(fixture);
        var version = new C.PackageSourceVersion("  arbitrary\r\n版本\t ");
        var package = Success(ContentFactory.Create().OpenAuthoringPackage(ContentFixture.BuiltIn, new("core"), version, files));
        Assert.IsTrue(files.Calls > 0);
        files.Closed = true;
        var template = (C.DefinitionDraft.SettlementTemplate)Read(package, C.DefinitionKind.SettlementTemplate, "farm-start");
        var changed = template with { Value = template.Value with { Weight = 17 } };
        var plan = Plan(package, new C.PackageEdit.WriteDefinition(changed));
        Assert.AreEqual(new C.PackageId("core"), plan.Package);
        Assert.AreEqual(version, package.SourceVersion);
        Assert.AreEqual(version, plan.SourceVersion);
        var json = Json(plan.Changes.Single(c => c.RelativePath == WorldContentFixtures.TemplatePath));
        Assert.AreEqual(17, json["weight"]!.GetValue<int>());
        Assert.IsTrue(JsonNode.DeepEquals(WorldContentFixtures.Template()["display"], json["display"]));
        Rejected(ContentFactory.Create().PreparePackageWrite(package,
            [new C.PackageEdit.WriteDefinition(template with { Value = template.Value with { LocalId = "new-template" } })]), "/display");
    }

    [TestMethod]
    public void Template_inventory_preserves_voxels_and_composites_and_refuses_unrepresentable_blueprints()
    {
        var fixture = WorldContentFixtures.InitializedTemplate();
        WorldContentFixtures.Change(fixture, WorldContentFixtures.TemplatePath,
            json => json["inventory"]!.AsArray().RemoveAt(2));
        var package = Open(fixture);
        var template = (C.DefinitionDraft.SettlementTemplate)Read(package, C.DefinitionKind.SettlementTemplate, "farm-start");
        CollectionAssert.AreEqual(new K.GoodsQuantity[]
        {
            new(new K.GoodId.Voxel(new("core:block")), 11),
            new(new K.GoodId.Composite(new("core:device")), 2)
        }, template.Value.Inventory.ToArray());
        var change = Plan(package, new C.PackageEdit.WriteDefinition(template)).Changes.Single(c => c.RelativePath == WorldContentFixtures.TemplatePath);
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse("""[{"voxel":"core:block","quantity":11},{"blueprint":"core:device","quantity":2}]"""), Json(change)["inventory"]));
        Rejected(ContentFactory.Create().PreparePackageWrite(package, [new C.PackageEdit.WriteDefinition(template with
        {
            Value = template.Value with { Inventory = [new(new K.GoodId.Assembly(new("opaque-composition")), 1)] }
        })]), "/inventory/0");
        foreach (var id in new[] { "core:frame", "core:unknown", "11111111-1111-4111-8111-111111111111:device" })
            Rejected(ContentFactory.Create().PreparePackageWrite(package, [new C.PackageEdit.WriteDefinition(template with
            {
                Value = template.Value with { Inventory = [new(new K.GoodId.Composite(new(id)), 1)] }
            })]), "/inventory/0", "invalid_field");
        foreach (var id in new[] { "frame", "unknown" })
        {
            WorldContentFixtures.Change(fixture, WorldContentFixtures.TemplatePath,
                json => json["inventory"] = JsonNode.Parse($$"""[{"blueprint":"{{id}}","quantity":1}]"""));
            var lookup = Open(fixture).Read(new(C.DefinitionKind.SettlementTemplate, "farm-start"));
            Assert.IsInstanceOfType<C.ContentLookup<C.DefinitionDraft>.Missing>(lookup);
            Assert.IsTrue(((C.ContentLookup<C.DefinitionDraft>.Missing)lookup).Diagnostics.Any(d => d.Field == "/inventory/0"));
        }
    }

    [TestMethod]
    public void Recovery_plan_reference_survives_template_read_and_external_draft_round_trip()
    {
        var fixture = WorldContentFixtures.Content();
        fixture.AddFile(ContentFixture.BuiltIn, "resources/processes/recover.process.json",
            ProcessLoadingTests.Process("recovery", "recover").ToJsonString());
        WorldContentFixtures.Change(fixture, WorldContentFixtures.TemplatePath,
            json => json["production_plans"] = JsonNode.Parse("""[{"plot":1,"capability":"production","process":"recover","enabled":true,"priority":0}]"""));
        var definition = (C.DefinitionDraft.SettlementTemplate)Read(Open(fixture), C.DefinitionKind.SettlementTemplate, "farm-start");
        var reference = new K.ProcessRef.Recovery(new("core:recover"));
        Assert.AreEqual(reference, definition.Value.ProductionPlans.Single().Process);
        var saved = new C.SavedContentDraft(new(Guid.NewGuid()), new(1),
            new C.DraftTarget.PackageDefinition(new("core"), C.DefinitionKind.SettlementTemplate, "farm-start", new("source")), new C.DraftPayload.Definition(definition));
        var restored = Success(ContentFactory.Create().DecodeDraft(Success(ContentFactory.Create().EncodeDraft(saved))));
        var actual = (C.DefinitionDraft.SettlementTemplate)((C.DraftPayload.Definition)restored.Payload).Value;
        Assert.AreEqual(reference, actual.Value.ProductionPlans.Single().Process);
        var patch = Plan(Open(fixture), new C.PackageEdit.WriteDefinition(actual));
        var plan = Json(patch.Changes.Single())["production_plans"]![0]!;
        Assert.AreEqual("core:recover", plan["process"]!.GetValue<string>());
        Assert.IsNull(plan["process_kind"]);
    }

    [TestMethod]
    public void Lair_types_round_trip_through_package_edits_and_creature_blueprints_remain_definitions()
    {
        var fixture = DesignFixtures.Content();
        fixture.AddFile(ContentFixture.BuiltIn, "resources/lair-types/den.lair-type.json", """
            {"schema_version":1,"id":"den","display":{"name":"巢穴","description":"测试聚落"},
            "generation_weight":2,"members":[{"creature":"creature","weight":1}],"maximum_members":4,
            "spawn_interval_seconds":30,"raid_interval_seconds":60,"roaming_radius_hops":1,"raid_radius_hops":2,
            "rail_damage_work_seconds":15,"clearance_reward":10,"scene_model":"creature"}
            """);
        var creature = DesignFixtures.Blueprint("creature");
        creature["category"] = "creature";
        creature["simulation"] = JsonNode.Parse("""{"behaviors":{"creature":{"hp":20,"speed":3,"retreat_threshold":0.25,"raid_duration":10},"weapon":{"damage":2,"interval":1}}}""");
        WorldContentFixtures.AddBlueprint(fixture, creature);
        var package = Open(fixture);
        Assert.IsTrue(package.EditableDefinitions.Contains(new(C.DefinitionKind.LairType, "den")));
        var lair = (C.DefinitionDraft.LairType)Read(package, C.DefinitionKind.LairType, "den");
        Assert.AreEqual(new Train.Core.Contracts.CreatureDefinitionId("core:creature"), lair.Value.Members.Single().Creature);
        Rejected(ContentFactory.Create().PreparePackageWrite(package,
            [new C.PackageEdit.WriteDefinition(lair with { Value = lair.Value with { GenerationWeight = null } })]), "/generation_weight", "invalid_field");
        Rejected(ContentFactory.Create().PreparePackageWrite(package,
            [new C.PackageEdit.WriteDefinition(lair with { Value = lair.Value with { Members = [] } })]), "/members", "invalid_field");
        var changed = lair with { Value = lair.Value with { GenerationWeight = 5 } };
        Apply(fixture, Plan(package, new C.PackageEdit.WriteDefinition(changed)));
        Assert.AreEqual(5, ((C.DefinitionDraft.LairType)Read(Open(fixture), C.DefinitionKind.LairType, "den")).Value.GenerationWeight);
        Apply(fixture, Plan(Open(fixture), new C.PackageEdit.DeleteDefinition(new(C.DefinitionKind.LairType, "den"))));
        Assert.IsInstanceOfType<C.ContentLookup<C.DefinitionDraft>.NotFound>(Open(fixture).Read(new(C.DefinitionKind.LairType, "den")));
        package = Open(fixture);
        var draft = (C.DefinitionDraft.Blueprint)Read(package, C.DefinitionKind.Blueprint, "creature");
        Assert.IsInstanceOfType<C.DesignSpecification.Creature>(draft.Value.Specification);
        var renamed = draft with { Value = draft.Value with { Display = new("生物", "草稿") } };
        var plan = Plan(package, new C.PackageEdit.WriteDefinition(renamed));
        Apply(fixture, plan);
        var restored = (C.DefinitionDraft.Blueprint)Read(Open(fixture), C.DefinitionKind.Blueprint, "creature");
        Assert.AreEqual(renamed.Value.Display, restored.Value.Display);
        Assert.AreEqual(renamed.Value.Specification, restored.Value.Specification);
    }

    [TestMethod]
    public void Patch_is_sorted_omits_equal_bytes_and_preserves_unedited_unknown_files()
    {
        var fixture = DesignFixtures.Content();
        fixture.AddFile(ContentFixture.BuiltIn, "resources/future/unknown.bin", new byte[] { 9, 8, 7 });
        var package = Open(fixture);
        var voxel = (C.DefinitionDraft.Voxel)Read(package, C.DefinitionKind.Voxel, "block");
        var edits = new C.PackageEdit[]
        {
            new C.PackageEdit.WriteAsset("resources/shared/z.bin", [1]),
            new C.PackageEdit.WriteDefinition(voxel with { Value = voxel.Value with { Display = new("修改", "") } }),
            new C.PackageEdit.WriteAsset("resources/shared/a.bin", [2])
        };
        var plan = Plan(package, edits);
        CollectionAssert.AreEqual(plan.Changes.Select(c => c.RelativePath).Order(StringComparer.Ordinal).ToArray(),
            plan.Changes.Select(c => c.RelativePath).ToArray());
        Assert.IsFalse(plan.Changes.Any(c => c.RelativePath.Contains("unknown", StringComparison.Ordinal)));
        fixture.ReverseEnumeration = true;
        var reversed = Plan(Open(fixture), edits.Reverse().ToArray());
        CollectionAssert.AreEqual(plan.Changes.Select(c => c.RelativePath).ToArray(), reversed.Changes.Select(c => c.RelativePath).ToArray());
        Apply(fixture, plan);
        Assert.AreEqual(0, Plan(Open(fixture), edits).Changes.Length);
        var bytes = ((C.ContentIoResult<ImmutableArray<byte>>.Success)fixture.Read(new(ContentFixture.BuiltIn,
            "resources/future/unknown.bin"), 100)).Value;
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, bytes.ToArray());
    }

    [TestMethod]
    public void Definition_delete_includes_owned_import_files_but_preserves_shared_assets()
    {
        var fixture = DesignFixtures.Content();
        fixture.AddFile(ContentFixture.BuiltIn, "resources/voxels/block/tile.png.import", "import settings");
        fixture.AddFile(ContentFixture.BuiltIn, "resources/voxels/shared/tile.png.import", "shared settings");
        var plan = Plan(Open(fixture), new C.PackageEdit.DeleteDefinition(new(C.DefinitionKind.Voxel, "block")));
        CollectionAssert.AreEquivalent(new[]
        {
            "resources/voxels/block.voxel.json", "resources/voxels/block/icon.png",
            "resources/voxels/block/tile.png", "resources/voxels/block/tile.png.import"
        }, plan.Changes.Select(c => c.RelativePath).ToArray());
        Assert.IsTrue(plan.Changes.All(c => c.Bytes is null));
    }

    [TestMethod]
    public void Invalid_edits_reject_the_whole_batch_including_manifest_changes_and_escaping_paths()
    {
        var package = Open(DesignFixtures.Content());
        foreach (var edit in new C.PackageEdit[]
        {
            new C.PackageEdit.Manifest(package.Metadata),
            new C.PackageEdit.Manifest(package.Metadata with { Id = new("changed") }),
            new C.PackageEdit.WriteAsset("../outside.bin", [1]),
            new C.PackageEdit.WriteAsset("mod.json", [1]),
            new C.PackageEdit.WriteAsset("MOD.JSON", [1]),
            new C.PackageEdit.DeleteAsset("mod.json"),
            new C.PackageEdit.DeleteAsset("MOD.JSON"),
            new C.PackageEdit.DeleteAsset("resources/../../outside.bin"),
            new C.PackageEdit.WriteDefinition(new C.DefinitionDraft.Blueprint("../outside", DesignFixtures.Vehicle()))
        })
            Rejected(ContentFactory.Create().PreparePackageWrite(package,
                [new C.PackageEdit.WriteAsset("resources/shared/valid.bin", [1]), edit]));
        Rejected(ContentFactory.Create().PreparePackageWrite(package,
            [new C.PackageEdit.WriteAsset("resources/shared/a.png", [1]), new C.PackageEdit.WriteAsset("resources/shared/A.png", [2])]));

        const string id = "11111111-1111-4111-8111-111111111111";
        var fixture = new ContentFixture();
        var root = new C.ContentRoot("authoring-mod");
        fixture.AddFile(root, "mod.json", $$"""{"schema_version":2,"id":"{{id}}","name":"测试包","version":"1.0.0","authors":["作者"],"dependencies":[]}""");
        fixture.AddDirectory(root, "resources");
        var mod = Success(ContentFactory.Create().OpenAuthoringPackage(root, new(id), new("snapshot"), fixture));
        Rejected(ContentFactory.Create().PreparePackageWrite(mod,
            [new C.PackageEdit.Manifest(mod.Metadata with { Id = new("22222222-2222-4222-8222-222222222222") })]));
    }

    [TestMethod]
    public void Manifest_edits_preserve_dependency_names_without_publishing_implicit_core()
    {
        const string id = "11111111-1111-4111-8111-111111111111";
        const string dependencyId = "22222222-2222-4222-8222-222222222222";
        var fixture = new ContentFixture();
        var root = new C.ContentRoot("mod-source");
        fixture.AddFile(root, "mod.json", $$"""{"schema_version":2,"id":"{{id}}","name":"测试包","version":"1.0.0","authors":["作者"],"description":"保留说明","dependencies":[{"id":"{{dependencyId}}","name":"基础扩展","min_version":"1.0.0"}]}""");
        fixture.AddDirectory(root, "resources");
        var package = Success(ContentFactory.Create().OpenAuthoringPackage(root, new(id), new("source"), fixture));
        var metadata = package.Metadata with { Name = "新的包名称", Version = "1.1.0" };
        var change = Plan(package, new C.PackageEdit.Manifest(metadata)).Changes.Single();
        var json = Json(change);
        Assert.AreEqual(metadata.Name, json["name"]!.GetValue<string>());
        Assert.AreEqual(metadata.Version, json["version"]!.GetValue<string>());
        Assert.AreEqual("保留说明", json["description"]!.GetValue<string>());
        var dependency = json["dependencies"]!.AsArray().Single()!;
        Assert.AreEqual(dependencyId, dependency["id"]!.GetValue<string>());
        Assert.AreEqual("基础扩展", dependency["name"]!.GetValue<string>());
        var bytes = change.Bytes!.Value;
        Assert.IsTrue(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(metadata.Name)) >= 0);
        Assert.AreEqual((byte)'\n', bytes[^1]);
        var changedDependencies = metadata.Dependencies.Select(d => d.Package.Value == dependencyId ? d with { MinimumVersion = "2.0.0" } : d)
            .Append(new C.PackageDependency(new("33333333-3333-4333-8333-333333333333"), "1.0.0")).ToImmutableArray();
        var changed = Json(Plan(package, new C.PackageEdit.Manifest(metadata with { Dependencies = changedDependencies })).Changes.Single())["dependencies"]!.AsArray();
        Assert.AreEqual(2, changed.Count);
        Assert.AreEqual("基础扩展", changed[0]!["name"]!.GetValue<string>());
        Assert.AreEqual("2.0.0", changed[0]!["min_version"]!.GetValue<string>());
        var removed = Json(Plan(package, new C.PackageEdit.Manifest(metadata with { Dependencies = [] })).Changes.Single());
        Assert.AreEqual(0, removed["dependencies"]!.AsArray().Count);
    }

    [TestMethod]
    public void Mod_open_ignores_git_and_source_files_outside_resources()
    {
        const string id = "11111111-1111-4111-8111-111111111111";
        var fixture = new ContentFixture();
        var root = new C.ContentRoot("mod-source");
        fixture.AddFile(root, "mod.json", $$"""{"schema_version":2,"id":"{{id}}","name":"测试包","version":"1.0.0","authors":["作者"]}""");
        fixture.AddDirectory(root, "resources");
        fixture.SetReadFailure(root, ".git/large.pack", C.ContentIoFailure.LimitExceeded);
        fixture.SetEnumerationFailure(root, ".git", C.ContentIoFailure.Inaccessible);
        fixture.SetReadFailure(root, "model.blend", C.ContentIoFailure.LimitExceeded);
        fixture.AddFile(root, "resources/voxels/block.voxel.json", ContentFixture.VoxelJson);
        var package = Success(ContentFactory.Create().OpenAuthoringPackage(root, new(id), new("source"), fixture));
        Assert.AreEqual(new C.PackageId(id), package.Package);
        Assert.IsInstanceOfType<C.ContentLookup<C.DefinitionDraft>.Found>(package.Read(new(C.DefinitionKind.Voxel, "block")));
    }

    [TestMethod]
    public void Nonfinite_form_values_are_field_failures_for_package_and_external_draft_encoding()
    {
        var package = Open(DesignFixtures.Content());
        var voxel = ((C.DefinitionDraft.Voxel)Read(package, C.DefinitionKind.Voxel, "block")).Value;
        foreach (var number in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        foreach (var value in new[]
        {
            voxel with { Simulation = voxel.Simulation! with { MassTons = number } },
            voxel with { Simulation = voxel.Simulation! with { FuelEnergyPerUnit = number } },
            voxel with { Collision = new C.VoxelCollision.LocalBox(new(number, 0, 0), new(0.5, 0.5, 0.5)) },
            voxel with { Collision = new C.VoxelCollision.LocalBox(new(0, 0, 0), new(number, 0.5, 0.5)) },
            voxel with { AppearanceBehaviors = new(false, new(new("#ffffff"), number)) }
        })
        {
            var definition = new C.DefinitionDraft.Voxel(value);
            Rejected(ContentFactory.Create().PreparePackageWrite(package, [new C.PackageEdit.WriteDefinition(definition)]), "/value", "invalid_field");
            var saved = new C.SavedContentDraft(new(Guid.NewGuid()), new(1),
                new C.DraftTarget.PackageDefinition(package.Package, C.DefinitionKind.Voxel, value.LocalId, package.SourceVersion), new C.DraftPayload.Definition(definition));
            var result = ContentFactory.Create().EncodeDraft(saved);
            Assert.IsInstanceOfType<C.ContentResult<string>.Failure>(result);
            Assert.IsTrue(((C.ContentResult<string>.Failure)result).Diagnostics.Any(d => d.Code == "invalid_field" && d.Field == "/payload/value"));
        }
    }

    private static C.IAuthoringPackage Open(ContentFixture fixture) => Success(ContentFactory.Create().OpenAuthoringPackage(
        ContentFixture.BuiltIn, new("core"), new("snapshot"), fixture));
    private static C.DefinitionDraft Read(C.IAuthoringPackage package, C.DefinitionKind kind, string id) =>
        WorldContentFixtures.Found(package.Read(new(kind, id)));
    private static C.PackageWritePlan Plan(C.IAuthoringPackage package, params C.PackageEdit[] edits)
    {
        var plan = Success(ContentFactory.Create().PreparePackageWrite(package, [.. edits]));
        Assert.IsFalse(plan.Diagnostics.Any(d => d.Severity == C.ContentDiagnosticSeverity.Error));
        return plan;
    }
    private static JsonNode Json(C.PackageFileChange change) => JsonNode.Parse(change.Bytes!.Value.AsSpan())!;
    private static void Apply(ContentFixture fixture, C.PackageWritePlan plan)
    {
        foreach (var change in plan.Changes)
            if (change.Bytes is { } bytes) fixture.AddFile(ContentFixture.BuiltIn, change.RelativePath, bytes.ToArray());
            else fixture.RemoveEntry(ContentFixture.BuiltIn, change.RelativePath);
    }
    private static T Success<T>(C.ContentResult<T> result) where T : notnull
    {
        Assert.IsInstanceOfType<C.ContentResult<T>.Success>(result, result.ToString());
        return ((C.ContentResult<T>.Success)result).Value;
    }
    private static void Rejected(C.ContentResult<C.PackageWritePlan> result, string? field = null, string? code = null)
    {
        var diagnostics = result switch
        {
            C.ContentResult<C.PackageWritePlan>.Failure failure => failure.Diagnostics,
            C.ContentResult<C.PackageWritePlan>.Success success => success.Value.Diagnostics,
            _ => AssertFailed(result)
        };
        Assert.IsTrue(diagnostics.Any(d => d.Severity == C.ContentDiagnosticSeverity.Error &&
            (field is null || d.Field == field) && (code is null || d.Code == code)), result.ToString());
        if (result is C.ContentResult<C.PackageWritePlan>.Success plan) Assert.AreEqual(0, plan.Value.Changes.Length);
    }
    private static ImmutableArray<C.ContentDiagnostic> AssertFailed(object result)
    {
        Assert.Fail(result.ToString() ?? "未知结果。");
        return [];
    }
    private sealed class ClosableFiles(C.IContentFiles inner) : C.IContentFiles
    {
        internal bool Closed { get; set; }
        internal int Calls { get; private set; }
        public C.ContentIoResult<ImmutableArray<C.ContentFileEntry>> Enumerate(C.ContentPath directory)
        {
            Assert.IsFalse(Closed, "Authoring package must use its fixed snapshot.");
            Calls++;
            return inner.Enumerate(directory);
        }
        public C.ContentIoResult<ImmutableArray<byte>> Read(C.ContentPath file, long maximumBytes)
        {
            Assert.IsFalse(Closed, "Authoring package must use its fixed snapshot.");
            Calls++;
            return inner.Read(file, maximumBytes);
        }
    }
}
