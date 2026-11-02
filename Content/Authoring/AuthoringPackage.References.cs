using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Train.Content.Loading;
using static Train.Content.Loading.DefinitionJson;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Authoring;

internal sealed partial class AuthoringPackage
{
    internal LoadPackage ParsingPackage() => new(_package.Root, "", _package.BuiltIn) { Metadata = Metadata };
    internal ImmutableDictionary<string, ImmutableArray<byte>> Files => _files;

    internal AuthoringPackage Overlay(ImmutableArray<C.PackageEdit> edits)
    {
        var files = _files.ToBuilder();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits)
        {
            ImmutableArray<C.PackageFileChange> changes;
            if (edit is C.PackageEdit.WriteDefinition definition)
            {
                var path = DefinitionDraftJson.Path(DefinitionDraftJson.Address(definition.Draft));
                var node = DefinitionDraftJson.Write(definition.Draft, incomplete: true);
                if (definition.Draft is C.DefinitionDraft.SettlementTemplate && node["production_plans"] is JsonArray plans)
                    foreach (var plan in plans.OfType<JsonObject>()) plan.Remove("process_kind");
                CheckFinite(node, "");
                changes = [new(path, Encode(node).ToImmutableArray())];
            }
            else changes = Prepare([edit]) switch
            {
                C.ContentResult<C.PackageWritePlan>.Success plan => plan.Value.Changes,
                C.ContentResult<C.PackageWritePlan>.Failure failure => throw new DefinitionFormatException(failure.Diagnostics[0].Field, failure.Diagnostics[0].Message, failure.Diagnostics[0].Code),
                _ => throw new InvalidOperationException("无法准备创作修改。")
            };
            foreach (var change in changes)
            {
                if (!paths.Add(change.RelativePath)) Fail("/edits", "多个编辑指向同一个文件。");
                if (change.Bytes is { } bytes) files[change.RelativePath] = bytes;
                else files.Remove(change.RelativePath);
            }
        }
        var parser = ParsingPackage();
        if (files.TryGetValue("mod.json", out var manifest)) PackageLoader.ParseManifest(manifest.ToArray(), parser, "mod.json");
        if (parser.Diagnostics.Count > 0) throw new DefinitionFormatException(parser.Diagnostics[0].Field, parser.Diagnostics[0].Message, parser.Diagnostics[0].Code);
        return new(parser, SourceVersion, files.ToImmutable());
    }

    private static void CheckFinite(JsonNode? node, string field)
    {
        if (node is JsonObject obj)
            foreach (var item in obj) CheckFinite(item.Value, field + "/" + item.Key);
        else if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++) CheckFinite(array[index], field + "/" + index);
        else if (node is JsonValue value && value.TryGetValue<double>(out var number) && !double.IsFinite(number))
            Fail(field, "必须为有限数值。");
    }

    internal ImmutableArray<C.AuthoringReferenceChoice> References()
    {
        var result = ImmutableArray.CreateBuilder<C.AuthoringReferenceChoice>();
        foreach (var (path, bytes) in _files.OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            var parts = path.Split('/');
            if (parts.Length != 3 || parts[0] != "resources") continue;
            var wire = parts[1] switch { "voxels" => "voxel", "blueprints" => "blueprint", "terrain-types" => "terrain-type", "settlement-types" => "settlement-type", "processes" => "process", _ => null };
            if (wire is null || !parts[2].EndsWith("." + wire + ".json", StringComparison.Ordinal)) continue;
            using var document = Parse(bytes, path);
            var root = document.RootElement;
            var json = new DefinitionJson(root, ParsingPackage(), path, wire, wire == "terrain-type" ? 2 : wire == "process" ? 1 : 3);
            var display = Required(root, "display", "");
            var text = new C.DisplayText(Text(Required(display, "name", "/display"), "/display/name"), Text(Required(display, "description", "/display"), "/display/description"));
            C.AuthoringReferenceKind? kind = wire switch
            {
                "voxel" => root.TryGetProperty("simulation", out _) ? C.AuthoringReferenceKind.InventoryVoxel : null,
                "terrain-type" => C.AuthoringReferenceKind.TerrainType,
                "settlement-type" => C.AuthoringReferenceKind.SettlementType,
                "blueprint" => Text(Required(root, "category", ""), "/category") switch
                {
                    "building" => C.AuthoringReferenceKind.Building, "scene_model" => C.AuthoringReferenceKind.SceneModel,
                    "creature" => C.AuthoringReferenceKind.Creature, "large_equipment" => C.AuthoringReferenceKind.InventoryBlueprint, _ => null
                },
                "process" => Text(Required(Required(root, "simulation", ""), "kind", "/simulation"), "/simulation/kind") switch
                {
                    "production" or "extraction" => C.AuthoringReferenceKind.ProductionProcess, "recovery" => C.AuthoringReferenceKind.RecoveryProcess,
                    _ => throw new DefinitionFormatException("/simulation/kind", "未知流程种类。", "invalid_field")
                },
                _ => null
            };
            if (kind is { } found) result.Add(Choice(found, json.Id, text));
        }
        return result.ToImmutable();
    }

    internal static C.AuthoringReferenceChoice Choice(C.AuthoringReferenceKind kind, string id, C.DisplayText display)
    {
        C.ContentReference reference = kind switch
        {
            C.AuthoringReferenceKind.InventoryVoxel => new C.ContentReference.Core(new K.CoreDefinitionReference.Voxel(new(id))),
            C.AuthoringReferenceKind.TerrainType => new C.ContentReference.Core(new K.CoreDefinitionReference.Terrain(new(id))),
            C.AuthoringReferenceKind.SettlementType => new C.ContentReference.Core(new K.CoreDefinitionReference.SettlementType(new(id))),
            C.AuthoringReferenceKind.Creature => new C.ContentReference.Core(new K.CoreDefinitionReference.Creature(new(id))),
            C.AuthoringReferenceKind.ProductionProcess => new C.ContentReference.Core(new K.CoreDefinitionReference.Process(new K.ProcessRef.Production(new(id)))),
            C.AuthoringReferenceKind.RecoveryProcess => new C.ContentReference.Core(new K.CoreDefinitionReference.Process(new K.ProcessRef.Recovery(new(id)))),
            _ => new C.ContentReference.Blueprint(new(C.BlueprintLibrary.Packages, new(id)))
        };
        return new(kind, reference, display);
    }
}
