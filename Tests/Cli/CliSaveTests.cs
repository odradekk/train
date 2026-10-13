using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;

namespace Train.Cli.Tests;

public sealed partial class CliProcessTests
{
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
            Assert.AreEqual(3, new[] { savingProcess, resumingProcess, continuousProcess }.Distinct().Count());
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
    [OSCondition(OperatingSystems.Windows)]
    public async Task Committed_example_checks_without_changing_committed_files()
    {
        var directory = CreateDirectory();
        try
        {
            var committed = Path.Combine(Repository, "examples", "basic-through", "save.json");
            var originalSave = await File.ReadAllBytesAsync(committed);
            var originalRecipe = await File.ReadAllBytesAsync(RecipePath);
            await Run(directory, "check", "check", Repository, RecipePath, committed);
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

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DataRow("seed")]
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
            if (changed == "seed")
            {
                var recipe = JsonNode.Parse(originalRecipe)!;
                recipe["seed"] = recipe["seed"]!.GetValue<ulong>() + 1;
                await File.WriteAllTextAsync(recipePath, recipe.ToJsonString());
            }
            else
            {
                var container = JsonNode.Parse(originalSave)!;
                var payload = JsonNode.Parse(container["CorePayload"]!.GetValue<string>())!;
                payload["next_command_number"] = payload["next_command_number"]!.GetValue<ulong>() + 1;
                container["CorePayload"] = payload.ToJsonString();
                await File.WriteAllTextAsync(save, container.ToJsonString());
            }
            await Run(directory, "changed-check", 1, "check", Repository, recipePath, save);
            var diagnostic = await File.ReadAllTextAsync(Path.Combine(directory, "changed-check.stderr.log"));
            StringAssert.Contains(diagnostic, "检查点不同：");
            StringAssert.Contains(diagnostic, changed == "seed" ? "/world/source/world_seed" : "/next_command_number");
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
