namespace Train.Content;

/// <summary>将内容扫描与发布诊断交给宿主记录。</summary>
public interface IContentLogger
{
    /// <summary>记录诊断细节。</summary>
    void Debug(string category, string eventId, string message, IReadOnlyDictionary<string, object?>? attributes = null, Exception? exception = null);
    /// <summary>记录正常信息。</summary>
    void Info(string category, string eventId, string message, IReadOnlyDictionary<string, object?>? attributes = null, Exception? exception = null);
    /// <summary>记录可恢复的问题。</summary>
    void Warning(string category, string eventId, string message, IReadOnlyDictionary<string, object?>? attributes = null, Exception? exception = null);
    /// <summary>记录失败。</summary>
    void Error(string category, string eventId, string message, IReadOnlyDictionary<string, object?>? attributes = null, Exception? exception = null);
}
