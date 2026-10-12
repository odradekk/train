using Chickensoft.GoDotTest;
using Godot;
using Train.App;
using Train.App.Logging;
using Train.Content;
using Train.Content.Markets;
using Train.Content.Settlements;
using Train.Content.Voxels;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class ContentTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public void PhysicalContentReloadAddsAndRemovesDefinitionsWithoutChangingOldSnapshots()
    {
        var directory = Path.Combine(Path.GetTempPath(), "engine-content-" + Guid.NewGuid().ToString("N"));
        var resources = Path.Combine(directory, "resources");
        try
        {
            ContentFixture.WriteVoxel(resources, "grain");
            ContentFixture.WriteEconomy(resources, 0.5, 2);
            using var logger = new TrainLogger(Path.Combine(directory, "logs"), LogLevel.Info, new GodotLogOutput());
            var reloader = new ContentReloader(new GodotContentSource(logger, Path.Combine(directory, "user"), resources), logger);
            Require(reloader.Reload().IsCommitted, "Initial physical content was rejected.");
            var initial = reloader.Current!;
            var probe = new VoxelDefinitionId("core", "smoke-ore");
            Require(!initial.Voxels.ContainsKey(probe), "Probe definition already exists before addition.");
            RequireEconomy(initial, 0.5, 2);
            ContentFixture.WriteVoxel(resources, "smoke-ore");
            ContentFixture.WriteEconomy(resources, 0.75, 3);
            Require(reloader.Reload().IsCommitted, "Added content was rejected.");
            var added = reloader.Current!;
            Require(added.Generation == initial.Generation + 1 && added.Voxels.ContainsKey(probe) && !initial.Voxels.ContainsKey(probe), "Addition did not publish a new immutable snapshot.");
            RequireEconomy(initial, 0.5, 2);
            RequireEconomy(added, 0.75, 3);
            File.Delete(Path.Combine(resources, "voxels/smoke-ore.voxels.json"));
            ContentFixture.WriteEconomy(resources, 0.5, 2);
            Require(reloader.Reload().IsCommitted, "Removed content was rejected.");
            var removed = reloader.Current!;
            Require(removed.Generation == added.Generation + 1 && !removed.Voxels.ContainsKey(probe) && added.Voxels.ContainsKey(probe), "Removal did not publish a new immutable snapshot.");
            RequireEconomy(added, 0.75, 3);
            RequireEconomy(removed, 0.5, 2);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void RequireEconomy(ContentSnapshot snapshot, double sensitivity, int capacity)
    {
        var grain = new VoxelDefinitionId("core", "grain");
        Require(snapshot.MarketTables.Count == 1 && snapshot.SettlementTypes.Count == 1, "Market/settlement catalogs differ from fixture.");
        var market = snapshot.MarketTables[new MarketTableDefinitionId("core", "smoke-prices")].Definition;
        var price = market.Goods[grain];
        Require(market.Goods.Count == 1 && price.Sensitivity == sensitivity && price.MinPrice == 1 && price.MaxPrice == 1000, "Market values changed or retained stale data.");
        var type = snapshot.SettlementTypes[new SettlementTypeDefinitionId("core", "smoke-settlement")].Definition;
        var supply = type.Supply[grain];
        Require(type.Supply.Count == 1 && supply.Capacity == capacity && supply.RestorePerTick == 1 && type.Demand.Count == 0 &&
            type.PreferredTerrain.Count == 0 && type.Weight == 1 && type.Population.Min == 1 && type.Population.Max == 1 && type.InitialPlatformCount == 0,
            "Settlement values changed or retained stale data.");
    }
}
