using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Train.App.Assets;
using Train.App.Saves;
using Train.Content.Contracts;

namespace Train.App.Authoring;

internal sealed class ContentFilesSnapshot : IContentFiles
{
    private readonly ImmutableDictionary<ContentPath, ImmutableArray<ContentFileEntry>> directories;
    private readonly ImmutableDictionary<ContentPath, ImmutableArray<byte>> files;

    private ContentFilesSnapshot(ImmutableDictionary<ContentPath, ImmutableArray<ContentFileEntry>> directories,
        ImmutableDictionary<ContentPath, ImmutableArray<byte>> files)
        => (this.directories, this.files) = (directories, files);

    public static ContentFilesSnapshot Capture(IContentFiles source, ImmutableArray<ContentRoot> roots)
    {
        var directories = ImmutableDictionary.CreateBuilder<ContentPath, ImmutableArray<ContentFileEntry>>();
        var files = ImmutableDictionary.CreateBuilder<ContentPath, ImmutableArray<byte>>();
        foreach (var root in roots)
        {
            var path = new ContentPath(root, "");
            if (source.Enumerate(path) is ContentIoResult<ImmutableArray<ContentFileEntry>>.Failure { Kind: ContentIoFailure.Missing }) continue;
            Visit(path);
        }
        return new(directories.ToImmutable(), files.ToImmutable());

        void Visit(ContentPath path)
        {
            var entries = Require(source.Enumerate(path)).OrderBy(e => e.Name, StringComparer.Ordinal).ToImmutableArray();
            directories.Add(path, entries);
            foreach (var entry in entries)
            {
                var child = path with { RelativePath = Join(path.RelativePath, entry.Name) };
                if (entry.Kind == ContentEntryKind.Link) throw new IOException($"包不允许链接：{child.RelativePath}");
                if (entry.Kind == ContentEntryKind.Directory) Visit(child);
                else files.Add(child, Require(source.Read(child, long.MaxValue)));
            }
        }
    }

    internal static T Require<T>(ContentIoResult<T> result) where T : notnull => result switch
    {
        ContentIoResult<T>.Success success => success.Value,
        ContentIoResult<T>.Failure failure => throw new IOException(failure.Diagnostic),
        _ => throw new InvalidOperationException()
    };

    private static string Join(string prefix, string name) => prefix.Length == 0 ? name : prefix + "/" + name;

    public ContentFilesSnapshot WithChanges(ContentRoot root, string prefix, ImmutableArray<PackageFileChange> changes)
    {
        var updated = files.ToBuilder();
        var dirs = directories.ToBuilder();
        foreach (var change in changes)
        {
            PackageWriter.ValidateRelativePath(change.RelativePath);
            var path = new ContentPath(root, Join(prefix, change.RelativePath));
            if (dirs.ContainsKey(path)) throw new ArgumentException("包补丁不能把目录当作文件。");
            if (change.Bytes is { } bytes) updated[path] = bytes;
            else updated.Remove(path);
        }
        foreach (var path in updated.Keys)
        {
            var parent = path.RelativePath;
            while (parent.Contains('/'))
            {
                parent = parent[..parent.LastIndexOf('/')];
                if (updated.ContainsKey(new(path.Root, parent))) throw new ArgumentException("包补丁存在文件与目录冲突。");
                dirs.TryAdd(new(path.Root, parent), []);
            }
        }
        foreach (var path in dirs.Keys.ToArray())
        {
            var children = dirs.Keys.Where(p => p.Root == path.Root && p.RelativePath != path.RelativePath)
                .Select(p => (Path: p, Kind: ContentEntryKind.Directory))
                .Concat(updated.Keys.Where(p => p.Root == path.Root).Select(p => (Path: p, Kind: ContentEntryKind.File)))
                .Where(p => Parent(p.Path.RelativePath) == path.RelativePath)
                .Select(p => new ContentFileEntry(p.Path.RelativePath.Split('/')[^1], p.Kind))
                .OrderBy(p => p.Name, StringComparer.Ordinal).ToImmutableArray();
            dirs[path] = children;
        }
        return new(dirs.ToImmutable(), updated.ToImmutable());
    }

    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory) =>
        directories.TryGetValue(directory, out var value) ? new ContentIoResult<ImmutableArray<ContentFileEntry>>.Success(value)
            : new ContentIoResult<ImmutableArray<ContentFileEntry>>.Failure(ContentIoFailure.Missing, directory.RelativePath);
    public ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        if (!files.TryGetValue(file, out var bytes)) return new ContentIoResult<ImmutableArray<byte>>.Failure(ContentIoFailure.Missing, file.RelativePath);
        return bytes.Length > maximumBytes ? new ContentIoResult<ImmutableArray<byte>>.Failure(ContentIoFailure.LimitExceeded, file.RelativePath)
            : new ContentIoResult<ImmutableArray<byte>>.Success(bytes);
    }

    internal PackageSourceVersion Version()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var pair in directories.OrderBy(p => p.Key.RelativePath, StringComparer.Ordinal))
        {
            Append(pair.Key.RelativePath);
            foreach (var entry in pair.Value) { Append(entry.Name); Append(entry.Kind.ToString()); }
        }
        foreach (var pair in files.OrderBy(p => p.Key.RelativePath, StringComparer.Ordinal))
        { Append(pair.Key.RelativePath); hash.AppendData(pair.Value.AsSpan()); }
        return new(Convert.ToHexString(hash.GetHashAndReset()));
        void Append(string value) { var bytes = Encoding.UTF8.GetBytes(value); hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes); }
    }

    internal bool SameFiles(ContentFilesSnapshot other) => directories.Count == other.directories.Count
        && files.Count == other.files.Count
        && directories.All(pair => other.directories.TryGetValue(pair.Key, out var value) && pair.Value.SequenceEqual(value))
        && files.All(pair => other.files.TryGetValue(pair.Key, out var value) && pair.Value.AsSpan().SequenceEqual(value.AsSpan()));
}

