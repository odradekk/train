using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;

namespace Train.Cli.Tests;

public sealed partial class CliProcessTests
{
    [TestMethod]
    public async Task Full_starting_fleet_keeps_wallet_goods_and_jobs_when_real_commission_materials_are_missing()
    {
        var directory = CreateDirectory();
        try
        {
            var recipe = Path.Combine(Repository, "examples", "commission-shortage", "recipe.json");
            var continuousPath = Path.Combine(directory, "continuous.json");
            var resumedPath = Path.Combine(directory, "resumed.json");
            var boundarySave = Path.Combine(directory, "before-commission.save.json");
            await Run(directory, "strict-replay", "replay", Repository, recipe, continuousPath);
            await Run(directory, "save-before-commission", "save", Repository, recipe, boundarySave, "4000");
            await Run(directory, "resume-commission", "resume", Repository, recipe, boundarySave, resumedPath);
            await Run(directory, "compare-resumed", "compare", continuousPath, resumedPath);
            if (OperatingSystem.IsWindows())
                await Run(directory, "check-example", "check", Repository, recipe,
                    Path.Combine(Repository, "examples", "commission-shortage", "save.json"));

            var continuous = await ReadEconomyOutput(continuousPath);
            var resumed = await ReadEconomyOutput(resumedPath);
            Assert.IsTrue(continuous.ReplayVerified);
            var receipt = continuous.CommandReceipts.Single(r => r.Kind == "construction.commission_vehicle_work");
            Assert.AreEqual("rejected", receipt.Decision);
            Assert.AreEqual("insufficient_goods", receipt.Problems.Single().Code);
            Assert.IsNull(receipt.QuotedTotal);
            Assert.AreEqual(JsonSerializer.Serialize(receipt, Recipe.Json),
                JsonSerializer.Serialize(resumed.CommandReceipts.Single(), Recipe.Json));
            Assert.AreEqual(1, continuous.RejectedCommands);
            var before = continuous.Samples.Single(s => s.Position == new SimulationPosition(4000, 0));
            var after = continuous.Samples.Single(s => s.Position == new SimulationPosition(4000, 1));
            foreach (var train in before.Trains)
            {
                var cargo = before.Inventories.Single(i => i.Id == train.Inventory);
                Assert.IsTrue(cargo.Capacity > 0);
                Assert.AreEqual(cargo.Capacity, cargo.Used);
                Assert.IsTrue(cargo.Goods.All(g => g.Good.Definition is "core:staple-food" or "core:structural-metal"));
            }
            Assert.AreEqual(continuous.Samples[0].Trains.Length, before.Trains.Length);
            var paid = before.Trains.Sum(t => t.RecentReports.Sum(r => r.Totals.PurchasePayments)
                + (t.CurrentRun?.PurchasePayments ?? 0));
            Assert.IsTrue(paid > 0);
            Assert.AreEqual(continuous.Samples[0].Wallet - paid, before.Wallet);
            Assert.AreEqual(JsonSerializer.Serialize(before with { Position = after.Position }, Recipe.Json),
                JsonSerializer.Serialize(after, Recipe.Json));
            Assert.IsTrue(after.Settlements.All(s => s.CommissionJobs.IsEmpty));
            CollectionAssert.AreEqual(continuous.Checkpoints.Where(c => c.Position.CompletedSteps >= 4000).ToArray(), resumed.Checkpoints.ToArray());
            Assert.AreEqual(JsonSerializer.Serialize(continuous.Samples.Where(s => s.Position.CompletedSteps >= 4000), Recipe.Json),
                JsonSerializer.Serialize(resumed.Samples, Recipe.Json));
        }
        catch
        {
            TestContext.WriteLine($"CLI 委托失败证据：{directory}");
            throw;
        }
        Directory.Delete(directory, recursive: true);
    }
}
