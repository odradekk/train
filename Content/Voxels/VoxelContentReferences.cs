namespace Train.Content.Voxels;

internal static class VoxelContentReferences
{
    public static void ValidateGoods(IEnumerable<VoxelDefinitionId> goods,
        IReadOnlyDictionary<VoxelDefinitionId, VoxelContentEntry> voxels,
        ContentPackageLoad owner, string sourceFile)
    {
        var diagnostics = owner.Diagnostics;
        diagnostics.Source = sourceFile;
        foreach (var id in goods.Distinct())
        {
            if (!voxels.TryGetValue(id, out var entry))
                diagnostics.Reject(ContentDiagnosticCodes.UnresolvedVoxelReference,
                    $"Good '{id}' is missing from the candidate content.", id.Value);
            else if (entry.Definition.Tags.Contains(VoxelTags.SceneOnly))
                diagnostics.Reject(ContentDiagnosticCodes.NonGoodsVoxelReference,
                    $"Voxel '{id}' is scene_only and cannot be used as goods.", id.Value);
        }
    }
}
