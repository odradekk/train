// 从命名数据例子验证新领域格式复用同一加载、包隔离和原子发布流程。
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Core.Content.Markets;
using Train.Core.Content.Settlements;
using Train.Core.Content.Voxels;
using Train.Tests.Core.Logging;

namespace Train.Tests.Core.Content;

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:content")]
[TestCategory("scope:markets")]
[TestCategory("scope:settlements")]
public sealed class MarketSettlementPublicationTests
{
    [TestMethod]
    public void All_four_domains_publish_together_and_captured_definitions_stay_unchanged()
    {
        using var session = LoggerSession.Create();
        var source = MarketSettlementFixtures.CompleteSource();
        var reloader = new ContentReloader(source, session.Logger);
        var changes = new List<ContentChange>();
        reloader.Changed += change =>
        {
            Assert.AreSame(change.Current, reloader.Current);
            Assert.IsTrue(change.Current.MarketTables.ContainsKey(MarketSettlementExamples.PricesId));
            Assert.IsTrue(change.Current.SettlementTypes.ContainsKey(MarketSettlementExamples.DepotId));
            changes.Add(change);
        };

        var first = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, first.Status);
        var original = first.Snapshot!;
        Assert.AreEqual(1L, original.Generation);
        Assert.AreEqual(2, original.Voxels.Count);
        Assert.AreEqual(1, original.Blueprints.Count);
        Assert.AreEqual(1, original.MarketTables.Count);
        Assert.AreEqual(2, original.SettlementTypes.Count);
        Assert.AreEqual(200L, original.Blueprints[ContentReloadExamples.BlueprintId].Attributes.BomValue);
        Assert.AreEqual(10d, original.Blueprints[ContentReloadExamples.BlueprintId].Attributes.Mass);

        var prices = original.MarketTables[MarketSettlementExamples.PricesId];
        Assert.AreEqual(MarketSettlementFixtures.MarketFile, prices.Origin.SourceFile);
        Assert.AreEqual(2, prices.Definition.Goods.Count);
        AssertPrice(MarketSettlementExamples.GrainPrice, prices.Definition.Goods[MarketSettlementExamples.GrainId]);
        AssertPrice(MarketSettlementExamples.CoalPrice, prices.Definition.Goods[MarketSettlementExamples.CoalId]);
        var depot = original.SettlementTypes[MarketSettlementExamples.DepotId].Definition;
        AssertQuantity(MarketSettlementExamples.GrainSupply, depot.Supply[MarketSettlementExamples.GrainId]);
        AssertQuantity(MarketSettlementExamples.GrainDemand, depot.Demand[MarketSettlementExamples.GrainId]);
        AssertQuantity(MarketSettlementExamples.CoalDemand, depot.Demand[MarketSettlementExamples.CoalId]);
        Assert.AreEqual(MarketSettlementExamples.DepotPopulation.Min, depot.Population.Min);
        Assert.AreEqual(MarketSettlementExamples.DepotPopulation.Max, depot.Population.Max);
        CollectionAssert.AreEquivalent(new[] { "forest", "plains" }, depot.PreferredTerrain.ToArray());
        Assert.AreEqual(2, depot.Weight);
        Assert.AreEqual(1, depot.InitialPlatformCount);
        var quiet = original.SettlementTypes[MarketSettlementExamples.QuietTypeId].Definition;
        Assert.AreEqual(0, quiet.Supply.Count);
        Assert.AreEqual(0, quiet.Demand.Count);
        Assert.AreEqual(0, quiet.PreferredTerrain.Count);
        Assert.AreEqual(1, quiet.Population.Min);
        Assert.AreEqual(1, quiet.Population.Max);
        Assert.AreEqual(0, quiet.InitialPlatformCount);

