using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text;
using Train.Content.Designs;
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

    internal ContentSourceSnapshot WithUserBlueprint(C.BlueprintId id, C.BlueprintRecord? blueprint)
    {
        var key = "$user-library";
        while (key == Installation.BuiltIn.Key || key == Installation.Mods.Key) key += "$";
        var root = Installation.UserLibrary ?? new C.ContentRoot(key);
        var path = new C.ContentPath(root, id.Value + ".blueprint.json");
        var directory = new C.ContentPath(root, "");
        var nextDirectories = directories.ToDictionary();
        var entries = directories.GetValueOrDefault(directory) is C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Success found
            ? found.Value.Where(e => e.Name != path.RelativePath).ToImmutableArray() : [];
        if (blueprint is not null) entries = entries.Add(new(path.RelativePath, C.ContentEntryKind.File));
        nextDirectories[directory] = new C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Success(entries);
        var nextFiles = files.Where(f => f.Key.Path != path).ToDictionary();
        if (blueprint is not null)
            nextFiles[(path, ContentLimits.JsonFileMaxBytes)] = new C.ContentIoResult<ImmutableArray<byte>>.Success(
                Encoding.UTF8.GetBytes(PlayerBlueprint.Encode(blueprint)).ToImmutableArray());
        return new(Installation with { UserLibrary = root }, nextDirectories.ToFrozenDictionary(), nextFiles.ToFrozenDictionary(), inspections);
    }
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
        writer.WriteString("user_library", Installation.UserLibrary?.Key);
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
                WriteFailure(writer, failure.Kind, failure.Diagnostic);
            else
            {
                writer.WriteStartArray("entries");
                foreach (var child in ((C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Success)entry.Value).Value
                    .OrderBy(child => child.Name, StringComparer.Ordinal).ThenBy(child => DesignEvaluator.Wire(child.Kind), StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", child.Name);
                    writer.WriteString("kind", DesignEvaluator.Wire(child.Kind));
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
                WriteFailure(writer, failure.Kind, failure.Diagnostic);
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
        foreach (var entry in inspections.OrderBy(e => DesignEvaluator.Wire(e.Key.Kind), StringComparer.Ordinal)
                     .ThenBy(e => e.Key.Revision.Value, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", DesignEvaluator.Wire(entry.Key.Kind));
            writer.WriteString("revision", entry.Key.Revision.Value);
            if (entry.Value is C.AssetInspection.Failure failure) WriteFailure(writer, failure.Kind, failure.Diagnostic);
            else
            {
                var metadata = ((C.AssetInspection.Success)entry.Value).Metadata;
                writer.WriteString("metadata_kind", metadata is C.AssetMetadata.Png ? "png" : "glb");
                writer.WritePropertyName("metadata");
                WriteMetadata(writer, metadata);
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

    private static void WriteFailure(Utf8JsonWriter writer, Enum kind, string diagnostic)
    {
        writer.WriteString("failure", DesignEvaluator.Wire(kind));
        writer.WriteString("diagnostic", diagnostic);
    }

    internal static ContentSourceSnapshot Read(JsonElement root, IReadOnlyDictionary<string, ImmutableArray<byte>> blobs)
    {
        Object(root, "/source", "installation", "directories", "files", "inspections");
        var install = Required(root, "installation", "/source");
        Object(install, "/source/installation", "built_in", "mods", "user_library", "game_version", "enabled_mods");
        var enabled = Items(Required(install, "enabled_mods", "/source/installation"), "/source/installation/enabled_mods")
            .Select(item => new C.PackageId(Text(item.Value, item.Field))).ToImmutableArray();
        Unique(enabled, "/source/installation/enabled_mods");
        var installation = new C.ContentInstallation(new(Text(Required(install, "built_in", "/source/installation"), "/source/installation/built_in")),
            new(Text(Required(install, "mods", "/source/installation"), "/source/installation/mods")),
            Required(install, "user_library", "/source/installation").ValueKind == JsonValueKind.Null ? null :
                new C.ContentRoot(Text(install.GetProperty("user_library"), "/source/installation/user_library")),
            Text(Required(install, "game_version", "/source/installation"), "/source/installation/game_version"), enabled);
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
                        EnumValue<C.ContentEntryKind>(Required(item.Value, "kind", item.Field), item.Field + "/kind"));
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
            var kind = EnumValue<C.AssetKind>(Required(value, "kind", field), field + "/kind");
            var revision = new C.AssetRevision(ContentCapture.ReadHash(Required(value, "revision", field), field + "/revision"));
            C.AssetInspection result = failed
                ? new C.AssetInspection.Failure(EnumValue<C.AssetInspectionFailure>(failure, field + "/failure"), Text(Required(value, "diagnostic", field), field + "/diagnostic"))
                : new C.AssetInspection.Success(ReadMetadata(Required(value, "metadata", field), Text(Required(value, "metadata_kind", field), field + "/metadata_kind"), field + "/metadata"));
            if (!inspections.TryAdd((kind, revision), result)) ContentCapture.Inconsistent(field, "资产检查请求重复。");
        }
        return new(installation, directories.ToFrozenDictionary(), files.ToFrozenDictionary(), inspections.ToFrozenDictionary());
    }

    private static C.ContentPath ReadPath(JsonElement value, string field) => new(
        new(Text(Required(value, "root", field), field + "/root")), Text(Required(value, "path", field), field + "/path"));
    private static C.ContentIoFailure IoFailure(JsonElement value, string field) => EnumValue<C.ContentIoFailure>(value, field + "/failure");

    private static void WriteMetadata(Utf8JsonWriter writer, C.AssetMetadata metadata)
    {
        writer.WriteStartObject();
        if (metadata is C.AssetMetadata.Png png)
        {
            writer.WriteNumber("width", png.Width);
            writer.WriteNumber("height", png.Height);
        }
        else
        {
            var glb = (C.AssetMetadata.Glb)metadata;
            writer.WriteNumber("mesh_node_count", glb.MeshNodeCount);
            writer.WriteStartArray("surfaces");
            foreach (var surface in glb.Surfaces) writer.WriteStringValue(DesignEvaluator.Wire(surface));
            writer.WriteEndArray();
            writer.WriteStartObject("bounds");
            WritePoint(writer, "minimum_meters", glb.Bounds.MinimumMeters);
            WritePoint(writer, "maximum_meters", glb.Bounds.MaximumMeters);
            writer.WriteEndObject();
            writer.WriteBoolean("has_skeleton", glb.HasSkeleton);
            writer.WriteBoolean("has_morph_targets", glb.HasMorphTargets);
            writer.WriteBoolean("has_animation", glb.HasAnimation);
            writer.WriteBoolean("has_camera", glb.HasCamera);
            writer.WriteBoolean("has_light", glb.HasLight);
            writer.WriteBoolean("has_external_images", glb.HasExternalImages);
        }
        writer.WriteEndObject();
    }

    private static void WritePoint(Utf8JsonWriter writer, string key, C.LocalPoint point)
    {
        writer.WriteStartObject(key);
        WriteCoordinate("x", point.X);
        WriteCoordinate("y", point.Y);
        WriteCoordinate("z", point.Z);
        writer.WriteEndObject();

        void WriteCoordinate(string axis, double value)
        {
            if (double.IsFinite(value)) writer.WriteNumber(axis, value);
            else writer.WriteString(axis, double.IsNaN(value) ? "nan" : value > 0 ? "positive_infinity" : "negative_infinity");
        }
    }

    private static C.AssetMetadata ReadMetadata(JsonElement value, string kind, string field)
    {
        if (kind == "png")
        {
            Object(value, field, "width", "height");
            return new C.AssetMetadata.Png(Int32(Required(value, "width", field), field + "/width"),
                Int32(Required(value, "height", field), field + "/height"));
        }
        if (kind != "glb") ContentCapture.Inconsistent(field, "资产元数据种类无效。");
        Object(value, field, "mesh_node_count", "surfaces", "bounds", "has_skeleton", "has_morph_targets",
            "has_animation", "has_camera", "has_light", "has_external_images");
        var surfaces = Items(Required(value, "surfaces", field), field + "/surfaces")
            .Select(item => EnumValue<C.AssetAlphaMode>(item.Value, item.Field)).ToImmutableArray();
        var boundsField = field + "/bounds";
        var bounds = Required(value, "bounds", field);
        Object(bounds, boundsField, "minimum_meters", "maximum_meters");
        return new C.AssetMetadata.Glb(Int32(Required(value, "mesh_node_count", field), field + "/mesh_node_count"), surfaces,
            new(ReadPoint(Required(bounds, "minimum_meters", boundsField), boundsField + "/minimum_meters"),
                ReadPoint(Required(bounds, "maximum_meters", boundsField), boundsField + "/maximum_meters")),
            ReadFlag("has_skeleton"), ReadFlag("has_morph_targets"), ReadFlag("has_animation"),
            ReadFlag("has_camera"), ReadFlag("has_light"), ReadFlag("has_external_images"));

        bool ReadFlag(string key) => Boolean(Required(value, key, field), field + "/" + key);
    }

    private static C.LocalPoint ReadPoint(JsonElement value, string field)
    {
        Object(value, field, "x", "y", "z");
        return new(ReadCoordinate("x"), ReadCoordinate("y"), ReadCoordinate("z"));

        double ReadCoordinate(string axis)
        {
            var coordinate = Required(value, axis, field);
            var coordinateField = field + "/" + axis;
            if (coordinate.ValueKind == JsonValueKind.Number && coordinate.TryGetDouble(out var number) && double.IsFinite(number))
                return number;
            return Text(coordinate, coordinateField) switch
            {
                "nan" => double.NaN,
                "positive_infinity" => double.PositiveInfinity,
                "negative_infinity" => double.NegativeInfinity,
                _ => throw new DefinitionFormatException(coordinateField, "必须为数值或非有限值事实标记。", "invalid_field")
            };
        }
    }
}
