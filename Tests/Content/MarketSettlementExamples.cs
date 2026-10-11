// 市场表与聚居地类型的命名数据例子；预期由输入和手算结果独立给定。
// 功能测试比较解析与完整快照发布结果，不调用被测函数生成预期。
using System.Text.Json.Nodes;
using Train.Content;
using Train.Content.Markets;
using Train.Content.Settlements;
using Train.Content.Voxels;

namespace Train.Content.Tests;

/// <summary>价格参数的人工预期，金额单位为游戏货币。</summary>
internal sealed record MarketPriceExample(double Sensitivity, long MinPrice, long MaxPrice);

/// <summary>供给或需求的人工预期，两个数均为货物单位数。</summary>
internal sealed record MarketQuantityExample(int Capacity, int RestorePerTick);

/// <summary>初始人口闭区间的人工预期，单位为人。</summary>
internal sealed record PopulationRangeExample(int Min, int Max);

internal static class MarketSettlementExamples
{
    public static readonly MarketTableDefinitionId PricesId = new("core", "prices");
    public static readonly SettlementTypeDefinitionId DepotId = new("core", "depot");
    public static readonly SettlementTypeDefinitionId QuietTypeId = new("core", "quiet");
    public static readonly VoxelDefinitionId GrainId = new("core", "grain");
    public static readonly VoxelDefinitionId CoalId = new("core", "coal");

    public static readonly MarketPriceExample GrainPrice = new(0.6, 60, 180);
    public static readonly MarketPriceExample CoalPrice = new(0.5, 120, 320);
    public static readonly MarketPriceExample ConstantPrice = new(0, 1, 1);
    public static readonly MarketQuantityExample GrainSupply = new(8, 1);
    public static readonly MarketQuantityExample GrainDemand = new(3, 1);
    public static readonly MarketQuantityExample CoalDemand = new(6, 1);
    public static readonly MarketQuantityExample NoRecovery = new(8, 0);
    public static readonly MarketQuantityExample RecoveryExceedsCapacity = new(3, 5);
    public static readonly PopulationRangeExample DepotPopulation = new(100, 300);
    public static readonly PopulationRangeExample SingleResident = new(1, 1);

    public static readonly string PricesFile = """
        {
          "schema_version": 1,
          "market_tables": [{
            "id": "prices",
            "goods": [
              { "definition": "core:grain", "sensitivity": 0.6, "min_price": 60, "max_price": 180 },
              { "definition": "core:coal", "sensitivity": 0.5, "min_price": 120, "max_price": 320 }
            ]
          }]
        }
        """;

    // 敏感度可为零，价格区间可退化为同一个正整数；不要求区间覆盖体素 Value。
    public static readonly string ConstantPriceFile = """
        {
          "schema_version": 1,
          "market_tables": [{
            "id": "prices",
            "goods": [{ "definition": "grain", "sensitivity": 0, "min_price": 1, "max_price": 1 }]
          }]
        }
        """;

    // 粮食在两边都出现；各边内部身份唯一。地貌是集合，排序不属于语义。
    public static readonly string DepotFile = """
        {
          "schema_version": 1,
          "settlement_types": [{
            "id": "depot", "name": "货站",
            "supply": [{ "definition": "core:grain", "capacity": 8, "restore_per_tick": 1 }],
            "demand": [
              { "definition": "core:coal", "capacity": 6, "restore_per_tick": 1 },
              { "definition": "core:grain", "capacity": 3, "restore_per_tick": 1 }
            ],
            "preferred_terrain": ["core:plains", "core:forest"], "weight": 2,
            "population_min": 100, "population_max": 300, "initial_platform_count": 1
          }]
        }
        """;

    // 供需可以为空、地貌不限、人口闭区间相等、站台数为零。
    public static readonly string QuietTypeFile = """
        {
          "schema_version": 1,
          "settlement_types": [{
            "id": "quiet", "name": "无站台聚居地", "supply": [], "demand": [],
            "preferred_terrain": [], "weight": 1,
            "population_min": 1, "population_max": 1, "initial_platform_count": 0
          }]
        }
        """;

    public static readonly string NoRecoveryFile = DepotFile.Replace(
        "\"restore_per_tick\": 1", "\"restore_per_tick\": 0", StringComparison.Ordinal);
    public static readonly string RecoveryExceedsCapacityFile = DepotFile.Replace(
        "\"capacity\": 3, \"restore_per_tick\": 1", "\"capacity\": 3, \"restore_per_tick\": 5", StringComparison.Ordinal);

    public const string MissingFieldsFile = "{}";
    public const string NoMarketDefinitionsFile = "{\"schema_version\":1,\"market_tables\":[]}";
    public const string NoSettlementDefinitionsFile = "{\"schema_version\":1,\"settlement_types\":[]}";

}

internal static class MarketSettlementFixtures
{
    public const string MarketFile = "markets/prices.market-tables.json";
    public const string TypeFile = "settlement-types/depot.settlement-types.json";

    // 两单位粮食各 5 t、Value 100、耐久 100；蓝图为 10 t、Value 200、结构耐久 200。
    public static MemoryContentSource BaseSource()
    {
        var source = ContentFixtures.CorePackage();
        source.AddDefinitionFile("core", "goods.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("grain", "粮食", "价格底价来自 Value", value: 100, mass: 5, tagsJson: "[]"),
            ContentFixtures.Good("coal", "煤", "另一种货物", value: 200, mass: 5)));
        ContentFixtures.AddSquareImages(source, "iron.png");
        source.AddDefinitionFile("core", "pair.composites.json", ContentReloadExamples.BlueprintFile
            .Replace("core:structure", "core:grain", StringComparison.Ordinal));
        return source;
    }

    public static MemoryContentSource CompleteSource()
    {
        var source = BaseSource();
        source.AddDefinitionFile("core", MarketFile, MarketSettlementExamples.PricesFile);
        source.AddDefinitionFile("core", TypeFile, MarketSettlementExamples.DepotFile);
        source.AddDefinitionFile("core", "settlement-types/quiet.settlement-types.json", MarketSettlementExamples.QuietTypeFile);
        return source;
    }

    // 仅用标准 JSON DOM 构造测试输入；预期合法性与数值由测试显式给出。
    public static string Edit(string json, string pointer, string? replacement)
    {
        var root = JsonNode.Parse(json)!;
        var parts = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parent = root;
        foreach (var part in parts[..^1])
            parent = parent is JsonArray array ? array[int.Parse(part)]! : parent[part]!;
        var field = parts[^1];
        if (parent is JsonArray items)
            items[int.Parse(field)] = JsonNode.Parse(replacement!);
        else if (replacement is null)
            ((JsonObject)parent).Remove(field);
        else
            parent[field] = JsonNode.Parse(replacement);
        return root.ToJsonString();
    }
}
