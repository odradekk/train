using System.Text.Json.Nodes;
using Godot;

namespace Train.Engine.Tests;

internal static class ContentFixture
{
    internal static string ReadResource(string relative) => Godot.FileAccess.GetFileAsString("res://builtin/core/resources/" + relative);

    internal static void WriteVoxel(string resources, string id)
    {
        var voxels = Path.Combine(resources, "voxels");
        Directory.CreateDirectory(Path.Combine(voxels, "grain"));
        var definition = JsonNode.Parse(ReadResource("voxels/grain.voxels.json"))!;
        definition["voxels"]![0]!["id"] = id;
        File.WriteAllText(Path.Combine(voxels, id + ".voxels.json"), definition.ToJsonString());
        File.WriteAllBytes(Path.Combine(voxels, "grain/placeholder.png"), Godot.FileAccess.GetFileAsBytes("res://builtin/core/resources/voxels/grain/placeholder.png"));
    }

    internal static void WriteMod(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "mod.json"), $$"""
            {"schema_version":1,"id":"{{Guid.NewGuid():D}}","name":"Engine fixture","version":"1.0.0","authors":["Engine tests"]}
            """);
        WriteVoxel(Path.Combine(directory, "resources"), "engine-grain");
    }

    internal static void WriteEconomy(string resources, double sensitivity, int capacity)
    {
        Directory.CreateDirectory(Path.Combine(resources, "markets"));
        Directory.CreateDirectory(Path.Combine(resources, "settlement-types"));
        File.WriteAllText(Path.Combine(resources, "markets/smoke.market-tables.json"), $$"""
            {"schema_version":1,"market_tables":[{"id":"smoke-prices","goods":[{"definition":"core:grain","sensitivity":{{sensitivity.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"min_price":1,"max_price":1000}]}]}
            """);
        File.WriteAllText(Path.Combine(resources, "settlement-types/smoke.settlement-types.json"), $$"""
            {"schema_version":1,"settlement_types":[{"id":"smoke-settlement","name":"Smoke settlement","supply":[{"definition":"core:grain","capacity":{{capacity}},"restore_per_tick":1}],"demand":[],"preferred_terrain":[],"weight":1,"population_min":1,"population_max":1,"initial_platform_count":0}]}
            """);
    }
}
