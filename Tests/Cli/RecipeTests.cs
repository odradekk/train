using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.Cli.Tests;

// 配方读取要打开配方及其 base 的真实文件，所以归入集成层，但不启动子进程。
[TestClass]
[TestCategory("integration")]
public sealed class RecipeTests
{
    private static string Repository
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
            return directory?.FullName ?? throw new AssertFailedException("找不到仓库。");
        }
    }

    private static string BasicThrough => Path.Combine(Repository, "examples", "basic-through", "recipe.json");

    [TestMethod]
    public void Standard_world_long_recipe_reads_with_its_base()
    {
        var standard = Recipe.Read(Path.Combine(Repository, "examples", "standard-world", "recipe.json"));
        var recipe = Recipe.Read(Path.Combine(Repository, "examples", "standard-world-long", "recipe.json"));
        Assert.AreEqual(standard.Seed, recipe.Seed);
        Assert.AreEqual(standard.Save, recipe.Save);
        CollectionAssert.AreEqual(standard.Commands.Select(command => (command.At, command.Kind)).ToArray(),
            recipe.Commands.Take(standard.Commands.Length).Select(command => (command.At, command.Kind)).ToArray());
        Assert.AreEqual(standard.Commands.Length + 1, recipe.Commands.Length);
        Assert.AreEqual("train.start", recipe.Commands[^1].Kind);
        Assert.AreEqual(standard.Expectations.Length, recipe.Expectations.Length);
        Assert.AreEqual(standard.CheckpointSteps[0], recipe.CheckpointSteps[0]);
        // 负载达到后至少推进 6 模拟小时，负载起点必须是检查点。
        Assert.IsNotNull(recipe.Load);
        Assert.IsTrue(recipe.Target.CompletedSteps >= recipe.Load.From + 6 * 3600 * 60);
        CollectionAssert.Contains(recipe.CheckpointSteps.ToArray(), recipe.Load.From);
    }

    [TestMethod]
    public void Base_recipe_appends_commands_after_its_target()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "train-recipe-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var basic = Recipe.Read(BasicThrough);
            var merged = Recipe.Read(Write(directory, "merged", """
                {"commands":[{"at":{"completed_steps":200,"sequence":0},"kind":"train.start","train":1}],
                 "target":{"completed_steps":300,"sequence":0},"checkpoint_steps":[250]}
                """));
            Assert.AreEqual(basic.Commands.Length + 1, merged.Commands.Length);
            CollectionAssert.AreEqual(basic.CheckpointSteps.Add(250).ToArray(), merged.CheckpointSteps.ToArray());

            foreach (var (name, body, message) in new[]
            {
                ("seed", """{"seed":41,"target":{"completed_steps":300,"sequence":0}}""", "不能覆盖 base 的 seed"),
                ("early", """{"commands":[{"at":{"completed_steps":100,"sequence":0},"kind":"train.start","train":1}],"target":{"completed_steps":300,"sequence":0}}""",
                    "base 目标 200 步之后"),
                ("load", """{"target":{"completed_steps":300,"sequence":0},"load":{"from":0,"targets":[{"key":"unknown","minimum":1}]}}""",
                    "load.targets 每项须有已知 key")
            })
            {
                var error = Assert.ThrowsExactly<InvalidDataException>(() => Recipe.Read(Write(directory, name, body)));
                StringAssert.Contains(error.Message, message);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string Write(string directory, string name, string body)
    {
        var recipe = JsonNode.Parse(body)!.AsObject();
        recipe["format_version"] = 1;
        recipe["base"] = BasicThrough;
        var path = Path.Combine(directory, name + ".recipe.json");
        File.WriteAllText(path, recipe.ToJsonString());
        return path;
    }
}
