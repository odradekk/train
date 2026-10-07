// 属于存档模块。
// 表示存档文件访问的结果，不包含文档格式校验或世界恢复状态。

namespace Train.Core.Saves;

/// <summary>表示存档文件读取或发布的结果；成功不表示文档内容有效或世界已恢复。</summary>
/// <example>文件不存在时 TryRead 返回 false 和 NotFound；见 <c>FileSaveStoreTests.Missing_target_does_not_read_or_publish_a_candidate</c>。</example>
public enum SaveStoreStatus
{
    /// <summary>候选文件已成功发布为目标存档文件。</summary>
    Saved,
    /// <summary>已读取目标文件的文本；尚未解析或校验文档。</summary>
    Loaded,
    /// <summary>写入或发布失败；候选文件已尝试清理。</summary>
    WriteFailed,
    /// <summary>目标文件存在，但读取过程中发生异常。</summary>
    ReadFailed,
    /// <summary>目标路径未通过根目录包含检查，或 File.Exists 未确认目标文件存在。</summary>
    NotFound
}
