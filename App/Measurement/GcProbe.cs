using System.Diagnostics.Tracing;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Godot;

namespace Train.App.Measurement;

// #721 临时探针：按 10 秒分桶记录 AllocationTick（类别、类型、线程）、GC 次数和每次 GC 后的分代大小，
// 并在阶段切换和强制回收时记录分代读数。只在本地探针分支使用。
internal sealed class GcProbe : EventListener
{
    private readonly object _gate = new();
    private Dictionary<string, long> _bytes = new(StringComparer.Ordinal);
    private Dictionary<string, long> _gcs = new(StringComparer.Ordinal);
    private object? _lastHeapStats;
    private long _maxLoh, _maxPoh;
    private StreamWriter? _writer;
    private double _bucketStarted = -1;
    private string _lastPhase = "";
    private readonly HashSet<string> _phases = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly string[] Kinds = ["small", "large", "pinned"];

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("libc", EntryPoint = "gettid")]
    private static extern int LinuxThreadId();

    // AllocationTick 的 OSThreadId 是系统线程号；Linux 上用 gettid，取不到时记 0。
    private static long MainThreadId()
    {
        try { return OperatingSystem.IsWindows() ? GetCurrentThreadId() : LinuxThreadId(); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return 0; }
    }

    internal void Start(string path)
    {
        _writer = new StreamWriter(path);
        Write(new { kind = "config", main_os_thread = MainThreadId(),
            server = GCSettings.IsServerGC, latency = GCSettings.LatencyMode.ToString(),
            variables = GC.GetConfigurationVariables().ToDictionary(item => item.Key, item => item.Value.ToString()),
            conserve_env = System.Environment.GetEnvironmentVariable("DOTNET_GCConserveMemory") });
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        // GC 关键字 0x1，Verbose 才有 AllocationTick。
        if (System.Environment.GetEnvironmentVariable("TRAIN_774_MODE") == "sample" && source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        if (data.EventName is null || data.Payload is null) return;
        object? Field(string name)
        {
            var index = data.PayloadNames!.IndexOf(name);
            return index < 0 ? null : data.Payload[index];
        }
        if (data.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal))
        {
            var kind = Convert.ToInt32(Field("AllocationKind"));
            var amount = Convert.ToInt64(Field("AllocationAmount64") ?? Field("AllocationAmount"));
            var type = Field("TypeName") as string ?? "?";
            var key = $"{(kind is >= 0 and < 3 ? Kinds[kind] : kind.ToString())}|{type}|{data.OSThreadId}";
            lock (_gate) _bytes[key] = _bytes.GetValueOrDefault(key) + amount;
        }
        else if (data.EventName.StartsWith("GCStart", StringComparison.Ordinal))
        {
            var key = $"gen{Field("Depth")}|{Field("Reason")}|{Field("Type")}";
            lock (_gate) _gcs[key] = _gcs.GetValueOrDefault(key) + 1;
        }
        else if (data.EventName.StartsWith("GCHeapStats", StringComparison.Ordinal))
        {
            var loh = Convert.ToInt64(Field("GenerationSize3"));
            var poh = Convert.ToInt64(Field("GenerationSize4") ?? 0L);
            var stats = new { gen0 = Field("GenerationSize0"), gen1 = Field("GenerationSize1"), gen2 = Field("GenerationSize2"),
                loh, poh, pinned_objects = Field("PinnedObjectCount"), handles = Field("GCHandleCount") };
            lock (_gate) { _lastHeapStats = stats; _maxLoh = Math.Max(_maxLoh, loh); _maxPoh = Math.Max(_maxPoh, poh); }
        }
    }

    internal void Frame(double at, string phase, int segment, long privateBytes, long workingBytes)
    {
        if (_writer is null) return;
        if (_bucketStarted < 0) _bucketStarted = at;
        _phases.Add(phase);
        if (phase != _lastPhase)
        {
            Write(new { kind = "phase", at, segment, from = _lastPhase, to = phase, private_bytes = privateBytes,
                working_set_bytes = workingBytes, heap = Heap(GC.GetGCMemoryInfo()), godot = GodotObjects() });
            _lastPhase = phase;
        }
        if (at - _bucketStarted < 10) return;
        Dictionary<string, long> bytes, gcs;
        object? stats;
        long maxLoh, maxPoh;
        lock (_gate)
        {
            (bytes, gcs, stats, maxLoh, maxPoh) = (_bytes, _gcs, _lastHeapStats, _maxLoh, _maxPoh);
            _bytes = new(StringComparer.Ordinal);
            _gcs = new(StringComparer.Ordinal);
            _maxLoh = _maxPoh = 0;
        }
        var parsed = bytes.Select(item => (Parts: item.Key.Split('|'), Bytes: item.Value)).ToArray();
        Write(new { kind = "ticks", from = _bucketStarted, to = at, segment, phases = _phases.ToArray(),
            private_bytes = privateBytes, totals = parsed.GroupBy(item => item.Parts[0]).ToDictionary(group => group.Key, group => group.Sum(item => item.Bytes)),
            large = parsed.Where(item => item.Parts[0] != "small").GroupBy(item => item.Parts[0] + "|" + item.Parts[1])
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Bytes)),
            large_threads = parsed.Where(item => item.Parts[0] == "large").GroupBy(item => item.Parts[2])
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Bytes)),
            small_top = parsed.Where(item => item.Parts[0] == "small").GroupBy(item => item.Parts[1])
                .Select(group => (group.Key, Bytes: group.Sum(item => item.Bytes))).OrderByDescending(item => item.Bytes).Take(12)
                .ToDictionary(item => item.Key, item => item.Bytes),
            gcs, heap_stats = stats, max_loh = maxLoh, max_poh = maxPoh,
            counts = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
            total_pause_seconds = GC.GetTotalPauseDuration().TotalSeconds, total_allocated = GC.GetTotalAllocatedBytes(false), heap = Heap(GC.GetGCMemoryInfo()) });
        _bucketStarted = at;
        _phases.Clear();
    }

    internal void Collection(double at, int cycle, string phase, GCMemoryInfo info)
        => Write(new { kind = "collection", at, cycle, phase, heap = Heap(info), godot = GodotObjects(),
            counts = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
            total_allocated = GC.GetTotalAllocatedBytes(false) });

    private static object Heap(GCMemoryInfo info) => new
    {
        index = info.Index, generation = info.Generation, compacted = info.Compacted, concurrent = info.Concurrent,
        heap_bytes = info.HeapSizeBytes, fragmented_bytes = info.FragmentedBytes, committed_bytes = info.TotalCommittedBytes,
        promoted_bytes = info.PromotedBytes, pinned_objects = info.PinnedObjectsCount, memory_load = info.MemoryLoadBytes,
        high_load_threshold = info.HighMemoryLoadThresholdBytes,
        generations = info.GenerationInfo.ToArray().Select(item => new { size_after = item.SizeAfterBytes,
            fragmentation_after = item.FragmentationAfterBytes })
    };

    private static object GodotObjects() => new
    {
        objects = Performance.GetMonitor(Performance.Monitor.ObjectCount),
        resources = Performance.GetMonitor(Performance.Monitor.ObjectResourceCount),
        nodes = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
        orphan_nodes = Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount)
    };

    private void Write(object record)
    {
        _writer!.WriteLine(JsonSerializer.Serialize(record, Json));
        _writer.Flush();
    }

    public override void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
        base.Dispose();
    }
}
