// 属于内容层的体素引用模块。
// 市场表与供需共用货物存在性和 scene_only 排除规则。
namespace Train.Core.Content.Voxels;

/// <summary>校验同一候选中的货物引用；诊断归入实际所属包，而非从命名空间判断严重程度。</summary>
internal static class VoxelContentReferences
{
    /// <summary>检查每个货物身份存在且可交易；失败设置 owner 的来源、诊断和整包无效状态。</summary>
    public static void ValidateGoods(IEnumerable<VoxelDefinitionId> goods,
        IReadOnlyDictionary<VoxelDefinitionId, VoxelContentEntry> voxels,
        ContentPackageLoad owner, string sourceFile)
    {
        var diagnostics = owner.Diagnostics;
        diagnostics.Source = sourceFile;
        var severity = owner.Package.IsRequired ? ContentDiagnosticSeverity.Error : ContentDiagnosticSeverity.Warning;
        foreach (var id in goods.Distinct())
        {
            if (!voxels.TryGetValue(id, out var entry))
                diagnostics.Add(severity, ContentDiagnosticCodes.UnresolvedVoxelReference,
                    $"Good '{id}' is missing from the candidate content.", id.Value);
            else if (entry.Definition.Tags.Contains(VoxelTags.SceneOnly))
                diagnostics.Add(severity, ContentDiagnosticCodes.NonGoodsVoxelReference,
                    $"Voxel '{id}' is scene_only and cannot be used as goods.", id.Value);
        }
    }
}
