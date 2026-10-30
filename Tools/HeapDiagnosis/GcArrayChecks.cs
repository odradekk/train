using System.Diagnostics;
using System.Text.Json;
using Graphs;

namespace Train.HeapDiagnosis;

internal static class GcArrayChecks
{
    internal static void Run(string directory)
    {
        StaticFields();
        Directory.CreateDirectory(directory);
        var scratch = Scratch();
        try
        {
            var before = Path.Combine(scratch, "before.gcdump");
            var after = Path.Combine(scratch, "after.gcdump");
            Fixture(before, false);
            Fixture(after, true);
            GcReport.WritePair(before, after, 0, 0, directory);
            using var first = Read(directory, "snapshot-1", "gc-heap.json");
            using var last = Read(directory, "snapshot-3", "gc-heap.json");
            using var delta = Read(directory, "gc-difference.json");
            var a = last.RootElement.GetProperty("Arrays");
            var bytes = a.GetProperty("LargestByteArrays");
            var chars = a.GetProperty("LargestCharArrays");
            Check(bytes.GetArrayLength() == 50 && chars.GetArrayLength() == 20, "largest_limits");
            Check(bytes[49].GetProperty("Bytes").GetInt64() == 1012 && chars[19].GetProperty("Bytes").GetInt64() == 2013, "largest_boundary_selection");
            Check(bytes[0].GetProperty("Bytes").GetInt64() == 8192, "largest_growth_not_first_static");
            Check(first.RootElement.GetProperty("Arrays").GetProperty("LargestByteArrays")[0].GetProperty("Bytes").GetInt64() == 4096, "before_largest");
            Check(first.RootElement.GetProperty("Arrays").GetProperty("LargestByteArrays")[0].GetProperty("BeforeSameSizeCount").GetInt64() == 1, "first_same_size_count");
            var refs = bytes[0].GetProperty("DirectReferrers");
            Check(Row(refs, "ParentType", "Example.Holder").GetProperty("Count").GetInt64() == 2, "distinct_parents_same_type_duplicate_edge");
            Check(Row(refs, "ParentType", "Example.OtherHolder").GetProperty("Count").GetInt64() == 1, "different_parent_type");
            Check(bytes[0].GetProperty("BeforeSameSizeCount").GetInt64() == 0, "new_size_count");
            var same = bytes.EnumerateArray().Single(x => x.GetProperty("Bytes").GetInt64() == 3000);
            Check(same.GetProperty("BeforeSameSizeCount").GetInt64() == 2 && same.GetProperty("SameSizeCount").GetInt64() == 1, "same_size_coarse_count");
            var hist = a.GetProperty("Histogram");
            foreach (var (lower, upper, count, total) in new (long, long, long, long)[] { (0,1,1,0), (1,2,1,1), (2,4,2,5), (4,8,2,11), (8,16,2,23), (16,32,2,47) })
            {
                var row = hist.EnumerateArray().Single(x => x.GetProperty("Type").GetString() == "System.Byte[]" && x.GetProperty("LowerBytes").GetInt64() == lower);
                Check(row.GetProperty("UpperExclusiveBytes").GetInt64() == upper && row.GetProperty("Count").GetInt64() == count && row.GetProperty("Bytes").GetInt64() == total, "power_of_two_bucket_" + lower);
            }
            var stack = a.GetProperty("StackPaths").EnumerateArray().Single(x => x.GetProperty("RootCategory").GetString() == "stack" && x.GetProperty("ArrayType").GetString() == "System.Byte[]");
            Check(stack.GetProperty("Count").GetInt64() == 1 && stack.GetProperty("Bytes").GetInt64() == 8192 && stack.GetProperty("ViaPoolCount").GetInt64() == 1 && stack.GetProperty("ViaPoolBytes").GetInt64() == 8192, "stack_longer_pool_path_cycle");
            var pool = Row(a.GetProperty("PoolTypes"), "PoolType", "Example.ArrayPoolPretender");
            Check(pool.GetProperty("ObjectCount").GetInt64() == 1 && pool.GetProperty("ByteArrayCount").GetInt64() == 1 && pool.GetProperty("ByteArrayBytes").GetInt64() == 8192, "pool_name_only_direct_edges");
            var unreachable = chars.EnumerateArray().Single(x => x.GetProperty("Bytes").GetInt64() == 9000);
            Check(!unreachable.GetProperty("FirstRootPath").GetProperty("RootReachable").GetBoolean(), "unrooted_array");
            var tls = chars.EnumerateArray().Single(x => x.GetProperty("Bytes").GetInt64() == 8000);
            Check(tls.GetProperty("FirstRootCategory").GetString() == "thread_static", "thread_static_category");
            var field = tls.GetProperty("DirectReferrers").EnumerateArray().Single();
            Check(field.GetProperty("DeclaringType").GetString() == "Example.Tls<System.Byte>" && field.GetProperty("Field").GetString() == "Buffer", "safe_static_field");
            Check(tls.GetProperty("FirstRootPath").GetProperty("Steps").EnumerateArray().Any(x => x.GetProperty("DeclaringType").GetString() == "Example.Tls<System.Byte>" && x.GetProperty("Field").GetString() == "Buffer"), "path_safe_static_field");
            var unsafeRef = chars.EnumerateArray().Single(x => x.GetProperty("Bytes").GetInt64() == 7000).GetProperty("DirectReferrers").EnumerateArray().Single();
            Check(unsafeRef.GetProperty("DeclaringType").ValueKind == JsonValueKind.Null && unsafeRef.GetProperty("Field").ValueKind == JsonValueKind.Null, "unsafe_field_null");
            var parent = a.GetProperty("ParentTypes").EnumerateArray().Single(x => x.GetProperty("ArrayType").GetString() == "System.Byte[]" && x.GetProperty("ParentType").GetString() == "Example.Holder");
            Check(parent.GetProperty("Count").GetInt64() == 1 && parent.GetProperty("Bytes").GetInt64() == 8192, "parent_type_array_once");
            var json = File.ReadAllText(Path.Combine(directory, "snapshot-3", "gc-heap.json"));
            Check(!json.Contains("secret") && !json.Contains("0x"), "unsafe_labels_not_emitted");
            var growth = delta.RootElement.GetProperty("arrays").GetProperty("Histogram").EnumerateArray().Single(x => x.GetProperty("Type").GetString() == "System.Byte[]" && x.GetProperty("LowerBytes").GetInt64() == 8192);
            Check(growth.GetProperty("before_count").GetInt64() == 0 && growth.GetProperty("after_count").GetInt64() == 1 && growth.GetProperty("bytes_delta").GetInt64() == 8192, "histogram_empty_bucket_aligned");
            var differences = delta.RootElement.GetProperty("arrays");
            Check(Row(differences.GetProperty("ParentTypes"), "ParentType", "Example.Holder").GetProperty("bytes_delta").GetInt64() == 4096, "parent_delta");
            Check(Row(differences.GetProperty("PoolTypes"), "PoolType", "Example.ArrayPoolPretender").GetProperty("byte_array_bytes_delta").GetInt64() == 4096, "pool_delta");
            var stackDelta = differences.GetProperty("StackPaths").EnumerateArray().Single(x => x.GetProperty("RootCategory").GetString() == "stack" && x.GetProperty("ArrayType").GetString() == "System.Byte[]");
            Check(stackDelta.GetProperty("via_pool_bytes_delta").GetInt64() == 4096 && stackDelta.GetProperty("count_delta").GetInt64() == 0, "stack_delta");
            GcReportChecks.Run(Path.Combine(directory, "legacy"));
            DeepFixture(Path.Combine(scratch, "deep.gcdump"));
            GcReport.WriteOne(Path.Combine(scratch, "deep.gcdump"), 0, Path.Combine(directory, "deep"));
            using var deep = Read(directory, "deep", "gc-heap.json");
            var deepPath = deep.RootElement.GetProperty("Arrays").GetProperty("LargestByteArrays")[0].GetProperty("FirstRootPath");
            Check(deepPath.GetProperty("Truncated").GetBoolean() && deepPath.GetProperty("RootReachable").GetBoolean() && deepPath.GetProperty("Steps").GetArrayLength() == 128, "deep_20000_chain_bounded_path");
            Check(deep.RootElement.GetProperty("Arrays").GetProperty("LargestByteArrays")[0].GetProperty("BeforeSameSizeCount").ValueKind == JsonValueKind.Null,
                "single_report_match_unknown");
            Console.WriteLine("gc array deterministic checks passed");
        }
        finally { Directory.Delete(scratch, true); }
    }

