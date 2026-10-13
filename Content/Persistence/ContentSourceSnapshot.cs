using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using Train.Content.Loading;
using static Train.Content.Loading.DefinitionJson;
using C = Train.Content.Contracts;

namespace Train.Content.Persistence;

internal sealed class RecordingContentSource(C.ContentInstallation installation, C.IContentFiles files,
    C.IAssetInspector inspector) : C.IContentFiles, C.IAssetInspector
{
    private readonly Dictionary<C.ContentPath, C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>> _directories = [];
    private readonly Dictionary<(C.ContentPath Path, long Maximum), C.ContentIoResult<ImmutableArray<byte>>> _files = [];
    private readonly Dictionary<(C.AssetKind Kind, C.AssetRevision Revision), C.AssetInspection> _inspections = [];

    public C.ContentIoResult<ImmutableArray<C.ContentFileEntry>> Enumerate(C.ContentPath directory)
    {
        if (!_directories.TryGetValue(directory, out var result))
        {
            result = files.Enumerate(directory);
            if (result is C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Success { Value.IsDefault: true })
                ContentCapture.Inconsistent("/source/directories", "目录事实必须初始化。");
            _directories.Add(directory, result);
        }
        return result;
    }

    public C.ContentIoResult<ImmutableArray<byte>> Read(C.ContentPath file, long maximumBytes)
    {
        var key = (file, maximumBytes);
        if (!_files.TryGetValue(key, out var result))
        {
            result = files.Read(file, maximumBytes);
            if (result is C.ContentIoResult<ImmutableArray<byte>>.Success { Value.IsDefault: true })
                ContentCapture.Inconsistent("/source/files", "文件字节必须初始化。");
            _files.Add(key, result);
        }
        return result;
    }

    public C.AssetInspection Inspect(C.AssetKind kind, C.AssetRevision revision, ImmutableArray<byte> bytes)
    {
        var key = (kind, revision);
        if (!_inspections.TryGetValue(key, out var result))
        {
            result = inspector.Inspect(kind, revision, bytes);
            if (result is C.AssetInspection.Success { Metadata: C.AssetMetadata.Glb { Surfaces.IsDefault: true } })
                ContentCapture.Inconsistent("/source/inspections", "模型表面事实必须初始化。");
            _inspections.Add(key, result);
        }
        return result;
    }

    internal ContentSourceSnapshot Freeze() => new(installation, _directories.ToFrozenDictionary(),
        _files.ToFrozenDictionary(), _inspections.ToFrozenDictionary());
}

