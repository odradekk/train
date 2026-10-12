// 属于内容层的市场表加载模块。
// 解析价格表、编码修订并校验同轮货物引用；发现、读取和整包隔离由共同流程完成。
using Train.Content.Voxels;

namespace Train.Content.Markets;

/// <summary>一轮加载中的市场表格式；所需货物来自同轮体素候选，不单独执行 IO 或发布。</summary>
internal sealed class MarketTableContentFormat : ContentFormat<MarketTableDefinitionId, MarketTableContentEntry>
{
    public IReadOnlyDictionary<MarketTableDefinitionId, MarketTableContentEntry> Entries => Candidates;
    public override string FileSuffix => ".market-tables.json";
    public override string LogKind => "market-tables";
    protected override MarketTableDefinitionId Identity(MarketTableContentEntry entry) => entry.Definition.Id;
    public override bool HasChanges(ContentSnapshot previous) =>
        HasChanges(Entries, previous.MarketTables, static entry => entry.PublicationFingerprint);

    /// <summary>从已读取字节取得本文件的候选条目；解析失败归入 load 的共享包诊断。</summary>
    /// <remarks>效果：更新 load.Diagnostics；不修改已发布快照。</remarks>
    protected override IReadOnlyList<MarketTableContentEntry> Parse(
        IContentSource source, ContentPackageLoad load, string file, byte[] bytes)
    {
        var definitions = MarketTablesJsonParser.Parse(bytes, load.Package.Namespace, file, load.Diagnostics);
        return definitions is null ? Array.Empty<MarketTableContentEntry>() : definitions.Select(definition =>
            new MarketTableContentEntry(definition, new ContentOrigin(load.Package.Namespace, file),
                MarketTableRevisions.ComputeSemanticRevision(definition))).ToArray();
    }

    /// <summary>检查 Entries 的每个货物引用在同轮体素候选中存在且非 scene_only。</summary>
    /// <remarks>效果：失败写入实际所属包的诊断并使整包无效；不替换任何已发布数据。</remarks>
    public void ResolveReferences(IReadOnlyDictionary<VoxelDefinitionId, VoxelContentEntry> voxels)
    {
        foreach (var (id, entry) in Entries)
        {
            var owner = Owners[id];
            if (!owner.Diagnostics.Invalid)
                VoxelContentReferences.ValidateGoods(entry.Definition.Goods.Keys, voxels, owner, entry.Origin.SourceFile);
        }
    }
}
