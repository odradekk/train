// 属于内容模块。
// 定义完整内容批次及一次发布前后的不可变视图。
using System.Collections.Frozen;
using Train.Core.Content.Composites;
using Train.Core.Content.Markets;
using Train.Core.Content.Settlements;
using Train.Core.Content.Voxels;

namespace Train.Core.Content;

/// <summary>表示同一发布批次的体素、蓝图、市场表与聚居地类型。</summary>
/// <remarks>
/// 数据种类：结构；四个索引是按强类型身份组织的有限映射。
/// Generation 是从 1 开始的发布序号，任何有效内容变化只增加一次；它不作为持久身份。
/// 索引被冻结，键等于条目的定义身份。蓝图引用及设计属性由加载器基于本批次体素校验和计算。
/// 读取方在一次操作开始时捕获一个快照，并从该快照读取所有相关定义；持有的旧快照不会随重载变化。
/// </remarks>
/// <example>初次发布、依赖变化和移除全部内容的数据例子见 ContentReloadExamples。</example>
public sealed class ContentSnapshot
{
    /// <summary>获取整个内容批次的正整数发布序号。</summary>
    public long Generation { get; }

    /// <summary>获取本批次的全部体素定义、修订和已校验表现资产。</summary>
    public IReadOnlyDictionary<VoxelDefinitionId, VoxelContentEntry> Voxels { get; }

    /// <summary>获取本批次的全部蓝图及按本批次体素计算的设计属性。</summary>
    public IReadOnlyDictionary<CompositeDefinitionId, CompositeContentEntry> Blueprints { get; }

    /// <summary>获取本批次全部市场价格表；底价从本批次的体素 Value 读取。</summary>
    public IReadOnlyDictionary<MarketTableDefinitionId, MarketTableContentEntry> MarketTables { get; }

    /// <summary>获取本批次全部聚居地类型和供需规格；不含聚居地实例状态。</summary>
    public IReadOnlyDictionary<SettlementTypeDefinitionId, SettlementTypeContentEntry> SettlementTypes { get; }

    /// <summary>保存一个已完成候选校验的内容批次，并冻结四个强类型索引。</summary>
    /// <param name="generation">大于零的发布序号。</param>
    /// <param name="voxels">已完成定义与资产校验的体素条目；允许为空。</param>
    /// <param name="blueprints">已完成引用校验和属性计算的蓝图条目；允许为空。</param>
    /// <param name="marketTables">已完成货物引用校验的市场表条目；允许为空。</param>
    /// <param name="settlementTypes">已完成货物引用校验的聚居地类型条目；允许为空。</param>
    /// <remarks>跨定义引用和派生属性在加载阶段校验；构造只保证世代范围、键与定义身份一致及集合所有权。</remarks>
    /// <exception cref="ArgumentOutOfRangeException">generation 不大于零。</exception>
    /// <exception cref="ArgumentNullException">任一索引为 null。</exception>
    /// <exception cref="ArgumentException">条目为 null，或索引键与定义身份不同。</exception>
    internal ContentSnapshot(
        long generation,
        IReadOnlyDictionary<VoxelDefinitionId, VoxelContentEntry> voxels,
        IReadOnlyDictionary<CompositeDefinitionId, CompositeContentEntry> blueprints,
        IReadOnlyDictionary<MarketTableDefinitionId, MarketTableContentEntry> marketTables,
        IReadOnlyDictionary<SettlementTypeDefinitionId, SettlementTypeContentEntry> settlementTypes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        Generation = generation;
        Voxels = Freeze(voxels, static entry => entry.Definition.Id);
        Blueprints = Freeze(blueprints, static entry => entry.Definition.Id);
        MarketTables = Freeze(marketTables, static entry => entry.Definition.Id);
        SettlementTypes = Freeze(settlementTypes, static entry => entry.Definition.Id);
    }

    /// <summary>检查索引所有权和身份一致性，并保存不可修改的副本。</summary>
    private static IReadOnlyDictionary<TId, TEntry> Freeze<TId, TEntry>(
        IReadOnlyDictionary<TId, TEntry> entries, Func<TEntry, TId> identity)
        where TId : notnull
        where TEntry : class
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var (id, entry) in entries)
        {
            if (entry is null || !EqualityComparer<TId>.Default.Equals(id, identity(entry)))
                throw new ArgumentException("Content index keys must match their definitions.", nameof(entries));
        }
        return entries.ToFrozenDictionary();
    }
}

/// <summary>表示一次成功内容发布前后的完整快照。</summary>
/// <remarks>
/// 数据种类：结构；Previous 是 null 或已发布快照，Current 是已发布快照。
/// 首次发布的 Previous 为 null、Current.Generation 为 1；其余发布的序号恰好增加 1。
/// 无变化或拒绝重载不产生本数据。事件发送时 Current 已成为重载器的当前快照。
/// </remarks>
/// <example>首次发布和后续发布的数据例子见 ContentReloadExamples。</example>
public sealed class ContentChange
{
    /// <summary>获取提交前的完整快照；首次发布为 null。</summary>
    public ContentSnapshot? Previous { get; }

    /// <summary>获取提交后的完整快照。</summary>
    public ContentSnapshot Current { get; }

    /// <summary>保存一次发布前后的快照，并检查发布序号连续。</summary>
    /// <param name="previous">提交前的快照；首次发布为 null。</param>
    /// <param name="current">提交后的非 null 快照，其序号必须等于上一序号加 1。</param>
    /// <exception cref="ArgumentNullException">current 为 null。</exception>
    /// <exception cref="ArgumentException">发布序号不连续。</exception>
    internal ContentChange(ContentSnapshot? previous, ContentSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Generation != checked((previous?.Generation ?? 0) + 1))
            throw new ArgumentException("A content publication must advance one generation.", nameof(current));
        Previous = previous;
        Current = current;
    }
}
