// 属于内容层的聚居地类型加载模块。
// 解析类型、编码修订并校验同轮货物引用；发现、读取和整包隔离由共同流程完成。
using Train.Core.Content.Voxels;

namespace Train.Core.Content.Settlements;

/// <summary>一轮加载中的聚居地类型格式；供需只依赖同轮体素候选，不依赖市场表选择。</summary>
internal sealed class SettlementTypeContentFormat : ContentFormat<SettlementTypeDefinitionId, SettlementTypeContentEntry>
{
    private readonly IReadOnlyDictionary<VoxelDefinitionId, VoxelContentEntry> _voxels;

    /// <summary>保存本轮共享的体素候选；重合并后仍读取同一个索引对象。</summary>
    public SettlementTypeContentFormat(IReadOnlyDictionary<VoxelDefinitionId, VoxelContentEntry> voxels) => _voxels = voxels;

    public override string FileSuffix => ".settlement-types.json";
    public override string LogKind => "settlement-types";
    public override bool Accepts(ContentPackage package, string file) =>
        package.Kind != ContentPackageKind.PlayerBlueprint && base.Accepts(package, file);
    protected override SettlementTypeDefinitionId Identity(SettlementTypeContentEntry entry) => entry.Definition.Id;
    protected override string Fingerprint(SettlementTypeContentEntry entry) => entry.PublicationFingerprint;
    public override bool HasChanges(ContentSnapshot previous) => HasChanges(previous.SettlementTypes);

    /// <summary>从已读取字节取得本文件的候选条目；解析失败归入 load 的共享包诊断。</summary>
    /// <remarks>效果：更新 load.Diagnostics；不修改已发布快照。</remarks>
    protected override IReadOnlyList<SettlementTypeContentEntry> Parse(
        IContentSource source, ContentPackageLoad load, string file, byte[] bytes)
    {
        var definitions = SettlementTypesJsonParser.Parse(bytes, load.Package.Namespace, file, load.Diagnostics);
        if (definitions is null)
            return Array.Empty<SettlementTypeContentEntry>();
        return definitions.Select(definition =>
        {
            var semantic = SettlementTypeRevisions.ComputeSemanticRevision(definition);
            return new SettlementTypeContentEntry(definition, new ContentOrigin(load.Package.Namespace, file), semantic,
                SettlementTypeRevisions.ComputePublicationFingerprint(definition, semantic));
        }).ToArray();
    }

    /// <summary>检查 Entries 的所有供给和需求引用在同轮体素候选中存在且非 scene_only。</summary>
    /// <remarks>效果：失败写入实际所属包的诊断并使整包无效；不执行世界生成或供需结算。</remarks>
    public override void ResolveReferences()
    {
        foreach (var (id, entry) in Entries)
        {
            var owner = Owners[id];
            if (!owner.Diagnostics.Invalid)
                VoxelContentReferences.ValidateGoods(entry.Definition.Supply.Keys.Concat(entry.Definition.Demand.Keys),
                    _voxels, owner, entry.Origin.SourceFile);
        }
    }
}
