namespace Train.Content;

public interface IContentLogger
{
    void Debug(string category, string eventId, string message, IReadOnlyDictionary<string, object?>? attributes = null, Exception? exception = null);
    void Info(string category, string eventId, string message, IReadOnlyDictionary<string, object?>? attributes = null, Exception? exception = null);
    void Warning(string category, string eventId, string message, IReadOnlyDictionary<string, object?>? attributes = null, Exception? exception = null);
    void Error(string category, string eventId, string message, IReadOnlyDictionary<string, object?>? attributes = null, Exception? exception = null);
}
