using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;

namespace Train.Cli.Tests;

[TestClass]
[TestCategory("integration")]
public sealed partial class CliProcessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Separate_processes_run_the_same_recipe_with_identical_checkpoints()
    {
        var directory = CreateDirectory();
        try
        {
            var first = Path.Combine(directory, "first.json");
            var second = Path.Combine(directory, "second.json");
            var one = await Run(directory, "first", "run", Repository, RecipePath, first);
            var two = await Run(directory, "second", "run", Repository, RecipePath, second);
            using var firstOutput = JsonDocument.Parse(await File.ReadAllTextAsync(first));
            using var secondOutput = JsonDocument.Parse(await File.ReadAllTextAsync(second));
            Assert.AreEqual(one, firstOutput.RootElement.GetProperty("process_id").GetInt32());
            Assert.AreEqual(two, secondOutput.RootElement.GetProperty("process_id").GetInt32());
            Assert.AreNotEqual(one, two);
            Assert.AreNotEqual(Environment.ProcessId, one);
            Assert.AreNotEqual(Environment.ProcessId, two);
            Assert.IsTrue(firstOutput.RootElement.GetProperty("checkpoints").GetArrayLength() > 2);
            var expected = JsonSerializer.Deserialize<CoreCheckpoint[]>(
                firstOutput.RootElement.GetProperty("checkpoints").GetRawText(), Recipe.Json)!;
            var actual = JsonSerializer.Deserialize<CoreCheckpoint[]>(
                secondOutput.RootElement.GetProperty("checkpoints").GetRawText(), Recipe.Json)!;
            CollectionAssert.AreEqual(expected, actual);
            Assert.AreEqual(3, firstOutput.RootElement.GetProperty("accepted_commands").GetInt32());
            Assert.AreEqual(0, firstOutput.RootElement.GetProperty("rejected_commands").GetInt32());
            await Run(directory, "compare", "compare", first, second);

            var modified = JsonNode.Parse(await File.ReadAllTextAsync(first))!;
            var checkpoint = modified["checkpoints"]![1]!;
            var state = JsonNode.Parse(checkpoint["canonical_text"]!.GetValue<string>())!;
            state["next_command_number"] = state["next_command_number"]!.GetValue<ulong>() + 1;
            var text = state.ToJsonString();
            checkpoint["canonical_text"] = text;
            checkpoint["sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            var changed = Path.Combine(directory, "changed.json");
            await File.WriteAllTextAsync(changed, modified.ToJsonString());
            await Run(directory, "changed-compare", 1, "compare", changed, second);
            StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(directory, "changed-compare.stderr.log")),
                "/next_command_number");

            modified["input_identity"]!["value"] = "different-core-input";
            await File.WriteAllTextAsync(changed, modified.ToJsonString());
            await Run(directory, "changed-input-compare", 1, "compare", changed, second);
            StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(directory, "changed-input-compare.stderr.log")),
                "配方或内容已变，需要重新制作。");
        }
        catch
        {
            TestContext.WriteLine($"CLI 失败证据：{directory}");
            throw;
        }
        Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public async Task Current_recording_strictly_replays_accepted_and_rejected_commands()
    {
        var directory = CreateDirectory();
        try
        {
            var recipe = JsonNode.Parse(await File.ReadAllTextAsync(RecipePath))!;
            var commands = recipe["commands"]!.AsArray();
            commands[0]!["at"]!["sequence"] = 1;
            commands[1]!["at"]!["sequence"] = 2;
            commands.Insert(0, JsonNode.Parse("""
                {"at":{"completed_steps":0,"sequence":0},"kind":"train.start","train":1}
                """));
            var modified = Path.Combine(directory, "with-rejection.recipe.json");
            await File.WriteAllTextAsync(modified, recipe.ToJsonString());
            var output = Path.Combine(directory, "replayed.json");
            await Run(directory, "replay", "replay", Repository, modified, output);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            Assert.IsTrue(result.RootElement.GetProperty("replay_verified").GetBoolean());
            Assert.AreEqual(3, result.RootElement.GetProperty("accepted_commands").GetInt32());
            Assert.AreEqual(1, result.RootElement.GetProperty("rejected_commands").GetInt32());
            var checkpoints = result.RootElement.GetProperty("checkpoints");
            Assert.AreEqual(200UL, checkpoints[checkpoints.GetArrayLength() - 1]
                .GetProperty("position").GetProperty("completed_steps").GetUInt64());
        }
        catch
        {
            TestContext.WriteLine($"CLI 失败证据：{directory}");
            throw;
        }
        Directory.Delete(directory, recursive: true);
    }

    private Task<int> Run(string directory, string name, params string[] arguments) => Run(directory, name, 0, arguments);

    private async Task<int> Run(string directory, string name, int expectedExitCode, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = directory
        };
        start.ArgumentList.Add(typeof(CliRunner).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new AssertFailedException("无法启动 CLI。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(TestContext.CancellationToken); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".stdout.log"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".stderr.log"), await stderr);
        }
        Assert.AreEqual(expectedExitCode, process.ExitCode, $"CLI 退出码不符：{directory}/{name}.stderr.log\n{await stderr}");
        return process.Id;
    }

    private static string Repository
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
            return directory?.FullName ?? throw new AssertFailedException("找不到仓库。");
        }
    }
    private static string RecipePath => Path.Combine(Repository, "examples", "basic-through", "recipe.json");
    private static string CreateDirectory() => Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "train-cli-" + Guid.NewGuid().ToString("N"))).FullName;
}
