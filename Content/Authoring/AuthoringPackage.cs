using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Train.Content.Loading;
using static Train.Content.Loading.DefinitionJson;
using C = Train.Content.Contracts;

namespace Train.Content.Authoring;

internal sealed class AuthoringPackage : C.IAuthoringPackage
{
    private static readonly JsonSerializerOptions FileJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly ImmutableDictionary<string, ImmutableArray<byte>> _files;
    private readonly LoadPackage _package;
    public C.PackageId Package => Metadata.Id;
    public C.PackageSourceVersion SourceVersion { get; }
    public C.PackageMetadata Metadata => _package.Metadata!;
    public ImmutableArray<C.AuthoringDefinitionAddress> EditableDefinitions { get; }

    private AuthoringPackage(LoadPackage package, C.PackageSourceVersion sourceVersion,
        ImmutableDictionary<string, ImmutableArray<byte>> files)
    {
        _package = package;
        SourceVersion = sourceVersion;
        _files = files;
        EditableDefinitions = files.Keys.Select(Address).OfType<C.AuthoringDefinitionAddress>()
            .OrderBy(a => DefinitionDraftJson.Path(a), StringComparer.Ordinal).ToImmutableArray();
    }

    internal static C.ContentResult<C.IAuthoringPackage> Open(C.ContentRoot root, C.PackageId id,
        C.PackageSourceVersion sourceVersion, C.IContentFiles source)
    {
        if (id.Value != "core" && (!PackageIdentity.TryCanonicalUuid(id.Value, out var canonical) || canonical != id.Value))
            Fail("/package", "包 ID 必须为 core 或规范 UUID。");
        var package = new LoadPackage(root, "", id.Value == "core")
        {
            Metadata = new(id, id.Value, "0.0.0", [], [])
        };
        var files = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        void Scan(string directory)
        {
            var result = source.Enumerate(new(root, directory));
            if (result is C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Failure failure)
            {
                package.Reject(ContentDocument.IoCode(failure.Kind), directory, "", failure.Diagnostic);
                return;
            }
            foreach (var entry in ((C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Success)result).Value.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (directory.Length == 0 && entry.Name != "resources" && !string.Equals(entry.Name, "mod.json", StringComparison.OrdinalIgnoreCase)) continue;
                var path = directory.Length == 0 ? entry.Name : directory + "/" + entry.Name;
                if (!ContentDocument.SafeEntry(entry.Name) || entry.Kind == C.ContentEntryKind.Link)
                { package.Reject("linked_path", path, "", "源包包含无效路径或链接。"); continue; }
                if (directory.Length == 0 && (entry.Name == "resources" ? entry.Kind != C.ContentEntryKind.Directory : entry.Kind != C.ContentEntryKind.File))
                { package.Reject("invalid_field", path, "", "resources 必须是目录，manifest 必须是文件。"); continue; }
                if (entry.Kind == C.ContentEntryKind.Directory) { Scan(path); continue; }
                var read = source.Read(new(root, path), path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ContentLimits.JsonFileMaxBytes : AssetLoader.MaximumBytes);
                if (read is C.ContentIoResult<ImmutableArray<byte>>.Failure readFailure)
                    package.Reject(ContentDocument.IoCode(readFailure.Kind), path, "", readFailure.Diagnostic);
                else files.Add(directory.Length == 0 ? "mod.json" : path, ((C.ContentIoResult<ImmutableArray<byte>>.Success)read).Value);
            }
        }
        Scan("");
        if (files.TryGetValue("mod.json", out var manifest))
        {
            PackageLoader.ParseManifest(manifest.ToArray(), package, "mod.json");
            if (package.Metadata?.Id != id) package.Reject("invalid_field", "mod.json", "/id", "manifest 包 ID 与目标包不一致。");
        }
        else if (id.Value != "core") package.Reject("file_missing", "mod.json", "", "Mod 缺少 manifest。");
        return package.Diagnostics.Count > 0
            ? new C.ContentResult<C.IAuthoringPackage>.Failure(package.Diagnostics.ToImmutableArray())
            : new C.ContentResult<C.IAuthoringPackage>.Success(new AuthoringPackage(package, sourceVersion, files.ToImmutable()));
    }

