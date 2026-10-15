using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Train.Content.Tests.Loading.WorldContentFixtures;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class WorldContentTests
{
    [TestMethod]
    [DataRow(null, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public void Settlement_type_display_reports_major_with_false_default(bool? major, bool expected)
    {
        var fixture = WorldContentFixtures.Content();
        if (major.HasValue)
            Change(fixture, "resources/settlement-types/farm.settlement-type.json", json => json["display"]!["major"] = major.Value);
        using var candidate = fixture.Load();
        var display = Found(candidate.View.FindDefinitionDisplay(new K.CoreDefinitionReference.SettlementType(new("core:farm"))));
        Assert.AreEqual(expected, display.Major);
    }

    [TestMethod]
    public void Terrain_models_resolve_to_designs_available_in_the_fixed_view()
    {
        var fixture = WorldContentFixtures.Content();
        var terrain = WorldContentFixtures.Terrain();
        terrain["decorations"] = new JsonArray(new JsonObject { ["scene_model"] = "scene", ["weight"] = 3 });
        terrain["decoration_target_per_square_meter"] = 0.1;
        fixture.AddFile(ContentFixture.BuiltIn, "resources/terrain-types/plains.terrain-type.json", terrain.ToJsonString());
        using var candidate = fixture.Load();
        var appearance = Found(candidate.View.FindTerrain(new("core:plains")));
        var expected = Design(candidate.View, "scene");
        Assert.AreEqual(expected, appearance.Decorations.Single().SceneModel);
        foreach (var model in appearance.RailwayModels)
        {
            Assert.AreEqual(expected, model.Bridge);
            Assert.AreEqual(expected, model.TunnelPortal);
            Assert.IsInstanceOfType<C.DesignEvaluation.Complete>(Found(candidate.View.FindDesign(model.Bridge)));
        }
    }

    [TestMethod]
    public void Factory_publishes_world_definitions_and_resolves_generation_rules_and_ordered_fleet()
    {
        var fixture = WorldContentFixtures.Content();
        Change(fixture, ProfilePath, json =>
        {
            var configuration = json["configuration"]!;
            configuration["hostile_settlement_ratio"] = 0.25;
            configuration["moisture_noise"] = new JsonArray(new JsonObject { ["scale_meters"] = 300, ["amplitude"] = 2 });
            configuration["river_noise"] = new JsonArray(new JsonObject { ["scale_meters"] = 400, ["amplitude"] = 3 });
        });
        using var candidate = fixture.Load();
        var view = candidate.View;
        Assert.AreEqual("core:people", view.Core.PopulationRules.Single().Id.Value);
        Assert.AreEqual("core:market", view.Core.MarketRules.Single().Id.Value);
        Assert.AreEqual("core:building", view.Core.ConstructionRules.Single().Id.Value);
        Assert.AreEqual("core:rail", view.Core.RailwayRules.Single().Id.Value);
        Assert.AreEqual("core:battle", view.Core.CombatRules.Single().Id.Value);
        Assert.AreEqual(new C.RailwayAppearanceProjection(new("core:rail"), 20, 0.1),
            Found(view.FindRailwayAppearance(new("core:rail"))));
        var terrain = Found(view.FindTerrain(new("core:plains")));
        Assert.AreEqual(new K.VoxelDefinitionId("core:scenery"), terrain.SurfaceVoxel);
        CollectionAssert.AreEqual(new[] { 1, 2 }, terrain.RailwayModels.Select(model => model.Tracks).ToArray());
        Assert.AreEqual(2, view.Core.SettlementTypes.Length);
        Assert.IsFalse(view.Core.SettlementTemplates.Any(template => template.Type.Value == "core:unused"));
        var template = view.Core.SettlementTemplates.Single();
        Assert.AreEqual("core:farm", template.Type.Value);
        Assert.AreEqual(2, template.Weight);
        CollectionAssert.AreEqual(new[] { 7, 11 }, template.Plots.Select(plot => plot.Number).ToArray());
        Assert.AreEqual(Design(view, "station"), template.Plots[0].PrebuiltDesign);
        Assert.IsNull(template.Plots[1].PrebuiltDesign);
        var world = World(view, 987, "generator-test");
        Assert.AreEqual(987UL, world.WorldSeed);
        Assert.AreEqual("generator-test", world.GeneratorVersion);
        Assert.AreEqual(1000d, world.Configuration.WidthMeters);
        Assert.AreEqual(2000d, world.Configuration.HeightMeters);
        Assert.AreEqual(0.5, world.Configuration.SettlementRatio);
        Assert.AreEqual(0d, world.Configuration.LairRatio);
        Assert.AreEqual(0.25, world.Configuration.HostileSettlementRatio);
        CollectionAssert.AreEqual(new[] { new K.NoiseOctave(100, 0), new K.NoiseOctave(200, 1) }, world.Configuration.ElevationNoise.ToArray());
        CollectionAssert.AreEqual(new[] { new K.NoiseOctave(300, 2) }, world.Configuration.MoistureNoise.ToArray());
        CollectionAssert.AreEqual(new[] { new K.NoiseOctave(400, 3) }, world.Configuration.RiverNoise.ToArray());
        Assert.AreEqual(new K.RuleSelection(new("core:people"), new("core:market"), new("core:building"), new("core:rail"), new("core:battle")), world.Configuration.Rules);
        Assert.IsTrue(world.Configuration.NpcInitialization.FillInputsAndLivingTargets);
        Assert.AreEqual(2d, world.Configuration.NpcInitialization.OutputReserveHours);
        CollectionAssert.AreEquivalent(new[] { Design(view, "wagon"), Design(view, "engine") }, world.Configuration.NpcInitialization.ForSaleVehicleCandidates.ToArray());
        CollectionAssert.AreEqual(new[] { Design(view, "wagon"), Design(view, "engine"), Design(view, "wagon") }, world.Start.Trains[0].Vehicles.ToArray());
        Assert.AreEqual(Design(view, "engine"), world.Start.Trains[1].Vehicles.Single());
        Assert.AreEqual(Design(view, "engine"), world.Configuration.StartConstraints.RequiredAffordableCommissionDesign);
        Assert.AreEqual(long.MaxValue, world.Start.InitialMoney.Value);
        Assert.AreEqual(long.MaxValue, world.Start.Trains[0].Inventory.Single().Quantity);
        Assert.AreEqual(0.25, world.Configuration.Resources.Single().GlobalRegenerationRemainingReserveRatio);
    }

    [TestMethod]
    public void Reversed_enumeration_preserves_projected_values_and_semantic_identities()
    {
        var firstFixture = WorldContentFixtures.Content();
        var secondFixture = WorldContentFixtures.Content();
        foreach (var fixture in new[] { firstFixture, secondFixture })
        {
            var alternate = Template();
            alternate["id"] = "farm-alternate";
            alternate["weight"] = 5;
            fixture.AddFile(ContentFixture.BuiltIn, "resources/settlement-templates/farm-alternate.settlement-template.json", alternate.ToJsonString());
        }
        secondFixture.ReverseEnumeration = true;
        using var first = firstFixture.Load();
        using var second = secondFixture.Load();
        Assert.AreEqual(first.View.Core.Identity, second.View.Core.Identity);
        CollectionAssert.AreEqual(new[] { "core:farm-alternate", "core:farm-start" }, first.View.Core.SettlementTemplates.Select(template => template.Id.Value).ToArray());
        CollectionAssert.AreEqual(first.View.Core.SettlementTemplates.Select(template => (template.Id, template.Type, template.Weight)).ToArray(),
            second.View.Core.SettlementTemplates.Select(template => (template.Id, template.Type, template.Weight)).ToArray());
        foreach (var (left, right) in first.View.Core.SettlementTemplates.Zip(second.View.Core.SettlementTemplates))
            CollectionAssert.AreEqual(left.Plots.ToArray(), right.Plots.ToArray());
        CollectionAssert.AreEqual(first.View.Core.PopulationRules.Single().ConsumptionGoods.Select(good => (good.Good, good.SelectionOrder)).ToArray(),
            second.View.Core.PopulationRules.Single().ConsumptionGoods.Select(good => (good.Good, good.SelectionOrder)).ToArray());
        Assert.AreEqual(first.View.Core.MarketRules.Single().Revision, second.View.Core.MarketRules.Single().Revision);
        Assert.AreEqual(first.View.Core.ConstructionRules.Single(), second.View.Core.ConstructionRules.Single());
        Assert.AreEqual(first.View.Core.RailwayRules.Single(), second.View.Core.RailwayRules.Single());
        Assert.AreEqual(first.View.Core.CombatRules.Single(), second.View.Core.CombatRules.Single());
        var firstWorld = World(first.View);
        var secondWorld = World(second.View);
        Assert.AreEqual(firstWorld.GenerationContentRevision, secondWorld.GenerationContentRevision);
        Assert.AreEqual(firstWorld.Configuration.Rules, secondWorld.Configuration.Rules);
        CollectionAssert.AreEqual(firstWorld.Configuration.Resources.Select(resource => (resource.Good, resource.Renewable, resource.MinimumReserve, resource.MaximumReserve)).ToArray(),
            secondWorld.Configuration.Resources.Select(resource => (resource.Good, resource.Renewable, resource.MinimumReserve, resource.MaximumReserve)).ToArray());
        CollectionAssert.AreEqual(firstWorld.Configuration.NpcInitialization.ForSaleVehicleCandidates.ToArray(), secondWorld.Configuration.NpcInitialization.ForSaleVehicleCandidates.ToArray());
        Assert.AreEqual(firstWorld.Start.InitialMoney, secondWorld.Start.InitialMoney);
        Assert.AreEqual(firstWorld.Start.Trains.Length, secondWorld.Start.Trains.Length);
        foreach (var (left, right) in firstWorld.Start.Trains.Zip(secondWorld.Start.Trains))
        {
            CollectionAssert.AreEqual(left.Vehicles.ToArray(), right.Vehicles.ToArray());
            CollectionAssert.AreEqual(left.Inventory.ToArray(), right.Inventory.ToArray());
        }
        var firstTerrain = Found(first.View.FindTerrain(new("core:plains")));
        var secondTerrain = Found(second.View.FindTerrain(new("core:plains")));
        Assert.AreEqual(firstTerrain.Id, secondTerrain.Id);
        Assert.AreEqual(firstTerrain.SurfaceVoxel, secondTerrain.SurfaceVoxel);
        Assert.AreEqual(firstTerrain.SubsurfaceVoxel, secondTerrain.SubsurfaceVoxel);
        Assert.AreEqual(firstTerrain.RockVoxel, secondTerrain.RockVoxel);
        CollectionAssert.AreEqual(firstTerrain.RailwayModels.ToArray(), secondTerrain.RailwayModels.ToArray());
    }

    [TestMethod]
    [DataRow("production")]
    [DataRow("extraction")]
    [DataRow("recovery")]
    public void Template_initialization_resolves_buildings_three_good_kinds_and_typed_plans_in_replay(string kind)
    {
        var fixture = InitializedTemplate(kind);
        using var candidate = fixture.Load();
        var view = candidate.View;
        var template = view.Core.SettlementTemplates.Single();
        CollectionAssert.AreEqual(new[] { new K.FoundingBuildingProjection(7, Design(view, "station")),
            new K.FoundingBuildingProjection(11, Design(view, "workshop")) }, template.FoundingBuildings.ToArray());
        Assert.AreEqual(new K.GoodsQuantity(new K.GoodId.Voxel(new("core:block")), 11),
            template.SpecialInitialInventory.Single(q => q.Good is K.GoodId.Voxel));
        Assert.AreEqual(new K.GoodsQuantity(new K.GoodId.Composite(new("core:device")), 2),
            template.SpecialInitialInventory.Single(q => q.Good is K.GoodId.Composite));
        var frame = (C.DesignEvaluation.Complete)Found(view.FindDesign(Design(view, "frame")));
        Assert.AreEqual(new K.GoodsQuantity(new K.GoodId.Assembly(frame.Composition), 3),
            template.SpecialInitialInventory.Single(q => q.Good is K.GoodId.Assembly));
        K.ProcessRef process = kind == "recovery" ? new K.ProcessRef.Recovery(new("core:test")) : new K.ProcessRef.Production(new("core:test"));
        Assert.AreEqual(new K.TemplateProductionPlan(11, kind == "extraction" ? K.CapabilityKind.Extraction : K.CapabilityKind.Production,
            process, false, -5), template.ProductionPlans.Single());
        var factory = ContentFactory.Create();
        var replay = ContentCaptureTests.Success(factory.CaptureReplay([view], new([], [], [], [view.Core.Identity])));
        var historical = ContentCaptureTests.Success(factory.DecodeReplay(replay.Encode())).History.Single().Core.SettlementTemplates.Single();
        CollectionAssert.AreEqual(template.FoundingBuildings.ToArray(), historical.FoundingBuildings.ToArray());
        CollectionAssert.AreEqual(template.SpecialInitialInventory.ToArray(), historical.SpecialInitialInventory.ToArray());
        CollectionAssert.AreEqual(template.ProductionPlans.ToArray(), historical.ProductionPlans.ToArray());
    }

    [TestMethod]
    [DataRow("missing-founding-plot", "/founding/0/plot", "invalid_field")]
    [DataRow("founding-fit", "/founding/0/building", "invalid_field")]
    [DataRow("inventory-vehicle", "/inventory", "reference_wrong_kind")]
    [DataRow("inventory-alias", "/inventory", "invalid_field")]
    [DataRow("empty-plan-plot", "/production_plans/0/plot", "invalid_field")]
    [DataRow("missing-capability", "/production_plans/0/capability", "invalid_field")]
    [DataRow("wrong-process-kind", "/production_plans/0/process", "invalid_field")]
    [DataRow("wrong-facility-tag", "/production_plans/0/process", "invalid_field")]
    [DataRow("duplicate-plan", "/production_plans", "invalid_field")]
    public void Invalid_initialization_rejects_the_package_with_a_field_diagnostic(string change, string field, string code)
    {
        var fixture = InitializedTemplate();
        Change(fixture, TemplatePath, json =>
        {
            switch (change)
            {
                case "missing-founding-plot": json["founding"]![0]!["plot"] = 99; break;
                case "founding-fit": json["founding"]![0]!["building"] = "station"; break;
                case "inventory-vehicle": json["inventory"]![1]!["blueprint"] = "engine"; break;
                case "inventory-alias": json["inventory"]!.AsArray().Add(new JsonObject { ["blueprint"] = "frame-alias", ["quantity"] = 1 }); break;
                case "empty-plan-plot": json["plots"]![1]!.AsObject().Remove("building"); break;
                case "wrong-process-kind": json["production_plans"]![0]!["capability"] = "extraction"; break;
                case "duplicate-plan": json["production_plans"]!.AsArray().Add(json["production_plans"]![0]!.DeepClone()); break;
            }
        });
        if (change == "inventory-alias")
        {
            var alias = DesignFixtures.Blueprint("frame-alias");
            alias["category"] = "large_equipment";
            alias.Remove("simulation");
            AddBlueprint(fixture, alias);
        }
        if (change == "missing-capability")
            Change(fixture, "resources/voxels/machine.voxel.json", json => json["simulation"]!["behaviors"]!.AsObject().Remove("production"));
        if (change == "wrong-facility-tag")
            Change(fixture, "resources/blueprints/workshop.blueprint.json", json => json["simulation"]!["facility_tags"] = new JsonArray());
        Assert.IsTrue(Failure(fixture.Prepare()).Diagnostics.Any(d => d.Code == code && d.Field == field));
    }

    [TestMethod]
    public void Ordinary_building_can_use_a_larger_plot_and_empty_initialization_arrays_are_accepted()
    {
        var fixture = WorldContentFixtures.Content();
        var template = WorldContentFixtures.Template();
        template["plots"]![1]!["building"] = "workshop";
        foreach (var field in new[] { "founding", "inventory", "production_plans" }) template[field] = new JsonArray();
        fixture.AddFile(ContentFixture.BuiltIn, WorldContentFixtures.TemplatePath, template.ToJsonString());
        using var candidate = fixture.Load();
        Assert.AreEqual(Design(candidate.View, "workshop"), candidate.View.Core.SettlementTemplates.Single().Plots[1].PrebuiltDesign);
        Assert.AreEqual(K.PlotSize.Medium, candidate.View.Core.SettlementTemplates.Single().Plots[1].Size);
    }

    [TestMethod]
    public void Copies_use_final_owners_and_change_only_one_persistent_revision_per_batch()
    {
        var fixture = WorldContentFixtures.Content();
        using var candidate = fixture.Load();
        var original = candidate.View;
        var factory = ContentFactory.Create();
        var copied = factory.WithSettlementCopies(original, [Reference(41), Reference(42)]);
        Assert.AreEqual(original.PersistentRevision.Value + 1, copied.PersistentRevision.Value);
        Assert.AreEqual(original.Generation, copied.Generation);
        Assert.AreEqual(original.Core.Identity, copied.Core.Identity);
        Assert.AreEqual(0UL, original.PersistentRevision.Value);
        Assert.IsInstanceOfType<C.ContentLookup<C.SettlementLayoutProjection>.NotFound>(original.FindSettlementLayout(new(41)));
        foreach (var owner in new[] { 41UL, 42UL })
        {
            var layout = Found(copied.FindSettlementLayout(new(owner)));
            Assert.AreEqual(new K.SettlementId(owner), layout.Owner);
            Assert.AreEqual(new K.SettlementTemplateId("core:farm-start"), layout.SourceTemplate);
            Assert.AreEqual(Design(original, "scene"), layout.SceneModel.Design);
            Assert.AreEqual(new C.SettlementPlotLayout(7, K.PlotSize.SmallStation, new(0, 0, 0), C.VoxelOrientation.PxPy), layout.Plots[0]);
            Assert.AreEqual(new C.SettlementPlotLayout(11, K.PlotSize.Medium, new(160, 0, 0), C.VoxelOrientation.PzPy), layout.Plots[1]);
        }
        var empty = factory.WithSettlementCopies(copied, []);
        Assert.AreEqual(copied.PersistentRevision, empty.PersistentRevision);
        AssertLayoutEqual(Found(copied.FindSettlementLayout(new(41))), Found(empty.FindSettlementLayout(new(41))));
    }

    [TestMethod]
    [DataRow("missing-source")]
    [DataRow("plot-number")]
    [DataRow("plot-size")]
    [DataRow("existing-owner")]
    [DataRow("duplicate-batch")]
    public void Copy_precondition_errors_throw_and_leave_the_input_view_unchanged(string mutation)
    {
        using var candidate = WorldContentFixtures.Content().Load();
        var factory = ContentFactory.Create();
        var basis = factory.WithSettlementCopies(candidate.View, [Reference(41)]);
        var reference = Reference(42);
        reference = mutation switch
        {
            "missing-source" => reference with { SourceTemplate = new("core:absent") },
            "plot-number" => reference with { Plots = [new(8, K.PlotSize.SmallStation), new(11, K.PlotSize.Medium)] },
            "plot-size" => reference with { Plots = [new(7, K.PlotSize.Small), new(11, K.PlotSize.Medium)] },
            "existing-owner" => reference with { Owner = new(41) },
            _ => reference
        };
        ImmutableArray<K.OwnedSettlementTemplateReference> copies = mutation == "duplicate-batch" ? [reference, reference] : [reference];
        var retained = Found(basis.FindSettlementLayout(new(41)));
        Assert.ThrowsExactly<ArgumentException>(() => factory.WithSettlementCopies(basis, copies));
        AssertLayoutEqual(retained, Found(basis.FindSettlementLayout(new(41))));
        Assert.IsInstanceOfType<C.ContentLookup<C.SettlementLayoutProjection>.NotFound>(basis.FindSettlementLayout(new(42)));
        Assert.AreEqual(1UL, basis.PersistentRevision.Value);
    }

    [TestMethod]
    public void A_new_initial_load_can_change_source_templates_while_retained_owner_copies_keep_their_values()
    {
        var fixture = WorldContentFixtures.Content();
        using var first = fixture.Load();
        var owned = ContentFactory.Create().WithSettlementCopies(first.View, [Reference(41)]);
        var retained = Found(owned.FindSettlementLayout(new(41)));
        var changed = WorldContentFixtures.Template();
        changed["weight"] = 9;
        changed["plots"]![1]!["size"] = "large";
        changed["plots"]![1]!["at"] = new JsonArray(240, 0, 0);
        fixture.AddFile(ContentFixture.BuiltIn, WorldContentFixtures.TemplatePath, changed.ToJsonString());
        using var second = fixture.Load();
        Assert.AreEqual(9, second.View.Core.SettlementTemplates.Single().Weight);
        Assert.AreEqual(K.PlotSize.Large, second.View.Core.SettlementTemplates.Single().Plots[1].Size);
        var newlyOwned = ContentFactory.Create().WithSettlementCopies(second.View,
            [Reference(42) with { Plots = [new(7, K.PlotSize.SmallStation), new(11, K.PlotSize.Large)] }]);
        Assert.AreEqual(new C.GridPosition(240, 0, 0), Found(newlyOwned.FindSettlementLayout(new(42))).Plots[1].At);
        AssertLayoutEqual(retained, Found(owned.FindSettlementLayout(new(41))));
    }

    [TestMethod]
    public void A_zero_lair_ratio_does_not_hide_missing_weight_references()
    {
        var fixture = WorldContentFixtures.Content();
        var profile = WorldContentFixtures.Profile();
        profile["configuration"]!["lair_weights"] = new JsonArray(new JsonObject { ["type"] = "nest", ["weight"] = 1 });
        fixture.AddFile(ContentFixture.BuiltIn, WorldContentFixtures.ProfilePath, profile.ToJsonString());
        Assert.IsTrue(Failure(fixture.Prepare()).Diagnostics.Any(diagnostic => diagnostic.Code == "reference_missing" &&
            diagnostic.Source?.Path == WorldContentFixtures.ProfilePath && diagnostic.Field.StartsWith("/configuration/lair_weights", StringComparison.Ordinal)));
    }

    private static void AssertLayoutEqual(C.SettlementLayoutProjection expected, C.SettlementLayoutProjection actual)
    {
        Assert.AreEqual(expected.Owner, actual.Owner);
        Assert.AreEqual(expected.SourceTemplate, actual.SourceTemplate);
        Assert.AreEqual(expected.Display, actual.Display);
        Assert.AreEqual(expected.SceneModel.Design, actual.SceneModel.Design);
        Assert.AreEqual(expected.SceneModel.Display, actual.SceneModel.Display);
        Assert.AreEqual(expected.SceneModel.Scene.RootFlatten, actual.SceneModel.Scene.RootFlatten);
        CollectionAssert.AreEqual(expected.Plots.ToArray(), actual.Plots.ToArray());
        CollectionAssert.AreEqual(expected.SceneModel.Scene.Voxels.Select(voxel => (voxel.At, voxel.Voxel, voxel.Orientation, voxel.Shape, voxel.Paint)).ToArray(),
            actual.SceneModel.Scene.Voxels.Select(voxel => (voxel.At, voxel.Voxel, voxel.Orientation, voxel.Shape, voxel.Paint)).ToArray());
    }
}
