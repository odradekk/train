using System.Collections.Immutable;
using Train.Content.Designs;
using Train.Content.Loading;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Authoring;

internal static class AuthoringValidation
{
    internal static ImmutableArray<C.ContentDiagnostic> Validate(AuthoringPackage package, ContentView basis, C.AuthoringDefinitionAddress definition)
    {
        var path = DefinitionDraftJson.Path(definition);
        if (!package.Files.TryGetValue(path, out var bytes)) throw new DefinitionFormatException("/id", "定义不存在。", "reference_missing");
        var parser = package.ParsingPackage();
        object? parsed = definition.Kind switch
        {
            C.DefinitionKind.Voxel => new VoxelParser().Parse(bytes.ToArray(), parser, path),
            C.DefinitionKind.Blueprint => new BlueprintParser().Parse(bytes.ToArray(), parser, path),
            C.DefinitionKind.SettlementType => SettlementParser.Type(bytes.ToArray(), parser, path),
            C.DefinitionKind.SettlementTemplate => SettlementParser.Template(bytes.ToArray(), parser, path),
            C.DefinitionKind.LairType => LairParser.Parse(bytes.ToArray(), parser, path),
            _ => throw new ArgumentException("此定义种类没有创作表单。", nameof(definition))
        };
        if (parsed is null) return parser.Diagnostics.ToImmutableArray();
        if (parsed is ParsedVoxel) return [];
        var choices = package.References().Concat(basis.AuthoringReferences().Where(choice =>
            !InitialContentFactory.ReferenceId(choice.Reference).StartsWith(package.Package.Value + ":", StringComparison.Ordinal))).ToArray();
        var known = choices.Select(choice => InitialContentFactory.ReferenceId(choice.Reference)).ToHashSet(StringComparer.Ordinal);
        var view = basis.ForAuthoring(package);
        var references = new WorldLoader.References(view.AuthoringVoxels(parser), view.AuthoringBlueprints(), known);
        switch (parsed)
        {
            case ParsedBlueprint blueprint:
                var evaluation = DesignEvaluator.Evaluate(view, blueprint.Definition, compositeIdentity:
                    blueprint.Definition.Specification is C.DesignSpecification.FunctionalComposite ? new K.CompositeDefinitionId(blueprint.Address.Id.Value) : null);
                var diagnostics = evaluation switch
                {
                    C.DesignEvaluation.Complete complete => complete.Diagnostics,
                    C.DesignEvaluation.Partial partial => partial.Diagnostics,
                    C.DesignEvaluation.Failed failed => failed.Diagnostics,
                    _ => throw new InvalidOperationException()
                };
                foreach (var diagnostic in diagnostics)
                    parser.Reject(diagnostic.Code, path, diagnostic.Field, diagnostic.Message, new C.ContentReference.Blueprint(blueprint.Address), diagnostic.Element);
                break;
            case ParsedSettlementType type:
                foreach (var terrain in type.Core.PreferredTerrain)
                    references.Need(parser, path, new C.ContentReference.Core(new K.CoreDefinitionReference.SettlementType(type.Core.Id)), terrain.Value,
                        choices.Any(choice => choice.Reference == new C.ContentReference.Core(new K.CoreDefinitionReference.Terrain(terrain))), "/simulation/preferred_terrain");
                break;
            case ParsedLair lair: WorldLoader.ResolveLair(parser, lair, references); break;
            case ParsedSettlementTemplate template:
                var processes = basis.AuthoringProcesses(package.Package).ToDictionary(process => process.Id, StringComparer.Ordinal);
                foreach (var (file, body) in package.Files.Where(file => ContentView.DefinitionFile(file.Key, "processes", "process")))
                    if (ProcessParser.Parse(body.ToArray(), package.ParsingPackage(), file) is { } process) processes[process.Id] = process;
                WorldLoader.ResolveTemplate(parser, template, references, choices.Any(choice => choice.Reference ==
                    new C.ContentReference.Core(new K.CoreDefinitionReference.SettlementType(template.Type))), processes);
                break;
        }
        return parser.Diagnostics.ToImmutableArray();
    }
}
