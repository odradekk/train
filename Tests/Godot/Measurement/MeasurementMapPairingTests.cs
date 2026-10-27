using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Measurement;
using static Train.Godot.Tests.Measurement.MeasurementAssessmentTests;

namespace Train.Godot.Tests.Measurement;

[TestClass]
[TestCategory("integration")]
public sealed class MeasurementMapPairingTests
{
    [TestMethod]
    public void EachNodeUsesTheLatestNaturalMapBeforeItsClick()
    {
        var directory = Path.Combine(Path.GetTempPath(), "train-map-pairing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Write(directory, "scene-frames.jsonl",
            [
                Frame(0, 9000, 900, 900), Frame(3.9, 9000, 900, 900),
                Frame(4, 90, 10, 40), Frame(4.5, 110, 30, 60), Frame(5, 100, 20, 50)
            ]);
            Write(directory, "scene-operations.jsonl",
            [
                Open(6, Memory(12, 1000, 120, 250)),
                Map(20, 900, 500, 700),
                Open(21, Memory(27, 1100, 600, 800)),
                Map(21, 0, 0, 0),
                Map(28, 0, 0, 0),
                Map(19, 100, 20, 50)
            ]);
            Write(directory, "scene-collections.jsonl",
            [
                new { cycle = 0, phase = "initial-map", at = 5.5, managed_live_bytes = 100 },
                new { cycle = 1, phase = "retired-map", at = 19.5, managed_live_bytes = 250 },
                new { cycle = 2, phase = "final-map", at = 30.0, managed_live_bytes = 300 }
            ]);
            using var report = JsonDocument.Parse(MeasurementReport.Serialize("scene-memory", new Dictionary<string, object?>(),
                Timing(), evidence: MeasurementEvidence.Read("scene-memory", directory)));

            // 首次地图最后一秒 P50 为 (100,20,50)，首次节点增量为 (900,300)。
            // 第二次应配 at=20：增量为 (200,200)；at=21/28 不在点击前，文件末尾 at=19 更早。
            Assert.AreEqual(900d, Item(report, "node_private_delta").GetProperty("measured").GetDouble());
            Assert.AreEqual(300d, Item(report, "node_render_delta").GetProperty("measured").GetDouble());
            Assert.AreEqual(200d, Item(report, "retired_heap_delta").GetProperty("measured").GetDouble());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static object Open(double click, object steady) => new
    {
        kind = "open", name = "memory-node", target = "Node { Id = 2 }", click,
        observation_ended = click + 6, click_to_operable_seconds = .2,
        operable_to_ready_seconds = 1, sample_valid = true, steady
    };

    private static object Map(double at, long privateBytes, long buffer, long texture) =>
        new { kind = "natural-map", cycle = 1, at, memory = Memory(at, privateBytes, buffer, texture) };

    private static object Memory(double at, long privateBytes, long buffer, long texture) => new
    {
        at, samples = 3, private_bytes = privateBytes, working_set_bytes = privateBytes + 100,
        buffer_bytes = buffer, texture_bytes = texture, video_bytes = buffer + texture
    };

    private static object Frame(double at, long privateBytes, long buffer, long texture) => new
    {
        at, phase = "initial-map", private_bytes = privateBytes, working_set_bytes = privateBytes + 100,
        buffer_bytes = buffer, texture_bytes = texture, video_bytes = buffer + texture
    };

    private static void Write(string directory, string name, object[] records) =>
        File.WriteAllLines(Path.Combine(directory, name), records.Select(record => JsonSerializer.Serialize(record)));
}