    public C.ContentLookup<C.DefinitionDraft> Read(C.AuthoringDefinitionAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        try
        {
            var path = DefinitionDraftJson.Path(address);
            if (!_files.TryGetValue(path, out var bytes)) return new C.ContentLookup<C.DefinitionDraft>.NotFound();
            using var document = Parse(bytes, path);
            var draft = DefinitionDraftJson.Read(document.RootElement, address.Kind, _package, IsComposite, IsRecovery);
            if (DefinitionDraftJson.Address(draft) != address) Fail("/id", "定义 ID 与文件名不一致。");
            return new C.ContentLookup<C.DefinitionDraft>.Found(draft);
        }
        catch (DefinitionFormatException exception)
        {
            return new C.ContentLookup<C.DefinitionDraft>.Missing([Diagnostic(exception, address)]);
        }
    }

    private JsonDocument Parse(ImmutableArray<byte> bytes, string path)
    {
        var diagnostics = new LoadPackage(_package.Root, "", _package.BuiltIn) { Metadata = Metadata };
        var document = ContentDocument.Parse(bytes.ToArray(), diagnostics, path);
        if (document is null)
        {
            var error = diagnostics.Diagnostics[0];
            throw new DefinitionFormatException(error.Field, error.Message, error.Code);
        }
        return document;
    }

    private bool IsComposite(string reference)
    {
        var prefix = Package.Value + ":";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var lookup = Read(new(C.DefinitionKind.Blueprint, reference[prefix.Length..]));
        return lookup is C.ContentLookup<C.DefinitionDraft>.Found { Value: C.DefinitionDraft.Blueprint { Value.Specification: C.DesignSpecification.FunctionalComposite } };
    }

    private bool IsRecovery(string reference)
    {
        var prefix = Package.Value + ":";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var path = "resources/processes/" + reference[prefix.Length..] + ".process.json";
        if (!_files.TryGetValue(path, out var bytes)) return false;
        using var document = Parse(bytes, path);
        var simulation = Required(document.RootElement, "simulation", "");
        return Text(Required(simulation, "kind", "/simulation"), "/simulation/kind") == "recovery";
    }

