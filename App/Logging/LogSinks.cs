
using System.Text.Json;

namespace Train.App.Logging;

public interface ILogSink
{
    void Emit(LogEvent logEvent);
}

public interface IGodotLogOutput
{
    void Write(LogEvent logEvent);
}

public sealed class MemoryLogSink : ILogSink
{
    private readonly List<LogEvent> _events = new();
    private readonly object _gate = new();

    public IReadOnlyList<LogEvent> Snapshot()
    {
        lock (_gate)
            return _events.ToArray();
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        lock (_gate)
            _events.Add(logEvent);
    }
}

public sealed class RingBufferLogSink : ILogSink
{
    public const int Capacity = 500;

    private readonly Queue<LogEvent> _events = new(Capacity);
    private readonly object _gate = new();

    public IReadOnlyList<LogEvent> Snapshot()
    {
        lock (_gate)
            return _events.ToArray();
    }

    public void Clear()
    {
        lock (_gate)
            _events.Clear();
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        lock (_gate)
        {
            if (_events.Count == Capacity)
                _events.Dequeue();
            _events.Enqueue(logEvent);
        }
    }
}

public sealed class JsonlLogSink : ILogSink, IDisposable
{
    public const string FilePrefix = "train-session-";
    public const string FileSearchPattern = "train-session-*.jsonl";
    public const int RetainedSessionFiles = 10;

    private readonly object _gate = new();
    private Stream? _stream;
    private bool _disposed;

    public string? FilePath { get; }
    public bool IsEnabled => _stream is not null;

    public JsonlLogSink(string directory, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(directory);
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{FilePrefix}{sessionId:D}.jsonl");
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            FilePath = path;
            TryPruneRetention(directory, path);
        }
        catch
        {
            _stream?.Dispose();
            _stream = null;
            FilePath = null;
        }
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        lock (_gate)
        {
            if (_stream is null)
                return;

            try
            {
                WriteLine(_stream, logEvent);
            }
            catch
            {
                _stream.Dispose();
                _stream = null;
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _stream?.Dispose();
            _stream = null;
        }
    }

    internal static void WriteLine(Stream stream, LogEvent logEvent)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteEvent(writer, logEvent);
        buffer.WriteByte((byte)'\n');
        stream.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
        if (stream is FileStream file)
            file.Flush(true);
        else
            stream.Flush();
    }

    internal static void WriteEvent(Utf8JsonWriter writer, LogEvent logEvent)
    {
        writer.WriteStartObject();
        writer.WriteString("timestamp", logEvent.Timestamp.UtcDateTime.ToString("O"));
        writer.WriteString("session_id", logEvent.SessionId);
        writer.WriteNumber("sequence", logEvent.Sequence);
        writer.WriteString("level", LogLevelText.ToSerialized(logEvent.Level));
        writer.WriteString("category", logEvent.Category);
        writer.WriteString("event_id", logEvent.EventId);
        writer.WriteString("message", logEvent.Message);
        writer.WritePropertyName("attributes");
        WriteValue(writer, logEvent.Attributes);
        writer.WritePropertyName("exception");
        if (logEvent.Exception is null)
            writer.WriteNullValue();
        else
            WriteException(writer, logEvent.Exception);
        writer.WriteEndObject();
    }

    private static void WriteException(Utf8JsonWriter writer, LogExceptionData exception)
    {
        writer.WriteStartObject();
        writer.WriteString("type", exception.Type);
        writer.WriteString("message", exception.Message);
        if (exception.StackTrace is null)
            writer.WriteNull("stack_trace");
        else
            writer.WriteString("stack_trace", exception.StackTrace);
        writer.WritePropertyName("inner_exceptions");
        writer.WriteStartArray();
        foreach (var inner in exception.InnerExceptions)
            WriteException(writer, inner);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string text:
                writer.WriteStringValue(text);
                return;
            case bool flag:
                writer.WriteBooleanValue(flag);
                return;
            case sbyte n:
                writer.WriteNumberValue(n);
                return;
            case byte n:
                writer.WriteNumberValue(n);
                return;
            case short n:
                writer.WriteNumberValue(n);
                return;
            case ushort n:
                writer.WriteNumberValue(n);
                return;
            case int n:
                writer.WriteNumberValue(n);
                return;
            case uint n:
                writer.WriteNumberValue(n);
                return;
            case long n:
                writer.WriteNumberValue(n);
                return;
            case ulong n:
                writer.WriteNumberValue(n);
                return;
            case float n:
                writer.WriteNumberValue(n);
                return;
            case double n:
                writer.WriteNumberValue(n);
                return;
            case IReadOnlyDictionary<string, object?> dictionary:
                writer.WriteStartObject();
                foreach (var (key, item) in dictionary)
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, item);
                }

                writer.WriteEndObject();
                return;
            case IReadOnlyList<object?> list:
                writer.WriteStartArray();
                foreach (var item in list)
                    WriteValue(writer, item);
                writer.WriteEndArray();
                return;
            default:
                writer.WriteStringValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                return;
        }
    }

    private static void TryPruneRetention(string directory, string currentPath)
    {
        try
        {
            var files = Directory.GetFiles(directory, FileSearchPattern);
            if (files.Length <= RetainedSessionFiles)
                return;

            var stale = files
                .OrderBy(path => File.GetCreationTimeUtc(path))
                .ThenBy(path => path, StringComparer.Ordinal)
                .Where(path => !string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
                .Take(files.Length - RetainedSessionFiles);

            foreach (var path in stale)
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                }
            }
        }
        catch
        {
            // 尽力执行的清理不得妨碍当前会话记录日志。
        }
    }
}
