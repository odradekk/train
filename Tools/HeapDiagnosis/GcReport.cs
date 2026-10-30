using System.Globalization;
using System.Text.Json;
using Graphs;

namespace Train.HeapDiagnosis;

// Only the diagnostic tool owns this format. Graph IDs and source labels never leave this class.
internal sealed class GcReport
{
    public string Attribution => "Objects counted once; tool size-bucket suffixes merged into CLR type names. Root categories partition BFS first-root reachability, not retained sizes. Paths are first BFS type paths, capped at 128 final steps. Unreachable objects remain counted. Non-current does not mean previous session.";
    public Dictionary<string, GcTally> Types { get; } = [];
    public Dictionary<string, GcRoot> RootCategories { get; } = [];
    public List<GcView> ContentViews { get; } = [];
    public object Summary { get; private set; } = new();
    public object Completeness { get; private set; } = new();
    private readonly Dictionary<string, GcPath> _firstPaths = [];

    internal static long Live(string text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value >= 0 ? value : throw new ArgumentException("invalid_live_bytes");

    internal static void WriteOne(string file, long live, string directory, bool strict = false)
    {
        var report = Read(file, live, strict);
        Write(directory, "gc-heap.json", report);
        Console.WriteLine("gc heap analysis complete");
    }

    internal static void WritePair(string before, string after, long beforeLive, long afterLive, string directory)
    {
        // Read returns only anonymous aggregates: the first graph is no longer retained when reading the second.
        var first = Read(before, beforeLive, false);
        Write(Path.Combine(directory, "snapshot-1"), "gc-heap.json", first);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var last = Read(after, afterLive, false);
        Write(Path.Combine(directory, "snapshot-3"), "gc-heap.json", last);
        var growth = first.Types.Keys.Union(last.Types.Keys).Select(type =>
        {
            first.Types.TryGetValue(type, out var a);
            last.Types.TryGetValue(type, out var b);
            last._firstPaths.TryGetValue(type, out var path);
            return new { type, before_count = a?.Count ?? 0, after_count = b?.Count ?? 0,
                count_delta = (b?.Count ?? 0) - (a?.Count ?? 0), before_bytes = a?.Bytes ?? 0,
                after_bytes = b?.Bytes ?? 0, bytes_delta = (b?.Bytes ?? 0) - (a?.Bytes ?? 0), after_first_root_path = path };
        }).OrderByDescending(x => x.bytes_delta).ThenByDescending(x => x.count_delta)
            .ThenBy(x => x.type, StringComparer.Ordinal).Take(30).ToArray();
        Write(directory, "gc-difference.json", new { before_summary = first.Summary, after_summary = last.Summary,
            ordering = "bytes_delta_desc_then_count_delta_desc_then_type_ordinal; includes zero or negative deltas if fewer than 30 growing types",
            top_30 = growth });
        Console.WriteLine("gc pair analysis complete");
    }

    private static void Write(string directory, string file, object value)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, file), JsonSerializer.Serialize(value, JsonOptions.Value));
    }

    private static GcReport Read(string file, long live, bool strict)
    {
        var dump = new GCHeapDump(file);
        var graph = dump.MemoryGraph;
        var report = new GcReport();
        var count = checked((int)graph.NodeIndexLimit);
        if (count == 0 || graph.RootIndex == NodeIndex.Invalid) throw new InvalidDataException("missing_graph_root");
        var node = graph.AllocNodeStorage();
        var typeStorage = graph.AllocTypeNodeStorage();
        var names = new string[(int)graph.NodeTypeIndexLimit];
        for (var i = 0; i < names.Length; i++) names[i] = graph.GetType((NodeTypeIndex)i, typeStorage).Name;
        var parents = new int[count];
        Array.Fill(parents, -2);
        var categories = new string?[count];
        var queue = new Queue<int>();
        var root = (int)graph.RootIndex;
        parents[root] = -1;
        categories[root] = "other";
        queue.Enqueue(root);
        var invalidEdges = 0L;
        var undefined = 0L;
        var undefinedObjects = 0L;
        var hosts = new List<int>();
        var views = new List<int>();
        while (queue.TryDequeue(out var index))
        {
            graph.GetNode((NodeIndex)index, node);
            if (!node.Defined) continue;
            var category = graph.GetAddress((NodeIndex)index) == 0 ? RootCategory(names[(int)node.TypeIndex]) : null;
            category ??= categories[index];
            categories[index] = category;
            for (var child = node.GetFirstChildIndex(); child != NodeIndex.Invalid; child = node.GetNextChildIndex())
            {
                var next = (int)child;
                if (next < 0 || next >= count) { invalidEdges++; continue; }
                if (parents[next] != -2) continue;
                parents[next] = index;
                categories[next] = category;
                queue.Enqueue(next);
            }
        }
        string Type(int index)
        {
            graph.GetNode((NodeIndex)index, node);
            return SafeType(names[(int)node.TypeIndex]);
        }
        bool IsObject(int index) => graph.GetAddress((NodeIndex)index) != 0;
        for (var i = 0; i < count; i++)
        {
            graph.GetNode((NodeIndex)i, node);
            if (!node.Defined)
            {
                undefined++;
                if (IsObject(i)) undefinedObjects++;
                continue;
            }
            if (!IsObject(i)) continue;
            var name = SafeType(names[(int)node.TypeIndex]);
            Add(report.Types, name, node.Size);
            var category = parents[i] == -2 ? "unreachable" : categories[i] ?? "other";
            if (!report.RootCategories.TryGetValue(category, out var tally)) report.RootCategories[category] = tally = new();
            tally.FirstPathObjects++;
            tally.FirstPathBytes += node.Size;
            if (parents[i] >= 0 && !IsObject(parents[i])) { tally.DirectRootTargets++; tally.DirectRootTargetBytes += node.Size; }
            if (parents[i] != -2 && !report._firstPaths.ContainsKey(name)) report._firstPaths[name] = PathTo(i, -1, -1);
            if (name is "Train.App.HostCoordinator" or "Train.HeapDiagnosis.HostCoordinator" && parents[i] != -2) hosts.Add(i);
            if (name is "Train.Content.Loading.ContentView" or "Train.HeapDiagnosis.ContentView") views.Add(i);
        }
        var current = -1;
        var directCandidates = new HashSet<int>();
        if (hosts.Count == 1)
        {
            graph.GetNode((NodeIndex)hosts[0], node);
            for (var child = node.GetFirstChildIndex(); child != NodeIndex.Invalid; child = node.GetNextChildIndex())
                if (views.Contains((int)child)) directCandidates.Add((int)child);
            if (directCandidates.Count == 1) current = directCandidates.Single();
        }
        foreach (var view in views)
        {
            graph.GetNode((NodeIndex)view, node);
            report.ContentViews.Add(new(Type(view), node.Size, current < 0 ? "unknown" : view == current ?
                "current_inferred_from_unique_host_direct_edge" : "non-current", PathTo(view, hosts.Count == 1 ? hosts[0] : -1, current)));
        }
        var total = report.Types.Values.Sum(x => x.Count);
        var bytes = report.Types.Values.Sum(x => x.Bytes);
        var reachable = report.RootCategories.Where(x => x.Key != "unreachable").Sum(x => x.Value.FirstPathObjects);
        var sampled = dump.CountMultipliersByType != null || dump.AverageCountMultiplier != 1 || dump.AverageSizeMultiplier != 1;
        report.Completeness = new
        {
            status = "unknown", reason = "gcdump_has_no_verified_event_loss_or_complete_heap_certificate",
            sampled, average_count_multiplier = dump.AverageCountMultiplier, average_size_multiplier = dump.AverageSizeMultiplier,
            per_type_count_multipliers_present = dump.CountMultipliersByType != null,
            statistics_scope = sampled ? "sampled_raw_counts_not_full_heap" : "raw_graph_counts_not_completeness_certificate",
            events_lost = (long?)null, collection_log_present = !string.IsNullOrEmpty(dump.CollectionLog),
            collection_log_emitted = false, undefined_nodes = undefined, invalid_edges = invalidEdges,
            undefined_nonzero_address_nodes = undefinedObjects,
            undefined_zero_address_nodes = undefined - undefinedObjects,
            corrupted_object = dump.DotNetHeapInfo?.CorruptedObject,
            undumped_segment_region = dump.DotNetHeapInfo?.UndumpedSegementRegion,
            instance_field_metadata_available = false,
            current_inference_basis = "source-verified sole direct IContentView field Train.App.HostCoordinator._content; requires one reachable host and one direct ContentView target"
        };
        report.Summary = new
        {
            object_count = total, object_bytes = bytes, graph_total_size = graph.TotalSize,
            graph_nodes_including_synthetic = count, gc_live_bytes = live,
            object_bytes_minus_gc_live_bytes = bytes - live, graph_size_minus_gc_live_bytes = graph.TotalSize - live,
            estimated_heap_bytes = double.IsFinite(graph.TotalSize * (double)dump.AverageSizeMultiplier) && dump.AverageSizeMultiplier > 0
                ? graph.TotalSize * (double)dump.AverageSizeMultiplier : (double?)null,
            estimate_basis = "GCHeapDump.AverageSizeMultiplier: sampledSize * multiplier = originalSize; estimate only",
            statistics_scope = sampled ? "sampled_raw_counts_not_full_heap" : "raw_graph_counts_not_completeness_certificate",
            gc_live_comparison = "caller_supplied_nearby_observation_not_same_gc_certificate",
            reachable_objects = reachable, unreachable_objects = total - reachable,
            content_view_count = views.Count, current = current < 0 ? (int?)null : 1,
            non_current = current < 0 ? (int?)null : views.Count - 1, unknown = current < 0 ? views.Count : 0,
            reachable_host_count = hosts.Count, unique_direct_view_targets = directCandidates.Count,
            instance_field_metadata_available = false, completeness = report.Completeness, root_categories = report.RootCategories
        };
        if (strict)
        {
            var chains = report.Types.Where(x => x.Key.StartsWith("System.Collections.Generic.LinkedListNode", StringComparison.Ordinal)).Sum(x => x.Value.Count);
            if (views.Count != 2 || hosts.Count != 1 || current < 0 || chains != ContentView.ChainLength * 2 ||
                !report.Types.TryGetValue("System.Byte[]", out var b) || b.Bytes < 2_200_000 ||
                !report.Types.TryGetValue("System.Char[]", out var c) || c.Bytes < 2_400_000 ||
                report.ContentViews.Any(x => x.Path.Steps.Count == 0) || sampled || undefinedObjects != 0 || invalidEdges != 0)
                throw new InvalidDataException("gc_preflight_contract_failed");
        }
        return report;

        GcPath PathTo(int index, int host, int view)
        {
            if (parents[index] == -2) return new([], false, false);
            var steps = new List<GcStep>();
            var cursor = index;
            while (cursor >= 0 && steps.Count < 128)
            {
                var parent = parents[cursor];
                var real = IsObject(cursor);
                steps.Add(new(real ? Type(cursor) : "synthetic_root", real ? null : categories[cursor],
                    cursor == view && parent == host ? "_content" : null,
                    cursor == view && parent == host ? "source-verified-inference" : null));
                cursor = parent;
            }
            steps.Reverse();
            return new(steps, cursor >= 0, true);
        }
    }

    private static void Add(Dictionary<string, GcTally> tallies, string type, int size)
    {
        if (!tallies.TryGetValue(type, out var tally)) tallies[type] = tally = new();
        tally.Count++;
        tally.Bytes += size;
    }

    private static string SafeType(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains("0x", StringComparison.OrdinalIgnoreCase) ||
            value.IndexOfAny(['/', '\\', ':', '\n', '\r', '\t', '"']) >= 0 || value.StartsWith('[')) return "unknown_type";
        // DotNetHeapDumpGraphReader.GetObjectSizeSuffix adds these labels; they are not CLR types.
        var suffix = value.LastIndexOf(" (Bytes > ", StringComparison.Ordinal);
        if (suffix >= 0 && value[suffix..] is " (Bytes > 1K)" or " (Bytes > 10K)" or " (Bytes > 100K)" or
            " (Bytes > 1M)" or " (Bytes > 10M)" or " (Bytes > 100M)") return value[..suffix];
        return value;
    }

    private static string? RootCategory(string name) => name switch
    {
        "[local vars]" => "stack", "[static vars]" => "static", "[strong Handles]" => "strong_handle",
        "[pinning Handles]" => "pinned_handle", "[finalizer Handles]" => "finalizer_handle",
        "[Dependent Handles]" => "dependent_handle", "[COM/WinRT Objects]" => "interop",
        "[other Handles]" => "other_handle", _ => null
    };
}

internal sealed class GcTally { public long Count { get; set; } public long Bytes { get; set; } }
internal sealed class GcRoot
{
    public long FirstPathObjects { get; set; }
    public long FirstPathBytes { get; set; }
    public long DirectRootTargets { get; set; }
    public long DirectRootTargetBytes { get; set; }
}
internal sealed record GcStep(string Type, string? RootCategory, string? Field, string? FieldSource);
internal sealed record GcPath(List<GcStep> Steps, bool Truncated, bool RootReachable);
internal sealed record GcView(string Type, long Bytes, string Category, GcPath Path);
