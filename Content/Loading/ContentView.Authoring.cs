using System.Collections.Immutable;
using System.Collections.Frozen;
using Train.Content.Authoring;
using Train.Content.Designs;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal sealed partial class ContentView
{
    private ContentView(ContentView basis, AuthoringPackage package) : this(basis)
    {
        var prefix = package.Package.Value + ":";
        var voxels = _voxels.Where(pair => !pair.Key.Value.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary();
        foreach (var (path, bytes) in package.Files.Where(file => DefinitionFile(file.Key, "voxels", "voxel")))
        {
            var parsed = new VoxelParser().Parse(bytes.ToArray(), package.ParsingPackage(), path);
            if (parsed is null) continue;
            // 求值只消费形状和模拟资料；资产解码仍由写入前的普通加载完成。
            var revision = new C.AssetRevision("");
            C.VoxelPresentation presentation = parsed.Presentation switch
            {
                ParsedPresentation.Cube cube => new C.VoxelPresentation.Cube(cube.Shapes, cube.Material, cube.TileSpan, revision, revision, revision, revision),
                ParsedPresentation.Mesh mesh => new C.VoxelPresentation.Mesh(revision, mesh.SurfaceMaterials),
                _ => throw new InvalidOperationException()
            };
            voxels[parsed.Definition] = new(parsed, new(parsed.Definition, parsed.Orientations, parsed.Collision, presentation, parsed.Behaviors), new(parsed.Definition, parsed.Display, revision));
        }
        _voxels = voxels.ToFrozenDictionary();
        var drafts = package.Files.Where(file => DefinitionFile(file.Key, "blueprints", "blueprint"))
            .Select(file => new BlueprintParser().Parse(file.Value.ToArray(), package.ParsingPackage(), file.Key)).OfType<ParsedBlueprint>().ToArray();
        var previous = basis._blueprints.Values.Where(blueprint => blueprint.Address.Library == C.BlueprintLibrary.Packages
            && blueprint.Address.Id.Value.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(blueprint => blueprint.Address);
        var unchanged = _voxels.Count == basis._voxels.Count && _voxels.All(pair => basis._voxels.TryGetValue(pair.Key, out var old)
            && pair.Value.Definition.Revision == old.Definition.Revision && SameShapes(pair.Value.Appearance.Presentation, old.Appearance.Presentation))
            && drafts.Length == previous.Count && drafts.All(draft => previous.TryGetValue(draft.Address, out var old)
                && old.Evaluation is C.DesignEvaluation.Complete complete && complete.Design == DesignEvaluator.Identity(draft.Definition));
        if (unchanged) return;
        var records = _blueprints.Where(pair => !pair.Key.Id.Value.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary();
        _blueprints = records.ToFrozenDictionary();
        foreach (var draft in drafts.OrderBy(draft => draft.Definition.Specification is C.DesignSpecification.FunctionalComposite ? 0 : 1))
        {
            var evaluation = DesignEvaluator.Evaluate(this, draft);
            if (evaluation is not C.DesignEvaluation.Complete { IsValid: true } complete) continue;
            records[draft.Address] = new(draft.Address, complete.Definition, [], complete);
            _blueprints = records.ToFrozenDictionary();
        }
    }

    internal ContentView ForAuthoring(AuthoringPackage package) => new(this, package);
    internal Dictionary<K.VoxelDefinitionId, (LoadPackage Package, LoadedVoxel Voxel)> AuthoringVoxels(LoadPackage package) =>
        _voxels.ToDictionary(pair => pair.Key, pair => (package, pair.Value));
    internal Dictionary<string, C.BlueprintRecord> AuthoringBlueprints() => _blueprints.Values
        .Where(blueprint => blueprint.Address.Library == C.BlueprintLibrary.Packages).ToDictionary(blueprint => blueprint.Address.Id.Value, StringComparer.Ordinal);
    internal IEnumerable<ParsedProcess> AuthoringProcesses(C.PackageId replaced) => _world.Processes.Where(process => !process.Id.StartsWith(replaced.Value + ":", StringComparison.Ordinal));

    internal static bool DefinitionFile(string path, string directory, string kind) => path.StartsWith("resources/" + directory + "/", StringComparison.Ordinal)
        && path.Count(character => character == '/') == 2 && path.EndsWith("." + kind + ".json", StringComparison.Ordinal);
    private static bool SameShapes(C.VoxelPresentation first, C.VoxelPresentation second) => (first, second) switch
    {
        (C.VoxelPresentation.Cube a, C.VoxelPresentation.Cube b) => a.Shapes.SequenceEqual(b.Shapes),
        (C.VoxelPresentation.Mesh, C.VoxelPresentation.Mesh) => true,
        _ => false
    };

    internal IEnumerable<C.AuthoringReferenceChoice> AuthoringReferences()
    {
        foreach (var voxel in _voxels.Values.Where(voxel => voxel.Definition.Good is not null))
            yield return AuthoringPackage.Choice(C.AuthoringReferenceKind.InventoryVoxel, voxel.Definition.Definition.Value, voxel.Definition.Display);
        foreach (var blueprint in _blueprints.Values.Where(blueprint => blueprint.Address.Library == C.BlueprintLibrary.Packages))
        {
            C.AuthoringReferenceKind? kind = blueprint.Definition.Specification switch
            {
                C.DesignSpecification.Building => C.AuthoringReferenceKind.Building,
                C.DesignSpecification.SceneModel => C.AuthoringReferenceKind.SceneModel,
                C.DesignSpecification.Creature => C.AuthoringReferenceKind.Creature,
                C.DesignSpecification.Assembly or C.DesignSpecification.FunctionalComposite => C.AuthoringReferenceKind.InventoryBlueprint,
                _ => null
            };
            if (kind is { } value) yield return AuthoringPackage.Choice(value, blueprint.Address.Id.Value, blueprint.Definition.Display);
        }
        foreach (var terrain in _world.Terrains) yield return AuthoringPackage.Choice(C.AuthoringReferenceKind.TerrainType, terrain.Id.Value, terrain.Display);
        foreach (var type in _world.Types) yield return AuthoringPackage.Choice(C.AuthoringReferenceKind.SettlementType, type.Source.Core.Id.Value, type.Source.Display);
        foreach (var process in _world.Processes)
            yield return AuthoringPackage.Choice(process.Reference is K.ProcessRef.Recovery ? C.AuthoringReferenceKind.RecoveryProcess : C.AuthoringReferenceKind.ProductionProcess, process.Id, process.Display);
    }
}
