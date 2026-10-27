using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;

namespace Train.Cli.Tests;

public sealed partial class CliProcessTests
{
    [TestMethod]
    public async Task Count_expectations_gate_make_and_unknown_keys_are_rejected()
    {
        var directory = CreateDirectory();
        try
        {
            var recipe = JsonNode.Parse(await File.ReadAllTextAsync(RecipePath))!.AsObject();
            // 基础贯通的两列起始列车各为 4 辆车，节点 2、5 各有一个聚居地及两个站台。
            recipe["expectations"] = JsonNode.Parse("""
                [{"at":200,"key":"largest_train_vehicles","minimum":4,"maximum":4},
                 {"at":200,"node":2,"key":"nodes","minimum":1,"maximum":1},
                 {"at":200,"node":5,"key":"platforms","minimum":2,"maximum":2},
                 {"at":200,"node":2,"key":"settlements","minimum":1,"maximum":1},
                 {"at":200,"key":"nodes","minimum":2}]
                """);
            await Make("holding", 0);
            recipe["expectations"]![0]!["minimum"] = 5;
            await Make("unreachable", 1);
            StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(directory, "unreachable.stderr.log")), "实际 4");
            recipe["expectations"]![0]!["minimum"] = 4;
            recipe["expectations"]![2]!["minimum"] = 3;
            recipe["expectations"]![3]!["minimum"] = 2;
            await Make("unreachable-node", 1);
            StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(directory, "unreachable-node.stderr.log")),
                "platforms（节点 5）");
            recipe["expectations"]![0]!["key"] = "unknown";
            await Make("unknown", 1);
            StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(directory, "unknown.stderr.log")), "key");

            async Task Make(string name, int exitCode)
            {
                var path = Path.Combine(directory, name + ".recipe.json");
                var save = Path.Combine(directory, name + ".save.json");
                await File.WriteAllTextAsync(path, recipe.ToJsonString());
                await Run(directory, name, exitCode, "make", Repository, path, save);
                Assert.AreEqual(exitCode == 0, File.Exists(save));
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
    public async Task Save_in_one_process_resumes_in_another_with_the_continuous_checkpoint_suffix()
    {
        var directory = CreateDirectory();
        try
        {
            var save = Path.Combine(directory, "prefix.save.json");
            var resumedPath = Path.Combine(directory, "resumed.json");
            var continuousPath = Path.Combine(directory, "continuous.json");
            var savingProcess = await Run(directory, "save", "save", Repository, RecipePath, save, "150");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(save));
            using var recipe = JsonDocument.Parse(await File.ReadAllTextAsync(RecipePath));
            var header = document.RootElement.GetProperty("Header");
            var metadata = recipe.RootElement.GetProperty("save");
            Assert.AreEqual(6, header.GetProperty("SaveFormatVersion").GetInt32());
            Assert.AreEqual(metadata.GetProperty("id").GetGuid(), header.GetProperty("Id").GetGuid());
            Assert.AreEqual(metadata.GetProperty("name").GetString(), header.GetProperty("Name").GetString());
            Assert.AreEqual(metadata.GetProperty("saved_at").GetDateTimeOffset(),
                header.GetProperty("SavedAt").GetDateTimeOffset());
            Assert.AreEqual(150UL, header.GetProperty("Position").GetProperty("CompletedSteps").GetUInt64());
            Assert.AreEqual(0UL, header.GetProperty("Position").GetProperty("Sequence").GetUInt64());
            Assert.IsFalse(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("ContentPayload").GetString()));

            var resumingProcess = await Run(directory, "resume", "resume", Repository, RecipePath, save, resumedPath);
            var continuousProcess = await Run(directory, "continuous", "run", Repository, RecipePath, continuousPath);
            using var resumed = JsonDocument.Parse(await File.ReadAllTextAsync(resumedPath));
            using var continuous = JsonDocument.Parse(await File.ReadAllTextAsync(continuousPath));
            Assert.AreEqual(resumingProcess, resumed.RootElement.GetProperty("process_id").GetInt32());
            Assert.AreEqual(continuousProcess, continuous.RootElement.GetProperty("process_id").GetInt32());
            Assert.IsFalse(new[] { savingProcess, resumingProcess, continuousProcess }.Contains(Environment.ProcessId));
            var actual = JsonSerializer.Deserialize<CoreCheckpoint[]>(
                resumed.RootElement.GetProperty("checkpoints").GetRawText(), Recipe.Json)!;
            var full = JsonSerializer.Deserialize<CoreCheckpoint[]>(
                continuous.RootElement.GetProperty("checkpoints").GetRawText(), Recipe.Json)!;
            var expected = full.SkipWhile(checkpoint => checkpoint.Position != new SimulationPosition(150, 0)).ToArray();
            Assert.IsTrue(expected.Length >= 3);
            CollectionAssert.AreEqual(expected, actual);
            Assert.AreEqual(new SimulationPosition(200, 0), actual[^1].Position);
            Assert.AreEqual(1, resumed.RootElement.GetProperty("accepted_commands").GetInt32());
            Assert.AreEqual(0, resumed.RootElement.GetProperty("rejected_commands").GetInt32());
            await Run(directory, "compare-resumed", "compare", continuousPath, resumedPath);
        }
        catch
        {
            TestContext.WriteLine($"CLI 失败证据：{directory}");
            throw;
        }
        Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task Committed_lease_example_resumes_at_its_final_boundary_without_advancing()
    {
        var directory = CreateDirectory();
        try
        {
            var save = Path.Combine(Repository, "examples", "lease-expiry", "save.json");
            var recipe = Path.Combine(Repository, "examples", "lease-expiry", "recipe.json");
            var output = Path.Combine(directory, "resumed.json");
            using var container = JsonDocument.Parse(await File.ReadAllTextAsync(save));
            var position = container.RootElement.GetProperty("Header").GetProperty("Position");
            await Run(directory, "resume", "resume", Repository, recipe, save, output);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var checkpoints = JsonSerializer.Deserialize<CoreCheckpoint[]>(
                result.RootElement.GetProperty("checkpoints").GetRawText(), Recipe.Json)!;
            Assert.AreEqual(1, checkpoints.Length);
            Assert.AreEqual(new SimulationPosition(position.GetProperty("CompletedSteps").GetUInt64(),
                position.GetProperty("Sequence").GetUInt64()), checkpoints[0].Position);
            Assert.AreEqual(0, result.RootElement.GetProperty("accepted_commands").GetInt32());
            Assert.AreEqual(0, result.RootElement.GetProperty("rejected_commands").GetInt32());
        }
        catch
        {
            TestContext.WriteLine($"CLI 失败证据：{directory}");
            throw;
        }
        Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DataRow("basic-through")]
    [DataRow("storage-tight")]
    [DataRow("commission-branches")]
    [DataRow("retreat")]
    [DataRow("damaged-vehicle-trade")]
    public async Task Committed_example_checks_without_changing_committed_files(string example)
    {
        var directory = CreateDirectory();
        try
        {
            var committed = Path.Combine(Repository, "examples", example, "save.json");
            var recipePath = Path.Combine(Repository, "examples", example, "recipe.json");
            var originalSave = await File.ReadAllBytesAsync(committed);
            var originalRecipe = await File.ReadAllBytesAsync(recipePath);
            await Run(directory, "check", "check", Repository, recipePath, committed);
            if (example == "storage-tight")
            {
                var output = Path.Combine(directory, "capacity.json");
                await Run(directory, "capacity", "resume", Repository, recipePath, committed, output);
                var final = (await ReadEconomyOutput(output)).Samples[^1];
                var train = final.Trains.Single(train => train.Id == 1);
                var cargo = final.Inventories.Single(inventory => inventory.Id == train.Inventory);
                Assert.AreEqual(0L, cargo.Available);
                Assert.AreEqual(cargo.Capacity, cargo.Used);
                Assert.AreEqual(cargo.Capacity, cargo.Goods.Sum(stack => stack.Quantity));
                Assert.AreEqual(2, cargo.Goods.Length);
                var rule = train.CurrentRun!.IncompleteRules.Single(rule => rule.RuleIndex == 1);
                Assert.IsTrue(cargo.Goods.Single(stack => stack.Good == rule.Good).Quantity > 0);
                var problem = rule.IncompleteReasons.Single();
                Assert.AreEqual("insufficient_capacity", problem.Code);
                Assert.AreEqual("Train.Inventory.Capacity", problem.Field);
                var seller = final.Settlements.Single(settlement => settlement.Node == train.Position.Id);
                var stock = final.Inventories.Single(inventory => inventory.Id == seller.Inventory);
                Assert.IsTrue(stock.Goods.Single(stack => stack.Good == rule.Good).Quantity > 0);
                Assert.IsTrue(final.Wallet > 0);
            }
            CollectionAssert.AreEqual(originalSave, await File.ReadAllBytesAsync(committed));
            CollectionAssert.AreEqual(originalRecipe, await File.ReadAllBytesAsync(recipePath));
        }
        catch
        {
            TestContext.WriteLine($"CLI 失败证据：{directory}");
            throw;
        }
        Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DataRow("command")]
    [DataRow("world_seed")]
    [DataRow("next_command_number")]
    public async Task Check_reports_first_state_difference_without_changing_committed_files(string changed)
    {
        var directory = CreateDirectory();
        try
        {
            var committed = Path.Combine(Repository, "examples", "basic-through", "save.json");
            var originalSave = await File.ReadAllBytesAsync(committed);
            var originalRecipe = await File.ReadAllBytesAsync(RecipePath);
            var save = Path.Combine(directory, "save.json");
            var recipePath = Path.Combine(directory, "recipe.json");
            await File.WriteAllBytesAsync(save, originalSave);
            await File.WriteAllBytesAsync(recipePath, originalRecipe);
            if (changed == "command")
            {
                var recipe = JsonNode.Parse(originalRecipe)!;
                var commands = recipe["commands"]!.AsArray();
                commands.RemoveAt(commands.Count - 1);
                await File.WriteAllTextAsync(recipePath, recipe.ToJsonString());
            }
            else
            {
                var container = JsonNode.Parse(originalSave)!;
                var payload = JsonNode.Parse(container["CorePayload"]!.GetValue<string>())!;
                if (changed == "world_seed")
                    payload["world"]!["source"]!["world_seed"] = payload["world"]!["source"]!["world_seed"]!.GetValue<ulong>() + 1;
                else
                    payload["next_command_number"] = payload["next_command_number"]!.GetValue<ulong>() + 1;
                container["CorePayload"] = payload.ToJsonString();
                await File.WriteAllTextAsync(save, container.ToJsonString());
            }
            await Run(directory, "changed-check", 1, "check", Repository, recipePath, save);
            var diagnostic = await File.ReadAllTextAsync(Path.Combine(directory, "changed-check.stderr.log"));
            StringAssert.Contains(diagnostic, "检查点不同：");
            StringAssert.Contains(diagnostic, changed == "world_seed" ? "/world/source/world_seed" : "/next_command_number");
            CollectionAssert.AreEqual(originalSave, await File.ReadAllBytesAsync(committed));
            CollectionAssert.AreEqual(originalRecipe, await File.ReadAllBytesAsync(RecipePath));
        }
        catch
        {
            TestContext.WriteLine($"CLI 失败证据：{directory}");
            throw;
        }
        Directory.Delete(directory, recursive: true);
    }
}
