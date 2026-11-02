using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Train.Content.Loading;
using static Train.Content.Loading.DefinitionJson;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Authoring;

internal sealed partial class AuthoringPackage
{
    internal LoadPackage ParsingPackage() => new(_package.Root, "", _package.BuiltIn) { Metadata = Metadata, ImplicitCoreDependency = _package.ImplicitCoreDependency };
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

    internal ImmutableArray<C.AuthoringReferenceChoice> References(ContentView basis)
    {
        var result = basis.ForAuthoring(this).AuthoringReferences()
            .Where(choice => choice.Kind is C.AuthoringReferenceKind.InventoryVoxel or C.AuthoringReferenceKind.Building
                or C.AuthoringReferenceKind.SceneModel or C.AuthoringReferenceKind.Creature or C.AuthoringReferenceKind.InventoryBlueprint)
            .Where(choice => InitialContentFactory.ReferenceId(choice.Reference).StartsWith(Package.Value + ":", StringComparison.Ordinal))
            .ToImmutableArray().ToBuilder();
        foreach (var (path, bytes) in _files)
        {
            if (ContentView.DefinitionFile(path, "terrain-types", "terrain-type") && TerrainParser.Parse(bytes.ToArray(), ParsingPackage(), path) is { } terrain)
                result.Add(Choice(C.AuthoringReferenceKind.TerrainType, terrain.Id.Value, terrain.Display));
            else if (ContentView.DefinitionFile(path, "settlement-types", "settlement-type") && SettlementParser.Type(bytes.ToArray(), ParsingPackage(), path) is { } type)
                result.Add(Choice(C.AuthoringReferenceKind.SettlementType, type.Core.Id.Value, type.Display));
            else if (ContentView.DefinitionFile(path, "processes", "process") && ProcessParser.Parse(bytes.ToArray(), ParsingPackage(), path) is { } process)
                result.Add(Choice(process.Reference is K.ProcessRef.Recovery ? C.AuthoringReferenceKind.RecoveryProcess : C.AuthoringReferenceKind.ProductionProcess, process.Id, process.Display));
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
