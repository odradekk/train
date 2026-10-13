using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;
using static Train.Content.Tests.Loading.WorldContentFixtures;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class WorldIdentityTests
{
    [TestMethod]
    public void Display_assets_scene_layout_and_railway_appearance_change_without_changing_business_inputs()
    {
        var fixture = WorldContentFixtures.Content();
        using var first = fixture.Load();
        Change(fixture, "resources/settlement-types/farm.settlement-type.json", j => j["display"]!["name"] = "新类型名称");
        fixture.AddAsset(ContentFixture.BuiltIn, "resources/settlement-types/farm/symbol.png", [33, 34], new C.AssetMetadata.Png(48, 48));
        Change(fixture, WorldContentFixtures.TemplatePath, j =>
        {
            j["display"]!["name"] = "新模板";
            j["plots"]![0]!["at"] = new JsonArray(5, 0, 0);
        });
        Change(fixture, WorldContentFixtures.ProfilePath, j => j["display"]!["name"] = "新配置");
        Change(fixture, "resources/railway-rules/rail.railway-rule.json", j => j["appearance"]!["maximum_grade"] = 0.3);
        Change(fixture, "resources/blueprints/station.blueprint.json", j => j["display"]!["name"] = "新站点");
        Change(fixture, "resources/blueprints/scene.blueprint.json", j => j["layout"]!["cells"]![0]![0] = 2);
        using var second = fixture.Load();
        Assert.AreEqual(first.View.Core.Identity, second.View.Core.Identity);
        Assert.AreEqual(World(first.View).GenerationContentRevision, World(second.View).GenerationContentRevision);
        var reference = new K.CoreDefinitionReference.SettlementType(new("core:farm"));
        var oldDisplay = WorldContentFixtures.Found(first.View.FindDefinitionDisplay(reference));
        var newDisplay = WorldContentFixtures.Found(second.View.FindDefinitionDisplay(reference));
        Assert.AreEqual("新类型名称", newDisplay.Text.Name);
        Assert.AreNotEqual(oldDisplay.MapSymbol, newDisplay.MapSymbol);
        Assert.AreEqual(0.3, WorldContentFixtures.Found(second.View.FindRailwayAppearance(new("core:rail"))).MaximumGrade);
        Assert.AreEqual("新配置", second.View.GenerationProfiles.Single().Display.Name);
        Assert.AreNotEqual(first.View.Core.SettlementTemplates.Single().Plots[0].PrebuiltDesign,
            second.View.Core.SettlementTemplates.Single().Plots[0].PrebuiltDesign);
    }

    [TestMethod]
    [DataRow("type")]
    [DataRow("template")]
    [DataRow("building-composition")]
    [DataRow("rule")]
    [DataRow("voxel-specification")]
    public void Effective_business_changes_change_core_and_generation_identities(string change)
    {
        var fixture = WorldContentFixtures.Content();
        using var first = fixture.Load();
        switch (change)
        {
            case "type": Change(fixture, "resources/settlement-types/farm.settlement-type.json", j => j["simulation"]!["weight"] = 8); break;
            case "template": Change(fixture, WorldContentFixtures.TemplatePath, j => j["weight"] = 8); break;
            case "building-composition": Change(fixture, "resources/blueprints/station.blueprint.json", j => j["layout"]!["cells"]![0]![0] = 1); break;
            case "rule": Change(fixture, "resources/railway-rules/rail.railway-rule.json", j => j["braking_deceleration"] = 9); break;
            case "voxel-specification": Change(fixture, "resources/voxels/machine.voxel.json", j => j["simulation"]!["max_durability"] = 9); break;
        }
        using var second = fixture.Load();
        Assert.AreNotEqual(first.View.Core.Identity, second.View.Core.Identity);
        Assert.AreNotEqual(World(first.View).GenerationContentRevision, World(second.View).GenerationContentRevision);
    }

    [TestMethod]
    public void Profile_parameters_affect_generation_but_not_external_core_input()
    {
        var fixture = WorldContentFixtures.Content();
        using var first = fixture.Load();
        Change(fixture, WorldContentFixtures.ProfilePath, j => j["configuration"]!["resources"]![0]!["global_regeneration_remaining_reserve_ratio"] = 0.5);
        using var second = fixture.Load();
        Assert.AreEqual(first.View.Core.Identity, second.View.Core.Identity);
        Assert.AreNotEqual(World(first.View).GenerationContentRevision, World(second.View).GenerationContentRevision);
    }

    [TestMethod]
    public void Changing_only_a_rule_good_changes_its_revision_and_both_business_identities()
    {
        var fixture = WorldContentFixtures.Content();
        const string path = "resources/rules/market.market-rule.json";
        Change(fixture, path, j => j["simulation"]!["good_prices"] = new JsonArray(new JsonObject
        {
            ["good"] = "ore", ["parameters"] = j["simulation"]!["default_goods_prices"]!.DeepClone()
        }));
        using var first = fixture.Load();
        Change(fixture, path, j => j["simulation"]!["good_prices"]![0]!["good"] = "rail");
        using var second = fixture.Load();
        Assert.AreEqual(new K.GoodId.Voxel(new("core:ore")), first.View.Core.MarketRules.Single().GoodPrices.Single().Good);
        Assert.AreEqual(new K.GoodId.Voxel(new("core:rail")), second.View.Core.MarketRules.Single().GoodPrices.Single().Good);
        Assert.AreNotEqual(first.View.Core.MarketRules.Single().Revision, second.View.Core.MarketRules.Single().Revision);
        Assert.AreNotEqual(first.View.Core.Identity, second.View.Core.Identity);
        // ore and rail are already referenced by resources/start inventory and railway in both loads.
        Assert.AreNotEqual(World(first.View).GenerationContentRevision, World(second.View).GenerationContentRevision);
    }

    [TestMethod]
    public void Changing_only_start_inventory_good_changes_generation_with_the_same_referenced_goods()
    {
        var fixture = WorldContentFixtures.Content();
        using var first = fixture.Load();
        Change(fixture, WorldContentFixtures.ProfilePath, j => j["start"]!["trains"]![0]!["inventory"]![0]!["voxel"] = "rail");
        using var second = fixture.Load();
        var original = World(first.View);
        var changed = World(second.View);
        Assert.AreEqual(new K.GoodId.Voxel(new("core:ore")), original.Start.Trains[0].Inventory.Single().Good);
        Assert.AreEqual(new K.GoodId.Voxel(new("core:rail")), changed.Start.Trains[0].Inventory.Single().Good);
        Assert.AreEqual(original.Start.Trains[0].Inventory.Single().Quantity, changed.Start.Trains[0].Inventory.Single().Quantity);
        Assert.AreEqual(first.View.Core.Identity, second.View.Core.Identity);
        // ore is already a resource and rail is already the railway good in both loads.
        Assert.AreNotEqual(original.GenerationContentRevision, changed.GenerationContentRevision);
    }

    [TestMethod]
    public void Duplicate_terrain_bands_preserve_generation_revision_after_core_normalization()
    {
        var fixture = WorldContentFixtures.Content();
        using var first = fixture.Load();
        Change(fixture, WorldContentFixtures.ProfilePath, j =>
        {
            var bands = j["configuration"]!["terrain_bands"]!.AsArray();
            bands.Add(bands[0]!.DeepClone());
        });
        using var second = fixture.Load();
        Assert.AreEqual(2, World(second.View).Configuration.TerrainBands.Length);
        Assert.AreEqual(first.View.Core.Identity, second.View.Core.Identity);
        Assert.AreEqual(World(first.View).GenerationContentRevision, World(second.View).GenerationContentRevision);
    }

    [TestMethod]
    public void Unreferenced_blueprint_does_not_change_either_business_identity()
    {
        var fixture = WorldContentFixtures.Content();
        using var first = fixture.Load();
        WorldContentFixtures.AddBlueprint(fixture, DesignFixtures.Blueprint("spare"));
        using var second = fixture.Load();
        Assert.AreEqual(first.View.Core.Identity, second.View.Core.Identity);
        Assert.AreEqual(World(first.View).GenerationContentRevision, World(second.View).GenerationContentRevision);
        Assert.AreEqual(first.View.Blueprints.Length + 1, second.View.Blueprints.Length);
    }

    [TestMethod]
    public void Type_without_a_template_changes_core_catalog_but_not_generation_candidates()
    {
        var fixture = WorldContentFixtures.Content();
        using var first = fixture.Load();
        Change(fixture, "resources/settlement-types/unused.settlement-type.json", j => j["simulation"]!["weight"] = 100);
        using var second = fixture.Load();
        Assert.AreNotEqual(first.View.Core.Identity, second.View.Core.Identity);
        Assert.AreEqual(World(first.View).GenerationContentRevision, World(second.View).GenerationContentRevision);
    }

}
