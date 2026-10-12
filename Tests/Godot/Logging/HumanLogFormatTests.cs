
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Logging;

namespace Train.Godot.Tests.Logging;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:logging")]
public sealed class HumanLogFormatTests
{
    [TestMethod]
    public void Display_names_are_fixed_width_and_independent_of_jsonl_names()
    {
        Assert.AreEqual("debug", LogLevelText.ToSerialized(LogLevel.Debug));
        Assert.AreEqual("info", LogLevelText.ToSerialized(LogLevel.Info));
        Assert.AreEqual("warning", LogLevelText.ToSerialized(LogLevel.Warning));
        Assert.AreEqual("error", LogLevelText.ToSerialized(LogLevel.Error));

        StringAssert.Contains(Format(LogLevel.Debug, "detail"), "[DEBUG]");
        StringAssert.Contains(Format(LogLevel.Info, "ready"), "[INFO ]");
        StringAssert.Contains(Format(LogLevel.Warning, "caution"), "[WARN ]");
        StringAssert.Contains(Format(LogLevel.Error, "boom"), "[ERROR]");
        Assert.IsFalse(Format(LogLevel.Warning, "caution").Contains("warning", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Timestamp_is_local_clock_time_with_milliseconds_and_no_date()
    {
        var timestamp = new DateTimeOffset(2026, 9, 3, 14, 31, 4, 123, TimeSpan.Zero);
        var line = Format(LogLevel.Info, "Ready.", timestamp: timestamp);
        var local = timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

        StringAssert.Contains(line, $"[{local}]");
        StringAssert.Contains(line, "[content]");
        StringAssert.Contains(line, "[ready]");
        Assert.IsFalse(line.Contains("2026", StringComparison.Ordinal));
        Assert.IsFalse(line.Contains("T14:31:04", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Attributes_follow_the_message_in_insertion_order()
    {
        var attributes = new Dictionary<string, object?>
        {
            ["count"] = 4,
            ["name"] = "oak",
            ["flags"] = new[] { true, false },
            ["nested"] = new Dictionary<string, object?> { ["units"] = 2 }
        };
        var line = Format(LogLevel.Info, "Loaded.", attributes: attributes);

        StringAssert.Contains(line, "Loaded.");
        var countAt = line.IndexOf("count=4", StringComparison.Ordinal);
        var nameAt = line.IndexOf("name=oak", StringComparison.Ordinal);
        var flagsAt = line.IndexOf("flags=[", StringComparison.Ordinal);
        var nestedAt = line.IndexOf("nested=", StringComparison.Ordinal);
        Assert.IsTrue(countAt > line.IndexOf("Loaded.", StringComparison.Ordinal));
        Assert.IsTrue(nameAt > countAt);
        Assert.IsTrue(flagsAt > nameAt);
        Assert.IsTrue(nestedAt > flagsAt);
        StringAssert.Contains(line, "flags=[True, False]");
        StringAssert.Contains(line, "\"units\":2");
    }

    [TestMethod]
    public void Empty_attributes_do_not_append_and_messages_truncate_at_200()
    {
        var shortLine = Format(LogLevel.Info, "Ready.");
        Assert.IsTrue(shortLine.EndsWith("Ready.", StringComparison.Ordinal));
        Assert.IsFalse(shortLine.Contains('=', StringComparison.Ordinal));

        var message = new string('a', 201);
        var line = Format(LogLevel.Debug, message, attributes: new Dictionary<string, object?> { ["kept"] = 1 });
        Assert.IsFalse(line.Contains(message, StringComparison.Ordinal));
        StringAssert.Contains(line, new string('a', 200));
        StringAssert.Contains(line, "kept=1");
        Assert.IsTrue(line.IndexOf("kept=1", StringComparison.Ordinal) > line.IndexOf(new string('a', 200), StringComparison.Ordinal));
    }

    [TestMethod]
    public void Console_coloring_dims_meta_and_tints_level_and_message()
    {
        var error = FormatConsole(LogLevel.Error, "failed [id]");
        StringAssert.Contains(error, "#ff6b68");
        StringAssert.Contains(error, "#7f7f7f");
        StringAssert.Contains(error, "[lb]ERROR]");
        StringAssert.Contains(error, "[lb]id]");
        Assert.IsFalse(error.Contains("[ERROR]", StringComparison.Ordinal));

        var warning = FormatConsole(LogLevel.Warning, "careful");
        StringAssert.Contains(warning, "#e5c07b");

        var debug = FormatConsole(LogLevel.Debug, "trace");
        StringAssert.Contains(debug, "#7f7f7f");

        var info = FormatConsole(LogLevel.Info, "ready");
        Assert.IsFalse(info.Contains("#ff6b68", StringComparison.Ordinal));
        Assert.IsFalse(info.Contains("#e5c07b", StringComparison.Ordinal));
        StringAssert.Contains(info, "#7f7f7f");
        StringAssert.Contains(info, "ready");
    }

    [TestMethod]
    public void Command_echo_uses_the_cyan_message_color()
    {
        var line = LogLevelText.FormatConsole(CreateEvent(
            LogLevel.Info,
            "help extra",
            category: "console",
            eventId: "command_echo"));
        StringAssert.Contains(line, "#56b6c2");
        StringAssert.Contains(line, "help extra");
        StringAssert.Contains(line, "#7f7f7f");
    }

    private static string Format(
        LogLevel level,
        string message,
        DateTimeOffset? timestamp = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        string category = "content",
        string eventId = "ready") =>
        LogLevelText.FormatBounded(CreateEvent(level, message, timestamp, category, eventId, attributes));

    private static string FormatConsole(
        LogLevel level,
        string message,
        string category = "content",
        string eventId = "ready") =>
        LogLevelText.FormatConsole(CreateEvent(level, message, category: category, eventId: eventId));

    private static LogEvent CreateEvent(
        LogLevel level,
        string message,
        DateTimeOffset? timestamp = null,
        string category = "content",
        string eventId = "ready",
        IReadOnlyDictionary<string, object?>? attributes = null) =>
        new(
            timestamp ?? new DateTimeOffset(2026, 9, 3, 14, 31, 4, 123, TimeSpan.Zero),
            Guid.Empty,
            1,
            level,
            category,
            eventId,
            message,
            attributes ?? new Dictionary<string, object?>(),
            exception: null);
}
