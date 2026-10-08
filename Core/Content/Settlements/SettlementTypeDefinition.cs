// 属于内容层的聚居地类型模块。
// 描述生成参数和临时市场供需规格，不持有聚居地实例状态。
using System.Collections.Frozen;
using Train.Core.Content.Voxels;

namespace Train.Core.Content.Settlements;

/// <summary>表示聚居地类型的稳定强类型身份；规则与其他内容身份相同。</summary>
/// <remarks>数据种类：结构；命名空间与局部 ID 分别为 1–64 个小写 ASCII 身份字符。默认值无效，不可用于发布定义。</remarks>
/// <example><c>core:depot</c>；命名数据例子见 MarketSettlementExamples。</example>
public readonly record struct SettlementTypeDefinitionId
{
    /// <summary>获取类型所属内容包的命名空间。</summary>
    public string Namespace { get; }
    /// <summary>获取类型在包内的局部 ID。</summary>
    public string LocalId { get; }
    /// <summary>获取 namespace:local-id 形式的完整身份。</summary>
    public string Value => $"{Namespace}:{LocalId}";

    /// <summary>创建身份并去除两部分首尾空白；null 或无效身份部分分别抛出 ArgumentNullException 或 ArgumentException。</summary>
    public SettlementTypeDefinitionId(string @namespace, string localId)
    {
        Namespace = ContentIdentity.NormalizePart(@namespace, nameof(@namespace));
        LocalId = ContentIdentity.NormalizePart(localId, nameof(localId));
    }

    /// <summary>解析 namespace:local-id；null 或格式、字符无效分别抛出 ArgumentNullException 或 ArgumentException。</summary>
    public static SettlementTypeDefinitionId Parse(string value)
    {
        var (ns, localId) = ContentIdentity.Split(value, nameof(SettlementTypeDefinitionId));
        return new SettlementTypeDefinitionId(ns, localId);
    }

    /// <summary>返回完整身份。</summary>
    public override string ToString() => Value;
}

/// <summary>表示一项供给或需求的上限及每个市场刻恢复量，单位都是整数货物单位。</summary>
/// <remarks>
/// 数据种类：结构。Capacity 是正 Int32；RestorePerTick 是非负 Int32，允许为零或超过上限。
/// 运行时从满额开始；恢复时截到 Capacity。这里只保存规格，不保存当前数量或执行恢复。
/// 范围由解析器校验。
/// </remarks>
/// <example>粮食供给上限 8、每刻恢复 1；零恢复例子见 MarketSettlementExamples.NoRecovery。</example>
public sealed class SettlementMarketQuantity
{
    /// <summary>获取这种货物的供给或需求上限。</summary>
    public int Capacity { get; }
    /// <summary>获取每个全局市场刻补充的单位数。</summary>
    public int RestorePerTick { get; }

    /// <summary>保存解析器已校验的容量和恢复量。</summary>
    internal SettlementMarketQuantity(int capacity, int restorePerTick)
    {
        Capacity = capacity;
        RestorePerTick = restorePerTick;
    }
}

/// <summary>表示生成初始常住人口时使用的闭区间。</summary>
/// <remarks>数据种类：区间；Min、Max 均为正 Int32 人数，Min ≤ Max，允许两端相同。范围由解析器校验。</remarks>
/// <example>100–300 人和恰好 1 人；见 MarketSettlementExamples。</example>
public sealed class SettlementPopulationRange
{
    /// <summary>获取允许的最少人数，包含端点。</summary>
    public int Min { get; }
    /// <summary>获取允许的最多人数，包含端点。</summary>
    public int Max { get; }

    /// <summary>保存解析器已校验的初始人口闭区间。</summary>
    internal SettlementPopulationRange(int min, int max)
    {
        Min = min;
        Max = max;
    }
}

/// <summary>表示一种聚居地的不可变生成参数及临时供需规格。</summary>
/// <remarks>
/// 数据种类：结构。Name 是去除首尾空白的 1–100 个 Unicode 标量值；Weight 是正 Int32 相对生成权重。
/// Supply、Demand 各自是有限映射，允许为空；每项引用同批次的非 scene_only 体素，同一货物允许同时供给和收购。
/// PreferredTerrain 是不重复的规范身份字符串集合，空集合表示不限地貌；当前没有地貌定义目录，不作外部引用校验。
/// InitialPlatformCount 是非负 Int32 初始生成参数；建筑接入后实际站台由完好建筑部件派生。
/// 本定义不引用市场表、不包含当前供需、抽样人口、站台占用或布局实例。
/// </remarks>
/// <example>有两种货物的 DepotFile，以及无市场、无站台的 QuietTypeFile；见 MarketSettlementExamples。</example>
public sealed class SettlementTypeDefinition
{
    /// <summary>获取聚居地类型稳定身份。</summary>
    public SettlementTypeDefinitionId Id { get; }
    /// <summary>获取类型显示名。</summary>
    public string Name { get; }
    /// <summary>获取按货物身份索引的供给上限与恢复量。</summary>
    public IReadOnlyDictionary<VoxelDefinitionId, SettlementMarketQuantity> Supply { get; }
    /// <summary>获取按货物身份索引的需求上限与恢复量。</summary>
    public IReadOnlyDictionary<VoxelDefinitionId, SettlementMarketQuantity> Demand { get; }
    /// <summary>获取地貌偏好标签；空集合表示不限。</summary>
    public IReadOnlySet<string> PreferredTerrain { get; }
    /// <summary>获取相对生成权重。</summary>
    public int Weight { get; }
    /// <summary>获取初始人口的闭区间。</summary>
    public SettlementPopulationRange Population { get; }
    /// <summary>获取临时生成模型的初始站台数。</summary>
    public int InitialPlatformCount { get; }

    /// <summary>保存已校验的类型规格，并冻结各集合的副本。</summary>
    internal SettlementTypeDefinition(
        SettlementTypeDefinitionId id, string name,
        IReadOnlyDictionary<VoxelDefinitionId, SettlementMarketQuantity> supply,
        IReadOnlyDictionary<VoxelDefinitionId, SettlementMarketQuantity> demand,
        IEnumerable<string> preferredTerrain, int weight,
        SettlementPopulationRange population, int initialPlatformCount)
    {
        Id = id;
        Name = name;
        Supply = supply.ToFrozenDictionary();
        Demand = demand.ToFrozenDictionary();
        PreferredTerrain = preferredTerrain.ToFrozenSet(StringComparer.Ordinal);
        Weight = weight;
        Population = population;
        InitialPlatformCount = initialPlatformCount;
    }
}
