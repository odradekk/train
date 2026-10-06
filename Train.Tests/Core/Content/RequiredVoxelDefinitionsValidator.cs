// Core 内容测试夹具：模拟宿主对候选体素目录的外部引用约束。
// 它只检查标识是否存在，不引入聚居地或世界运行态规则。
using Train.Core.Content;
using Train.Core.Content.Voxels;

namespace Train.Tests;

internal sealed class RequiredVoxelDefinitionsValidator : IContentPreCommitValidator
{
    private readonly IReadOnlySet<VoxelDefinitionId> _required;

    public RequiredVoxelDefinitionsValidator(IEnumerable<VoxelDefinitionId> required)
    {
        _required = required.ToHashSet();
    }

    public ContentPreCommitDecision Validate(VoxelCatalogSnapshot candidate)
    {
        var missing = _required
            .Where(id => !candidate.Entries.ContainsKey(id))
            .OrderBy(id => id.Value, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length == 0)
            return ContentPreCommitDecision.Allow();

        return ContentPreCommitDecision.Reject(missing.Select(id =>
            new ContentDiagnostic(
                ContentDiagnosticSeverity.Error,
                ContentDiagnosticCodes.UnresolvedVoxelReference,
                $"Required voxel definition is unavailable: {id.Value}.")));
    }
}
