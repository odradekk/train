using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Train.Content.Tests.Loading.ContentCaptureTests;
using static Train.Content.Tests.Loading.ContentRestoreTests;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class ProcessLoadingTests
{
    [TestMethod]
    public void Production_extraction_and_recovery_are_projected_with_typed_identities_and_display()
    {
        var fixture = DesignFixtures.Content();
        var production = Process("production", "press");
        production["simulation"]!["work_required"] = long.MaxValue;
        production["simulation"]!.AsObject().Remove("inputs");
        Add(fixture, production);
        Add(fixture, Process("extraction", "mine"));
        Add(fixture, Process("recovery", "recover"));
        using var candidate = fixture.Load();
        var processes = candidate.View.Core.Processes;
        Assert.AreEqual(3, processes.Length);
        var extraction = processes.Single(p => p.Details is K.ProcessDetails.Extraction);
        Assert.AreEqual(new K.GoodsQuantity(new K.GoodId.Voxel(new("core:block")), 3),
            ((K.ProcessDetails.Extraction)extraction.Details).Extracts);
        Assert.AreEqual(new K.ProductionProcessDefinitionId("core:mine"), ((K.ProcessDetails.Extraction)extraction.Details).Id);
        Assert.AreEqual(K.FacilityTag.Extraction, extraction.RequiredFacilityTag);
        var press = processes.Single(p => p.Details is K.ProcessDetails.Production);
        Assert.AreEqual(new K.ProductionProcessDefinitionId("core:press"), ((K.ProcessDetails.Production)press.Details).Id);
        Assert.IsTrue(((K.ProcessDetails.Production)press.Details).Inputs.IsEmpty);
        Assert.AreEqual(2L, ((K.ProcessDetails.Production)press.Details).Outputs.Single().Quantity);
        Assert.AreEqual(long.MaxValue, press.WorkRequired);
        Assert.AreEqual(5d, press.PowerPerWork);
        Assert.AreEqual(0d, press.LaborPerWork);
        var recovery = (K.ProcessDetails.Recovery)processes.Single(p => p.Details is K.ProcessDetails.Recovery).Details;
        Assert.AreEqual(new K.RecoveryProcessDefinitionId("core:recover"), recovery.Id);
        Assert.AreEqual(new K.GoodsQuantity(new K.GoodId.Voxel(new("core:machine")), 4), recovery.Inputs.Single());
        Assert.AreEqual(new K.GoodsQuantity(new K.GoodId.Voxel(new("core:block")), 2), recovery.Outputs.Single());
        var display = WorldContentFixtures.Found(candidate.View.FindDefinitionDisplay(new K.CoreDefinitionReference.Process(
            new K.ProcessRef.Recovery(new("core:recover")))));
        Assert.AreEqual(new C.DisplayText("流程", "测试流程"), display.Text);
        Assert.IsNull(display.Icon);
    }

    [TestMethod]
    [DataRow("non-good", "reference_wrong_kind", "/simulation/outputs/0/voxel")]
    [DataRow("not-raw", "invalid_field", "/simulation/extracts/voxel")]
    [DataRow("tag", "invalid_field", "/simulation/facility_tag")]
    [DataRow("terrain", "reference_missing", "/simulation/environment/terrain_types/0")]
    [DataRow("fractional-work", "invalid_field", "/simulation/work_required")]
    [DataRow("overflow-work", "invalid_field", "/simulation/work_required")]
    [DataRow("zero-work", "invalid_field", "/simulation/work_required")]
    [DataRow("nonfinite-power", "invalid_field", "/simulation/power_per_work")]
    [DataRow("kind-fields", "invalid_field", "/simulation/inputs")]
    [DataRow("empty-outputs", "invalid_field", "/simulation/outputs")]
    [DataRow("missing-recovery-inputs", "invalid_field", "/simulation/inputs")]
    [DataRow("duplicate-goods", "invalid_field", "/simulation/inputs")]
    [DataRow("empty-terrain", "invalid_field", "/simulation/environment/terrain_types")]
    [DataRow("duplicate-terrain", "invalid_field", "/simulation/environment/terrain_types")]
    public void Invalid_processes_report_source_identity_and_field(string mutation, string code, string field)
    {
        var fixture = DesignFixtures.Content();
        var kind = mutation is "not-raw" or "kind-fields" ? "extraction" : mutation == "missing-recovery-inputs" ? "recovery" : "production";
        var process = Process(kind);
        var sim = process["simulation"]!.AsObject();
        switch (mutation)
        {
            case "non-good": sim["outputs"]![0]!["voxel"] = "scenery"; break;
            case "not-raw": WorldContentFixtures.Change(fixture, "resources/voxels/block.voxel.json", j => j["simulation"]!["tags"] = new JsonArray()); break;
            case "tag": sim["facility_tag"] = "unknown"; break;
            case "terrain": sim["environment"] = JsonNode.Parse("""{"terrain_types":["absent"]}"""); break;
            case "fractional-work": sim["work_required"] = 1.5; break;
            case "overflow-work": sim["work_required"] = JsonNode.Parse("9223372036854775808"); break;
            case "zero-work": sim["work_required"] = 0; break;
            case "nonfinite-power": sim["power_per_work"] = JsonNode.Parse("1e999"); break;
            case "kind-fields": sim["inputs"] = new JsonArray(); break;
            case "empty-outputs": sim["outputs"] = new JsonArray(); break;
            case "missing-recovery-inputs": sim.Remove("inputs"); break;
            case "duplicate-goods": sim["inputs"]!.AsArray().Add(sim["inputs"]![0]!.DeepClone()); break;
            case "empty-terrain": sim["environment"] = JsonNode.Parse("""{"terrain_types":[]}"""); break;
            case "duplicate-terrain": sim["environment"] = JsonNode.Parse("""{"terrain_types":["absent","absent"]}"""); break;
        }
        Add(fixture, process);
        var result = fixture.Prepare();
        Failure(result, code);
        var diagnostic = ((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Single(d => d.Code == code);
        Assert.AreEqual(Path, diagnostic.Source!.Path);
        Assert.AreEqual(field, diagnostic.Field);
        K.ProcessRef reference = kind == "recovery" ? new K.ProcessRef.Recovery(new("core:test")) : new K.ProcessRef.Production(new("core:test"));
        Assert.AreEqual(new C.ContentReference.Core(new K.CoreDefinitionReference.Process(reference)), diagnostic.Subject);
    }

    [TestMethod]
    [DataRow(false, C.PackageState.Invalid)]
    [DataRow(true, C.PackageState.Available)]
    public void Cross_package_goods_and_terrain_require_declared_dependencies(bool declared, C.PackageState expected)
    {
        var fixture = WorldContentFixtures.Content();
        const string provider = "22222222-2222-4222-8222-222222222222";
        fixture.Installation = fixture.Installation with { EnabledMods = [new(ModId), new(provider)] };
        fixture.AddFile(ContentFixture.Mods, "provider/mod.json", $$$"""
            {"schema_version":2,"id":"{{{provider}}}","name":"供给包","version":"1.0.0","authors":["测试"],"dependencies":[]}
            """);
        fixture.AddFile(ContentFixture.Mods, "provider/resources/voxels/block.voxel.json", ContentFixture.VoxelJson);
        fixture.AddAsset(ContentFixture.Mods, "provider/resources/voxels/block/tile.png", [1, 2, 3], new C.AssetMetadata.Png(32, 32));
        fixture.AddAsset(ContentFixture.Mods, "provider/resources/voxels/block/icon.png", [4, 5, 6], new C.AssetMetadata.Png(128, 128));
        var terrain = WorldContentFixtures.Terrain();
        foreach (var key in new[] { "surface_voxel", "subsurface_voxel", "rock_voxel" }) terrain[key] = "core:scenery";
        terrain["construction_site"] = "core:scene";
        foreach (var model in terrain["railway_models"]!.AsArray())
        {
            model!["bridge"] = "core:scene";
            model["tunnel_portal"] = "core:scene";
            model["repair_site"] = "core:scene";
        }
        fixture.AddFile(ContentFixture.Mods, "provider/resources/terrain-types/plains.terrain-type.json", terrain.ToJsonString());
        var dependencies = declared ? new JsonArray(new JsonObject { ["id"] = provider, ["min_version"] = "1.0.0" }) : new JsonArray();
        fixture.AddFile(ContentFixture.Mods, "processes/mod.json", $$$"""
            {"schema_version":2,"id":"{{{ModId}}}","name":"流程包","version":"1.0.0","authors":["测试"],
             "dependencies":{{{dependencies.ToJsonString()}}}}
            """);
        var process = Process("production");
        process["simulation"]!["inputs"]![0]!["voxel"] = provider + ":block";
        process["simulation"]!["outputs"]![0]!["voxel"] = provider + ":block";
        process["simulation"]!["environment"] = new JsonObject { ["terrain_types"] = new JsonArray(provider + ":plains") };
        fixture.AddFile(ContentFixture.Mods, "processes/" + Path, process.ToJsonString());
        using var candidate = fixture.Load();
        var package = candidate.Report.Packages.Single(p => p.Metadata?.Id.Value == ModId);
        Assert.AreEqual(expected, package.State);
        if (declared)
            Assert.AreEqual(new K.TerrainTypeId(provider + ":plains"), candidate.View.Core.Processes.Single().AllowedTerrainTypes.Single());
        else
        {
            Assert.AreEqual("reference_missing", package.Diagnostics.Single().Code);
            Assert.AreEqual("/simulation/inputs/0/voxel", package.Diagnostics.Single().Field);
            Assert.IsTrue(candidate.View.Core.Processes.IsEmpty);
        }
    }

    [TestMethod]
    public void Invalid_process_excludes_its_mod_and_dependents_without_removing_builtin_content()
    {
        var fixture = DesignFixtures.Content();
        const string dependent = "22222222-2222-4222-8222-222222222222";
        fixture.Installation = fixture.Installation with { EnabledMods = [new(ModId), new(dependent)] };
        fixture.AddFile(ContentFixture.Mods, "source/mod.json", $$$"""
            {"schema_version":2,"id":"{{{ModId}}}","name":"源包","version":"1.0.0","authors":["测试"],"dependencies":[]}
            """);
        fixture.AddFile(ContentFixture.Mods, "dependent/mod.json", $$$"""
            {"schema_version":2,"id":"{{{dependent}}}","name":"依赖包","version":"1.0.0","authors":["测试"],
             "dependencies":[{"id":"{{{ModId}}}","min_version":"1.0.0"}]}
            """);
        var process = Process("production");
        process["simulation"]!["facility_tag"] = "unknown";
        fixture.AddFile(ContentFixture.Mods, "source/" + Path, process.ToJsonString());
        fixture.AddDirectory(ContentFixture.Mods, "dependent/resources");
        using var candidate = fixture.Load();
        Assert.AreEqual(C.PackageState.Invalid, candidate.Report.Packages.Single(p => p.Metadata?.Id.Value == ModId).State);
        Assert.AreEqual(C.PackageState.DependencyUnavailable, candidate.Report.Packages.Single(p => p.Metadata?.Id.Value == dependent).State);
        Assert.AreEqual(3, candidate.View.Voxels.Length);
    }

    [TestMethod]
    public void Unordered_goods_and_terrain_have_stable_projection_revision_and_core_identity()
    {
        var fixture = WorldContentFixtures.Content();
        var terrain = WorldContentFixtures.Terrain();
        terrain["id"] = "hills";
        fixture.AddFile(ContentFixture.BuiltIn, "resources/terrain-types/hills.terrain-type.json", terrain.ToJsonString());
        var process = Process("production");
        process["simulation"]!["inputs"]!.AsArray().Add(JsonNode.Parse("""{"voxel":"block","quantity":1}"""));
        process["simulation"]!["environment"] = JsonNode.Parse("""{"terrain_types":["plains","hills"]}""");
        Add(fixture, process);
        using var first = fixture.Load();
        var inputs = process["simulation"]!["inputs"]!.AsArray();
        process["simulation"]!["inputs"] = new JsonArray(inputs.Reverse().Select(i => i!.DeepClone()).ToArray());
        process["simulation"]!["environment"]!["terrain_types"] = new JsonArray("hills", "plains");
        Add(fixture, process);
        fixture.ReverseEnumeration = true;
        using var second = fixture.Load();
        var before = first.View.Core.Processes.Single();
        var after = second.View.Core.Processes.Single();
        CollectionAssert.AreEqual(new[] { new K.GoodId.Voxel(new K.VoxelDefinitionId("core:block")), new K.GoodId.Voxel(new K.VoxelDefinitionId("core:machine")) },
            ((K.ProcessDetails.Production)after.Details).Inputs.Select(g => g.Good).ToArray());
        CollectionAssert.AreEqual(new[] { new K.TerrainTypeId("core:hills"), new K.TerrainTypeId("core:plains") }, after.AllowedTerrainTypes.ToArray());
        Assert.AreEqual(before.Revision, after.Revision);
        Assert.AreEqual(first.View.Core.Identity, second.View.Core.Identity);
    }

    [TestMethod]
    public void Display_only_reload_preserves_business_identity_while_work_changes_publish_new_specification()
    {
        var fixture = Content();
        using var initial = fixture.Load();
        var basis = initial.View;
        var references = References(basis);
        WorldContentFixtures.Change(fixture, Path, p => p["display"]!["name"] = "新名称");
        using var display = Reload(fixture, basis, references);
        Assert.AreEqual(C.ContentChange.Presentation, display.Changes);
        Assert.AreEqual(basis.Core.Identity, display.View.Core.Identity);
        Assert.AreEqual(basis.Core.Processes.Single().Revision, display.View.Core.Processes.Single().Revision);
        Assert.AreEqual("新名称", WorldContentFixtures.Found(display.View.FindDefinitionDisplay(references.Definitions.Single().Definition)).Text.Name);
        WorldContentFixtures.Change(fixture, Path, p =>
        {
            p["simulation"]!["work_required"] = 99;
            p["simulation"]!["facility_tag"] = "recovery";
            p["simulation"]!["environment"] = JsonNode.Parse("""{"terrain_types":["plains"]}""");
        });
        using var changed = Reload(fixture, display.View, references);
        Assert.AreEqual(C.ContentChange.CoreInput, changed.Changes);
        Assert.AreNotEqual(basis.Core.Identity, changed.View.Core.Identity);
        Assert.AreNotEqual(basis.Core.Processes.Single().Revision, changed.View.Core.Processes.Single().Revision);
        Assert.AreEqual(99L, changed.View.Core.Processes.Single().WorkRequired);
        Assert.AreEqual(17L, basis.Core.Processes.Single().WorkRequired);
    }

    [TestMethod]
    [DataRow("production", "extraction")]
    [DataRow("extraction", "production")]
    [DataRow("production", "recovery")]
    [DataRow("recovery", "production")]
    public void Referenced_kind_changes_reject_reload_but_restore_resolves_current_content(string before, string after)
    {
        var fixture = Content(before);
        using var initial = fixture.Load();
        var basis = initial.View;
        var references = References(basis);
        Add(fixture, Process(after));
        var result = ContentFactory.Create().PrepareLoad(new(fixture.Installation, new C.ContentLoadPurpose.Reload(basis, references)), fixture, fixture);
        Failure(result, "reference_missing");
        var diagnostic = ((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Single();
        Assert.AreEqual("reload", diagnostic.Stage);
        Assert.AreEqual("facility.process", diagnostic.Field);
        StringAssert.Contains(diagnostic.Message, "种类改变视为删除原流程");
        Assert.AreEqual(17L, basis.Core.Processes.Single().WorkRequired);
        using var restored = Restore(fixture, Success(ContentFactory.Create().Capture(basis, references)), references);
        Assert.AreEqual((before == "recovery") != (after == "recovery") ? 1 : 0, restored.Report.RuntimeMissing.Length);
    }

    [TestMethod]
    public void Unreferenced_kind_changes_are_allowed_but_deleting_a_referenced_process_rejects()
    {
        var fixture = Content();
        using var initial = fixture.Load();
        Add(fixture, Process("extraction"));
        using var changed = Reload(fixture, initial.View, new([], [], [], [initial.View.Core.Identity]));
        Assert.IsInstanceOfType<K.ProcessDetails.Extraction>(changed.View.Core.Processes.Single().Details);
        fixture.RemoveEntry(ContentFixture.BuiltIn, Path);
        var result = ContentFactory.Create().PrepareLoad(new(fixture.Installation,
            new C.ContentLoadPurpose.Reload(changed.View, References(changed.View))), fixture, fixture);
        Failure(result, "reference_missing");
    }

    [TestMethod]
    public void Restore_reports_missing_process_and_reload_retains_then_recovers_it()
    {
        var fixture = Content();
        using var initial = fixture.Load();
        var factory = ContentFactory.Create();
        var references = References(initial.View);
        var saved = Success(factory.Capture(initial.View, references));
        fixture.RemoveEntry(ContentFixture.BuiltIn, Path);
        using var restored = Restore(fixture, Success(factory.DecodeCapture(saved.Encode())), references);
        var missing = restored.Report.RuntimeMissing.Single();
        Assert.AreEqual("facility.process", missing.Field);
        Assert.AreEqual(new C.PackageId("core"), missing.Package);
        Assert.IsInstanceOfType<C.ContentLookup<C.ContentDisplay>.Missing>(restored.View.FindDefinitionDisplay(references.Definitions.Single().Definition));
        var confirmed = references with { Definitions = [references.Definitions.Single() with { WasMissing = true }] };
        using var retained = Reload(fixture, restored.View, confirmed);
        Assert.AreSame(restored.View, retained.View);
        Add(fixture, Process("extraction"));
        using var recovered = Reload(fixture, retained.View, confirmed);
        Assert.IsTrue(recovered.Report.RuntimeMissing.IsEmpty);
        Assert.IsInstanceOfType<K.ProcessDetails.Extraction>(recovered.View.Core.Processes.Single().Details);
    }

    internal const string Path = "resources/processes/test.process.json";
    internal static ContentFixture Content(string kind = "production")
    {
        var fixture = WorldContentFixtures.Content();
        Add(fixture, Process(kind));
        return fixture;
    }
    internal static void Add(ContentFixture fixture, JsonObject process) => fixture.AddFile(ContentFixture.BuiltIn,
        "resources/processes/" + process["id"]!.GetValue<string>() + ".process.json", process.ToJsonString());
    internal static JsonObject Process(string kind, string id = "test")
    {
        var process = JsonNode.Parse("""
            {"schema_version":1,"id":"test","simulation":{"kind":"production",
             "inputs":[{"voxel":"machine","quantity":4}],"outputs":[{"voxel":"block","quantity":2}],
             "work_required":17,"power_per_work":5,"labor_per_work":0,"facility_tag":"metallurgy"},
             "display":{"name":"流程","description":"测试流程"}}
            """)!.AsObject();
        process["id"] = id;
        process["simulation"]!["kind"] = kind;
        if (kind == "extraction")
        {
            var sim = process["simulation"]!.AsObject();
            sim.Remove("inputs"); sim.Remove("outputs");
            sim["extracts"] = JsonNode.Parse("""{"voxel":"block","quantity":3}""");
            sim["facility_tag"] = "extraction";
        }
        return process;
    }
    private static K.CoreReferenceManifest References(C.IContentView view) => new([], [],
        [new(new K.CoreDefinitionReference.Process(view.Core.Processes.Single().Details is K.ProcessDetails.Recovery
            ? new K.ProcessRef.Recovery(new("core:test")) : new K.ProcessRef.Production(new("core:test"))),
            view.Core.Processes.Single().Revision, "facility.process", false)], [view.Core.Identity]);
    private static C.IContentCandidate Reload(ContentFixture fixture, C.IContentView basis, K.CoreReferenceManifest references) =>
        Success(ContentFactory.Create().PrepareLoad(new(fixture.Installation, new C.ContentLoadPurpose.Reload(basis, references)), fixture, fixture));
}
