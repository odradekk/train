// 属于存档模块。
// 使用物理文件系统读写 UTF-8 文本，并通过候选文件发布保护已有存档。

using System.Text;
using Train.Core.Content;

namespace Train.Core.Saves;

/// <summary>在用户数据根目录下按存档 UUID 保存、读取和替换存档文件。</summary>
/// <remarks>
/// 路径布局见存档设计的“文件、身份与写入”。文本作为不透明数据处理，不校验 JSON 或恢复世界。
/// 根目录由调用方选定，路径检查只验证文本包含关系，不拒绝链接；同一存档的读写须由调用方串行协调。
/// </remarks>
/// <example>替换失败时原文件保持不变；见 <c>FileSaveStoreTests.Failed_overwrite_keeps_the_previous_file</c>。</example>
public sealed class FileSaveStore : ISaveStore
{
    /// <summary>用户数据根目录下的存档子目录名；来自存档设计的“文件、身份与写入”，不是可调整的玩法参数。</summary>
    public const string SavesFolder = "saves";
    /// <summary>每个存档目录中的已发布文件名；来自存档设计的“文件、身份与写入”。</summary>
    public const string FileName = "save.json";
    /// <summary>发布前的临时文件名；沿用既有文件写入约定，读取操作不会使用此文件。</summary>
    public const string CandidateFileName = "save.json.candidate";

    /// <summary>获取规范化后的用户数据绝对路径；不保证目录已存在或可访问。</summary>
    public string Root { get; }

    /// <summary>选定存档的用户数据根目录，不创建目录或访问存档文件。</summary>
    /// <param name="userDataDirectory">非 null、非空的物理路径；相对路径按当前工作目录解析。</param>
    /// <exception cref="ArgumentNullException"><paramref name="userDataDirectory"/> 为 null。</exception>
    /// <exception cref="ArgumentException">路径为空或平台无法将其解释为有效路径。</exception>
    /// <exception cref="PathTooLongException">平台报告路径长度超过系统限制。</exception>
    /// <exception cref="IOException">取得完整路径时发生其他 I/O 错误。</exception>
    /// <remarks>其他平台路径转换异常原样传播；路径规则见 <see cref="ExternalPath.NormalizeRoot"/>。</remarks>
    public FileSaveStore(string userDataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(userDataDirectory);
        Root = ExternalPath.NormalizeRoot(userDataDirectory);
    }

    /// <summary>将存档 UUID 转换为用于目录名的规范文本。</summary>
    /// <param name="id">存档 UUID；允许 Guid.Empty，不校验文档中的身份。</param>
    /// <returns>小写、带连字符的 D 格式 UUID 文本。</returns>
    public static string FormatId(Guid id) => id.ToString("D");

    /// <summary>取得指定存档的物理目录路径，不创建或检查目录。</summary>
    /// <param name="id">用于选择目录的存档 UUID。</param>
    /// <returns>用户数据根目录下 saves/&lt;UUID&gt; 的完整路径。</returns>
    public string DirectoryFor(Guid id) => Path.Combine(Root, SavesFolder, FormatId(id));

    /// <summary>取得指定存档的已发布文件路径，不检查文件是否存在。</summary>
    /// <param name="id">用于选择目录的存档 UUID。</param>
    /// <returns>该存档目录下 save.json 的完整路径。</returns>
    public string PathFor(Guid id) => Path.Combine(DirectoryFor(id), FileName);