        source.AddDefinitionFile("core", MarketSettlementFixtures.MarketFile,
            MarketSettlementFixtures.Edit(MarketSettlementExamples.PricesFile, "/market_tables/0/goods/0/sensitivity", "0.7"));
        var updated = reloader.Reload();
        Assert.IsTrue(updated.Changed);
        Assert.AreEqual(2L, updated.Snapshot!.Generation);
        Assert.AreEqual(2, changes.Count);
        Assert.AreSame(original, changes[1].Previous);
        var newPrices = updated.Snapshot.MarketTables[MarketSettlementExamples.PricesId];
        Assert.AreEqual(0.7, newPrices.Definition.Goods[MarketSettlementExamples.GrainId].Sensitivity);
        Assert.AreNotEqual(prices.SemanticRevision, newPrices.SemanticRevision);
        AssertPrice(MarketSettlementExamples.GrainPrice, prices.Definition.Goods[MarketSettlementExamples.GrainId]);

        var unchanged = reloader.Reload();
        Assert.IsFalse(unchanged.Changed);
        Assert.AreSame(updated.Snapshot, unchanged.Snapshot);
        Assert.AreEqual(2, changes.Count);
    }

    [TestMethod]
    public void Voxel_value_updates_the_complete_snapshot_without_revising_price_or_type_specs()
    {
        using var session = LoggerSession.Create();
        var source = MarketSettlementFixtures.CompleteSource();
        var reloader = new ContentReloader(source, session.Logger);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        var previous = reloader.Current!;
        source.AddDefinitionFile("core", "goods.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("grain", "粮食", "价格底价来自 Value", value: 125, mass: 5, tagsJson: "[]"),
            ContentFixtures.Good("coal", "煤", "另一种货物", value: 200, mass: 5)));

        var updated = reloader.Reload();
        Assert.IsTrue(updated.IsCommitted && updated.Changed);
        Assert.AreEqual(2L, updated.Snapshot!.Generation);
        Assert.AreEqual(125L, updated.Snapshot.Voxels[MarketSettlementExamples.GrainId].Definition.Value);
        Assert.AreEqual(250L, updated.Snapshot.Blueprints[ContentReloadExamples.BlueprintId].Attributes.BomValue);
        Assert.AreEqual(100L, previous.Voxels[MarketSettlementExamples.GrainId].Definition.Value);
        Assert.AreEqual(200L, previous.Blueprints[ContentReloadExamples.BlueprintId].Attributes.BomValue);
        var oldPrices = previous.MarketTables[MarketSettlementExamples.PricesId];
        var newPrices = updated.Snapshot.MarketTables[MarketSettlementExamples.PricesId];
        Assert.AreEqual(oldPrices.SemanticRevision, newPrices.SemanticRevision);
        Assert.AreEqual(oldPrices.PublicationFingerprint, newPrices.PublicationFingerprint);
        Assert.AreEqual(previous.SettlementTypes[MarketSettlementExamples.DepotId].SemanticRevision,
            updated.Snapshot.SettlementTypes[MarketSettlementExamples.DepotId].SemanticRevision);
    }

    [TestMethod]
    public void Display_name_updates_publication_while_quantity_changes_update_type_semantics()
    {
        using var session = LoggerSession.Create();
        var source = MarketSettlementFixtures.CompleteSource();
        var reloader = new ContentReloader(source, session.Logger);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        var previous = reloader.Current!.SettlementTypes[MarketSettlementExamples.DepotId];
        var renamed = MarketSettlementFixtures.Edit(MarketSettlementExamples.DepotFile, "/settlement_types/0/name", "\"新货站\"");
        source.AddDefinitionFile("core", MarketSettlementFixtures.TypeFile, renamed);
        Assert.IsTrue(reloader.Reload().Changed);
        var current = reloader.Current!.SettlementTypes[MarketSettlementExamples.DepotId];
        Assert.AreEqual("新货站", current.Definition.Name);
        Assert.AreEqual(previous.SemanticRevision, current.SemanticRevision);
        Assert.AreNotEqual(previous.PublicationFingerprint, current.PublicationFingerprint);

        source.AddDefinitionFile("core", MarketSettlementFixtures.TypeFile,
            MarketSettlementFixtures.Edit(renamed, "/settlement_types/0/demand/1/capacity", "4"));
        Assert.IsTrue(reloader.Reload().Changed);
        var changed = reloader.Current!.SettlementTypes[MarketSettlementExamples.DepotId];
        Assert.AreEqual(4, changed.Definition.Demand[MarketSettlementExamples.GrainId].Capacity);
        Assert.AreNotEqual(current.SemanticRevision, changed.SemanticRevision);
        Assert.AreEqual(3, previous.Definition.Demand[MarketSettlementExamples.GrainId].Capacity);
    }

    [TestMethod]
    public void Array_order_whitespace_and_source_paths_do_not_publish_another_generation()
    {
        using var session = LoggerSession.Create();
        var input = new SwitchableContentSource(MarketSettlementFixtures.CompleteSource());
        var reloader = new ContentReloader(input, session.Logger);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        var previous = reloader.Current;
        Assert.AreEqual(1, previous!.MarketTables.Count);
        Assert.AreEqual(2, previous.SettlementTypes.Count);
        var reordered = MarketSettlementFixtures.BaseSource();
        var priceFile = MarketSettlementFixtures.Edit(MarketSettlementExamples.PricesFile, "/market_tables/0/goods", """
            [{"definition":"coal","max_price":320,"min_price":120,"sensitivity":0.5},
             {"definition":"grain","max_price":180,"min_price":60,"sensitivity":0.6}]
            """);
        var typeFile = MarketSettlementFixtures.Edit(MarketSettlementExamples.DepotFile, "/settlement_types/0/preferred_terrain", "[\"forest\",\"plains\"]");
        typeFile = MarketSettlementFixtures.Edit(typeFile, "/settlement_types/0/demand", """
            [{"definition":"grain","restore_per_tick":1,"capacity":3},
             {"definition":"coal","restore_per_tick":1,"capacity":6}]
            """);
        typeFile = MarketSettlementFixtures.Edit(typeFile, "/settlement_types/0/name", "\"  货站  \"");
        reordered.AddDefinitionFile("core", "other.market-tables.json", priceFile);
        reordered.AddDefinitionFile("core", "other.settlement-types.json", typeFile);
        reordered.AddDefinitionFile("core", "quiet.settlement-types.json", MarketSettlementExamples.QuietTypeFile);
        input.Current = reordered;

        var result = reloader.Reload();
        Assert.IsTrue(result.IsCommitted);
        Assert.IsFalse(result.Changed);
        Assert.AreSame(previous, result.Snapshot);
    }

    [DataTestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public void Invalid_goods_references_retain_every_previous_index(bool market, bool sceneOnly)
    {
        using var session = LoggerSession.Create();
        var source = MarketSettlementFixtures.CompleteSource();
        var reloader = new ContentReloader(source, session.Logger);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        var previous = reloader.Current;
        var notifications = 0;
        reloader.Changed += _ => notifications++;
        if (sceneOnly)
            source.AddDefinitionFile("core", "scene.voxels.json", ContentFixtures.GoodsFile(
                ContentFixtures.Good("not-goods", "场景", "不能交易", tagsJson: "[\"scene_only\"]")));
        var reference = sceneOnly ? "core:not-goods" : "core:missing";
        var file = market ? MarketSettlementFixtures.MarketFile : MarketSettlementFixtures.TypeFile;
        var json = market ? MarketSettlementExamples.PricesFile : MarketSettlementExamples.DepotFile;
        var pointer = market ? "/market_tables/0/goods/0/definition" : "/settlement_types/0/supply/0/definition";
        source.AddDefinitionFile("core", file, MarketSettlementFixtures.Edit(json, pointer, $"\"{reference}\""));

        var rejected = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status);
        Assert.IsFalse(rejected.Changed);
        Assert.AreSame(previous, rejected.Snapshot);
        Assert.AreSame(previous, reloader.Current);
        Assert.AreEqual(0, notifications);
        var code = sceneOnly ? ContentDiagnosticCodes.NonGoodsVoxelReference : ContentDiagnosticCodes.UnresolvedVoxelReference;
        Assert.IsTrue(rejected.Diagnostics.Any(item => item.Code == code && item.Severity == ContentDiagnosticSeverity.Error &&
            item.Source == file && item.Message.Contains(reference, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Optional_package_failure_removes_all_its_domains_and_cascading_dependants()
    {
        using var session = LoggerSession.Create();
        var source = MarketSettlementFixtures.CompleteSource();
        const string failed = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
        const string dependant = "bbbbbbbb-bbbb-4ccc-8ddd-eeeeeeeeeeee";
        source.AddMod("failed", failed);
        source.AddMod("dependant", dependant);
        source.AddDefinitionFile(failed, "goods.voxels.json", ContentFixtures.GoodsFile(ContentFixtures.Good("grain", "货物", "被隔离")));
        source.AddImage(failed, "iron.png", ContentFixtures.Square(16));
        source.AddDefinitionFile(failed, "pair.composites.json", ContentReloadExamples.BlueprintFile.Replace("core:structure", failed + ":grain", StringComparison.Ordinal));
        source.AddDefinitionFile(failed, "prices.market-tables.json", MarketSettlementExamples.ConstantPriceFile.Replace("\"grain\"", "\"core:missing\"", StringComparison.Ordinal));
        source.AddDefinitionFile(failed, "depot.settlement-types.json", MarketSettlementExamples.DepotFile);
        source.AddDefinitionFile(dependant, "depot.settlement-types.json", MarketSettlementExamples.DepotFile.Replace("core:grain", failed + ":grain", StringComparison.Ordinal));
        var reloader = new ContentReloader(source, session.Logger);

        var result = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.CommittedWithWarnings, result.Status);
        var snapshot = result.Snapshot!;
        Assert.AreEqual(2, snapshot.Voxels.Count);
        Assert.AreEqual(1, snapshot.Blueprints.Count);
        Assert.AreEqual(1, snapshot.MarketTables.Count);
        Assert.AreEqual(2, snapshot.SettlementTypes.Count);
        Assert.IsFalse(snapshot.Voxels.Keys.Any(id => id.Namespace == failed));
        Assert.IsFalse(snapshot.Blueprints.Keys.Any(id => id.Namespace == failed));
        Assert.IsFalse(snapshot.MarketTables.Keys.Any(id => id.Namespace == failed));
        Assert.IsFalse(snapshot.SettlementTypes.Keys.Any(id => id.Namespace is failed or dependant));
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UnresolvedVoxelReference));
    }

    [TestMethod]
    public void Mod_local_goods_resolve_in_the_owning_namespace_without_colliding_with_core_ids()
    {
        using var session = LoggerSession.Create();
        var source = MarketSettlementFixtures.CompleteSource();
        const string mod = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
        source.AddMod("valid", mod);
        source.AddDefinitionFile(mod, "goods.voxels.json", ContentFixtures.GoodsFile(ContentFixtures.Good("grain", "货物", "Mod 自有货物")));
        source.AddImage(mod, "iron.png", ContentFixtures.Square(16));
        source.AddDefinitionFile(mod, "prices.market-tables.json", MarketSettlementExamples.ConstantPriceFile);
        source.AddDefinitionFile(mod, "depot.settlement-types.json", MarketSettlementExamples.DepotFile.Replace("core:grain", "grain", StringComparison.Ordinal));
        var result = new ContentReloader(source, session.Logger).Reload();
        Assert.AreEqual(ContentReloadStatus.Committed, result.Status);
        var snapshot = result.Snapshot!;
        var localGood = new VoxelDefinitionId(mod, "grain");
        var prices = snapshot.MarketTables[new MarketTableDefinitionId(mod, "prices")];
        Assert.AreEqual(1, prices.Definition.Goods.Count);
        Assert.IsTrue(prices.Definition.Goods.ContainsKey(localGood));
        Assert.IsFalse(prices.Definition.Goods.ContainsKey(MarketSettlementExamples.GrainId));
        Assert.IsTrue(snapshot.SettlementTypes[new SettlementTypeDefinitionId(mod, "depot")].Definition.Supply.ContainsKey(localGood));
        Assert.AreEqual(2, snapshot.MarketTables.Count);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Required_reference_failure_uses_package_ownership_for_a_non_core_namespace(bool market)
    {
        using var session = LoggerSession.Create();
        var source = MarketSettlementFixtures.CompleteSource();
        var reloader = new ContentReloader(source, session.Logger);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        var previous = reloader.Current;
        const string owner = "additional-builtin";
        const string root = "memory://additional-builtin/resources";
        source.AddPackage(owner, root);
        var file = market ? "more.market-tables.json" : "more.settlement-types.json";
        var json = market ? MarketSettlementExamples.PricesFile : MarketSettlementExamples.DepotFile;
        source.AddDefinitionFile(owner, file, json.Replace("core:grain", "core:missing", StringComparison.Ordinal), root);

        var rejected = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status);
        Assert.AreSame(previous, rejected.Snapshot);
        Assert.IsTrue(rejected.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UnresolvedVoxelReference &&
            item.Severity == ContentDiagnosticSeverity.Error && item.Source == file));
    }

    [TestMethod]
    public void Removing_both_new_domains_publishes_once_and_does_not_require_market_bindings()
    {
        using var session = LoggerSession.Create();
        var input = new SwitchableContentSource(MarketSettlementFixtures.CompleteSource());
        var reloader = new ContentReloader(input, session.Logger);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        var previous = reloader.Current!;
        var empty = MarketSettlementFixtures.BaseSource();
        empty.AddDefinitionFile("core", MarketSettlementFixtures.MarketFile, MarketSettlementExamples.NoMarketDefinitionsFile);
        empty.AddDefinitionFile("core", MarketSettlementFixtures.TypeFile, MarketSettlementExamples.NoSettlementDefinitionsFile);
        input.Current = empty;
        var result = reloader.Reload();
        Assert.IsTrue(result.Changed && result.IsCommitted);
        Assert.AreEqual(2L, result.Snapshot!.Generation);
        Assert.AreEqual(0, result.Snapshot.MarketTables.Count);
        Assert.AreEqual(0, result.Snapshot.SettlementTypes.Count);
        Assert.AreEqual(1, previous.MarketTables.Count);
        Assert.AreEqual(2, previous.SettlementTypes.Count);

        empty.AddDefinitionFile("core", MarketSettlementFixtures.TypeFile, MarketSettlementExamples.DepotFile);
        var typeOnly = reloader.Reload();
        Assert.IsTrue(typeOnly.IsCommitted);
        Assert.AreEqual(0, typeOnly.Snapshot!.MarketTables.Count);
        Assert.AreEqual(1, typeOnly.Snapshot.SettlementTypes.Count);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Duplicate_definition_identity_rejects_the_entire_required_batch(bool market)
    {
        using var session = LoggerSession.Create();
        var source = MarketSettlementFixtures.CompleteSource();
        var reloader = new ContentReloader(source, session.Logger);
        Assert.IsTrue(reloader.Reload().IsCommitted);
        var previous = reloader.Current;
        source.AddDefinitionFile("core", market ? "duplicate.market-tables.json" : "duplicate.settlement-types.json",
            market ? MarketSettlementExamples.PricesFile : MarketSettlementExamples.DepotFile);
        var rejected = reloader.Reload();
        Assert.AreEqual(ContentReloadStatus.Rejected, rejected.Status);
        Assert.AreSame(previous, rejected.Snapshot);
        Assert.IsTrue(rejected.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.DuplicateLocalId));
    }

    private static void AssertPrice(MarketPriceExample expected, MarketPriceRule actual)
    {
        Assert.AreEqual(expected.Sensitivity, actual.Sensitivity);
        Assert.AreEqual(expected.MinPrice, actual.MinPrice);
        Assert.AreEqual(expected.MaxPrice, actual.MaxPrice);
    }

    private static void AssertQuantity(MarketQuantityExample expected, SettlementMarketQuantity actual)
    {
        Assert.AreEqual(expected.Capacity, actual.Capacity);
        Assert.AreEqual(expected.RestorePerTick, actual.RestorePerTick);
    }
}