    internal static void RunScale(string directory, int count)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var arrayCount = Math.Min(count, 20_000);
        Directory.CreateDirectory(directory);
        var scratch = Scratch();
        var timer = Stopwatch.StartNew();
        try
        {
            var graphCounts = ScaleFixture(Path.Combine(scratch, "scale.gcdump"), count);
            Check(graphCounts.Nodes == count + 3L && graphCounts.Edges == count + 3L, "scale_actual_nodes_edges");
            GcReport.WritePair(Path.Combine(scratch, "scale.gcdump"), Path.Combine(scratch, "scale.gcdump"), 0, 0, directory);
            using var doc = Read(directory, "snapshot-3", "gc-heap.json");
            var summary = doc.RootElement.GetProperty("Summary");
            Check(summary.GetProperty("object_count").GetInt64() == count + 1L && summary.GetProperty("reachable_objects").GetInt64() == count + 1L, "scale_all_objects_reachable");
            Check(summary.GetProperty("object_bytes").GetInt64() == arrayCount * 32L + (count - arrayCount + 1L) * 24L, "scale_all_object_bytes");
            var arrays = doc.RootElement.GetProperty("Arrays");
            Check(arrays.GetProperty("LargestByteArrays")[0].GetProperty("BeforeSameSizeCount").GetInt64() == arrayCount, "pair_report_size_match");
            var histogram = arrays.GetProperty("Histogram").EnumerateArray().Single();
            Check(histogram.GetProperty("Count").GetInt64() == arrayCount && histogram.GetProperty("Bytes").GetInt64() == arrayCount * 32L, "scale_histogram");
            var parents = Row(arrays.GetProperty("ParentTypes"), "ParentType", "Example.ArrayPoolScale");
            Check(parents.GetProperty("Count").GetInt64() == arrayCount && parents.GetProperty("Bytes").GetInt64() == arrayCount * 32L, "scale_incoming_edges");
            var stack = arrays.GetProperty("StackPaths").EnumerateArray().Single(x => x.GetProperty("RootCategory").GetString() == "stack" && x.GetProperty("ArrayType").GetString() == "System.Byte[]");
            Check(stack.GetProperty("Count").GetInt64() == arrayCount && stack.GetProperty("ViaPoolCount").GetInt64() == arrayCount, "scale_stack_paths");
            timer.Stop();
            using var process = Process.GetCurrentProcess();
            File.WriteAllText(Path.Combine(directory, "scale-check.json"), JsonSerializer.Serialize(new { workload_nodes = count, array_nodes = arrayCount, object_nodes = count + 1L, graph_nodes = graphCounts.Nodes, graph_edges = graphCounts.Edges, elapsed_ms = timer.ElapsedMilliseconds, peak_working_set_bytes = process.PeakWorkingSet64, checks = "passed" }, JsonOptions.Value));
            Console.WriteLine("gc array scale checks passed");
        }
        finally { Directory.Delete(scratch, true); }
    }

    private static void Fixture(string file, bool after)
    {
        var graph = new MemoryGraph(256);
        var root = graph.CreateNode();
        var statics = graph.CreateNode();
        var stack = graph.CreateNode();
        var tls = graph.CreateNode();
        var validField = graph.CreateNode();
        var unsafeField = graph.CreateNode();
        ulong address = 100;
        NodeIndex Object(string type, int size, params NodeIndex[] children)
        {
            var index = graph.GetNodeIndex(address++);
            graph.SetNode(index, graph.CreateType(type, "secret-module", -1), size, Children(children));
            return index;
        }
        void Synthetic(NodeIndex index, string label, params NodeIndex[] children) => graph.SetNode(index, graph.CreateType(label, "secret-module", 0), 0, Children(children));
        var small = Object("System.Byte[]", 32);
        var big = Object("System.Byte[]", after ? 8192 : 4096);
        var holder = Object("Example.Holder", 24, big, big);
        var holder2 = Object("Example.Holder", 24, big);
        var other = Object("Example.OtherHolder", 24, big);
        var pool = Object("Example.ArrayPoolPretender", 24, big, big);
        var bridge = Object("Example.Bridge", 24, pool);
        // Cycle preserves a pool path to the same array whose shortest stack path is direct.
        graph.SetNode(pool, graph.CreateType("Example.ArrayPoolPretender", null, 24), 24, Children(big, big, bridge));
        var staticTargets = new List<NodeIndex> { small, holder, holder2, other };
        foreach (var size in new[] { 0, 1, 2, 3, 4, 7, 8, 15, 16, 31 }) staticTargets.Add(Object("System.Byte[]", size));
        for (var i = 0; i < 60; i++) staticTargets.Add(Object("System.Byte[]", 1000 + i));
        staticTargets.Add(Object("System.Byte[]", 3000));
        if (!after) staticTargets.Add(Object("System.Byte[]", 3000));
        for (var i = 0; i < 30; i++) staticTargets.Add(Object("System.Char[]", 2000 + i));
        Object("System.Char[]", 9000);
        var tlsArray = Object("System.Char[]", 8000);
        var unsafeArray = Object("System.Char[]", 7000);
        Synthetic(validField, "[static var Example.Tls<System.Byte>.Buffer]", tlsArray);
        Synthetic(unsafeField, "[static var C:/secret.0x123]", unsafeArray);
        Synthetic(tls, "[thread static vars]", validField, unsafeField);
        Synthetic(statics, "[static vars]", staticTargets.ToArray());
        Synthetic(stack, "[local vars]", big, bridge);
        Synthetic(root, "[secret-root]", statics, stack, tls);
        graph.RootIndex = root;
        graph.AllowReading();
        GCHeapDump.WriteMemoryGraph(graph, file);
    }

    private static (int Nodes, long Edges) ScaleFixture(string file, int count)
    {
        var graph = new MemoryGraph(count + 3);
        var root = graph.CreateNode();
        var stack = graph.CreateNode();
        var pool = graph.GetNodeIndex(1);
        var arrayType = graph.CreateType("System.Byte[]", null, 32);
        var ordinaryType = graph.CreateType("Example.ScaleNode", null, 24);
        var targets = new System.Collections.Generic.GrowableArray<NodeIndex>(count);
        for (var i = 0; i < count; i++)
        {
            var index = graph.CreateNode();
            graph.SetAddress(index, (ulong)i + 2);
            var array = i < Math.Min(count, 20_000);
            graph.SetNode(index, array ? arrayType : ordinaryType, array ? 32 : 24, Children());
            targets.Add(index);
        }
        graph.SetNode(pool, graph.CreateType("Example.ArrayPoolScale", null, 24), 24, targets);
        graph.SetNode(stack, graph.CreateType("[local vars]", null, 0), 0, Children(pool));
        graph.SetNode(root, graph.CreateType("[root]", null, 0), 0, Children(stack, pool));
        graph.RootIndex = root;
        graph.AllowReading();
        long edges = 0;
        var node = graph.AllocNodeStorage();
        for (var i = 0; i < (int)graph.NodeIndexLimit; i++)
        {
            graph.GetNode((NodeIndex)i, node);
            for (var child = node.GetFirstChildIndex(); child != NodeIndex.Invalid; child = node.GetNextChildIndex()) edges++;
        }
        GCHeapDump.WriteMemoryGraph(graph, file);
        return ((int)graph.NodeIndexLimit, edges);
    }

    private static void DeepFixture(string file)
    {
        var graph = new MemoryGraph(20_002);
        var root = graph.CreateNode();
        var next = graph.GetNodeIndex(1);
        graph.SetNode(next, graph.CreateType("System.Byte[]", null, 64), 64, Children());
        var type = graph.CreateType("Example.Chain", null, 24);
        for (var i = 0; i < 20_000; i++)
        {
            var parent = graph.CreateNode();
            graph.SetAddress(parent, (ulong)i + 2);
            graph.SetNode(parent, type, 24, Children(next));
            next = parent;
        }
        graph.SetNode(root, graph.CreateType("[local vars]", null, 0), 0, Children(next));
        graph.RootIndex = root;
        graph.AllowReading();
        GCHeapDump.WriteMemoryGraph(graph, file);
    }

    private static string Scratch()
    {
        var path = Path.Combine(Path.GetTempPath(), "issue873-arrays-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static JsonDocument Read(string directory, params string[] components) => JsonDocument.Parse(File.ReadAllText(Path.Combine(new[] { directory }.Concat(components).ToArray())));
    private static JsonElement Row(JsonElement rows, string key, string value) => rows.EnumerateArray().Single(x => x.GetProperty(key).GetString() == value);
    private static void StaticFields()
    {
        foreach (var type in new[] { "System.Buffers.SharedArrayPool<System.Byte>", "System.Buffers.SharedArrayPool<System.ValueTuple<System.Byte,System.Char[]>>" })
            Check(GcArrays.TryStaticField("[static var " + type + ".t_tlsBuckets]", out var declaring, out var field) && declaring == type && field == "t_tlsBuckets", "generic_static_field");
        foreach (var label in new[] { "[static var C:/secret.Field]", "[static var Example.0x123.Field]", "[static var Example.\nField]", "[static var Example.Pool<System.Byte.Field]", "[static var Example.Pool<System.Byte>>.Field]", "[static var Example.Pool<,>.Field]" })
            Check(!GcArrays.TryStaticField(label, out _, out _), "unsafe_static_field_parser");
        Check(GcArrays.TryStaticField("[static var " + new string('A', 4096) + ".Field]", out _, out _), "static_type_length_boundary");
        Check(!GcArrays.TryStaticField("[static var " + new string('A', 4097) + ".Field]", out _, out _), "static_type_length_limit");
        var nested = string.Concat(Enumerable.Repeat("Pool<", 64)) + "Byte" + new string('>', 64);
        Check(GcArrays.TryStaticField("[static var " + nested + ".Field]", out _, out _), "static_generic_depth_boundary");
        Check(!GcArrays.TryStaticField("[static var Pool<" + nested + ">.Field]", out _, out _), "static_generic_depth_limit");
        Check(!GcArrays.TryStaticField("[static var " + nested[..^1] + ".Field]", out _, out _), "static_nested_generic_unbalanced");
        Check(!GcArrays.TryStaticField("[static var Example.Pool<C:\\private\\secret>.Field]", out _, out _), "static_nested_private_path");
    }
    private static void Check(bool value, string label)
    {
        if (value) return;
        Console.Error.WriteLine("CHECK_FAILED " + label);
        throw new InvalidDataException(label);
    }
    private static System.Collections.Generic.GrowableArray<NodeIndex> Children(params NodeIndex[] indices)
    {
        var children = new System.Collections.Generic.GrowableArray<NodeIndex>(indices.Length);
        foreach (var index in indices) children.Add(index);
        return children;
    }
}
