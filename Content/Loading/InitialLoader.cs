using System.Collections.Immutable;
using Train.Content.Designs;
using Train.Content.Persistence;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal sealed class InitialLoader
{
    private readonly C.ContentInstallation _installation;
    private readonly RecordingContentSource _source;
    private readonly AssetLoader _assets;
    private readonly VoxelParser _parser = new();
    private readonly BlueprintParser _blueprintParser = new();
    private readonly WorldLoader _world = new();
    internal ImmutableArray<C.ContentDiagnostic> AssetFailures { get; private set; } = [];

    internal InitialLoader(C.ContentInstallation installation, C.IContentFiles files, C.IAssetInspector inspector)
    {
        _installation = installation;
        _source = new(installation, files, inspector);
        _assets = new(_source, _source);
    }

    internal C.ContentResult<C.IContentCandidate> Load()
    {
        var packages = new PackageLoader(_installation, _source).Discover();
        var loaded = new List<(LoadPackage Package, LoadedVoxel Voxel)>();
        var parsedBlueprints = new List<(LoadPackage Package, ParsedBlueprint Blueprint)>();
        foreach (var package in packages)
        {
            PackageLoader.ExcludeDependents(packages);
            if (package.State != C.PackageState.Available) continue;
            LoadDefinitions(package, loaded, parsedBlueprints);
        }
        PackageLoader.ExcludeDependents(packages);
        var assets = _assets.Freeze();
        var blueprints = new List<(LoadPackage Package, C.BlueprintRecord Blueprint)>();
        // 功能定义没有嵌套，先求值它们，再解析宿主的引用。
        foreach (var functional in new[] { true, false })
        {
            var evaluationView = new ContentView(loaded.Where(item => item.Package.State == C.PackageState.Available)
                .Select(item => item.Voxel).OrderBy(voxel => voxel.Definition.Definition.Value, StringComparer.Ordinal).ToImmutableArray(), [], assets,
                blueprints.Where(item => item.Package.State == C.PackageState.Available).Select(item => item.Blueprint).ToImmutableArray());
            foreach (var package in packages)
            {
                PackageLoader.ExcludeDependents(packages);
                if (package.State != C.PackageState.Available) continue;
                foreach (var (_, blueprint) in parsedBlueprints.Where(item => item.Package == package &&
                    (item.Blueprint.Definition.Specification is C.DesignSpecification.FunctionalComposite) == functional))
                {
                    var subject = new C.ContentReference.Blueprint(blueprint.Address);
                    var missing = false;
                    foreach (var (reference, field) in DesignTraversal.References(blueprint.Definition))
                    {
                        var id = ContentView.DefinitionId(reference);
                        var found = reference switch { K.CoreDefinitionReference.Voxel v => evaluationView.FindVoxel(v.Id) is not null,
                            K.CoreDefinitionReference.Composite c => evaluationView.FindComposite(c.Id) is not null, _ => false };
                        if (found) continue;
                        var wrongKind = parsedBlueprints.Any(item => item.Blueprint.Address.Id.Value == id) || loaded.Any(item => item.Voxel.Definition.Definition.Value == id);
                        package.Reject(wrongKind ? "reference_wrong_kind" : "reference_missing", blueprint.Path,
                            field, $"引用不可用：{id}。", subject);
                        missing = true;
                    }
                    if (missing) continue;
                    var result = DesignEvaluator.Evaluate(evaluationView, blueprint.Definition,
                        compositeIdentity: functional ? new K.CompositeDefinitionId(blueprint.Address.Id.Value) : null);
                    if (result is not C.DesignEvaluation.Complete evaluation || !evaluation.IsValid)
                    {
                        var failures = result is C.DesignEvaluation.Failed failed
                            ? failed.Diagnostics : ((C.DesignEvaluation.Complete)result).Diagnostics;
                        foreach (var diagnostic in failures)
                            package.Reject(diagnostic.Code, blueprint.Path, diagnostic.Field, diagnostic.Message, subject, diagnostic.Element);
                        continue;
                    }
                    var usedPackages = DesignTraversal.References(blueprint.Definition).Select(entry => ContentView.DefinitionId(entry.Reference).Split(':')[0])
                        .Append(package.Metadata!.Id.Value).Distinct().Order(StringComparer.Ordinal)
                        .Select(id => packages.Single(p => p.Metadata?.Id.Value == id).Metadata!).ToImmutableArray();
                    blueprints.Add((package, new(blueprint.Address, evaluation.Definition, usedPackages, evaluation)));
                }
            }
        }
        PackageLoader.ExcludeDependents(packages);
        var world = _world.Resolve(packages, loaded, blueprints);
        AssetFailures = packages.SelectMany(package => package.AssetDiagnostics).ToImmutableArray();
        var diagnostics = packages.SelectMany(package => package.Diagnostics).ToImmutableArray();
        if (packages.Any(package => package.BuiltIn && package.State != C.PackageState.Available))
            return new C.ContentResult<C.IContentCandidate>.Failure(diagnostics);
        var statuses = packages.Select(package => package.Status).ToImmutableArray();
        var view = new ContentView(loaded.Where(item => item.Package.State == C.PackageState.Available)
            .Select(item => item.Voxel).OrderBy(voxel => voxel.Definition.Definition.Value, StringComparer.Ordinal).ToImmutableArray(), statuses, _assets.Freeze(),
            blueprints.Where(item => item.Package.State == C.PackageState.Available).Select(item => item.Blueprint).ToImmutableArray(), world, _source.Freeze());
        return new C.ContentResult<C.IContentCandidate>.Success(new ContentCandidate(view, new(statuses, [], [], diagnostics)));
    }

    private void LoadDefinitions(LoadPackage package, List<(LoadPackage Package, LoadedVoxel Voxel)> loaded,
        List<(LoadPackage Package, ParsedBlueprint Blueprint)> blueprints)
    {
        var resources = Enumerate(package, package.Resources);
        foreach (var entry in resources)
        {
            var path = package.Resources + "/" + entry.Name;
            if (!ContentDocument.SafeEntry(entry.Name) || entry.Kind == C.ContentEntryKind.Link)
            {
                package.Reject("linked_path", path, "", "内容目录包含无效路径或链接。");
                continue;
            }
            if (entry.Name is "lair-types" or "markets")
            {
                package.Reject("unsupported_format", path, "", $"当前尚未实现定义种类 {entry.Name}：{path}。");
                continue;
            }
            if (entry.Name is not ("voxels" or "blueprints") && !WorldLoader.Directory(entry.Name))
            {
                if (entry.Kind == C.ContentEntryKind.Directory)
                    package.Reject("unsupported_format", path, "", $"未知的定义目录 {entry.Name}：{path}。");
                continue;
            }
            if (entry.Kind != C.ContentEntryKind.Directory)
            {
                package.Reject("invalid_field", path, "", $"{entry.Name} 必须是目录。");
                continue;
            }
            foreach (var definition in Enumerate(package, path))
            {
                var file = path + "/" + definition.Name;
                if (!ContentDocument.SafeEntry(definition.Name) || definition.Kind == C.ContentEntryKind.Link)
                {
                    package.Reject("linked_path", file, "", "体素目录包含无效路径或链接。");
                    continue;
                }
                if (definition.Kind != C.ContentEntryKind.File || !definition.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                var kind = entry.Name switch { "voxels" => "voxel", "blueprints" => "blueprint", _ => WorldLoader.Kind(entry.Name, definition.Name) };
                var suffix = kind is null
                    ? ".population-rule.json、.market-rule.json 或 .construction-rule.json"
                    : "." + kind + ".json";
                if (kind is null || !definition.Name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    package.Reject("unsupported_format", file, "", $"定义文件必须使用 {suffix} 后缀。");
                    continue;
                }
                var read = _source.Read(new(package.Root, file), ContentLimits.JsonFileMaxBytes);
                if (read is C.ContentIoResult<ImmutableArray<byte>>.Failure failure)
                {
                    package.Reject(ContentDocument.IoCode(failure.Kind), file, "", failure.Diagnostic);
                    continue;
                }
                var bytes = ((C.ContentIoResult<ImmutableArray<byte>>.Success)read).Value.ToArray();
                if (entry.Name is not ("voxels" or "blueprints"))
                {
                    _world.Parse(package, kind!, file, bytes, _assets);
                    continue;
                }
                if (entry.Name == "blueprints")
                {
                    var blueprint = _blueprintParser.Parse(bytes, package, file);
                    if (blueprint is not null) blueprints.Add((package, blueprint));
                    continue;
                }
                var voxel = _parser.Parse(bytes, package, file);
                if (voxel is null) continue;
                var resolved = ResolveAppearance(package, voxel);
                if (resolved is not null) loaded.Add((package, resolved));
            }
        }
    }

    private ImmutableArray<C.ContentFileEntry> Enumerate(LoadPackage package, string path)
    {
        var result = _source.Enumerate(new(package.Root, path));
        if (result is C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Success success)
            return success.Value.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToImmutableArray();
        var failure = (C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Failure)result;
        package.Reject(ContentDocument.IoCode(failure.Kind), path, "", failure.Diagnostic);
        return [];
    }

    private LoadedVoxel? ResolveAppearance(LoadPackage package, ParsedVoxel voxel)
    {
        var icon = _assets.Load(package, voxel, voxel.Icon, "/display/icon", C.AssetKind.Png);
        var valid = icon is not null && AssetLoader.Png(icon, package, voxel, "/display/icon", false);
        C.VoxelPresentation? presentation = null;
        switch (voxel.Presentation)
        {
            case ParsedPresentation.Cube cube:
                var top = Face(cube.Top, "top");
                var side = Face(cube.Side, "side");
                var bottom = Face(cube.Bottom, "bottom");
                var front = Face(cube.Front, "front");
                if (top is not null && side is not null && bottom is not null && front is not null)
                {
                    if (top.Metadata is C.AssetMetadata.Png a && side.Metadata is C.AssetMetadata.Png b && bottom.Metadata is C.AssetMetadata.Png c &&
                        (a.Width != b.Width || a.Width != c.Width))
                    {
                        AssetLoader.Reject(side, package, voxel, "/appearance/presentation/faces/side",
                            $"顶面、侧面和底面贴图必须同尺寸；观测顶面 {a.Width}×{a.Height}、侧面 {b.Width}×{b.Height}、底面 {c.Width}×{c.Height}。");
                        valid = false;
                    }
                    presentation = new C.VoxelPresentation.Cube(cube.Shapes, cube.Material, cube.TileSpan, top.Revision, side.Revision, bottom.Revision, front.Revision);
                }
                break;
            case ParsedPresentation.Mesh mesh:
                var model = _assets.Load(package, voxel, mesh.Model, "/appearance/presentation/model", C.AssetKind.Glb);
                if (model is not null)
                {
                    valid &= AssetLoader.Glb(model, package, voxel, mesh.SurfaceMaterials.Length, "/appearance/presentation/model");
                    presentation = new C.VoxelPresentation.Mesh(model.Revision, mesh.SurfaceMaterials);
                }
                break;
        }
        return valid && presentation is not null
            ? new(voxel, new(voxel.Definition, voxel.Orientations, voxel.Collision, presentation, voxel.Behaviors), new(voxel.Definition, voxel.Display, icon!.Revision))
            : null;

        C.ContentAsset? Face(string name, string face)
        {
            var asset = _assets.Load(package, voxel, name, "/appearance/presentation/faces/" + face, C.AssetKind.Png);
            if (asset is not null) valid &= AssetLoader.Png(asset, package, voxel, "/appearance/presentation/faces/" + face, true);
            return asset;
        }
    }
}
