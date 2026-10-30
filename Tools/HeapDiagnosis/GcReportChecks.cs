using System.Text.Json;
using Graphs;

namespace Train.HeapDiagnosis;

internal static class GcReportChecks
{
    internal static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var temporary = Environment.GetEnvironmentVariable("HEAP_RUN_DIRECTORY") ?? Path.GetTempPath();
        var scratch = Path.Combine(temporary, "issue873-gc-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var before = Path.Combine(scratch, "fixture-before.gcdump");
        var after = Path.Combine(scratch, "fixture-after.gcdump");
        try
        {
            Fixture(before, false, false);
            Fixture(after, true, true);
            GcReport.WritePair(before, after, 80, 160, directory);
            using var one = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "snapshot-1", "gc-heap.json")));
            using var three = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "snapshot-3", "gc-heap.json")));
            using var diff = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "gc-difference.json")));
            var first = one.RootElement;
            var last = three.RootElement;
            Require(first.GetProperty("Summary").GetProperty("current").ValueKind == JsonValueKind.Null, "unknown_current");
            Require(first.GetProperty("Summary").GetProperty("unknown").GetInt32() == 2, "unknown_views");
            Require(last.GetProperty("Summary").GetProperty("current").GetInt32() == 1, "current_inferred");
            Require(last.GetProperty("Summary").GetProperty("non_current").GetInt32() == 1, "non_current");
            Require(last.GetProperty("Types").GetProperty("Example.Payload").GetProperty("Bytes").GetInt64() == 128, "aggregate_bytes");
            Require(last.GetProperty("Summary").GetProperty("object_bytes_minus_gc_live_bytes").GetInt64() == 40, "live_delta");
            var top = diff.RootElement.GetProperty("top_30")[0];
            Require(top.GetProperty("type").GetString() == "Example.Payload" && top.GetProperty("bytes_delta").GetInt64() == 96, "growth_order");
            Require(top.GetProperty("after_first_root_path").GetProperty("RootReachable").GetBoolean(), "root_path");
            foreach (var view in first.GetProperty("ContentViews").EnumerateArray())
                foreach (var step in view.GetProperty("Path").GetProperty("Steps").EnumerateArray())
                    Require(step.GetProperty("Field").ValueKind == JsonValueKind.Null, "field_unavailable");
            var text = File.ReadAllText(Path.Combine(directory, "snapshot-3", "gc-heap.json"));
            Require(!text.Contains("private-root-label") && !text.Contains("secret-module") && !text.Contains("0x"), "anonymous_output");
            Require(last.GetProperty("Completeness").GetProperty("events_lost").ValueKind == JsonValueKind.Null, "loss_unknown");
            Console.WriteLine("gc deterministic checks passed");
        }
        finally
        {
            File.Delete(before);
            File.Delete(after);
            Directory.Delete(scratch);
        }
    }

    private static void Require(bool value, string label)
    {
        if (!value) throw new InvalidDataException(label);
    }

    private static void Fixture(string file, bool host, bool large)
    {
        var graph = new MemoryGraph(16);
        var synthetic = graph.CreateNode();
        var roots = graph.CreateNode();
        var a = graph.GetNodeIndex(100);
        var b = graph.GetNodeIndex(200);
        var coordinator = graph.GetNodeIndex(300);
        var payload = graph.GetNodeIndex(400);
        var syntheticType = graph.CreateType("[private-root-label C:/secret-module]", "secret-module", 0);
        var rootType = graph.CreateType("[strong Handles]", "secret-module", 0);
        var viewType = graph.CreateType("Train.HeapDiagnosis.ContentView", "secret-module", 24);
        var hostType = graph.CreateType("Train.HeapDiagnosis.HostCoordinator", "secret-module", 24);
        var payloadType = graph.CreateType(large ? "Example.Payload (Bytes > 1K)" : "Example.Payload", "secret-module", -1);
        graph.SetNode(synthetic, syntheticType, 0, Children(roots));
        graph.SetNode(roots, rootType, 0, Children(coordinator, b));
        graph.SetNode(coordinator, hostType, 24, host ? Children(a, payload) : Children(payload));
        graph.SetNode(a, viewType, 24, Children());
        graph.SetNode(b, viewType, 24, Children(a));
        graph.SetNode(payload, payloadType, large ? 128 : 32, Children());
        graph.RootIndex = synthetic;
        graph.AllowReading();
        GCHeapDump.WriteMemoryGraph(graph, file);
    }

    private static System.Collections.Generic.GrowableArray<NodeIndex> Children(params NodeIndex[] indices)
    {
        var children = new System.Collections.Generic.GrowableArray<NodeIndex>(indices.Length);
        foreach (var index in indices) children.Add(index);
        return children;
    }
}
