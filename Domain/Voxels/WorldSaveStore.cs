using System.Text;
using Train.Content;

namespace Train.Domain.Voxels;

public interface IWorldSaveStore
{
    bool TryRead(Guid id, out string json, out WorldSaveStatus status);
    bool TryWrite(Guid id, string json, out WorldSaveStatus status);
}

public sealed class FileWorldSaveStore : IWorldSaveStore
{
    public const string SavesFolder = "saves";
    public const string FileName = "save.json";
    public const string CandidateFileName = "save.json.candidate";

    public string Root { get; }

    public FileWorldSaveStore(string userDataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(userDataDirectory);
        Root = ExternalPath.NormalizeRoot(userDataDirectory);
    }

    public static string FormatId(Guid id) => id.ToString("D");

    public string DirectoryFor(Guid id) => Path.Combine(Root, SavesFolder, FormatId(id));

    public string PathFor(Guid id) => Path.Combine(DirectoryFor(id), FileName);

    public bool TryRead(Guid id, out string json, out WorldSaveStatus status)
    {
        json = "";
        status = WorldSaveStatus.Loaded;
        var path = PathFor(id);
        if (!ExternalPath.IsSafeUnderRoot(Root, path) || !File.Exists(path))
        {
            status = WorldSaveStatus.NotFound;
            return false;
        }

        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
            return true;
        }
        catch
        {
            status = WorldSaveStatus.ReadFailed;
            return false;
        }
    }

    public bool TryWrite(Guid id, string json, out WorldSaveStatus status)
    {
        ArgumentNullException.ThrowIfNull(json);
        status = WorldSaveStatus.Saved;
        var directory = DirectoryFor(id);
        var target = PathFor(id);
        var candidate = Path.Combine(directory, CandidateFileName);
        if (!ExternalPath.IsSafeUnderRoot(Root, target) || !ExternalPath.IsSafeUnderRoot(Root, candidate))
        {
            status = WorldSaveStatus.WriteFailed;
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(candidate, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(candidate, target, overwrite: true);
            return true;
        }
        catch
        {
            status = WorldSaveStatus.WriteFailed;
            try
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
            catch
            {
                // Candidate cleanup must not hide the write failure.
            }

            return false;
        }
    }
}
