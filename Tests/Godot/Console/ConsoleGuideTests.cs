using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Console;

namespace Train.Godot.Tests.Console;

[TestClass]
public sealed class ConsoleGuideTests
{
    [TestMethod]
    [DataRow("  si", 4, "sim", 2, 2)]
    [DataRow("sim pa", 6, "pause", 4, 2)]
    [DataRow("LOGLEVEL W", 10, "warning", 9, 1)]
    [DataRow("help tr", 7, "train", 5, 2)]
    [DataRow("sim pa later", 6, "pause", 4, 2)]
    [DataRow("train for", 9, "form", 6, 3)]
    [DataRow("lighting ni", 11, "night", 9, 2)]
    [DataRow("dev op", 6, "open", 4, 2)]
    public void Completion_replaces_only_the_token_at_the_caret(string input, int caret, string expected, int start, int length)
    {
        using var harness = DispatchHarness.CreateMemory();
        var guide = harness.Dispatcher.DescribeInput(input, caret);
        Assert.AreEqual(expected, guide.Suggestions.Single().Text);
        Assert.IsTrue(guide.Suggestions[0].Description.Length > 0);
        Assert.AreEqual(start, guide.Start);
        Assert.AreEqual(length, guide.Length);
    }

    [TestMethod]
    public void Deeper_arguments_show_usage_without_suggestions()
    {
        using var harness = DispatchHarness.CreateMemory();
        var guide = harness.Dispatcher.DescribeInput("sim speed ", 10);
        Assert.AreEqual(0, guide.Suggestions.Count);
        StringAssert.Contains(guide.Usage!, "speed 1|2|4|8");
    }

    [TestMethod]
    public void Player_registration_is_the_source_for_completion_help_and_execution()
    {
        using var player = DispatchHarness.CreateMemory(developmentBuild: false);
        var names = player.Dispatcher.DescribeInput("", 0).Suggestions.Select(item => item.Text).ToArray();
        CollectionAssert.AreEquivalent(new[] { "help", "clear", "quit", "loglevel" }, names);
        CollectionAssert.AreEquivalent(names, player.Dispatcher.DescribeInput("help ", 5).Suggestions.Select(item => item.Text).ToArray());
        foreach (var name in new[] { "reload", "sim", "train", "lighting", "dev" })
        {
            player.Dispatcher.Execute("help " + name);
            Assert.AreEqual(ConsoleCommandDispatcher.UnknownEventId, player.ConsoleEvents.Last().EventId);
            player.Dispatcher.Execute(name);
            Assert.AreEqual(ConsoleCommandDispatcher.UnknownEventId, player.ConsoleEvents.Last().EventId);
        }
        player.Dispatcher.Execute("help");
        var help = player.ConsoleEvents.Last().Message;
        foreach (var name in names) StringAssert.Contains(help, name + "：");
        Assert.IsFalse(help.Contains("reload"));
    }

    [TestMethod]
    public void Help_for_one_command_explains_its_usage()
    {
        using var harness = DispatchHarness.CreateMemory();
        harness.Dispatcher.Execute("help loglevel");
        var help = harness.ConsoleEvents.Single(item => item.EventId == ConsoleCommandDispatcher.HelpEventId);
        StringAssert.Contains(help.Message, "用法：loglevel");
        StringAssert.Contains(help.Message, "磁盘日志");
    }

    [TestMethod]
    public void Unknown_commands_offer_the_nearest_registered_command()
    {
        using var harness = DispatchHarness.CreateMemory();
        harness.Dispatcher.Execute("trian");
        var warning = harness.ConsoleEvents.Last();
        Assert.AreEqual(ConsoleCommandDispatcher.UnknownEventId, warning.EventId);
        StringAssert.Contains(warning.Message, "最接近的命令是 train");
        StringAssert.Contains(warning.Message, "输入 help");
        using var player = DispatchHarness.CreateMemory(developmentBuild: false);
        player.Dispatcher.Execute("hlpe");
        StringAssert.Contains(player.ConsoleEvents.Last().Message, "最接近的命令是 help");
    }
}
