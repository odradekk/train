// 属于存档模块。
// 为存档文档的编解码与恢复流程提供独立的文件访问边界。

namespace Train.Core.Saves;

/// <summary>按存档 UUID 读取文本或发布完整文本，不解析存档格式和世界状态。</summary>
/// <remarks>UUID 仅用于选择文件，不检查它是否等于文档中的身份；实现不负责捕获或校验调用方状态。</remarks>
/// <example>保存带中文名称的文本后原样读取；见 <c>FileSaveStoreTests.Utf8_text_round_trips_and_overwrite_uses_the_same_target</c>。</example>
public interface ISaveStore
{
    /// <summary>读取指定存档的完整文本。</summary>
    /// <param name="id">存档文件的 UUID；不从文本中推导身份。</param>
    /// <param name="json">成功时为读取的文本；失败时为空字符串。</param>
    /// <param name="status">成功时为 Loaded；失败时为 NotFound 或 ReadFailed。</param>
    /// <returns>文件文本已读取时为 true；无法找到或读取时为 false。</returns>
    /// <remarks>读取成功不保证文本是 JSON，也不保证它符合任何存档格式。</remarks>
    bool TryRead(Guid id, out string json, out SaveStoreStatus status);

    /// <summary>发布指定存档的完整文本，允许替换同一 UUID 的现有文件。</summary>
    /// <param name="id">目标存档文件的 UUID。</param>
    /// <param name="json">非 null 的完整文本；允许空字符串，调用方负责文档校验。</param>
    /// <param name="status">发布成功时为 Saved；写入或发布失败时为 WriteFailed。</param>
    /// <returns>完整文本已发布为目标文件时为 true；失败时为 false。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> 为 null。</exception>
    /// <remarks>实现须先写候选文件，再发布到目标；失败时不得用不完整候选覆盖原文件。</remarks>
    bool TryWrite(Guid id, string json, out SaveStoreStatus status);
}
