using System.Collections.Immutable;
using Train.Content.Contracts;

namespace Train.App.Assets;

internal sealed class DiskContentFiles : IContentFiles
{
    private readonly string _directory;

    public DiskContentFiles(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("内容根必须是绝对路径。", nameof(directory));
        _directory = Path.GetFullPath(directory);
    }

    public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory)
    {
        var path = Resolve(directory, allowEmpty: true);
        try
        {
            if (HasLinkedSegment(path))
                return new ContentIoResult<ImmutableArray<ContentFileEntry>>.Failure(
                    ContentIoFailure.LinkedPath, path);
            var entries = ImmutableArray.CreateBuilder<ContentFileEntry>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                var attributes = File.GetAttributes(entry);
                var kind = (attributes & FileAttributes.ReparsePoint) != 0 ? ContentEntryKind.Link
                    : (attributes & FileAttributes.Directory) != 0 ? ContentEntryKind.Directory
                    : ContentEntryKind.File;
                entries.Add(new ContentFileEntry(Path.GetFileName(entry), kind));
            }
            return new ContentIoResult<ImmutableArray<ContentFileEntry>>.Success(entries.ToImmutable());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new ContentIoResult<ImmutableArray<ContentFileEntry>>.Failure(FailureKind(error), error.Message);
        }
    }

    public ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        var path = Resolve(file, allowEmpty: false);
        try
        {
            if (HasLinkedSegment(path))
                return new ContentIoResult<ImmutableArray<byte>>.Failure(ContentIoFailure.LinkedPath, path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadBounded(input, maximumBytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new ContentIoResult<ImmutableArray<byte>>.Failure(FailureKind(error), error.Message);
        }
    }

    internal static ContentIoResult<ImmutableArray<byte>> ReadBounded(Stream input, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var remaining = maximumBytes - output.Length;
            var requested = remaining >= buffer.Length ? buffer.Length : (int)remaining + 1;
            var count = input.Read(buffer, 0, requested);
            if (count == 0)
                return new ContentIoResult<ImmutableArray<byte>>.Success(
                    ImmutableArray.CreateRange(output.ToArray()));
            if (count > remaining)
                return new ContentIoResult<ImmutableArray<byte>>.Failure(
                    ContentIoFailure.LimitExceeded, "文件超过字节上限。");
            output.Write(buffer, 0, count);
        }
    }

    private string Resolve(ContentPath path, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(path.RelativePath);
        if (allowEmpty && path.RelativePath.Length == 0)
            return _directory;
        var segments = path.RelativePath.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."
                || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || segment.Contains('\\')))
            throw new ArgumentException("内容路径必须使用 / 分隔的有效相对路径。", nameof(path));
        return Path.Combine(_directory, Path.Combine(segments));
    }

    private bool HasLinkedSegment(string path)
    {
        var current = _directory;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            return true;
        var relative = Path.GetRelativePath(_directory, path);
        if (relative == ".") return false;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        return false;
    }

    private static ContentIoFailure FailureKind(Exception error) =>
        error is FileNotFoundException or DirectoryNotFoundException
            ? ContentIoFailure.Missing : ContentIoFailure.Inaccessible;
}
