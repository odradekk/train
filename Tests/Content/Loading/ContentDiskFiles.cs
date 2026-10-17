using System.Collections.Immutable;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

internal sealed class ContentDiskFiles : C.IContentFiles
{
    public C.ContentIoResult<ImmutableArray<C.ContentFileEntry>> Enumerate(C.ContentPath directory)
    {
        var path = Path.Combine(directory.Root.Key, directory.RelativePath);
        if (!Directory.Exists(path))
            return new C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Failure(C.ContentIoFailure.Missing, path);
        return new C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Success(Directory.EnumerateFileSystemEntries(path)
            .Select(entry => new C.ContentFileEntry(Path.GetFileName(entry),
                (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 ? C.ContentEntryKind.Link :
                Directory.Exists(entry) ? C.ContentEntryKind.Directory : C.ContentEntryKind.File)).ToImmutableArray());
    }

    public C.ContentIoResult<ImmutableArray<byte>> Read(C.ContentPath file, long maximumBytes)
    {
        var path = Path.Combine(file.Root.Key, file.RelativePath);
        if (!File.Exists(path))
            return new C.ContentIoResult<ImmutableArray<byte>>.Failure(C.ContentIoFailure.Missing, path);
        if (new FileInfo(path).Length > maximumBytes)
            return new C.ContentIoResult<ImmutableArray<byte>>.Failure(C.ContentIoFailure.LimitExceeded, path);
        return new C.ContentIoResult<ImmutableArray<byte>>.Success(File.ReadAllBytes(path).ToImmutableArray());
    }
}

