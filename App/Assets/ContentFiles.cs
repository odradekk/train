using System.Collections.Immutable;
using Godot;
using Train.Content.Contracts;
using FileAccess = Godot.FileAccess;

namespace Train.App.Assets;

internal abstract record ContentRootLocation
{
    private ContentRootLocation() { }
    internal sealed record Disk(string Directory) : ContentRootLocation;
    internal sealed record Resource(string Path) : ContentRootLocation;
}

internal sealed class ContentFiles : IContentFiles
{
    private readonly Dictionary<ContentRoot, IContentFiles> _roots;

    internal ContentFiles(GodotMainThreadDispatcher dispatcher,
        IReadOnlyDictionary<ContentRoot, ContentRootLocation> roots)
    {
        _roots = roots.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            ContentRootLocation.Disk disk => (IContentFiles)new DiskContentFiles(disk.Directory),
            ContentRootLocation.Resource resource => ResourceFiles(dispatcher, resource.Path),
            _ => throw new ArgumentException("未知的内容根种类。", nameof(roots))
        });
    }

    public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory) =>
        Find(directory.Root).Enumerate(directory);

    public ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes) =>
        Find(file.Root).Read(file, maximumBytes);

    private IContentFiles Find(ContentRoot root) => _roots.TryGetValue(root, out var files)
        ? files : throw new ArgumentException($"未登记的内容根：{root.Key}。", nameof(root));

    private static IContentFiles ResourceFiles(GodotMainThreadDispatcher dispatcher, string root)
    {
        if (!root.StartsWith("res://", StringComparison.Ordinal))
            throw new ArgumentException("资源根必须使用 res://。", nameof(root));
        ValidatePath(root[6..].TrimEnd('/'));
        // 原生 DirAccess 会临时改变进程工作目录；源码运行复用磁盘端口。
        return OS.HasFeature("template")
            ? new ResourceContentFiles(dispatcher, root)
            : new DiskContentFiles(ProjectSettings.GlobalizePath(root));
    }

    internal static void ValidatePath(string path)
    {
        if (path.Contains('\\') || path.Contains(':') || path.StartsWith('/') ||
            path.Length > 0 && path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("内容路径必须是无链接穿越的根内相对路径。", nameof(path));
    }

    private sealed class ResourceContentFiles : IContentFiles
    {
        private readonly GodotMainThreadDispatcher _dispatcher;
        private readonly string _root;

        internal ResourceContentFiles(GodotMainThreadDispatcher dispatcher, string root)
        {
            _dispatcher = dispatcher;
            _root = root.TrimEnd('/');
            if (_root == "res:") _root = "res://";
        }

        public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory)
        {
            ValidatePath(directory.RelativePath);
            return _dispatcher.InvokeOnCallingThread<ContentIoResult<ImmutableArray<ContentFileEntry>>>(() =>
            {
                var path = PathFor(directory.RelativePath);
                if (HasLink(path)) return Failure<ImmutableArray<ContentFileEntry>>(ContentIoFailure.LinkedPath, path);
                using var dir = DirAccess.Open(path);
                if (dir is null) return Failure<ImmutableArray<ContentFileEntry>>(IoFailure(DirAccess.GetOpenError()), path);
                var entries = ImmutableArray.CreateBuilder<ContentFileEntry>();
                var error = dir.ListDirBegin();
                if (error != Error.Ok) return Failure<ImmutableArray<ContentFileEntry>>(IoFailure(error), path);
                for (var name = dir.GetNext(); name.Length > 0; name = dir.GetNext())
                {
                    if (name is "." or "..") continue;
                    entries.Add(new(name, dir.IsLink(name) ? ContentEntryKind.Link :
                        dir.CurrentIsDir() ? ContentEntryKind.Directory : ContentEntryKind.File));
                }
                dir.ListDirEnd();
                return new ContentIoResult<ImmutableArray<ContentFileEntry>>.Success(entries.ToImmutable());
            });
        }

        public ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
            ValidatePath(file.RelativePath);
            return _dispatcher.InvokeOnCallingThread<ContentIoResult<ImmutableArray<byte>>>(() =>
            {
                var path = PathFor(file.RelativePath);
                if (HasLink(path)) return Failure<ImmutableArray<byte>>(ContentIoFailure.LinkedPath, path);
                using var input = FileAccess.Open(path, FileAccess.ModeFlags.Read);
                if (input is null) return Failure<ImmutableArray<byte>>(IoFailure(FileAccess.GetOpenError()), path);
                var bytes = new List<byte>();
                while (!input.EofReached())
                {
                    var remaining = maximumBytes - bytes.Count;
                    var block = input.GetBuffer(remaining < 8192 ? remaining + 1 : 8192);
                    if (block.Length > remaining) return Failure<ImmutableArray<byte>>(ContentIoFailure.LimitExceeded, path);
                    bytes.AddRange(block);
                    var error = input.GetError();
                    if (error is not (Error.Ok or Error.FileEof)) return Failure<ImmutableArray<byte>>(ContentIoFailure.Inaccessible, path);
                    if (block.Length == 0) break;
                }
                return new ContentIoResult<ImmutableArray<byte>>.Success(bytes.ToImmutableArray());
            });
        }

        private string PathFor(string relative) => relative.Length == 0 ? _root :
            _root == "res://" ? _root + relative : _root + "/" + relative;

        private static bool HasLink(string path)
        {
            var current = "res://";
            foreach (var segment in path[6..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                using var directory = DirAccess.Open(current);
                if (directory is null) return false;
                if (directory.IsLink(segment)) return true;
                current = current == "res://" ? current + segment : current + "/" + segment;
            }
            return false;
        }

        private static ContentIoFailure IoFailure(Error error) => error is Error.FileNotFound or Error.FileBadPath or Error.DoesNotExist
            ? ContentIoFailure.Missing : ContentIoFailure.Inaccessible;

        private static ContentIoResult<T>.Failure Failure<T>(ContentIoFailure kind, string path) where T : notnull =>
            new(kind, $"资源文件访问失败：{path}（{kind}）。");
    }
}
