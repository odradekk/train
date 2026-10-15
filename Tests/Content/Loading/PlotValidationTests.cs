using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Train.Content.Tests.Loading.WorldContentFixtures;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class PlotValidationTests
{
    [TestMethod]
    [DataRow("medium", "medium", 0d)]
    [DataRow("small_station", "medium", 96d)]
    [DataRow("small_station", "large_station", 128d)]
    public void Template_projects_longest_station_plot_even_without_buildings(string first, string second, double expected)
    {
        var fixture = WorldContentFixtures.Content();
        var template = Template();
        template["plots"] = new JsonArray(
            new JsonObject { ["id"] = 7, ["size"] = first, ["at"] = new JsonArray(-200, 0, 0), ["orientation"] = "pz_py" },
            new JsonObject { ["id"] = 11, ["size"] = second, ["at"] = new JsonArray(200, 0, 0) });
        fixture.AddFile(ContentFixture.BuiltIn, TemplatePath, template.ToJsonString());
        using var candidate = fixture.Load();
        Assert.AreEqual(expected, candidate.View.Core.SettlementTemplates.Single().PlatformLengthMeters);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Deleted_station_token_is_rejected_in_current_content_schema(bool blueprint)
    {
        var fixture = WorldContentFixtures.Content();
        if (blueprint) AddBlueprint(fixture, Building(size: "station"));
        else
        {
            var template = Template();
            template["plots"]![0]!["size"] = "station";
            fixture.AddFile(ContentFixture.BuiltIn, TemplatePath, template.ToJsonString());
        }
        Assert.IsTrue(Failure(fixture.Prepare()).Diagnostics.Any(d => d.Code == "invalid_field"
            && d.Field == (blueprint ? "/simulation/plot_size" : "/plots/0/size")));
    }

    [TestMethod]
    [DataRow("small", "111100")]
    [DataRow("medium", "011100")]
    [DataRow("large", "001100")]
    [DataRow("giant", "000100")]
    [DataRow("small_station", "000011")]
    [DataRow("large_station", "000001")]
    public void Buildings_follow_the_six_by_six_plot_fit_table(string buildingSize, string expected)
    {
        string[] columns = ["small", "medium", "large", "giant", "small_station", "large_station"];
        for (var index = 0; index < columns.Length; index++)
        {
            var fixture = WorldContentFixtures.Content();
            AddBlueprint(fixture, Building("table-building", buildingSize,
                buildingSize is "small_station" or "large_station" ? "station" : "production"));
            var template = Template();
            template["plots"] = new JsonArray(new JsonObject
            {
                ["id"] = 7, ["size"] = columns[index], ["at"] = new JsonArray(0, 0, 0), ["building"] = "table-building"
            });
            fixture.AddFile(ContentFixture.BuiltIn, TemplatePath, template.ToJsonString());
            using var candidate = expected[index] == '1' ? fixture.Load() : null;
            if (candidate is not null)
                Assert.IsNotNull(candidate.View.Core.SettlementTemplates.Single().Plots.Single().PrebuiltDesign);
            else Assert.IsTrue(Failure(fixture.Prepare()).Diagnostics.Any(d => d.Field == "/plots" && d.Code == "invalid_field"),
                $"{buildingSize} on {columns[index]}");
        }
    }

    [TestMethod]
    [DataRow(K.PlotSize.Small, 48, 48, 48)]
    [DataRow(K.PlotSize.Medium, 96, 96, 80)]
    [DataRow(K.PlotSize.Large, 192, 192, 128)]
    [DataRow(K.PlotSize.Giant, 384, 384, 256)]
    [DataRow(K.PlotSize.SmallStation, 192, 112, 48)]
    [DataRow(K.PlotSize.LargeStation, 256, 208, 64)]
    public void Building_envelopes_accept_boundary_cells_and_reject_each_outside_face(K.PlotSize size, int width, int depth, int height)
    {
        using var candidate = DesignFixtures.Content().Load();
        var draft = DesignFixtures.Vehicle() with
        {
            Specification = new C.DesignSpecification.Building(size,
                size is K.PlotSize.SmallStation or K.PlotSize.LargeStation ? K.BuildingKind.Station : K.BuildingKind.Production, [])
        };
        foreach (var (at, valid) in new (C.GridPosition, bool)[]
        {
            (new(-width / 2, 0, -depth / 2), true), (new(width / 2 - 1, height - 1, depth / 2 - 1), true),
            (new(-width / 2 - 1, 0, 0), false), (new(width / 2, 0, 0), false),
            (new(0, -1, 0), false), (new(0, height, 0), false),
            (new(0, 0, -depth / 2 - 1), false), (new(0, 0, depth / 2), false)
        })
            Assert.AreEqual(valid, DesignFixtures.Evaluate(candidate.View,
                draft with { Layout = new([DesignFixtures.Entry()], [new(at, 0)], []) }).IsValid, $"{size} at {at}");
    }

    [TestMethod]
    [DataRow("px_py", 48, 0, true)]
    [DataRow("pz_py", 48, 0, false)]
    [DataRow("pz_py", 49, 0, true)]
    [DataRow("nx_py", 48, 0, false)]
    [DataRow("nx_py", 49, 0, true)]
    [DataRow("nz_py", 0, 48, false)]
    [DataRow("nz_py", 0, 49, true)]
    public void Rotated_even_plot_cells_decide_adjacency_and_overlap_regardless_of_height(string orientation, int x, int z, bool valid)
    {
        var fixture = WorldContentFixtures.Content();
        var template = Template();
        template["plots"] = new JsonArray(
            new JsonObject { ["id"] = 7, ["size"] = "small", ["at"] = new JsonArray(0, 0, 0), ["orientation"] = orientation },
            new JsonObject { ["id"] = 11, ["size"] = "small", ["at"] = new JsonArray(x, 100, z) });
        fixture.AddFile(ContentFixture.BuiltIn, TemplatePath, template.ToJsonString());
        AssertSpatialResult(fixture, valid);
    }

    [TestMethod]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(int.MaxValue, false)]
    public void Rotated_station_fits_exact_scene_boundary_and_rejects_one_cell_or_overflow_beyond_it(int x, bool valid)
    {
        var fixture = WorldContentFixtures.Content();
        var scene = Scene();
        scene["appearance"]!["flatten"] = JsonNode.Parse("""{"from":[-55,-96],"to":[56,95],"height":0}""");
        AddBlueprint(fixture, scene);
        var template = Template();
        template["plots"] = new JsonArray(new JsonObject
        {
            ["id"] = 7, ["size"] = "small_station", ["at"] = new JsonArray(x, 0, 0), ["orientation"] = "pz_py"
        });
        fixture.AddFile(ContentFixture.BuiltIn, TemplatePath, template.ToJsonString());
        AssertSpatialResult(fixture, valid);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Scene_range_combines_occupied_bounds_and_flatten_including_plots_only_inside_flatten(bool spanBoth)
    {
        var fixture = WorldContentFixtures.Content();
        var scene = Scene();
        scene["appearance"]!["flatten"] = JsonNode.Parse("""{"from":[76,-24],"to":[123,23],"height":0}""");
        if (spanBoth) scene["layout"]!["cells"] = new JsonArray(new JsonArray(-24, 0, -24, 0));
        AddBlueprint(fixture, scene);
        var template = Template();
        template["plots"] = new JsonArray(new JsonObject
        {
            ["id"] = 7, ["size"] = "small", ["at"] = new JsonArray(spanBoth ? 0 : 100, 0, 0)
        });
        fixture.AddFile(ContentFixture.BuiltIn, TemplatePath, template.ToJsonString());
        using var candidate = fixture.Load();
        Assert.AreEqual(1, candidate.View.Core.SettlementTemplates.Single().Plots.Length);
    }

    private static void AssertSpatialResult(ContentFixture fixture, bool valid)
    {
        if (valid) { using var candidate = fixture.Load(); }
        else Assert.IsTrue(Failure(fixture.Prepare()).Diagnostics.Any(d => d.Code == "invalid_field" && d.Field.StartsWith("/plots/", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(false, "/plots/1/at")]
    [DataRow(true, "/plots/0/at")]
    public void Spatial_diagnostics_use_source_array_positions_when_plot_numbers_are_unsorted(bool overlap, string expectedField)
    {
        var fixture = WorldContentFixtures.Content();
        var template = Template();
        template["plots"] = new JsonArray(
            new JsonObject { ["id"] = 11, ["size"] = "small", ["at"] = new JsonArray(overlap ? 48 : 0, 0, 0) },
            new JsonObject { ["id"] = 7, ["size"] = "small", ["at"] = new JsonArray(overlap ? 0 : 500, 0, 0), ["orientation"] = "pz_py" });
        fixture.AddFile(ContentFixture.BuiltIn, TemplatePath, template.ToJsonString());
        var diagnostic = Failure(fixture.Prepare()).Diagnostics.Single(d => d.Code == "invalid_field"
            && d.Field.StartsWith("/plots/", StringComparison.Ordinal));
        Assert.AreEqual(expectedField, diagnostic.Field);
    }
}
