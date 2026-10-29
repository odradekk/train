using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using static Train.Godot.Tests.Measurement.MeasurementAssessmentTests;

namespace Train.Godot.Tests.Measurement;

[TestClass]
[TestCategory("integration")]
public sealed class ScenePreparationTests
{
    [TestMethod]
    public void PreparationIncludesBothKindsOfWorkAndPassesAtFourMilliseconds()
    {
        InDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "scene-resource-frames.jsonl"),
                """
                {"excluded_space_change":false,"process_frame":1,"at":10,"frame_seconds":1,"after_operable":false,"preparation_seconds":0.2,"candidate_preparation_seconds":0,"commit_seconds":0.1}
                {"excluded_space_change":false,"process_frame":2,"at":11,"frame_seconds":1,"after_operable":true,"preparation_seconds":0.003,"candidate_preparation_seconds":0.001,"commit_seconds":0.2}
                """);
            using var report = ReadReport(directory, 2);
            var item = Item(report, "scene_preparation_maximum");
            Assert.AreEqual(.004, item.GetProperty("measured").GetDouble(), 1e-12);
            Assert.AreEqual(.004, item.GetProperty("threshold").GetDouble());
            Assert.AreEqual("passed", item.GetProperty("status").GetString());
        });
    }

    [TestMethod]
    public void AMissingWindowMarkerCannotBeHiddenBehindEarlierValidFrames() => InDirectory(directory =>
    {
        File.WriteAllText(Path.Combine(directory, "scene-resource-frames.jsonl"),
            """
            {"excluded_space_change":false,"process_frame":1,"at":10,"frame_seconds":1,"after_operable":true,"preparation_seconds":0.003,"candidate_preparation_seconds":0,"commit_seconds":0}
            {"excluded_space_change":false,"process_frame":2,"at":11,"frame_seconds":1,"preparation_seconds":0.2,"candidate_preparation_seconds":0,"commit_seconds":0}
            """);
        using var report = ReadReport(directory, 2);
        Assert.AreEqual("unknown", Item(report, "scene_preparation_maximum").GetProperty("status").GetString());
    });

    [TestMethod]
    [DataRow("scene-node", .0039, "passed")]
    [DataRow("scene-node-first", .0041, "failed")]
    [DataRow("scene-edge", .004, "passed")]
    [DataRow("scene-follow", .0041, "failed")]
    [DataRow("scene-route-1x", .0041, "failed")]
    public void PreparationMaximumUsesAllPostOperableFrames(string scenario, double seconds, string expected) =>
        InDirectory(directory =>
        {
            WriteFrames(directory, [Frame(1, 10, false, .5), Frame(2, 11, true, seconds), Frame(3, 12, true, 0)]);
            using var report = ReadReport(directory, 3, scenario);
            var item = Item(report, "scene_preparation_maximum");
            Assert.AreEqual(seconds, item.GetProperty("measured").GetDouble());
            Assert.AreEqual(expected, item.GetProperty("status").GetString());
            if (expected == "passed") Assert.AreEqual(0, item.GetProperty("excess").GetDouble());
        });

    [TestMethod]
    [DataRow("missing")]
    [DataRow("malformed-tail")]
    [DataRow("complete-tail-missing")]
    [DataRow("no-post-operable-frame")]
    public void MissingOrTruncatedObservationCannotPassFromItsValidPrefix(string problem) => InDirectory(directory =>
    {
        if (problem != "missing")
            WriteFrames(directory, [Frame(1, 10, problem != "no-post-operable-frame", .003)]);
        if (problem == "malformed-tail")
            File.AppendAllText(Path.Combine(directory, "scene-resource-frames.jsonl"), "{\"at\":");
        using var report = ReadReport(directory, problem == "complete-tail-missing" ? 2 : 1);
        Assert.AreEqual("unknown", Item(report, "scene_preparation_maximum").GetProperty("status").GetString());
    });

    [TestMethod]
    public void FailedRunsAndIncompleteRoutesCannotPassRetainedPreparationSamples() => InDirectory(directory =>
    {
        WriteFrames(directory, [Frame(1, 10, true, .003)]);
        using var failed = ReadReport(directory, 1, failure: "interrupted");
        using var route = ReadReport(directory, 1, "scene-route-1x", routeCompleted: false);
        Assert.AreEqual("unknown", Item(failed, "scene_preparation_maximum").GetProperty("status").GetString());
        Assert.AreEqual("unknown", Item(route, "scene_preparation_maximum").GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, Item(failed, "scene_preparation_maximum").GetProperty("excess").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, Item(route, "scene_preparation_maximum").GetProperty("excess").ValueKind);
    });

    [TestMethod]
    [DataRow("null", "0")]
    [DataRow("-0.001", "0")]
    [DataRow("0", "-0.001")]
    [DataRow("1e308", "1e308")]
    public void InvalidPreparationDurationsAreUnknown(string active, string candidate) => InDirectory(directory =>
    {
        File.WriteAllText(Path.Combine(directory, "scene-resource-frames.jsonl"),
            $$"""{"excluded_space_change":false,"after_operable":true,"preparation_seconds":{{active}},"candidate_preparation_seconds":{{candidate}}}""");
        using var report = ReadReport(directory, 1);
        Assert.AreEqual("unknown", Item(report, "scene_preparation_maximum").GetProperty("status").GetString());
    });

    [TestMethod]
    public void TheReportStatesHowFarPreparationExceedsItsBudget() => InDirectory(directory =>
    {
        WriteFrames(directory, [Frame(1, 10, true, .006)]);
        using var report = ReadReport(directory, 1);
        Assert.AreEqual(.002, Item(report, "scene_preparation_maximum").GetProperty("excess").GetDouble(), 1e-12);
    });

    [TestMethod]
    public void NewSpaceFramesAreReportedSeparatelyWithoutRemovingTheirNeighbours() => InDirectory(directory =>
    {
        WriteFrames(directory,
        [
            Frame(1, 10, true, .003),
            new { process_frame = 2, at = 11, frame_seconds = 1, after_operable = false,
                excluded_space_change = true, preparation_seconds = .2, candidate_preparation_seconds = .1, commit_seconds = .05 },
            Frame(3, 12, true, .005)
        ]);
        using var report = ReadReport(directory, 3, "scene-route-1x");
        var item = Item(report, "scene_preparation_maximum");
        Assert.AreEqual(.005, item.GetProperty("measured").GetDouble());
        Assert.AreEqual("failed", item.GetProperty("status").GetString());
        Assert.AreEqual(1, item.GetProperty("excluded_frames").GetInt32());
    });

    [TestMethod]
    [DataRow(null)]
    [DataRow("null")]
    [DataRow("true")]
    public void MissingOrConflictingNewSpaceMarkersCannotPass(string? marker) => InDirectory(directory =>
    {
        var field = marker is null ? "" : $"\"excluded_space_change\":{marker},";
        File.WriteAllText(Path.Combine(directory, "scene-resource-frames.jsonl"),
            $$"""{ {{field}} "after_operable":true,"preparation_seconds":0.003,"candidate_preparation_seconds":0}""");
        using var report = ReadReport(directory, 1);
        var item = Item(report, "scene_preparation_maximum");
        Assert.AreEqual("unknown", item.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, item.GetProperty("excluded_frames").ValueKind);
    });

    [TestMethod]
    [DataRow("scene-follow-8x")]
    [DataRow("scene-memory")]
    [DataRow("scene-soak")]
    [DataRow("scene-map")]
    [DataRow("scene-editor")]
    public void UnrelatedScenariosDoNotAssessScenePreparation(string scenario) => InDirectory(directory =>
    {
        WriteFrames(directory, [Frame(1, 10, true, .005)]);
        using var report = ReadReport(directory, 1, scenario);
        Assert.IsFalse(report.RootElement.GetProperty("assessment").GetProperty("items").EnumerateArray()
            .Any(item => item.GetProperty("metric").GetString() == "scene_preparation_maximum"));
    });

    private static object Frame(int processFrame, double at, bool afterOperable, double seconds) =>
        new { process_frame = processFrame, at, frame_seconds = 1, after_operable = afterOperable, excluded_space_change = false,
            preparation_seconds = seconds, candidate_preparation_seconds = 0, commit_seconds = 0 };

    private static void WriteFrames(string directory, IEnumerable<object> frames) =>
        File.WriteAllLines(Path.Combine(directory, "scene-resource-frames.jsonl"), frames.Select(frame => JsonSerializer.Serialize(frame)));

    private static JsonDocument ReadReport(string directory, int frames, string scenario = "scene-node",
        bool routeCompleted = true, string? failure = null) => JsonDocument.Parse(MeasurementReport.Serialize(
        scenario, new Dictionary<string, object?>
        {
            ["scene_budget"] = new
            {
                resource_work = new { frame_count = frames },
                route = new { route_completed = routeCompleted }
            }
        }, Timing(.01), failure, MeasurementEvidence.Read(scenario, directory)));

    private static void InDirectory(Action<string> run)
    {
        var directory = Path.Combine(Path.GetTempPath(), "train-preparation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { run(directory); }
        finally { Directory.Delete(directory, true); }
    }
}
