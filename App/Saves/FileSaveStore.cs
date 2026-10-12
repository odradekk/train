
using System.Text;
namespace Train.App.Saves;

public sealed class FileSaveStore
{
    public const string SavesFolder = "saves";
    public const string FileName = "save.json";
    public const string CandidateFileName = "save.json.candidate";

    public string Root { get; }

    public FileSaveStore(string userDataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(userDataDirectory);
        Root = Path.GetFullPath(userDataDirectory);
    }

    public static string FormatId(Guid id) => id.ToString("D");

    public string DirectoryFor(Guid id) => Path.Combine(Root, SavesFolder, FormatId(id));

    public string PathFor(Guid id) => Path.Combine(DirectoryFor(id), FileName);

    public bool TryRead(Guid id, out string json, out SaveStoreStatus status)
    {
        json = "";
        status = SaveStoreStatus.Loaded;
        var path = PathFor(id);
        if (!File.Exists(path))
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

    public bool TryWrite(Guid id, string json, out SaveStoreStatus status) => TryWriteCore(id, json, true, out status);

    public bool TryWriteNew(Guid id, string json, out SaveStoreStatus status) => TryWriteCore(id, json, false, out status);

    private bool TryWriteCore(Guid id, string json, bool overwrite, out SaveStoreStatus status)
    {
        ArgumentNullException.ThrowIfNull(json);
        status = SaveStoreStatus.Saved;
        var directory = DirectoryFor(id);
        var target = PathFor(id);
        var candidate = Path.Combine(directory, CandidateFileName);
        try
        {
            Directory.CreateDirectory(directory);
            WriteCandidate(candidate, json);
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

    private static void WriteCandidate(string path, string json)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(json);
        writer.Flush();
        stream.Flush(true);
    }
}
