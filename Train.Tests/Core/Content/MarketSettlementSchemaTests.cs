// 从 schema 契约选择缺失、类型、范围和重复项的功能例子；通过真实共同加载入口观察结果。
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Tests.Core.Logging;

namespace Train.Tests.Core.Content;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:content")]
[TestCategory("scope:markets")]
[TestCategory("scope:settlements")]
public sealed class MarketSettlementSchemaTests
{
    [DataTestMethod]
    [DataRow(true, "/schema_version", "2", "schema_version_unsupported")]
    [DataRow(true, "/market_tables", null, "field_invalid")]
    [DataRow(true, "/market_tables/0/id", "\"Bad\"", "local_id_invalid")]
    [DataRow(true, "/market_tables/0/goods", "[]", "field_invalid")]
    [DataRow(true, "/market_tables/0/goods/0/sensitivity", null, "field_invalid")]
    [DataRow(true, "/market_tables/0/goods/0/sensitivity", "-1", "field_invalid")]
    [DataRow(true, "/market_tables/0/goods/0/sensitivity", "1e400", "field_invalid")]
    [DataRow(true, "/market_tables/0/goods/0/sensitivity", "\"0.6\"", "field_invalid")]
    [DataRow(true, "/market_tables/0/goods/0/min_price", "0", "field_invalid")]
    [DataRow(true, "/market_tables/0/goods/0/min_price", "1.5", "field_invalid")]
    [DataRow(true, "/market_tables/0/goods/0/max_price", "59", "field_invalid")]
    [DataRow(true, "/market_tables/0/goods/0/max_price", "9223372036854775808", "field_invalid")]
    [DataRow(true, "/market_tables/0/goods/0/definition", "\"Bad:grain\"", "local_id_invalid")]
    [DataRow(true, "/market_tables/0/goods/0/definition", "\" core:grain\"", "local_id_invalid")]
    [DataRow(true, "/market_tables/0/tick_interval_steps", "3600", "unknown_field")]
    [DataRow(false, "/schema_version", "0", "schema_version_unsupported")]
    [DataRow(false, "/settlement_types", "null", "field_invalid")]
    [DataRow(false, "/settlement_types/0/id", "\"core:depot\"", "local_id_invalid")]
    [DataRow(false, "/settlement_types/0/name", "\"   \"", "name_invalid")]
    [DataRow(false, "/settlement_types/0/supply", null, "field_invalid")]
    [DataRow(false, "/settlement_types/0/supply/0/definition", "\"core:grain \"", "local_id_invalid")]
    [DataRow(false, "/settlement_types/0/supply/0/capacity", "0", "field_invalid")]
    [DataRow(false, "/settlement_types/0/supply/0/capacity", "2147483648", "field_invalid")]
    [DataRow(false, "/settlement_types/0/supply/0/restore_per_tick", "-1", "field_invalid")]
    [DataRow(false, "/settlement_types/0/demand/0/restore_per_tick", "1.5", "field_invalid")]
    [DataRow(false, "/settlement_types/0/preferred_terrain", "\"plains\"", "field_invalid")]
    [DataRow(false, "/settlement_types/0/preferred_terrain/0", "\"Plains\"", "local_id_invalid")]
    [DataRow(false, "/settlement_types/0/preferred_terrain/0", "\"plains\"", "local_id_invalid")]
    [DataRow(false, "/settlement_types/0/preferred_terrain/0", "\" core:plains\"", "local_id_invalid")]
    [DataRow(false, "/settlement_types/0/preferred_terrain/0", "\"core:plains \"", "local_id_invalid")]
    [DataRow(false, "/settlement_types/0/preferred_terrain/0", "\"core:Plains\"", "local_id_invalid")]
    [DataRow(false, "/settlement_types/0/preferred_terrain/0", "\"core::plains\"", "local_id_invalid")]
    [DataRow(false, "/settlement_types/0/preferred_terrain/0", "\":plains\"", "local_id_invalid")]
    [DataRow(false, "/settlement_types/0/preferred_terrain/0", "\"core:\"", "local_id_invalid")]
    [DataRow(false, "/settlement_types/0/weight", "0", "field_invalid")]
    [DataRow(false, "/settlement_types/0/population_min", "0", "field_invalid")]
    [DataRow(false, "/settlement_types/0/population_max", "99", "field_invalid")]
    [DataRow(false, "/settlement_types/0/initial_platform_count", "-1", "field_invalid")]
    [DataRow(false, "/settlement_types/0/initial_platform_count", null, "field_invalid")]
    [DataRow(false, "/settlement_types/0/platform_count", "1", "unknown_field")]
    public void Invalid_fields_reject_with_the_source_and_exact_field_path(bool market, string pointer, string? replacement, string code)
    {
        using var session = LoggerSession.Create();
        var source = MarketSettlementFixtures.BaseSource();
        var file = market ? MarketSettlementFixtures.MarketFile : MarketSettlementFixtures.TypeFile;
        var json = market ? MarketSettlementExamples.PricesFile : MarketSettlementExamples.DepotFile;
        source.AddDefinitionFile("core", file, MarketSettlementFixtures.Edit(json, pointer, replacement));
        var reloader = new ContentReloader(source, session.Logger);
        var result = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsNull(result.Snapshot);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == code && item.Source == file && item.JsonPath == pointer),
            string.Join(Environment.NewLine, result.Diagnostics.Select(item => $"{item.Code} {item.JsonPath}: {item.Message}")));
    }

    [TestMethod]
    public void Complete_terrain_identities_preserve_namespaces_without_a_definition_catalog()
    {
        using var session = LoggerSession.Create();
        var source = MarketSettlementFixtures.BaseSource();
        var identities = new[] { "core:plains", "cccccccc-dddd-4eee-8fff-000000000003:plains", "a:b", new string('n', 64) + ":" + new string('t', 64) };
        var terrainJson = System.Text.Json.JsonSerializer.Serialize(identities);
        source.AddDefinitionFile("core", MarketSettlementFixtures.TypeFile,
            MarketSettlementFixtures.Edit(MarketSettlementExamples.DepotFile,
                "/settlement_types/0/preferred_terrain", terrainJson));
        var reloader = new ContentReloader(source, session.Logger);

        Assert.IsTrue(reloader.Reload().IsCommitted);
        CollectionAssert.AreEquivalent(identities,
            reloader.Current!.SettlementTypes[MarketSettlementExamples.DepotId].Definition.PreferredTerrain.ToArray());
    }

    [TestMethod]
    public void Duplicate_complete_terrain_identities_reject_at_the_second_item()
    {
        using var session = LoggerSession.Create();
        var source = MarketSettlementFixtures.BaseSource();
        source.AddDefinitionFile("core", MarketSettlementFixtures.TypeFile,
            MarketSettlementFixtures.Edit(MarketSettlementExamples.DepotFile,
                "/settlement_types/0/preferred_terrain", "[\"core:plains\",\"core:plains\"]"));

        var result = new ContentReloader(source, session.Logger).Reload();

        Assert.AreEqual(ContentReloadStatus.Rejected, result.Status);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.FieldInvalid &&
            item.Source == MarketSettlementFixtures.TypeFile && item.JsonPath == "/settlement_types/0/preferred_terrain/1"));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Duplicate_json_properties_and_duplicate_goods_are_rejected(bool market)
    {
        using var session = LoggerSession.Create();
        var file = market ? MarketSettlementFixtures.MarketFile : MarketSettlementFixtures.TypeFile;
        var json = market ? MarketSettlementExamples.PricesFile : MarketSettlementExamples.DepotFile;
        var source = MarketSettlementFixtures.BaseSource();
        source.AddDefinitionFile("core", file, json.Replace("\"schema_version\": 1", "\"schema_version\": 1, \"schema_version\": 1", StringComparison.Ordinal));
        var duplicateProperty = new ContentReloader(source, session.Logger).Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, duplicateProperty.Status);
        Assert.IsTrue(duplicateProperty.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.DuplicateProperty));

        var pointer = market ? "/market_tables/0/goods/1/definition" : "/settlement_types/0/demand/1/definition";
        var repeatedId = market ? "\"grain\"" : "\"coal\"";
        source.AddDefinitionFile("core", file, MarketSettlementFixtures.Edit(json, pointer, repeatedId));
        var duplicateGoods = new ContentReloader(source, session.Logger).Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, duplicateGoods.Status);
        Assert.IsTrue(duplicateGoods.Diagnostics.Any(item => item.Code == "field_invalid" && item.JsonPath == pointer));
    }

    [TestMethod]
    public void Zero_sensitivity_fixed_price_and_Int64_price_limit_are_valid()
    {
        using var session = LoggerSession.Create();
        var source = MarketSettlementFixtures.BaseSource();
        source.AddDefinitionFile("core", MarketSettlementFixtures.MarketFile, MarketSettlementExamples.ConstantPriceFile);
        var reloader = new ContentReloader(source, session.Logger);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        var rule = reloader.Current!.MarketTables[MarketSettlementExamples.PricesId].Definition.Goods[MarketSettlementExamples.GrainId];
        Assert.AreEqual(MarketSettlementExamples.ConstantPrice.Sensitivity, rule.Sensitivity);
        Assert.AreEqual(MarketSettlementExamples.ConstantPrice.MinPrice, rule.MinPrice);
        Assert.AreEqual(MarketSettlementExamples.ConstantPrice.MaxPrice, rule.MaxPrice);
        source.AddDefinitionFile("core", MarketSettlementFixtures.MarketFile,
            MarketSettlementFixtures.Edit(MarketSettlementExamples.ConstantPriceFile, "/market_tables/0/goods/0/sensitivity", "-0"));
        Assert.IsFalse(reloader.Reload().Changed);
        var maximum = MarketSettlementFixtures.Edit(MarketSettlementExamples.ConstantPriceFile, "/market_tables/0/goods/0/max_price", long.MaxValue.ToString());
        maximum = MarketSettlementFixtures.Edit(maximum, "/market_tables/0/goods/0/min_price", long.MaxValue.ToString());
        source.AddDefinitionFile("core", MarketSettlementFixtures.MarketFile, maximum);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        Assert.AreEqual(long.MaxValue, reloader.Current!.MarketTables[MarketSettlementExamples.PricesId].Definition.Goods[MarketSettlementExamples.GrainId].MaxPrice);
    }

    [TestMethod]
    public void Zero_or_excess_recovery_and_Int32_generation_limits_are_valid_specs()
    {
        using var session = LoggerSession.Create();
        var source = MarketSettlementFixtures.BaseSource();
        var reloader = new ContentReloader(source, session.Logger);
        source.AddDefinitionFile("core", MarketSettlementFixtures.TypeFile, MarketSettlementExamples.NoRecoveryFile);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        Assert.AreEqual(MarketSettlementExamples.NoRecovery.RestorePerTick,
            reloader.Current!.SettlementTypes[MarketSettlementExamples.DepotId].Definition.Supply[MarketSettlementExamples.GrainId].RestorePerTick);
        source.AddDefinitionFile("core", MarketSettlementFixtures.TypeFile, MarketSettlementExamples.RecoveryExceedsCapacityFile);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        var quantity = reloader.Current!.SettlementTypes[MarketSettlementExamples.DepotId].Definition.Demand[MarketSettlementExamples.GrainId];
        Assert.AreEqual(MarketSettlementExamples.RecoveryExceedsCapacity.Capacity, quantity.Capacity);
        Assert.AreEqual(MarketSettlementExamples.RecoveryExceedsCapacity.RestorePerTick, quantity.RestorePerTick);

        var maximum = MarketSettlementExamples.DepotFile;
        foreach (var pointer in new[] { "/settlement_types/0/weight", "/settlement_types/0/population_max", "/settlement_types/0/initial_platform_count", "/settlement_types/0/supply/0/capacity", "/settlement_types/0/supply/0/restore_per_tick" })
            maximum = MarketSettlementFixtures.Edit(maximum, pointer, int.MaxValue.ToString());
        source.AddDefinitionFile("core", MarketSettlementFixtures.TypeFile, maximum);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        var type = reloader.Current!.SettlementTypes[MarketSettlementExamples.DepotId].Definition;
        Assert.AreEqual(int.MaxValue, type.Weight);
        Assert.AreEqual(int.MaxValue, type.Population.Max);
        Assert.AreEqual(int.MaxValue, type.InitialPlatformCount);
        Assert.AreEqual(int.MaxValue, type.Supply[MarketSettlementExamples.GrainId].Capacity);
        Assert.AreEqual(int.MaxValue, type.Supply[MarketSettlementExamples.GrainId].RestorePerTick);
    }
}