internal sealed class ContentSourceSnapshot(C.ContentInstallation installation,
    FrozenDictionary<C.ContentPath, C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>> directories,
    FrozenDictionary<(C.ContentPath Path, long Maximum), C.ContentIoResult<ImmutableArray<byte>>> files,
    FrozenDictionary<(C.AssetKind Kind, C.AssetRevision Revision), C.AssetInspection> inspections)
    : C.IContentFiles, C.IAssetInspector
{
    internal C.ContentInstallation Installation { get; } = installation;
    internal IEnumerable<ImmutableArray<byte>> FileBytes => files.Values
        .OfType<C.ContentIoResult<ImmutableArray<byte>>.Success>().Select(result => result.Value);

    public C.ContentIoResult<ImmutableArray<C.ContentFileEntry>> Enumerate(C.ContentPath directory) =>
        directories.TryGetValue(directory, out var result) ? result : throw Missing("directories");
    public C.ContentIoResult<ImmutableArray<byte>> Read(C.ContentPath file, long maximumBytes) =>
        files.TryGetValue((file, maximumBytes), out var result) ? result : throw Missing("files");
    public C.AssetInspection Inspect(C.AssetKind kind, C.AssetRevision revision, ImmutableArray<byte> bytes) =>
        inspections.TryGetValue((kind, revision), out var result) ? result : throw Missing("inspections");

    private static DefinitionFormatException Missing(string field) =>
        new("/source/" + field, "复现来源缺少请求事实。", "inconsistent_capture");

    internal void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("installation");
        writer.WriteString("built_in", Installation.BuiltIn.Key);
        writer.WriteString("mods", Installation.Mods.Key);
        writer.WriteString("game_version", Installation.GameVersion);
        writer.WriteStartArray("enabled_mods");
        foreach (var mod in Installation.EnabledMods.Distinct().OrderBy(m => m.Value, StringComparer.Ordinal)) writer.WriteStringValue(mod.Value);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteStartArray("directories");
        foreach (var entry in directories.OrderBy(e => e.Key.Root.Key, StringComparer.Ordinal).ThenBy(e => e.Key.RelativePath, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            WritePath(writer, entry.Key);
            if (entry.Value is C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Failure failure)
                WriteFailure(writer, (int)failure.Kind, failure.Diagnostic);
            else
            {
                writer.WriteStartArray("entries");
                foreach (var child in ((C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Success)entry.Value).Value
                    .OrderBy(child => child.Name, StringComparer.Ordinal).ThenBy(child => child.Kind))
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", child.Name);
                    writer.WriteNumber("kind", (int)child.Kind);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("files");
        foreach (var entry in files.OrderBy(e => e.Key.Path.Root.Key, StringComparer.Ordinal)
                     .ThenBy(e => e.Key.Path.RelativePath, StringComparer.Ordinal).ThenBy(e => e.Key.Maximum))
        {
            writer.WriteStartObject();
            WritePath(writer, entry.Key.Path);
            writer.WriteNumber("maximum_bytes", entry.Key.Maximum);
            if (entry.Value is C.ContentIoResult<ImmutableArray<byte>>.Failure failure)
                WriteFailure(writer, (int)failure.Kind, failure.Diagnostic);
            else
            {
                var bytes = ((C.ContentIoResult<ImmutableArray<byte>>.Success)entry.Value).Value;
                writer.WriteString("bytes_hash", ContentHash.FromBytes(bytes.AsSpan()));
                writer.WriteNumber("length", bytes.Length);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("inspections");
        foreach (var entry in inspections.OrderBy(e => e.Key.Kind).ThenBy(e => e.Key.Revision.Value, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteNumber("kind", (int)entry.Key.Kind);
            writer.WriteString("revision", entry.Key.Revision.Value);
            if (entry.Value is C.AssetInspection.Failure failure) WriteFailure(writer, (int)failure.Kind, failure.Diagnostic);
            else
            {
                var metadata = ((C.AssetInspection.Success)entry.Value).Metadata;
                writer.WriteString("metadata_kind", metadata is C.AssetMetadata.Png ? "png" : "glb");
                writer.WritePropertyName("metadata");
                if (metadata is C.AssetMetadata.Png png) JsonSerializer.Serialize(writer, png);
                else JsonSerializer.Serialize(writer, (C.AssetMetadata.Glb)metadata);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WritePath(Utf8JsonWriter writer, C.ContentPath path)
    {
        writer.WriteString("root", path.Root.Key);
        writer.WriteString("path", path.RelativePath);
    }

    private static void WriteFailure(Utf8JsonWriter writer, int kind, string diagnostic)
    {
        writer.WriteNumber("failure", kind);
        writer.WriteString("diagnostic", diagnostic);
    }

    internal static ContentSourceSnapshot Read(JsonElement root, IReadOnlyDictionary<string, ImmutableArray<byte>> blobs)
    {
        if (ContentDocument.DuplicateField(root, "/source") is { } duplicate)
            Fail(duplicate, "JSON 字段重复。", "invalid_json");
        Object(root, "/source", "installation", "directories", "files", "inspections");
        var install = Required(root, "installation", "/source");
        Object(install, "/source/installation", "built_in", "mods", "game_version", "enabled_mods");
        var enabled = Items(Required(install, "enabled_mods", "/source/installation"), "/source/installation/enabled_mods")
            .Select(item => new C.PackageId(Text(item.Value, item.Field))).ToImmutableArray();
        Unique(enabled, "/source/installation/enabled_mods");
        var installation = new C.ContentInstallation(new(Text(Required(install, "built_in", ""), "/source/installation/built_in")),
            new(Text(Required(install, "mods", ""), "/source/installation/mods")), null,
            Text(Required(install, "game_version", ""), "/source/installation/game_version"), enabled);
        var directories = new Dictionary<C.ContentPath, C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>>();
        foreach (var (value, field) in Items(Required(root, "directories", "/source"), "/source/directories"))
        {
            if (value.ValueKind != JsonValueKind.Object) Fail(field, "必须为对象。");
            var failed = value.TryGetProperty("failure", out var failure);
            Object(value, field, failed ? ["root", "path", "failure", "diagnostic"] : ["root", "path", "entries"]);
            C.ContentIoResult<ImmutableArray<C.ContentFileEntry>> result = failed
                ? new C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Failure(IoFailure(failure, field), Text(Required(value, "diagnostic", field), field + "/diagnostic"))
                : new C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Success(Items(Required(value, "entries", field), field + "/entries").Select(item =>
                {
                    Object(item.Value, item.Field, "name", "kind");
                    return new C.ContentFileEntry(Text(Required(item.Value, "name", item.Field), item.Field + "/name"),
                        (C.ContentEntryKind)Int32(Required(item.Value, "kind", item.Field), item.Field + "/kind", 0, 2));
                }).ToImmutableArray());
            if (!directories.TryAdd(ReadPath(value, field), result)) ContentCapture.Inconsistent(field, "目录请求重复。");
        }
        var files = new Dictionary<(C.ContentPath Path, long Maximum), C.ContentIoResult<ImmutableArray<byte>>>();
        foreach (var (value, field) in Items(Required(root, "files", "/source"), "/source/files"))
        {
            if (value.ValueKind != JsonValueKind.Object) Fail(field, "必须为对象。");
            var failed = value.TryGetProperty("failure", out var failure);
            Object(value, field, failed ? ["root", "path", "maximum_bytes", "failure", "diagnostic"] : ["root", "path", "maximum_bytes", "bytes_hash", "length"]);
            var maximum = Int64(Required(value, "maximum_bytes", field), field + "/maximum_bytes", 0);
            C.ContentIoResult<ImmutableArray<byte>> result;
            if (failed) result = new C.ContentIoResult<ImmutableArray<byte>>.Failure(IoFailure(failure, field), Text(Required(value, "diagnostic", field), field + "/diagnostic"));
            else
            {
                var hash = ContentCapture.ReadHash(Required(value, "bytes_hash", field), field + "/bytes_hash");
                var length = Int32(Required(value, "length", field), field + "/length", 0);
                if (!blobs.TryGetValue(hash, out var bytes) || bytes.IsDefault || bytes.Length != length || length > maximum)
                    ContentCapture.Inconsistent(field, "文件字节缺失或长度与读取上限不一致。");
                result = new C.ContentIoResult<ImmutableArray<byte>>.Success(bytes);
            }
            if (!files.TryAdd((ReadPath(value, field), maximum), result)) ContentCapture.Inconsistent(field, "文件请求重复。");
        }
        var inspections = new Dictionary<(C.AssetKind Kind, C.AssetRevision Revision), C.AssetInspection>();
        foreach (var (value, field) in Items(Required(root, "inspections", "/source"), "/source/inspections"))
        {
            if (value.ValueKind != JsonValueKind.Object) Fail(field, "必须为对象。");
            var failed = value.TryGetProperty("failure", out var failure);
            Object(value, field, failed ? ["kind", "revision", "failure", "diagnostic"] : ["kind", "revision", "metadata_kind", "metadata"]);
            var kind = (C.AssetKind)Int32(Required(value, "kind", field), field + "/kind", 0, 1);
            var revision = new C.AssetRevision(ContentCapture.ReadHash(Required(value, "revision", field), field + "/revision"));
            C.AssetInspection result = failed
                ? new C.AssetInspection.Failure((C.AssetInspectionFailure)Int32(failure, field + "/failure", 0, 1), Text(Required(value, "diagnostic", field), field + "/diagnostic"))
                : new C.AssetInspection.Success(ReadMetadata(Required(value, "metadata", field), Text(Required(value, "metadata_kind", field), field + "/metadata_kind"), field + "/metadata"));
            if (!inspections.TryAdd((kind, revision), result)) ContentCapture.Inconsistent(field, "资产检查请求重复。");
        }
        return new(installation, directories.ToFrozenDictionary(), files.ToFrozenDictionary(), inspections.ToFrozenDictionary());
    }

    private static C.ContentPath ReadPath(JsonElement value, string field) => new(
        new(Text(Required(value, "root", field), field + "/root")), Text(Required(value, "path", field), field + "/path"));
    private static C.ContentIoFailure IoFailure(JsonElement value, string field) => (C.ContentIoFailure)Int32(value, field + "/failure", 0, 3);

    private static C.AssetMetadata ReadMetadata(JsonElement value, string kind, string field)
    {
        try
        {
            if (kind == "png")
            {
                Exact(value, field, "Width", "Height");
                return JsonSerializer.Deserialize<C.AssetMetadata.Png>(value)!;
            }
            if (kind != "glb") ContentCapture.Inconsistent(field, "资产元数据种类无效。");
            Exact(value, field, "MeshNodeCount", "Surfaces", "Bounds", "HasSkeleton", "HasMorphTargets", "HasAnimation", "HasCamera", "HasLight", "HasExternalImages");
            Array(Required(value, "Surfaces", field), field + "/Surfaces");
            var bounds = Required(value, "Bounds", field);
            Exact(bounds, field + "/Bounds", "MinimumMeters", "MaximumMeters");
            Exact(Required(bounds, "MinimumMeters", field), field + "/Bounds/MinimumMeters", "X", "Y", "Z");
            Exact(Required(bounds, "MaximumMeters", field), field + "/Bounds/MaximumMeters", "X", "Y", "Z");
            var metadata = JsonSerializer.Deserialize<C.AssetMetadata.Glb>(value)!;
            if (metadata.Surfaces.IsDefault) ContentCapture.Inconsistent(field, "模型表面事实必须初始化。");
            return metadata;
        }
        catch (JsonException exception) { throw new DefinitionFormatException(field, exception.Message, "inconsistent_capture"); }
    }

    private static void Exact(JsonElement value, string field, params string[] keys)
    {
        Object(value, field, keys);
        foreach (var key in keys) Required(value, key, field);
    }
}