    /// <inheritdoc/>
    /// <remarks>按 UTF-8 读取并沿用 .NET 的 BOM 识别和无效字节替换规则；不读取或提升残留候选。File.Exists 未确认存在时返回 NotFound，实际读取异常转换为 ReadFailed。</remarks>
    /// <example>文件被独占打开时读取失败且输出为空；见 <c>FileSaveStoreTests.Locked_target_returns_read_failed_with_empty_text</c>。</example>
    public bool TryRead(Guid id, out string json, out SaveStoreStatus status)
    {
        json = "";
        status = SaveStoreStatus.Loaded;
        var path = PathFor(id);
        if (!ExternalPath.IsSafeUnderRoot(Root, path) || !File.Exists(path))
        {
            status = SaveStoreStatus.NotFound;
            return false;
        }

        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
            return true;
        }
        catch
        {
            status = SaveStoreStatus.ReadFailed;
            return false;
        }
    }

    /// <inheritdoc/>
    /// <remarks>写入无 BOM 的 UTF-8 候选，刷新后通过同目录文件移动替换目标。失败时尽力删除候选；清理失败仍返回 WriteFailed，不掩盖最初失败。不承诺多写入者协调或断电后的目录元数据持久性。</remarks>
    /// <example>同一 UUID 的第二次保存替换原文本；见 <c>FileSaveStoreTests.Utf8_text_round_trips_and_overwrite_uses_the_same_target</c>。</example>
    public bool TryWrite(Guid id, string json, out SaveStoreStatus status) => TryWriteCore(id, json, true, out status);

    /// <summary>发布一个独立存档，拒绝替换任何已存在的目标文件。</summary>
    /// <param name="id">目标存档 UUID；如果文件已经存在，发布失败。</param>
    /// <param name="json">非 null 的完整文本；允许空字符串，调用方负责文档校验。</param>
    /// <param name="status">发布成功时为 Saved；写入、目标冲突或发布失败时为 WriteFailed。</param>
    /// <returns>新文件发布成功时为 true；失败时为 false，原目标保持不变。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> 为 null。</exception>
    /// <remarks>候选写入和失败清理与 TryWrite 相同；目标是否存在由最终文件移动操作判断。</remarks>
    /// <example>用不同文本再次发布同一 UUID 被拒绝；见 <c>FileSaveStoreTests.New_save_refuses_to_replace_existing_file_and_cleans_candidate</c>。</example>
    public bool TryWriteNew(Guid id, string json, out SaveStoreStatus status) => TryWriteCore(id, json, false, out status);

    /// <summary>写入完整候选后发布到目标；失败时尝试清理候选并保留失败结果。</summary>
    /// <param name="id">目标存档 UUID。</param>
    /// <param name="json">非 null 的完整文本；不解析其内容。</param>
    /// <param name="overwrite">是否允许替换已存在的目标文件。</param>
    /// <param name="status">成功为 Saved，失败为 WriteFailed。</param>
    /// <returns>候选成功移动到目标时为 true。</returns>
    private bool TryWriteCore(Guid id, string json, bool overwrite, out SaveStoreStatus status)
    {
        ArgumentNullException.ThrowIfNull(json);
        status = SaveStoreStatus.Saved;
        var directory = DirectoryFor(id);
        var target = PathFor(id);
        var candidate = Path.Combine(directory, CandidateFileName);
        if (!ExternalPath.IsSafeUnderRoot(Root, target) || !ExternalPath.IsSafeUnderRoot(Root, candidate))
        {
            status = SaveStoreStatus.WriteFailed;
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory);
            WriteCandidate(candidate, json, FileMode.Create);
            File.Move(candidate, target, overwrite);
            return true;
        }
        catch
        {
            status = SaveStoreStatus.WriteFailed;
            try
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
            catch
            {
                // 候选清理失败不能掩盖写入或发布失败，保留原失败结果供调用方处理。
            }

            return false;
        }
    }

    /// <summary>以无 BOM 的 UTF-8 写入候选并刷新到磁盘；不创建父目录、不发布或清理文件。</summary>
    /// <param name="path">调用方选定的物理候选路径，不执行根目录包含检查。</param>
    /// <param name="json">要写入的完整文本；不校验文档内容。</param>
    /// <param name="mode">打开文件的模式；Create 替换候选，CreateNew 拒绝已存在的候选。</param>
    /// <remarks>独占打开文件，先刷新文本编码缓冲，再调用 Flush(true)；路径、权限、写入或刷新异常原样传播，由发布流程转换为状态。</remarks>
    internal static void WriteCandidate(string path, string json, FileMode mode)
    {
        using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(json);
        writer.Flush();
        stream.Flush(true);
    }
}
