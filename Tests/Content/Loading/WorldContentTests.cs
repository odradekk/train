using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class WorldContentTests
{
    [TestMethod]
    public void Factory_publishes_world_definitions_and_resolves_generation_rules_and_ordered_fleet()
    {
        var fixture = WorldContentFixtures.Content();
        using var candidate = fixture.Load();
        var view = candidate.View;
        Assert.AreEqual("core:people", view.Core.PopulationRules.Single().Id.Value);
        Assert.AreEqual(600, view.Core.PopulationRules.Single().StatisticsPeriodTicks);
        Assert.AreEqual(9999, view.Core.MarketRules.Single().DefaultGoodsPrices.Spread.Value);
        Assert.AreEqual(60UL, view.Core.ConstructionRules.Single().FoundingDurationTicks);
        Assert.AreEqual(7L, view.Core.RailwayRules.Single().RepairRailUnitsPerBreak);
        Assert.AreEqual(0d, view.Core.CombatRules.Single().SurrenderThreshold);
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
        secondFixture.ReverseEnumeration = true;
        using var first = firstFixture.Load();
        using var second = secondFixture.Load();
        Assert.AreEqual(JsonSerializer.Serialize(first.View.Core), JsonSerializer.Serialize(second.View.Core));
        Assert.AreEqual(JsonSerializer.Serialize(World(first.View)), JsonSerializer.Serialize(World(second.View)));
        Assert.AreEqual(JsonSerializer.Serialize(Found(first.View.FindTerrain(new("core:plains")))),
            JsonSerializer.Serialize(Found(second.View.FindTerrain(new("core:plains")))));
    }

    [TestMethod]
    [DataRow("founding")]
    [DataRow("inventory")]
    [DataRow("production_plans")]
    public void Nonempty_later_stage_template_fields_reject_the_package(string field)
    {
        var fixture = WorldContentFixtures.Content();
        var template = WorldContentFixtures.Template();
        template[field] = new JsonArray(new JsonObject());
        fixture.AddFile(ContentFixture.BuiltIn, WorldContentFixtures.TemplatePath, template.ToJsonString());
        var failure = Failure(fixture.Prepare());
        Assert.IsTrue(failure.Diagnostics.Any(diagnostic => diagnostic.Code == "unsupported_format" && diagnostic.Field == "/" + field));
    }

    [TestMethod]
    [DataRow("ordinary-on-station")]
    [DataRow("station-on-ordinary")]
    public void Station_buildings_and_station_plots_must_match_in_both_directions(string mutation)
    {
        var fixture = WorldContentFixtures.Content();
        var template = WorldContentFixtures.Template();
        if (mutation == "ordinary-on-station") template["plots"]![0]!["building"] = "workshop";
        else template["plots"]![1]!["building"] = "station";
        fixture.AddFile(ContentFixture.BuiltIn, WorldContentFixtures.TemplatePath, template.ToJsonString());
        Assert.IsTrue(Failure(fixture.Prepare()).Diagnostics.Any(diagnostic => diagnostic.Code == "invalid_field" || diagnostic.Code == "design_invalid"));
    }

    [TestMethod]
    public void Ordinary_building_can_use_a_larger_plot_and_empty_future_arrays_are_accepted()
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
            Assert.AreEqual(new C.SettlementPlotLayout(7, K.PlotSize.Station, new(0, 0, 0), C.VoxelOrientation.PxPy), layout.Plots[0]);
            Assert.AreEqual(new C.SettlementPlotLayout(11, K.PlotSize.Medium, new(30, 0, 0), C.VoxelOrientation.PzPy), layout.Plots[1]);
        }
        var empty = factory.WithSettlementCopies(copied, []);
        Assert.AreEqual(copied.PersistentRevision, empty.PersistentRevision);
        Assert.AreEqual(JsonSerializer.Serialize(Found(copied.FindSettlementLayout(new(41)))),
            JsonSerializer.Serialize(Found(empty.FindSettlementLayout(new(41)))));
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
            "plot-number" => reference with { Plots = [new(8, K.PlotSize.Station), new(11, K.PlotSize.Medium)] },
            "plot-size" => reference with { Plots = [new(7, K.PlotSize.Small), new(11, K.PlotSize.Medium)] },
            "existing-owner" => reference with { Owner = new(41) },
            _ => reference
        };
        ImmutableArray<K.OwnedSettlementTemplateReference> copies = mutation == "duplicate-batch" ? [reference, reference] : [reference];
        var retained = JsonSerializer.Serialize(Found(basis.FindSettlementLayout(new(41))));
        Assert.ThrowsExactly<ArgumentException>(() => factory.WithSettlementCopies(basis, copies));
        Assert.AreEqual(retained, JsonSerializer.Serialize(Found(basis.FindSettlementLayout(new(41)))));
        Assert.IsInstanceOfType<C.ContentLookup<C.SettlementLayoutProjection>.NotFound>(basis.FindSettlementLayout(new(42)));
        Assert.AreEqual(1UL, basis.PersistentRevision.Value);
    }

    [TestMethod]
    public void A_new_initial_load_can_change_source_templates_while_retained_owner_copies_keep_their_values()
    {
        var fixture = WorldContentFixtures.Content();
        using var first = fixture.Load();
        var owned = ContentFactory.Create().WithSettlementCopies(first.View, [Reference(41)]);
        var retained = JsonSerializer.Serialize(Found(owned.FindSettlementLayout(new(41))));
        var changed = WorldContentFixtures.Template();
        changed["weight"] = 9;
        changed["plots"]![1]!["size"] = "large";
        changed["plots"]![1]!["at"] = new JsonArray(40, 0, 0);
        fixture.AddFile(ContentFixture.BuiltIn, WorldContentFixtures.TemplatePath, changed.ToJsonString());
        using var second = fixture.Load();
        Assert.AreEqual(9, second.View.Core.SettlementTemplates.Single().Weight);
        Assert.AreEqual(K.PlotSize.Large, second.View.Core.SettlementTemplates.Single().Plots[1].Size);
        var newlyOwned = ContentFactory.Create().WithSettlementCopies(second.View,
            [Reference(42) with { Plots = [new(7, K.PlotSize.Station), new(11, K.PlotSize.Large)] }]);
        Assert.AreEqual(new C.GridPosition(40, 0, 0), Found(newlyOwned.FindSettlementLayout(new(42))).Plots[1].At);
        Assert.AreEqual(retained, JsonSerializer.Serialize(Found(owned.FindSettlementLayout(new(41)))));
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

    private static K.OwnedSettlementTemplateReference Reference(ulong owner) =>
        new(new(owner), new("core:farm-start"), [new(7, K.PlotSize.Station), new(11, K.PlotSize.Medium)]);

    private static K.DesignHash Design(C.IContentView view, string id) =>
        ((C.DesignEvaluation.Complete)Found(view.FindBlueprint(new(C.BlueprintLibrary.Packages, new("core:" + id)))).Evaluation).Design;

    internal static K.WorldGenerationInput World(C.IContentView view, ulong seed = 1, string version = "test")
    {
        var result = ContentFactory.Create().CreateWorldInput(view, new("core:world"), seed, version);
        Assert.IsInstanceOfType<C.ContentResult<K.WorldGenerationInput>.Success>(result, result.ToString());
        return ((C.ContentResult<K.WorldGenerationInput>.Success)result).Value;
    }

    internal static T Found<T>(C.ContentLookup<T> lookup) where T : notnull
    {
        Assert.IsInstanceOfType<C.ContentLookup<T>.Found>(lookup, lookup.ToString());
        return ((C.ContentLookup<T>.Found)lookup).Value;
    }

    private static C.ContentResult<C.IContentCandidate>.Failure Failure(C.ContentResult<C.IContentCandidate> result)
    {
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result, result.ToString());
        return (C.ContentResult<C.IContentCandidate>.Failure)result;
    }
}