internal sealed class PackageSnapshot : IContentFiles
{
    private static readonly ContentRoot Root = new("package-snapshot");
    private readonly ContentFilesSnapshot snapshot;
    public string Directory { get; }
    public PackageSourceVersion Version { get; }
    private PackageSnapshot(string directory, ContentFilesSnapshot snapshot)
        => (Directory, this.snapshot, Version) = (directory, snapshot, snapshot.Version());
    public static PackageSnapshot Capture(string directory)
    {
        var full = Path.GetFullPath(directory);
        var disk = new DiskContentFiles(full);
        ContentFilesSnapshot.Require(disk.Enumerate(new(Root, "")));
        return new(full, ContentFilesSnapshot.Capture(disk, [Root]));
    }
    public bool MatchesDisk() => snapshot.SameFiles(Capture(Directory).snapshot);
    public PackageSnapshot WithChanges(ImmutableArray<PackageFileChange> changes) => new(Directory, snapshot.WithChanges(Root, "", changes));
    public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory) => snapshot.Enumerate(directory with { Root = Root });
    public ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes) => snapshot.Read(file with { Root = Root }, maximumBytes);
}

internal sealed record PackageWriteResult(bool Success, ImmutableArray<PackageFileChange> Completed,
    ImmutableArray<PackageFileChange> Remaining, PackageFileChange? Failed, string? Message);

internal static class PackageWriter
{
    internal static void ValidateRelativePath(string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Split('/').Any(s => s.Length == 0 || s is "." or ".."
            || s.EndsWith('.') || s.EndsWith(' ') || s.Contains('\\') || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("包路径必须是有效相对路径。", nameof(relative));
    }

    public static PackageWriteResult Execute(PackageSnapshot basis, PackageWritePlan plan, bool keepFiles,
        Action<PackageFileChange>? completed = null)
    {
        var changes = ImmutableArray.CreateBuilder<PackageFileChange>();
        var done = ImmutableArray.CreateBuilder<PackageFileChange>();
        PackageFileChange? current = null;
        try
        {
            foreach (var change in plan.Changes)
            {
                if (keepFiles && plan.Package.Value == "core" && change.Bytes is not null
                    && (Path.GetExtension(change.RelativePath).Equals(".png", StringComparison.OrdinalIgnoreCase)
                        || Path.GetExtension(change.RelativePath).Equals(".glb", StringComparison.OrdinalIgnoreCase))
                    && basis.Read(new(default, change.RelativePath), long.MaxValue) is ContentIoResult<ImmutableArray<byte>>.Failure
                    && basis.Read(new(default, change.RelativePath + ".import"), long.MaxValue) is ContentIoResult<ImmutableArray<byte>>.Failure
                    && !plan.Changes.Any(c => c.RelativePath == change.RelativePath + ".import"))
                    changes.Add(new(change.RelativePath + ".import", ImmutableArray.CreateRange(Encoding.UTF8.GetBytes("[remap]\n\nimporter=\"keep\"\n"))));
                changes.Add(change);
            }
            foreach (var change in changes)
            {
                current = change;
                ValidateRelativePath(change.RelativePath);
            }
            current = null;
            if (plan.SourceVersion != basis.Version || !basis.MatchesDisk())
                return new(false, [], changes.ToImmutable(), null, "源包已发生外部修改，请重新载入。");
            _ = basis.WithChanges(changes.ToImmutable());
            foreach (var change in changes)
            {
                current = change;
                var target = Path.Combine(basis.Directory, change.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var path = basis.Directory;
                foreach (var segment in change.RelativePath.Split('/'))
                {
                    path = Path.Combine(path, segment);
                    if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("包路径不允许链接。");
                }
                if (change.Bytes is { } bytes)
                {
                    var result = new FileSaveStore(basis.Directory).Write(target,
                        candidate => { using var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(bytes.AsSpan()); stream.Flush(true); },
                        candidate => { if (!File.ReadAllBytes(candidate).AsSpan().SequenceEqual(bytes.AsSpan())) throw new InvalidDataException("临时文件校验失败。"); }, true);
                    if (!result.Published) throw new IOException(result.Message);
                }
                else File.Delete(target);
                done.Add(change);
                completed?.Invoke(change);
            }
            return new(true, done.ToImmutable(), [], null, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(false, done.ToImmutable(), changes.Skip(done.Count).ToImmutableArray(), current, error.Message); }
    }
}
