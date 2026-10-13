using System.Collections.Immutable;
using Train.App.Assets;
using Train.Content.Contracts;

namespace Train.Cli;

internal sealed class CliContentFiles(string repository) : IContentFiles
{
    internal static readonly ContentRoot BuiltIn = new("builtin");
    internal static readonly ContentRoot Mods = new("mods");
    private readonly DiskContentFiles _builtIn = new(Path.Combine(Path.GetFullPath(repository), "builtin", "core"));

    public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory) =>
        directory.Root == BuiltIn ? _builtIn.Enumerate(directory) : directory.Root == Mods
            ? new ContentIoResult<ImmutableArray<ContentFileEntry>>.Failure(ContentIoFailure.Missing, "工具只使用内置内容。")
            : throw new ArgumentException("未登记的内容根。", nameof(directory));

    public ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes) =>
        file.Root == BuiltIn ? _builtIn.Read(file, maximumBytes) : file.Root == Mods
            ? new ContentIoResult<ImmutableArray<byte>>.Failure(ContentIoFailure.Missing, "工具只使用内置内容。")
            : throw new ArgumentException("未登记的内容根。", nameof(file));
}
