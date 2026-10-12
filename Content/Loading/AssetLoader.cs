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
        if (asset.Metadata is not C.AssetMetadata.Png png)
        {
            Reject(asset, package, voxel, field, $"PNG 资产必须提供 PNG 尺寸事实；观测类型 {asset.Metadata.GetType().Name}。");
            return false;
        }
        var valid = true;
        Check(png.Width > 0 && png.Height > 0, $"PNG 宽高必须大于 0；观测 {png.Width}×{png.Height}。");
        Check(png.Width == png.Height, $"PNG 必须是正方形；观测 {png.Width}×{png.Height}。");
        if (face)
            Check(png.Width > 0 && png.Height > 0 && (png.Width & (png.Width - 1)) == 0 && (png.Height & (png.Height - 1)) == 0,
                $"面贴图边长必须是 2 的幂；观测 {png.Width}×{png.Height}。");
        return valid;

        void Check(bool condition, string message)
        {
            if (condition) return;
            Reject(asset, package, voxel, field, message);
            valid = false;
        }
    }

    internal static bool Glb(C.ContentAsset asset, LoadPackage package, ParsedVoxel voxel, int surfaces, string field)
    {
        if (asset.Metadata is not C.AssetMetadata.Glb glb)
        {
            Reject(asset, package, voxel, field, $"模型必须提供 GLB 事实；观测类型 {asset.Metadata.GetType().Name}。");
            return false;
        }
        var valid = true;
        Check(glb.MeshNodeCount == 1, field, $"模型必须恰有一个带网格的节点；观测节点数 {glb.MeshNodeCount}。");
        Check(glb.Surfaces.Length == surfaces, "/appearance/presentation/surface_materials",
            $"表面数必须与 surface_materials 数量相同；观测表面数 {glb.Surfaces.Length}，surface_materials {surfaces} 项。");
        for (var i = 0; i < glb.Surfaces.Length; i++)
            Check(glb.Surfaces[i] is C.AssetAlphaMode.Opaque or C.AssetAlphaMode.Mask, field,
                $"表面只能使用 Opaque 或 Mask 透明方式；观测表面 {i} 为 {glb.Surfaces[i]}。");
        Check(!glb.HasSkeleton, field, $"模型不能含骨骼；观测 HasSkeleton={glb.HasSkeleton}。");
        Check(!glb.HasMorphTargets, field, $"模型不能含形变目标；观测 HasMorphTargets={glb.HasMorphTargets}。");
        Check(!glb.HasAnimation, field, $"模型不能含动画；观测 HasAnimation={glb.HasAnimation}。");
        Check(!glb.HasCamera, field, $"模型不能含相机；观测 HasCamera={glb.HasCamera}。");
        Check(!glb.HasLight, field, $"模型不能含灯光；观测 HasLight={glb.HasLight}。");
        Check(!glb.HasExternalImages, field, $"模型不能引用外部贴图；观测 HasExternalImages={glb.HasExternalImages}。");
        var min = glb.Bounds.MinimumMeters;
        var max = glb.Bounds.MaximumMeters;
        const double tolerance = 0.0005; // 1/1000 格；一格为 0.5 米。
        var finite = double.IsFinite(min.X) && double.IsFinite(min.Y) && double.IsFinite(min.Z) &&
            double.IsFinite(max.X) && double.IsFinite(max.Y) && double.IsFinite(max.Z);
        var observed = FormattableString.Invariant($"最小点 ({min.X:R}, {min.Y:R}, {min.Z:R})、最大点 ({max.X:R}, {max.Y:R}, {max.Z:R}) 米");
        Check(finite, field, $"包围盒坐标必须是有限数值；观测 {observed}。");
        if (finite)
        {
            Check(min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z, field, $"包围盒每轴最小值不得大于最大值；观测 {observed}。");
            Check(min.X >= -0.25 - tolerance && max.X <= 0.25 + tolerance, field, $"包围盒 X 必须在 [-0.25, 0.25] 米内，容差 0.0005 米；观测 {observed}。");
            Check(min.Y >= -tolerance && max.Y <= 0.5 + tolerance, field, $"包围盒 Y 必须在 [0, 0.5] 米内，容差 0.0005 米；观测 {observed}。");
            Check(min.Z >= -0.25 - tolerance && max.Z <= 0.25 + tolerance, field, $"包围盒 Z 必须在 [-0.25, 0.25] 米内，容差 0.0005 米；观测 {observed}。");
        }
        return valid;

        void Check(bool condition, string targetField, string message)
        {
            if (condition) return;
            Reject(asset, package, voxel, targetField, message);
            valid = false;
        }
    }

    internal static void Reject(C.ContentAsset asset, LoadPackage package, ParsedVoxel voxel, string field, string message) =>
        package.Reject("asset_rule_failed", asset.Sources[0].Path, field, message,
            new C.ContentReference.Core(new Train.Core.Contracts.CoreDefinitionReference.Voxel(voxel.Definition)));
}
