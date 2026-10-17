using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;

namespace Train.Cli.Tests;

public sealed partial class CliProcessTests
{
    [TestMethod]
    public async Task Full_starting_fleet_commissions_a_wagon_then_rejects_missing_materials_atomically()
    {
        var directory = CreateDirectory();
        try
        {
            var recipe = Path.Combine(Repository, "examples", "commission-branches", "recipe.json");
            var continuousPath = Path.Combine(directory, "continuous.json");
            await Run(directory, "strict-replay", "replay", Repository, recipe, continuousPath);

            var continuous = await ReadEconomyOutput(continuousPath);
            Assert.IsTrue(continuous.ReplayVerified);
            var receipts = continuous.CommandReceipts.Where(r => r.Kind == "construction.commission_vehicle_work").ToArray();
            Assert.AreEqual(2, receipts.Length);
            Assert.AreEqual("accepted", receipts[0].Decision);
            Assert.IsTrue(receipts[0].QuotedTotal > 0);
            var receipt = receipts[1];
            Assert.AreEqual("rejected", receipt.Decision);
            Assert.AreEqual("insufficient_goods", receipt.Problems.Single().Code);
            CollectionAssert.AreEqual(new[] { "voxel:core:bogie", "voxel:core:storage-equipment", "voxel:core:structural-metal" },
                receipt.Problems.Single().RelatedIdentities.ToArray());
            Assert.IsNull(receipt.QuotedTotal);
            Assert.AreEqual(1, continuous.RejectedCommands);
            var before = continuous.Samples.Single(s => s.Position == new SimulationPosition(10000, 0));
            var accepted = continuous.Samples.Single(s => s.Position == new SimulationPosition(10000, 1));
            var after = continuous.Samples.Single(s => s.Position == new SimulationPosition(10000, 2));
            foreach (var train in before.Trains)
            {
                var cargo = before.Inventories.Single(i => i.Id == train.Inventory);
                Assert.IsTrue(cargo.Capacity > 0);
                Assert.AreEqual(cargo.Capacity, cargo.Used);
                Assert.IsTrue(cargo.Goods.All(g => g.Good.Definition is "core:staple-food" or "core:structural-metal" or "core:fuel"));
            }
            Assert.AreEqual(continuous.Samples[0].Trains.Length, before.Trains.Length);
            var paid = before.Trains.Sum(t => t.RecentReports.Sum(r => r.Totals.PurchasePayments)
                + (t.CurrentRun?.PurchasePayments ?? 0));
            Assert.IsTrue(paid > 0);
            Assert.AreEqual(continuous.Samples[0].Wallet - paid, before.Wallet);
            Assert.AreEqual(before.Wallet - receipts[0].QuotedTotal, accepted.Wallet);
            Assert.IsTrue(accepted.UnassignedServicePayments > before.UnassignedServicePayments);
            Assert.AreEqual(1, accepted.Settlements.Single(s => s.Id == 6).CommissionJobs.Length);
            var job = accepted.CommissionJobs.Single();
            Assert.AreEqual("Working", job.State);
            Assert.AreEqual(0d, job.ProgressWork);
            Assert.AreEqual(0d, job.Progress);
            Assert.AreEqual(JsonSerializer.Serialize(accepted with { Position = after.Position }, Recipe.Json),
                JsonSerializer.Serialize(after, Recipe.Json));
        }
        catch
        {
            TestContext.WriteLine($"CLI 委托失败证据：{directory}");
            throw;
        }
        Directory.Delete(directory, recursive: true);
    }
}
