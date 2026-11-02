using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using static Train.Godot.Tests.Measurement.MeasurementAssessmentTests;

namespace Train.Godot.Tests.Measurement;

[TestClass]
[TestCategory("integration")]
public sealed class NodeMemoryBudgetTests
{
    [TestMethod]
    public void ANodeTwiceTheReferenceGetsTwoGiBAndRecordsBothCounts() =>
        WithReport([Open(6, 1610612736, 2 * MeasurementAssessment.ReferenceNodeVoxelCells)], report =>
        {
            var item = Item(report, "node_private_delta");
            Assert.AreEqual("passed", item.GetProperty("status").GetString());
            Assert.AreEqual(2147483648d, item.GetProperty("threshold").GetDouble());
            Assert.AreEqual(1610612736d, item.GetProperty("measured").GetDouble());
            Assert.AreEqual(0d, item.GetProperty("excess").GetDouble());
            Assert.AreEqual(2 * MeasurementAssessment.ReferenceNodeVoxelCells, item.GetProperty("node_voxel_cells").GetInt64());
            Assert.AreEqual(MeasurementAssessment.ReferenceNodeVoxelCells, item.GetProperty("reference_node_voxel_cells").GetInt64());
        });

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void EmptySmallerAndReferenceNodesKeepTheOneGiBMinimum(int halves) =>
        WithReport([Open(6, 1073741824, MeasurementAssessment.ReferenceNodeVoxelCells * halves / 2)], report =>
        {
            var item = Item(report, "node_private_delta");
            Assert.AreEqual(1073741824d, item.GetProperty("threshold").GetDouble());
            Assert.AreEqual("passed", item.GetProperty("status").GetString());
        });

    [TestMethod]
    public void TheVoxelRatioIsNotRoundedToAnInteger() => WithReport(
        [Open(6, 1200000000, MeasurementAssessment.ReferenceNodeVoxelCells * 8 / 7)], report =>
        {
            // 当前基准能被 7 整除；8/7 GiB 的手算值，不截断比例。
            Assert.AreEqual(1227133513.142857d, Item(report, "node_private_delta").GetProperty("threshold").GetDouble(), .000001);
        });

    [TestMethod]
    [DataRow(-1, "passed", 0)]
    [DataRow(0, "passed", 0)]
    [DataRow(1, "failed", 1)]
    public void ScaledBudgetIncludesEqualityAndReportsTheExcess(int offset, string status, int excess) =>
        WithReport([Open(6, 2147483648d + offset, 2 * MeasurementAssessment.ReferenceNodeVoxelCells)], report =>
        {
            var item = Item(report, "node_private_delta");
            Assert.AreEqual(status, item.GetProperty("status").GetString());
            Assert.AreEqual((double)excess, item.GetProperty("excess").GetDouble());
        });

    [TestMethod]
    public void TheWorstBudgetExcessWinsOverTheLargestPrivateDelta() => WithReport(
        [Open(6, 3221225472d, 4 * MeasurementAssessment.ReferenceNodeVoxelCells),
            Open(20, 1073741834d, MeasurementAssessment.ReferenceNodeVoxelCells)], report =>
        {
            var item = Item(report, "node_private_delta");
            Assert.AreEqual("failed", item.GetProperty("status").GetString());
            Assert.AreEqual(1073741834d, item.GetProperty("measured").GetDouble());
            Assert.AreEqual(1073741824d, item.GetProperty("threshold").GetDouble());
            Assert.AreEqual(10d, item.GetProperty("excess").GetDouble());
            Assert.AreEqual(MeasurementAssessment.ReferenceNodeVoxelCells, item.GetProperty("node_voxel_cells").GetInt64());
        });

    [TestMethod]
    public void MissingSizeOnAnyValidOpeningPreventsPassingFromOtherOpenings() => WithReport(
        [Open(6, 100, MeasurementAssessment.ReferenceNodeVoxelCells), Open(20, 200)], report =>
        Assert.AreEqual("unknown", Item(report, "node_private_delta").GetProperty("status").GetString()));

