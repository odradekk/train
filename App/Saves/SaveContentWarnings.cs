using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App.Saves;

internal static class SaveContentWarnings
{
    internal static ImmutableArray<string> Describe(ContentPreparationReport content,
        ImmutableArray<MissingCoreReference> core, ImmutableArray<PackageMetadata> savedPackages = default)
    {
        var references = content.RuntimeMissing.Select(m => (m.Reference, m.Package))
            .Concat(core.Select(m => ((ContentReference)new ContentReference.Core(m.Reference), (PackageId?)null)))
            .ToArray();
        if (references.Length == 0) return [];
        var packages = (savedPackages.IsDefault ? [] : savedPackages)
            .Concat(content.Packages.Select(p => p.Metadata).OfType<PackageMetadata>()).ToArray();
        var names = references.Select(m =>
        {
            var id = m.Item2?.Value ?? Details(m.Item1).Id?.Split(':')[0];
            return packages.FirstOrDefault(p => p.Id.Value == id)?.Name ?? "名称未知的内容包";
        }).Distinct();
        var warnings = ImmutableArray.CreateBuilder<string>();
        warnings.Add($"缺失内容来自：{string.Join("、", names)}。");
        warnings.Add($"缺失内容：{string.Join("、", references.Select(m => Details(m.Item1).Kind).Distinct())}。");
        var parts = references.Any(m => IsPart(m.Item1));
        if (parts)
        {
            warnings.Add("缺失的体素或功能复合体没有能力、质量和价值，不贡献结构，不受伤害；缺失格不能改装或修理。");
            warnings.Add("这些内容对应的缺失货物不能使用或交易。");
        }
        if (references.Any(m => !IsPart(m.Item1)))
            warnings.Add("这些内容的功能失效。");
        warnings.Add("继续读档会用占位保留原有状态；装回所需内容包后重新读档可恢复。");
        return warnings.ToImmutable();
    }

    private static bool IsPart(ContentReference reference) => reference is ContentReference.Core
        { Definition: CoreDefinitionReference.Voxel or CoreDefinitionReference.Composite };

    private static (string Kind, string? Id) Details(ContentReference reference) => reference switch
    {
        ContentReference.Core core => core.Definition switch
        {
            CoreDefinitionReference.Voxel value => ("体素", value.Id.Value),
            CoreDefinitionReference.Composite value => ("功能复合体", value.Id.Value),
            CoreDefinitionReference.Process { Id: ProcessRef.Production value } => ("生产流程", value.Definition.Value),
            CoreDefinitionReference.Process { Id: ProcessRef.Recovery value } => ("回收流程", value.Definition.Value),
            CoreDefinitionReference.Rule value => ("规则", value.Id.Value),
            CoreDefinitionReference.SettlementType value => ("聚居地类型", value.Id.Value),
            CoreDefinitionReference.Template value => ("聚居地模板", value.Id.Value),
            CoreDefinitionReference.Terrain value => ("地貌", value.Id.Value),
            CoreDefinitionReference.Creature value => ("生物", value.Id.Value),
            CoreDefinitionReference.Lair value => ("生物聚落类型", value.Id.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(reference))
        },
        ContentReference.Blueprint value => ("蓝图", value.Address.Id.Value),
        ContentReference.Design => ("设计", null),
        ContentReference.Draft => ("草稿", null),
        ContentReference.Generation value => ("世界生成配置", value.Id.Value),
        _ => throw new ArgumentOutOfRangeException(nameof(reference))
    };
}
