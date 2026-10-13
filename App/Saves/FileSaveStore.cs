using System.Text;
namespace Train.App.Saves;

internal sealed record FileWriteResult(bool Published, string? Code = null, string? Message = null,
    string? CleanupMessage = null);

internal sealed class FileSaveStore
{
    public const string SavesFolder = "saves";
    public const string FileName = "save.json";
    private readonly Action<string, string> writeCandidate;
    private readonly Action<string, string, bool> publish;
    public string Root { get; }

    public FileSaveStore(string userDataDirectory) : this(userDataDirectory, WriteCandidate, File.Move) { }

    internal FileSaveStore(string userDataDirectory, Action<string, string> writeCandidate,
        Action<string, string, bool> publish)
    {
        ArgumentException.ThrowIfNullOrEmpty(userDataDirectory);
        Root = Path.GetFullPath(userDataDirectory);
        this.writeCandidate = writeCandidate;
        this.publish = publish;
    }

    public static string FormatId(Guid id) => id.ToString("D");
    public string DirectoryFor(Guid id) => Path.Combine(Root, SavesFolder, FormatId(id));
    public string PathFor(Guid id) => Path.Combine(DirectoryFor(id), FileName);
    internal string AutoPathFor(Guid id, int slot)
    {
        if (slot is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(slot));
        return Path.Combine(DirectoryFor(id), $"autosave-{slot}.json");
    }

    internal SaveDocument Read(string target)
    {
        try
        {
            using var reader = new StreamReader(target, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            return SaveContainer.Decode(reader.ReadToEnd());
        }
        catch (DecoderFallbackException) { throw new SaveFormatException("invalid-save", "存档文件不是有效的 UTF-8"); }
    }
    internal SaveHeader ReadHeader(string target)
    {
        using var stream = File.OpenRead(target);
        return SaveContainer.ReadHeader(stream);
    }

    internal FileWriteResult Write(string target, SaveDocument document, bool overwrite,
        CancellationToken cancellation = default)
    {
        var fullTarget = Path.GetFullPath(target);
        var directory = Path.GetDirectoryName(fullTarget)!;
        var candidate = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        FileWriteResult result;
        var publishing = false;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            writeCandidate(candidate, SaveContainer.Encode(document));
            _ = Read(candidate);
            cancellation.ThrowIfCancellationRequested();
            publishing = true;
            // Windows 的短暂占用可能拒绝替换；进入发布后不再把取消当作撤销。
            for (var attempt = 0; ; attempt++)
            {
                try { publish(candidate, fullTarget, overwrite); break; }
                catch (Exception error) when ((error is IOException or UnauthorizedAccessException)
                    && attempt < 3 && (overwrite || !File.Exists(fullTarget)))
                { Thread.Sleep(25); }
            }
            result = new(true);
        }
        catch (OperationCanceledException) when (!publishing && cancellation.IsCancellationRequested)
        { result = new(false, "cancelled", "保存已取消"); }
        catch (SaveFormatException error)
        { result = new(false, error.Code, error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { result = new(false, "write-failed", error.Message); }
        try { if (File.Exists(candidate)) File.Delete(candidate); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return result with { CleanupMessage = $"临时文件清理失败：{error.Message}" }; }
        return result;
    }

    internal static void WriteCandidate(string path, string json)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(json);
        writer.Flush();
        stream.Flush(true);
    }
}