    [TestMethod]
    public void AnUnpairedOpeningPreventsPassingFromOtherPairs() => WithReport(
        [Open(1, 100, MeasurementAssessment.ReferenceNodeVoxelCells),
            Open(6, 200, MeasurementAssessment.ReferenceNodeVoxelCells)], report =>
        Assert.AreEqual("unknown", Item(report, "node_private_delta").GetProperty("status").GetString()));

    [TestMethod]
    public void AnEmptySetOfOpeningsCannotPass() => WithReport([], report =>
        Assert.AreEqual("unknown", Item(report, "node_private_delta").GetProperty("status").GetString()));

    [TestMethod]
    public void AFailedRunClearsTheBudgetExcess() => WithReport(
        [Open(6, 2147483648d, MeasurementAssessment.ReferenceNodeVoxelCells)], report =>
        {
            var item = Item(report, "node_private_delta");
            Assert.AreEqual("unknown", item.GetProperty("status").GetString());
            Assert.AreEqual(JsonValueKind.Null, item.GetProperty("excess").ValueKind);
        }, failure: "window lost");

    [TestMethod]
    [DataRow(-1L)]
    [DataRow(1.5)]
    [DataRow("100")]
    [DataRow(true)]
    public void InvalidSizeIsUnknown(object cells) => WithReport([Open(6, 100, cells)], report =>
        Assert.AreEqual("unknown", Item(report, "node_private_delta").GetProperty("status").GetString()));

    [TestMethod]
    public void LargerNodesDoNotRelaxRenderOrWholeGameLimits() => WithReport(
        [Open(6, 5368709120d, 10 * MeasurementAssessment.ReferenceNodeVoxelCells, 536870913)], report =>
        {
            Assert.AreEqual("passed", Item(report, "node_private_delta").GetProperty("status").GetString());
            Assert.AreEqual("failed", Item(report, "node_render_delta").GetProperty("status").GetString());
            Assert.AreEqual(536870912d, Item(report, "node_render_delta").GetProperty("threshold").GetDouble());
            Assert.AreEqual("failed", Item(report, "game_private").GetProperty("status").GetString());
            Assert.AreEqual(4294967296d, Item(report, "game_private").GetProperty("threshold").GetDouble());
        });

    [TestMethod]
    public void MissingNodeSizeCannotPassThePrivateBudget() => WithReport([Open(6, 100)], report =>
    {
        var item = Item(report, "node_private_delta");
        Assert.AreEqual("unknown", item.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, item.GetProperty("excess").ValueKind);
        Assert.IsFalse(string.IsNullOrWhiteSpace(item.GetProperty("reason").GetString()));
    });

    private static object Open(double click, double delta, object? cells = null, double render = 0) => new
    {
        kind = "open", name = "memory-node", target = "Node { Id = 2 }", click,
        observation_ended = click + 6, sample_valid = true, node_voxel_cells = cells,
        steady = new { samples = 3, private_bytes = 100 + delta, buffer_bytes = render, texture_bytes = 0 }
    };

    private static void WithReport(object[] openings, Action<JsonDocument> check, string? failure = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "train-node-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllLines(Path.Combine(directory, "scene-operations.jsonl"),
                openings.Select(value => JsonSerializer.Serialize(value)));
            File.WriteAllLines(Path.Combine(directory, "scene-frames.jsonl"), new[] { 4, 5 }.Select(at =>
                JsonSerializer.Serialize(new { at, phase = "initial-map", private_bytes = 100, buffer_bytes = 0, texture_bytes = 0 })));
            using var report = JsonDocument.Parse(MeasurementReport.Serialize("scene-memory", new Dictionary<string, object?>(),
                Timing(), failure, evidence: MeasurementEvidence.Read("scene-memory", directory)));
            check(report);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
