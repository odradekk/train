using System.Collections.Immutable;
using C = Train.Content.Contracts;

namespace Train.Content.Loading;

internal sealed class AssetLoader(C.IContentFiles files, C.IAssetInspector inspector)
{
    private const long MaximumBytes = 4 * 1024 * 1024;
    private readonly Dictionary<C.ContentPath, C.ContentAsset> _read = [];
    private readonly List<(LoadPackage Package, C.ContentAsset Asset)> _used = [];

    internal C.ContentAsset? Load(LoadPackage package, ParsedVoxel voxel, string name, string field, C.AssetKind kind)
    {
        var localId = voxel.Definition.Value[(voxel.Definition.Value.IndexOf(':') + 1)..];
        var path = package.Resources + "/voxels/" + (name.StartsWith("shared/", StringComparison.Ordinal) ? name : localId + "/" + name);
        var key = new C.ContentPath(package.Root, path);
        var subject = new C.ContentReference.Core(new Train.Core.Contracts.CoreDefinitionReference.Voxel(voxel.Definition));
        if (!_read.TryGetValue(key, out var asset))
        {
            var result = files.Read(key, MaximumBytes);
            if (result is C.ContentIoResult<ImmutableArray<byte>>.Failure failure)
            {
                package.Reject(ContentDocument.IoCode(failure.Kind), path, field, failure.Diagnostic, subject);
                return null;
            }
            var bytes = ((C.ContentIoResult<ImmutableArray<byte>>.Success)result).Value;
            if (bytes.Length > MaximumBytes)
            {
                package.Reject("file_too_large", path, field, "资产超过 4 MiB。", subject);
                return null;
            }
            var revision = new C.AssetRevision(ContentHash.FromBytes(bytes.AsSpan()));
            var inspected = inspector.Inspect(kind, revision, bytes);
            if (inspected is C.AssetInspection.Failure inspectionFailure)
            {
                package.Reject(inspectionFailure.Kind == C.AssetInspectionFailure.CannotDecode ? "asset_decode_failed" : "asset_unsupported",
                    path, field, inspectionFailure.Diagnostic, subject);
                return null;
            }
            asset = new(revision, bytes, ((C.AssetInspection.Success)inspected).Metadata, [new(package.Metadata!.Id, path)]);
            _read.Add(key, asset);
        }
        _used.Add((package, asset));
        return asset;
    }

    internal ImmutableArray<C.ContentAsset> Freeze() => _used.Where(item => item.Package.State == C.PackageState.Available)
        .GroupBy(item => item.Asset.Revision).OrderBy(group => group.Key.Value, StringComparer.Ordinal)
        .Select(group => group.First().Asset with
        {
            Sources = group.SelectMany(item => item.Asset.Sources).Distinct()
                .OrderBy(source => source.Package.Value, StringComparer.Ordinal).ThenBy(source => source.Path, StringComparer.Ordinal).ToImmutableArray()
        }).ToImmutableArray();

    internal static bool Png(C.ContentAsset asset, LoadPackage package, ParsedVoxel voxel, string field, bool face)
    {
        if (asset.Metadata is C.AssetMetadata.Png png && png.Width is > 0 and <= 4096 && png.Width == png.Height &&
            (!face || (png.Width & (png.Width - 1)) == 0)) return true;
        Reject(asset, package, voxel, field, "PNG 必须是有效正方形；面贴图边长必须是 2 的幂。");
        return false;
    }

    internal static bool Glb(C.ContentAsset asset, LoadPackage package, ParsedVoxel voxel, int surfaces, string field)
    {
        if (asset.Metadata is C.AssetMetadata.Glb glb && glb.MeshNodeCount == 1 && glb.Surfaces.Length == surfaces && surfaces > 0 &&
            glb.Surfaces.All(alpha => alpha is C.AssetAlphaMode.Opaque or C.AssetAlphaMode.Mask) &&
            !glb.HasSkeleton && !glb.HasMorphTargets && !glb.HasAnimation && !glb.HasCamera && !glb.HasLight && !glb.HasExternalImages &&
            BoundsValid(glb.Bounds)) return true;
        Reject(asset, package, voxel, field, "GLB 的节点、表面、静态内容或米制包围盒不符合单格模型规则。");
        return false;
    }

    private static bool BoundsValid(C.ModelBounds bounds)
    {
        var min = bounds.MinimumMeters;
        var max = bounds.MaximumMeters;
        const double tolerance = 0.0005; // 1/1000 格；一格为 0.5 米。
        return double.IsFinite(min.X) && double.IsFinite(min.Y) && double.IsFinite(min.Z) &&
            double.IsFinite(max.X) && double.IsFinite(max.Y) && double.IsFinite(max.Z) &&
            min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z &&
            min.X >= -0.25 - tolerance && max.X <= 0.25 + tolerance &&
            min.Y >= -tolerance && max.Y <= 0.5 + tolerance &&
            min.Z >= -0.25 - tolerance && max.Z <= 0.25 + tolerance;
    }

    internal static void Reject(C.ContentAsset asset, LoadPackage package, ParsedVoxel voxel, string field, string message) =>
        package.Reject("asset_rule_failed", asset.Sources[0].Path, field, message,
            new C.ContentReference.Core(new Train.Core.Contracts.CoreDefinitionReference.Voxel(voxel.Definition)));
}
