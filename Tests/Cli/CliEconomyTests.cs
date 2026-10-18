using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;

namespace Train.Cli.Tests;

public sealed partial class CliProcessTests
{
    [TestMethod]
    public async Task Buying_a_finite_vehicle_removes_only_that_offer_and_does_not_replenish_it()
    {
        var directory = CreateDirectory();
        try
        {
            var initialRecipe = await WriteEconomyRecipe(directory, "initial", "[]", 0);
            var initialOutput = Path.Combine(directory, "initial.json");
            await Run(directory, "initial", "run", Repository, initialRecipe, initialOutput);
            var offeredVehicles = (await ReadEconomyOutput(initialOutput)).Samples[0].Settlements
                .Single(settlement => settlement.Id == 6).VehiclesForSale;
            Assert.IsTrue(offeredVehicles.Length >= 2);
            var purchasedVehicle = offeredVehicles[1];
            var recipe = await WriteEconomyRecipe(directory, "finite-vehicle", $$"""
                [{"at":{"completed_steps":0,"sequence":0},"kind":"market.buy_vehicle",
                  "seller":6,"vehicle":{{purchasedVehicle}},"maximum_payment":9223372036854775807}]
                """, 600);
            var output = Path.Combine(directory, "finite-vehicle.json");
            await Run(directory, "finite-vehicle", "run", Repository, recipe, output);
            var result = await ReadEconomyOutput(output);
            var initial = result.Samples[0];
            var final = result.Samples[^1];
            var receipt = result.CommandReceipts.Single();
            Assert.AreEqual("market.buy_vehicle", receipt.Kind);
            Assert.AreEqual(0, receipt.CommandIndex);
            Assert.AreEqual("accepted", receipt.Decision);
            Assert.IsEmpty(receipt.Problems);
            Assert.IsNotNull(receipt.QuotedTotal);
            Assert.IsTrue(receipt.QuotedTotal.Value > 0);
            Assert.AreEqual(initial.Wallet - receipt.QuotedTotal.Value, final.Wallet);
            Assert.AreEqual(2, initial.Trains.Length);
            CollectionAssert.AreEqual(initial.Trains.Select(train => train.Id).ToArray(),
                final.Trains.Select(train => train.Id).ToArray());
            Assert.AreEqual(600UL, final.Position.CompletedSteps);
            var seller = initial.Settlements.Single(settlement => settlement.Id == 6);
            Assert.AreEqual(14UL, seller.Node);
            CollectionAssert.AreEquivalent(offeredVehicles.ToArray(), seller.VehiclesForSale.ToArray());
            foreach (var settlement in initial.Settlements)
            {
                var expected = settlement.Id == seller.Id
                    ? settlement.VehiclesForSale.Where(vehicle => vehicle != purchasedVehicle).ToArray()
                    : settlement.VehiclesForSale.ToArray();
                CollectionAssert.AreEquivalent(expected,
                    final.Settlements.Single(current => current.Id == settlement.Id).VehiclesForSale.ToArray());
            }
        }
        catch
        {
            TestContext.WriteLine($"CLI 失败证据：{directory}");
            throw;
        }
        Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public async Task Economy_commands_replay_and_resume_with_the_same_receipts_and_samples()
    {
        var directory = CreateDirectory();
        try
        {
            var recipe = await WriteEconomyRecipe(directory, "economy", """
                [
                  {"at":{"completed_steps":0,"sequence":0},"kind":"lease.acquire",
                   "request":{"kind":"rent","settlement":6,"capacity":1,"duration_steps":5184000},"maximum_payment":24},
                  {"at":{"completed_steps":0,"sequence":1},"kind":"train.replace_run_plan","train":1,
                   "plan":{"loop":false,"stops":[{"kind":"station","node":14,"dwell_steps":1000}]}},
                  {"at":{"completed_steps":0,"sequence":2},"kind":"train.start","train":1},
                  {"at":{"completed_steps":800,"sequence":0},"kind":"train.replace_run_plan","train":1,
                   "plan":{"loop":false,"stops":[{"kind":"station","node":14,"dwell_steps":1000,"goods_rules":[
                     {"action":{"kind":"buy","maximum_unit_price":null},"good":{"kind":"voxel","definition":"core:staple-food"},
                      "local_inventory":6,"target_quantity":2}]}]}},
                  {"at":{"completed_steps":800,"sequence":1},"kind":"inventory.unload_goods","train":1,
                   "settlement":6,"local_inventory":9,"good":{"kind":"voxel","definition":"core:staple-food"},"quantity":2},
                  {"at":{"completed_steps":800,"sequence":2},"kind":"inventory.unload_goods","train":1,
                   "settlement":6,"local_inventory":9,"good":{"kind":"voxel","definition":"core:staple-food"},"quantity":1},
                  {"at":{"completed_steps":800,"sequence":3},"kind":"inventory.load_goods","train":1,
                   "settlement":6,"local_inventory":9,"good":{"kind":"voxel","definition":"core:staple-food"},"quantity":1}
                ]
                """, 802, 800);
            var continuousPath = Path.Combine(directory, "continuous.json");
            var resumedPath = Path.Combine(directory, "resumed.json");
            var save = Path.Combine(directory, "economy.save.json");
            await Run(directory, "strict-replay", "replay", Repository, recipe, continuousPath);
            await Run(directory, "save-economy", "save", Repository, recipe, save, "800");
            await Run(directory, "resume-economy", "resume", Repository, recipe, save, resumedPath);
            await Run(directory, "compare-economy", "compare", continuousPath, resumedPath);
            var continuous = await ReadEconomyOutput(continuousPath);
            var resumed = await ReadEconomyOutput(resumedPath);
            Assert.IsTrue(continuous.ReplayVerified);
            Assert.AreEqual(6, continuous.AcceptedCommands);
            Assert.AreEqual(1, continuous.RejectedCommands);
            CollectionAssert.AreEqual(new[] { "lease.acquire", "train.replace_run_plan", "train.start",
                    "train.replace_run_plan", "inventory.unload_goods", "inventory.unload_goods", "inventory.load_goods" },
                continuous.CommandReceipts.Select(receipt => receipt.Kind).ToArray());
            foreach (var receipt in continuous.CommandReceipts)
            {
                Assert.AreEqual(receipt.CommandIndex == 4 ? "rejected" : "accepted", receipt.Decision);
                if (receipt.CommandIndex == 4)
                {
                    var problem = receipt.Problems.Single();
                    Assert.AreEqual("insufficient_capacity", problem.Code);
                    Assert.AreEqual("LocalInventory.Capacity", problem.Field);
                }
                else Assert.IsEmpty(receipt.Problems);
            }
            var boundary = new SimulationPosition(800, 0);
            CollectionAssert.AreEqual(continuous.Checkpoints.SkipWhile(checkpoint => checkpoint.Position != boundary).ToArray(),
                resumed.Checkpoints.ToArray());
            Assert.AreEqual(JsonSerializer.Serialize(continuous.Samples.SkipWhile(sample => sample.Position != boundary), Recipe.Json),
                JsonSerializer.Serialize(resumed.Samples, Recipe.Json));
            Assert.AreEqual(JsonSerializer.Serialize(continuous.CommandReceipts.Where(receipt => receipt.CommandIndex >= 3), Recipe.Json),
                JsonSerializer.Serialize(resumed.CommandReceipts, Recipe.Json));
            Assert.AreEqual(JsonSerializer.Serialize(continuous.GoodsTransfers.Where(transfer =>
                    transfer.Position.CompletedSteps > boundary.CompletedSteps
                    || transfer.Position.CompletedSteps == boundary.CompletedSteps && transfer.Position.Sequence > boundary.Sequence), Recipe.Json),
                JsonSerializer.Serialize(resumed.GoodsTransfers, Recipe.Json));
            var initial = continuous.Samples[0];
            var beforeTrade = continuous.Samples.Single(sample => sample.Position == boundary);
            var final = continuous.Samples[^1];
            var lease = final.Leases.Single();
            Assert.AreEqual(1UL, lease.Id);
            Assert.AreEqual(6UL, lease.Settlement);
            Assert.IsFalse(lease.Expired);
            var storage = final.Inventories.Single(inventory => inventory.Id == lease.Inventory);
            Assert.AreEqual("ReadWrite", storage.Access);
            Assert.AreEqual(1L, storage.Capacity);
            Assert.AreEqual(0L, storage.Used);
            Assert.IsEmpty(storage.Goods);
            var cargoId = final.Trains.Single(train => train.Id == 1).Inventory;
            Assert.AreEqual(0L, beforeTrade.Inventories.Single(inventory => inventory.Id == cargoId).Used);
            var cargo = final.Inventories.Single(inventory => inventory.Id == cargoId);
            Assert.AreEqual(2L, cargo.Used);
            var food = cargo.Goods.Single();
            Assert.AreEqual("core:staple-food", food.Good.Definition);
            Assert.AreEqual(2L, food.Quantity);
            var run = final.Trains.Single(train => train.Id == 1).CurrentRun;
            Assert.IsNotNull(run);
            Assert.IsTrue(run.PurchasePayments > 0);
            Assert.AreEqual(0L, run.SalesReceipts);
            Assert.AreEqual(-run.PurchasePayments, run.NetCashFlow);
            Assert.AreEqual(24L, initial.Wallet - beforeTrade.Wallet);
            Assert.AreEqual(run.PurchasePayments, beforeTrade.Wallet - final.Wallet);
            Assert.AreEqual(run.PurchasePayments, continuous.GoodsTransfers.Where(transfer => transfer.Action == "Buy")
                .Sum(transfer => transfer.ActualAmount));
            Assert.AreEqual(1L, continuous.GoodsTransfers.Single(transfer => transfer.Action == "Unload").Quantity);
            Assert.AreEqual(1L, continuous.GoodsTransfers.Single(transfer => transfer.Action == "Load").Quantity);
        }
        catch
        {
            TestContext.WriteLine($"CLI 失败证据：{directory}");
            throw;
        }
        Directory.Delete(directory, recursive: true);
    }

    private static async Task<string> WriteEconomyRecipe(string directory, string name, string commands,
        ulong targetStep, params ulong[] checkpointSteps)
    {
        var recipe = JsonNode.Parse(await File.ReadAllTextAsync(RecipePath))!;
        recipe["commands"] = JsonNode.Parse(commands);
        recipe["target"] = JsonSerializer.SerializeToNode(new { completed_steps = targetStep, sequence = 0UL });
        recipe["checkpoint_steps"] = JsonSerializer.SerializeToNode(checkpointSteps);
        var path = Path.Combine(directory, name + ".recipe.json");
        await File.WriteAllTextAsync(path, recipe.ToJsonString());
        return path;
    }

    private static async Task<RunOutput> ReadEconomyOutput(string path) =>
        JsonSerializer.Deserialize<RunOutput>(await File.ReadAllTextAsync(path), Recipe.Json)
        ?? throw new AssertFailedException("CLI 缺少运行结果。");
}
