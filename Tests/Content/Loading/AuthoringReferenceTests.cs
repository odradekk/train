using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class AuthoringReferenceTests
{
    [TestMethod]
    public void An_unactivated_source_can_reference_its_own_definitions_core_and_direct_dependencies_only()
    {
        const string dependency = "11111111-1111-4111-8111-111111111111";
        const string indirect = "22222222-2222-4222-8222-222222222222";
        const string unrelated = "33333333-3333-4333-8333-333333333333";
        const string own = "44444444-4444-4444-8444-444444444444";
        var fixture = DesignFixtures.Content();
        AddMod(fixture, dependency, indirect);
        AddMod(fixture, indirect);
        AddMod(fixture, unrelated);
        fixture.Installation = fixture.Installation with { EnabledMods = [new(dependency), new(indirect), new(unrelated)] };
        using var content = fixture.Load();
        var root = new C.ContentRoot("unactivated-source");
        fixture.AddFile(root, "mod.json", Manifest(own, dependency));
        fixture.AddFile(root, "resources/voxels/block.voxel.json", ContentFixture.VoxelJson);
        var factory = ContentFactory.Create();
        var source = Success(factory.OpenAuthoringPackage(root, new(own), new("fixed"), fixture));
        var choices = Success(factory.QueryAuthoringReferences(source, content.View, []));
        CollectionAssert.AreEquivalent(new[] { "core:block", "core:machine", dependency + ":block", own + ":block" },
            choices.Select(choice => ((Train.Core.Contracts.CoreDefinitionReference.Voxel)((C.ContentReference.Core)choice.Reference).Definition).Id.Value).ToArray());
        Assert.IsFalse(content.View.Packages.Any(package => package.Metadata?.Id.Value == own));
    }

    [TestMethod]
    public void Staged_additions_replacements_and_deletions_replace_the_fixed_source_catalog()
    {
        var fixture = DesignFixtures.Content();
        using var content = fixture.Load();
        var factory = ContentFactory.Create();
        var source = Open(factory, fixture);
        var block = (C.DefinitionDraft.Voxel)WorldContentFixtures.Found(source.Read(new(C.DefinitionKind.Voxel, "block")));
        var added = block with { Value = block.Value with { LocalId = "new", Display = new("新增", "说明") } };
        var sceneOnly = block with { Value = block.Value with { LocalId = "machine", Simulation = null } };
        var choices = Success(factory.QueryAuthoringReferences(source, content.View,
            [new C.PackageEdit.WriteDefinition(added), new C.PackageEdit.WriteDefinition(sceneOnly), new C.PackageEdit.DeleteDefinition(new(C.DefinitionKind.Voxel, "block"))]));
        Assert.AreEqual(1, choices.Length);
        Assert.AreEqual("新增", choices[0].Display.Name);
        Assert.AreEqual(new C.ContentReference.Core(new Train.Core.Contracts.CoreDefinitionReference.Voxel(new("core:new"))), choices[0].Reference);
        Assert.AreEqual(2, Success(factory.QueryAuthoringReferences(source, content.View, [])).Length);
    }

    [TestMethod]
    public void Terrain_and_blueprint_categories_are_listed_with_strong_references()
    {
        var fixture = WorldContentFixtures.InitializedTemplate();
        using var content = fixture.Load();
        var factory = ContentFactory.Create();
        var source = Open(factory, fixture);
        var choices = Success(factory.QueryAuthoringReferences(source, content.View, []));
        Assert.IsTrue(choices.Any(choice => choice.Kind == C.AuthoringReferenceKind.TerrainType && choice.Reference ==
            new C.ContentReference.Core(new Train.Core.Contracts.CoreDefinitionReference.Terrain(new("core:plains")))));
        Assert.IsTrue(choices.Any(choice => choice.Kind == C.AuthoringReferenceKind.SceneModel && choice.Reference ==
            new C.ContentReference.Blueprint(new(C.BlueprintLibrary.Packages, new("core:scene")))));
        Assert.IsTrue(choices.Any(choice => choice.Kind == C.AuthoringReferenceKind.Building && choice.Reference ==
            new C.ContentReference.Blueprint(new(C.BlueprintLibrary.Packages, new("core:station")))));
        Assert.IsTrue(choices.Any(choice => choice.Kind == C.AuthoringReferenceKind.InventoryBlueprint && choice.Reference ==
            new C.ContentReference.Blueprint(new(C.BlueprintLibrary.Packages, new("core:device")))));
        Assert.IsFalse(choices.Any(choice => choice.Reference == new C.ContentReference.Blueprint(new(C.BlueprintLibrary.Packages, new("core:wagon")))));
    }

    [TestMethod]
    [DataRow(false, "1.0.0", "dependency_missing")]
    [DataRow(true, "2.0.0", "dependency_version")]
    public void An_unavailable_or_too_old_dependency_fails_the_query(bool installed, string minimum, string code)
    {
        const string dependency = "11111111-1111-4111-8111-111111111111";
        const string own = "44444444-4444-4444-8444-444444444444";
        var fixture = DesignFixtures.Content();
        if (installed)
        {
            AddMod(fixture, dependency);
            fixture.Installation = fixture.Installation with { EnabledMods = [new(dependency)] };
        }
        using var content = fixture.Load();
        var root = new C.ContentRoot("unactivated-source");
        var manifest = JsonNode.Parse(Manifest(own, dependency))!;
        manifest["dependencies"]![0]!["min_version"] = minimum;
        fixture.AddFile(root, "mod.json", manifest.ToJsonString());
        fixture.AddDirectory(root, "resources");
        var factory = ContentFactory.Create();
        var source = Success(factory.OpenAuthoringPackage(root, new(own), new("fixed"), fixture));
        var result = factory.QueryAuthoringReferences(source, content.View, []);
        Assert.IsInstanceOfType<C.ContentResult<ImmutableArray<C.AuthoringReferenceChoice>>.Failure>(result);
        Assert.AreEqual(code, ((C.ContentResult<ImmutableArray<C.AuthoringReferenceChoice>>.Failure)result).Diagnostics.Single().Code);
    }

    [TestMethod]
    public void Inventory_choices_include_goods_but_exclude_scene_voxels()
    {
        var fixture = DesignFixtures.Content();
        using var content = fixture.Load();
        var factory = ContentFactory.Create();
        var source = Open(factory, fixture);
        var choices = Success(factory.QueryAuthoringReferences(source, content.View, []));
        CollectionAssert.AreEquivalent(new[] { "core:block", "core:machine" }, choices
            .Where(choice => choice.Kind == C.AuthoringReferenceKind.InventoryVoxel)
            .Select(choice => ((C.ContentReference.Core)choice.Reference).Definition)
            .OfType<Train.Core.Contracts.CoreDefinitionReference.Voxel>().Select(voxel => voxel.Id.Value).ToArray());
        Assert.AreEqual("方块", choices.Single(choice => choice.Reference ==
            new C.ContentReference.Core(new Train.Core.Contracts.CoreDefinitionReference.Voxel(new("core:block")))).Display.Name);
    }

    internal static C.IAuthoringPackage Open(C.IContentFactory factory, ContentFixture fixture) =>
        Success(factory.OpenAuthoringPackage(ContentFixture.BuiltIn, new("core"), new("fixed"), fixture));

    internal static T Success<T>(C.ContentResult<T> result) where T : notnull
    {
        Assert.IsInstanceOfType<C.ContentResult<T>.Success>(result, result.ToString());
        return ((C.ContentResult<T>.Success)result).Value;
    }

    private static string Manifest(string id, string? dependency = null) => new JsonObject
    {
        ["schema_version"] = 2, ["id"] = id, ["name"] = "测试包", ["version"] = "1.0.0", ["authors"] = new JsonArray("作者"),
        ["dependencies"] = dependency is null ? new JsonArray() : new JsonArray(new JsonObject { ["id"] = dependency, ["name"] = "依赖", ["min_version"] = "1.0.0" })
    }.ToJsonString();

    private static void AddMod(ContentFixture fixture, string id, string? dependency = null)
    {
        fixture.AddFile(ContentFixture.Mods, id + "/mod.json", Manifest(id, dependency));
        fixture.AddFile(ContentFixture.Mods, id + "/resources/voxels/block.voxel.json", ContentFixture.VoxelJson);
        fixture.AddAsset(ContentFixture.Mods, id + "/resources/voxels/block/tile.png", [1, 2, 3], new C.AssetMetadata.Png(32, 32));
        fixture.AddAsset(ContentFixture.Mods, id + "/resources/voxels/block/icon.png", [4, 5, 6], new C.AssetMetadata.Png(128, 128));
    }
}
