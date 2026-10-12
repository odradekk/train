// Content 测试辅助：记录内容诊断事件，供断言观察。
// 属性在写入时复制，快照不暴露记录集合。
using Train.Content;

namespace Train.Content.Tests;

internal enum LogLevel { Debug, Info, Warning, Error }

internal sealed record LoggedContentEvent(
    LogLevel Level, string Category, string EventId, string Message,
    IReadOnlyDictionary<string, object?> Attributes);

internal sealed class RecordingContentLogger : IContentLogger
{
    private readonly List<LoggedContentEvent> _events = new();

    public IReadOnlyList<LoggedContentEvent> Snapshot()
    {
        lock (_events)
            return _events.ToArray();
    }

    public void Debug(string category, string eventId, string message, IReadOnlyDictionary<string, object?>? attributes = null, Exception? exception = null) =>
        Add(LogLevel.Debug, category, eventId, message, attributes);

    public void Info(string category, string eventId, string message, IReadOnlyDictionary<string, object?>? attributes = null, Exception? exception = null) =>
        Add(LogLevel.Info, category, eventId, message, attributes);

    public void Warning(string category, string eventId, string message, IReadOnlyDictionary<string, object?>? attributes = null, Exception? exception = null) =>
        Add(LogLevel.Warning, category, eventId, message, attributes);

    public void Error(string category, string eventId, string message, IReadOnlyDictionary<string, object?>? attributes = null, Exception? exception = null) =>
        Add(LogLevel.Error, category, eventId, message, attributes);

    private void Add(LogLevel level, string category, string eventId, string message, IReadOnlyDictionary<string, object?>? attributes)
    {
        var entry = new LoggedContentEvent(level, category, eventId, message,
            attributes is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(attributes));
        lock (_events)
            _events.Add(entry);
    }

}
