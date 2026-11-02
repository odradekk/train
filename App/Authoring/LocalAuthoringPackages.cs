using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Train.App.Assets;
using Train.App.Saves;
using Train.Content.Contracts;

namespace Train.App.Authoring;

internal static class LocalAuthoringPackages
{
    internal static ImmutableArray<AuthoringSource> Discover(IContentFactory factory, string directory,
        ImmutableArray<PackageStatus> loaded)
    {
        var disk = new DiskContentFiles(directory);
        var root = new ContentRoot("authoring-mods");
        var installation = new ContentInstallation(new("authoring-core"), root, null, "0.1.0", []);
        var listed = disk.Enumerate(new(root, ""));
        if (listed is ContentIoResult<ImmutableArray<ContentFileEntry>>.Failure { Kind: ContentIoFailure.Missing }) return [];
        var entries = ContentFilesSnapshot.Require(listed);
        var sources = ImmutableArray.CreateBuilder<AuthoringSource>();
        foreach (var entry in entries.Where(entry => entry.Kind != ContentEntryKind.File).OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            var result = factory.Discover(installation, new ManifestFiles(disk, root, entry));
            if (result is not ContentResult<ImmutableArray<PackageStatus>>.Success success)
                throw new InvalidDataException(result is ContentResult<ImmutableArray<PackageStatus>>.Faulted fault ? fault.Message
                    : string.Join("；", ((ContentResult<ImmutableArray<PackageStatus>>.Failure)result).Diagnostics.Select(item => item.Message)));
            var status = success.Value.Single(item => item.Root == root);
            var active = status.Metadata is null ? null : loaded.FirstOrDefault(item => item.Metadata?.Id == status.Metadata.Id);
            var diagnostics = status.Diagnostics.Concat(active?.Diagnostics ?? []).Distinct().ToImmutableArray();
            sources.Add(new(Path.Combine(directory, entry.Name), status.Metadata?.Id, status.Metadata?.Name is { Length: > 0 } name ? name : entry.Name,
                status.State == PackageState.Invalid ? status.State : active?.State ?? status.State, diagnostics));
        }
        return sources.ToImmutable();
    }

    internal static AuthoringTarget Create(IContentFactory factory, string directory, PackageMetadata metadata)
    {
        // docs/content/manifest.md owns the on-disk manifest format; Content validates it before publication.
        var json = JsonSerializer.Serialize(new
        {
            schema_version = 2, id = metadata.Id.Value, name = metadata.Name, version = metadata.Version,
            authors = metadata.Authors,
            dependencies = metadata.Dependencies.Select(dependency => new { id = dependency.Package.Value, min_version = dependency.MinimumVersion })
        }) + "\n";
        var root = new ContentRoot("new-authoring-package");
        var bytes = Encoding.UTF8.GetBytes(json).ToImmutableArray();
        var source = ContentFilesSnapshot.Empty(root).WithChanges([new("mod.json", bytes)]);
        var validated = factory.OpenAuthoringPackage(root, metadata.Id, source.Version(), source);
        if (validated is not ContentResult<IAuthoringPackage>.Success)
            throw new InvalidDataException(validated is ContentResult<IAuthoringPackage>.Failure failure
                ? string.Join("；", failure.Diagnostics.Select(item => item.Message)) : ((ContentResult<IAuthoringPackage>.Faulted)validated).Message);
        var target = Path.Combine(directory, metadata.Id.Value);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("同名目录已存在，没有写入任何文件。");
        Directory.CreateDirectory(directory);
        if (DiskContentFiles.HasLinkedSegment(directory, target)) throw new IOException("模组目录不允许链接。");
        var written = new FileSaveStore(directory).Write(Path.Combine(target, "mod.json"),
            candidate => FileSaveStore.WriteCandidate(candidate, json),
            candidate =>
            {
                if (!File.ReadAllBytes(candidate).AsSpan().SequenceEqual(bytes.AsSpan()))
                    throw new InvalidDataException("新模组 manifest 临时文件校验失败。");
            }, overwrite: false);
        if (!written.Published) throw new IOException(written.Message);
        return new(metadata.Id, target, false);
    }

    private sealed class ManifestFiles(IContentFiles disk, ContentRoot root, ContentFileEntry entry) : IContentFiles
    {
        public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath path) =>
            path.Root == root && path.RelativePath.Length == 0
                ? new ContentIoResult<ImmutableArray<ContentFileEntry>>.Success([entry])
                : new ContentIoResult<ImmutableArray<ContentFileEntry>>.Failure(ContentIoFailure.Missing, "仅发现包清单。");

        public ContentIoResult<ImmutableArray<byte>> Read(ContentPath path, long maximumBytes) =>
            path.Root == root && path.RelativePath == entry.Name + "/mod.json"
                ? disk.Read(path, maximumBytes)
                : new ContentIoResult<ImmutableArray<byte>>.Failure(ContentIoFailure.Missing, "仅发现包清单。");
    }
}