    internal C.ContentResult<C.PackageWritePlan> Prepare(ImmutableArray<C.PackageEdit> edits)
    {
        if (edits.IsDefault || edits.Any(edit => edit is null)) throw new ArgumentException("编辑数组必须初始化且不能含空项。", nameof(edits));
        var changes = new Dictionary<string, ImmutableArray<byte>?>(StringComparer.OrdinalIgnoreCase);
        void Change(string path, ImmutableArray<byte>? bytes)
        {
            CheckPath(path);
            if (!changes.TryAdd(path, bytes)) Fail("/edits", "多个编辑指向同一个文件：" + path);
        }
        foreach (var edit in edits)
        {
            switch (edit)
            {
                case C.PackageEdit.WriteDefinition write:
                    var address = DefinitionDraftJson.Address(write.Draft);
                    var path = DefinitionDraftJson.Path(address);
                    JsonNode? display = null;
                    if (address.Kind == C.DefinitionKind.SettlementTemplate && _files.TryGetValue(path, out var original))
                    {
                        using var document = Parse(original, path);
                        display = JsonNode.Parse(Required(document.RootElement, "display", "").GetRawText());
                    }
                    try
                    {
                        var bytes = Encode(DefinitionDraftJson.Write(write.Draft, display));
                        using (var document = JsonDocument.Parse(bytes))
                            DefinitionDraftJson.Read(document.RootElement, address.Kind, isComposite: IsComposite);
                        Change(path, bytes.ToImmutableArray());
                    }
                    catch (ArgumentException exception) { Fail("/value", exception.Message); }
                    break;
                case C.PackageEdit.DeleteDefinition delete:
                    var definitionPath = DefinitionDraftJson.Path(delete.Definition);
                    if (!_files.ContainsKey(definitionPath)) Fail("/id", "要删除的定义不存在。", "reference_missing");
                    Change(definitionPath, null);
                    var directory = definitionPath[..definitionPath.LastIndexOf('/')] + "/" + delete.Definition.LocalId + "/";
                    foreach (var owned in _files.Keys.Where(file => file.StartsWith(directory, StringComparison.Ordinal)).Order(StringComparer.Ordinal)) Change(owned, null);
                    if (_files.ContainsKey(definitionPath + ".import")) Change(definitionPath + ".import", null);
                    break;
                case C.PackageEdit.WriteAsset asset:
                    if (asset.Bytes.IsDefault) Fail("/bytes", "资产字节必须初始化。");
                    if (string.Equals(asset.PackageRelativePath, "mod.json", StringComparison.OrdinalIgnoreCase)) Fail("/path", "manifest 必须通过 Manifest 编辑。");
                    Change(asset.PackageRelativePath, asset.Bytes);
                    break;
                case C.PackageEdit.DeleteAsset asset:
                    if (string.Equals(asset.PackageRelativePath, "mod.json", StringComparison.OrdinalIgnoreCase)) Fail("/path", "不能通过资产编辑删除 manifest。");
                    Change(asset.PackageRelativePath, null);
                    break;
                case C.PackageEdit.Manifest manifest:
                    if (!_files.TryGetValue("mod.json", out var source)) Fail("/manifest", "没有 manifest 的 core 不能编辑 manifest。");
                    if (manifest.Value.Id != Package) Fail("/id", "不能通过 manifest 修改包 ID。");
                    using (var document = Parse(source, "mod.json"))
                    {
                        var root = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
                        root["name"] = manifest.Value.Name;
                        root["version"] = manifest.Value.Version;
                        root["authors"] = new JsonArray(manifest.Value.Authors.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
                        var originalDependencies = root["dependencies"]?.AsArray().ToDictionary(d => d!["id"]!.GetValue<string>(), StringComparer.Ordinal);
                        var implicitCore = _package.ImplicitCoreDependency ? Metadata.Dependencies.Single(d => d.Package.Value == "core") : null;
                        var dependencies = new JsonArray(manifest.Value.Dependencies.Where(d => d != implicitCore).Select(d =>
                        {
                            var dependency = originalDependencies is not null && originalDependencies.TryGetValue(d.Package.Value, out var originalDependency)
                                ? originalDependency!.DeepClone().AsObject() : new JsonObject { ["id"] = d.Package.Value };
                            dependency["min_version"] = d.MinimumVersion;
                            return (JsonNode?)dependency;
                        }).ToArray());
                        if (root.ContainsKey("dependencies") || dependencies.Count > 0) root["dependencies"] = dependencies;
                        var bytes = Encode(root);
                        var parsed = new LoadPackage(_package.Root, "", false);
                        PackageLoader.ParseManifest(bytes, parsed, "mod.json");
                        if (parsed.Diagnostics.Count > 0) return new C.ContentResult<C.PackageWritePlan>.Failure(parsed.Diagnostics.ToImmutableArray());
                        Change("mod.json", bytes.ToImmutableArray());
                    }
                    break;
                default: Fail("/edits", "未知包编辑。"); break;
            }
        }
        return new C.ContentResult<C.PackageWritePlan>.Success(new(Package, SourceVersion,
            changes.Where(c => c.Value is { } bytes ? !_files.TryGetValue(c.Key, out var original) || !bytes.AsSpan().SequenceEqual(original.AsSpan()) : _files.ContainsKey(c.Key))
                .OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new C.PackageFileChange(c.Key, c.Value)).ToImmutableArray(), []));
    }

    private static byte[] Encode(JsonObject root) => Encoding.UTF8.GetBytes(root.ToJsonString(FileJson) + "\n");

    private static void CheckPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Split('/').Any(part => !ContentDocument.SafeEntry(part)))
            Fail("/path", "路径必须为包根内的相对文件路径。");
    }

    private C.ContentDiagnostic Diagnostic(DefinitionFormatException exception, C.AuthoringDefinitionAddress address) => new(
        exception.Code, C.ContentDiagnosticSeverity.Error, "authoring", null, new(Package,
            EditableDefinitions.Contains(address) ? DefinitionDraftJson.Path(address) : ""), exception.Field, null, exception.Message);

    private static C.AuthoringDefinitionAddress? Address(string path)
    {
        foreach (var kind in new[] { C.DefinitionKind.Voxel, C.DefinitionKind.Blueprint, C.DefinitionKind.SettlementType, C.DefinitionKind.SettlementTemplate })
        {
            var name = DefinitionDraftJson.Kind(kind);
            var prefix = "resources/" + name + "s/";
            var suffix = "." + name + ".json";
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var id = path[prefix.Length..^suffix.Length];
            if (ContentIdentity.IsValidPart(id) && id != "shared") return new(kind, id);
        }
        return null;
    }
}
