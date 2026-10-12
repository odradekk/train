using System.Collections.Immutable;
using C = Train.Content.Contracts;

namespace Train.Content.Loading;

internal sealed class InitialLoader(C.ContentInstallation installation, C.IContentFiles files, C.IAssetInspector inspector)
{
    private readonly AssetLoader _assets = new(files, inspector);
    private readonly VoxelParser _parser = new();

    internal C.ContentResult<C.IContentCandidate> Load()
    {
        var packages = new PackageLoader(installation, files).Discover();
        var loaded = new List<(LoadPackage Package, LoadedVoxel Voxel)>();
        foreach (var package in packages)
        {
            PackageLoader.ExcludeDependents(packages);
            if (package.State != C.PackageState.Available) continue;
            LoadVoxels(package, loaded);
        }
        PackageLoader.ExcludeDependents(packages);
        var diagnostics = packages.SelectMany(package => package.Diagnostics).ToImmutableArray();
        if (packages.Any(package => package.BuiltIn && package.State != C.PackageState.Available))
            return new C.ContentResult<C.IContentCandidate>.Failure(diagnostics);
        var statuses = packages.Select(package => package.Status).ToImmutableArray();
        var view = new ContentView(loaded.Where(item => item.Package.State == C.PackageState.Available)
            .Select(item => item.Voxel).OrderBy(voxel => voxel.Definition.Definition.Value, StringComparer.Ordinal).ToImmutableArray(), statuses, _assets.Freeze());
        return new C.ContentResult<C.IContentCandidate>.Success(new ContentCandidate(view, new(statuses, [], [], diagnostics)));
    }

    private void LoadVoxels(LoadPackage package, List<(LoadPackage Package, LoadedVoxel Voxel)> loaded)
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
            if (entry.Name is "blueprints" or "processes" or "settlement-types" or "settlement-templates" or "rules" or
                "railway-rules" or "combat-rules" or "lair-types" or "generation-profiles" or "terrain-types" or "markets")
            {
                package.Reject("unsupported_format", path, "", $"当前尚未实现定义种类 {entry.Name}：{path}。");
                continue;
            }
            if (entry.Name != "voxels")
            {
                if (entry.Kind == C.ContentEntryKind.Directory)
                    package.Reject("unsupported_format", path, "", $"未知的定义目录 {entry.Name}：{path}。");
                continue;
            }
            if (entry.Kind != C.ContentEntryKind.Directory)
            {
                package.Reject("invalid_field", path, "", "voxels 必须是目录。");
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
                if (!definition.Name.EndsWith(".voxel.json", StringComparison.Ordinal))
                {
                    package.Reject("unsupported_format", file, "", "体素文件必须使用 .voxel.json 后缀。");
                    continue;
                }
                var read = files.Read(new(package.Root, file), ContentLimits.JsonFileMaxBytes);
                if (read is C.ContentIoResult<ImmutableArray<byte>>.Failure failure)
                {
                    package.Reject(ContentDocument.IoCode(failure.Kind), file, "", failure.Diagnostic);
                    continue;
                }
                var voxel = _parser.Parse(((C.ContentIoResult<ImmutableArray<byte>>.Success)read).Value.ToArray(), package, file);
                if (voxel is null) continue;
                var resolved = ResolveAppearance(package, voxel);
                if (resolved is not null) loaded.Add((package, resolved));
            }
        }
    }

    private ImmutableArray<C.ContentFileEntry> Enumerate(LoadPackage package, string path)
    {
        var result = files.Enumerate(new(package.Root, path));
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
