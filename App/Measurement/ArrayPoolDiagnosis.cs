using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text;
using System.Text.Json;

namespace Train.App.Measurement;

// 临时取证：只在诊断进程启用，回调同步观察借用者，不记录载荷或主机信息。
internal sealed class ArrayPoolDiagnosis : EventListener
{
    private const string SourceName = "System.Buffers.ArrayPoolEventSource";
    private const int MinimumBytes = 1024 * 1024;
    [ThreadStatic] private static bool _insideCallback;
    private readonly object _gate = new();
    private readonly int _bytePool = ArrayPool<byte>.Shared.GetHashCode();
    private readonly int _charPool = ArrayPool<char>.Shared.GetHashCode();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly StreamWriter _writer;
    private volatile bool _enabled;

    internal static ArrayPoolDiagnosis? StartIfEnabled()
        => Environment.GetEnvironmentVariable("TRAIN_HEAP_DIAGNOSIS") is { Length: > 0 } directory
            ? new(directory) : null;

    private ArrayPoolDiagnosis(string directory)
    {
        Directory.CreateDirectory(directory);
        _writer = new(new FileStream(Path.Combine(directory, "array-pool.jsonl"), FileMode.Append,
            FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        _writer.WriteLine(JsonSerializer.Serialize(new
        {
            process_id = Environment.ProcessId, @event = "listener-started", timestamp = _started,
            stopwatch_frequency = Stopwatch.Frequency, seconds = 0
        }));
        // EventListener 的基类构造期间也会调用 OnEventSourceCreated，等输出已就绪再订阅。
        _enabled = true;
        foreach (var source in EventSource.GetSources()) OnEventSourceCreated(source);
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (_enabled && source.Name == SourceName) EnableEvents(source, EventLevel.Verbose);
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        if (!_enabled || _insideCallback || data.EventSource.Name != SourceName
            || data.EventName is not ("BufferRented" or "BufferAllocated" or "BufferReturned" or "BufferTrimmed" or "BufferDropped")
            || data.Payload is not { Count: >= 3 }) return;
        var length = (int)data.Payload[1]!;
        var pool = (int)data.Payload[2]!;
        int? width = pool == _bytePool ? 1 : pool == _charPool ? 2 : null;
        long? bytes = width is { } size ? (long)length * size : null;
        // 未知泛型池只知道元素数：保留 >=1M 元素作为字节下界，不把它冒充精确字节数。
        if ((bytes ?? length) < MinimumBytes) return;
        _insideCallback = true;
        try
        {
            var timestamp = Stopwatch.GetTimestamp();
            var stack = data.EventName is "BufferRented" or "BufferAllocated" ? CallerStack() : [];
            var row = new
            {
                process_id = Environment.ProcessId, @event = data.EventName,
                buffer_id = (int)data.Payload[0]!, buffer_length = length, pool_id = pool,
                element_type = width == 1 ? "System.Byte" : width == 2 ? "System.Char" : "unknown",
                element_size_bytes = width, size_bytes = bytes,
                thread_id = Environment.CurrentManagedThreadId, thread_pool = Thread.CurrentThread.IsThreadPoolThread,
                seconds = (timestamp - _started) / (double)Stopwatch.Frequency,
                timestamp, stopwatch_frequency = Stopwatch.Frequency, stack
            };
            lock (_gate) if (_enabled) _writer.WriteLine(JsonSerializer.Serialize(row));
        }
        catch (Exception error)
        {
            lock (_gate) if (_enabled) _writer.WriteLine(JsonSerializer.Serialize(new
            {
                process_id = Environment.ProcessId, @event = "listener-error",
                error_type = error.GetType().FullName
            }));
        }
        finally { _insideCallback = false; }
    }

    private static string[] CallerStack() => new StackTrace(false).GetFrames()
        .Select(frame => frame.GetMethod())
        .Where(method => method?.DeclaringType?.FullName is { } type
            && !type.StartsWith(typeof(ArrayPoolDiagnosis).FullName!, StringComparison.Ordinal)
            && !type.StartsWith("System.Diagnostics.Tracing.", StringComparison.Ordinal)
            && !type.StartsWith("System.Buffers.", StringComparison.Ordinal))
        .Select(method => method!.DeclaringType!.FullName + "." + method.Name).Take(20).ToArray();

    public override void Dispose()
    {
        _enabled = false;
        base.Dispose();
        lock (_gate) _writer.Dispose();
    }
}
