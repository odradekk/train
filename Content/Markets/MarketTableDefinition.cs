// 属于内容层的市场表模块。
// 只描述价格参数；选表、市场刻和当前供需属于玩法配置与运行状态。
using System.Collections.Frozen;
using Train.Content.Voxels;

namespace Train.Content.Markets;

/// <summary>表示市场表的稳定强类型身份；规则与其他内容身份相同。</summary>
/// <remarks>数据种类：结构；命名空间与局部 ID 分别为 1–64 个小写 ASCII 身份字符。默认值无效，不可用于发布定义。</remarks>
/// <example><c>core:prices</c>；命名数据例子见 MarketSettlementExamples。</example>
public readonly record struct MarketTableDefinitionId
{
    /// <summary>获取市场表所属内容包的命名空间。</summary>
    public string Namespace { get; }
    /// <summary>获取市场表在包内的局部 ID。</summary>
    public string LocalId { get; }
    /// <summary>获取 namespace:local-id 形式的完整身份。</summary>
    public string Value => $"{Namespace}:{LocalId}";

    /// <summary>创建身份并去除两部分首尾空白；null 或无效身份部分分别抛出 ArgumentNullException 或 ArgumentException。</summary>
    public MarketTableDefinitionId(string @namespace, string localId)
    {
        Namespace = ContentIdentity.NormalizePart(@namespace, nameof(@namespace));
        LocalId = ContentIdentity.NormalizePart(localId, nameof(localId));
    }

    /// <summary>解析 namespace:local-id；null 或格式、字符无效分别抛出 ArgumentNullException 或 ArgumentException。</summary>
    public static MarketTableDefinitionId Parse(string value)
    {
        var (ns, localId) = ContentIdentity.Split(value, nameof(MarketTableDefinitionId));
        return new MarketTableDefinitionId(ns, localId);
    }

    /// <summary>返回完整身份。</summary>
    public override string ToString() => Value;
}

/// <summary>表示一种货物的价格敏感度和货币价格区间，不包含该货物的 Value 或运行时报价。</summary>
/// <remarks>数据种类：结构。Sensitivity 为有限非负数；MinPrice、MaxPrice 是正 Int64 货币单位且 MinPrice ≤ MaxPrice。</remarks>
/// <example>粮食为敏感度 0.6、价格区间 60–180；见 MarketSettlementExamples.GrainPrice。</example>
public sealed class MarketPriceRule
{
    /// <summary>获取价格公式的无量纲敏感度。</summary>
    public double Sensitivity { get; }
    /// <summary>获取四舍五入后报价的货币下限。</summary>
    public long MinPrice { get; }
    /// <summary>获取四舍五入后报价的货币上限。</summary>
    public long MaxPrice { get; }

    /// <summary>创建价格规则，并将正零和负零规范化为同一数值。</summary>
    /// <exception cref="ArgumentOutOfRangeException">敏感度非有限或为负，价格非正，或者价格区间倒置。</exception>
    internal MarketPriceRule(double sensitivity, long minPrice, long maxPrice)
    {
        if (!double.IsFinite(sensitivity) || sensitivity < 0)
            throw new ArgumentOutOfRangeException(nameof(sensitivity), sensitivity, "Sensitivity must be finite and nonnegative.");
        if (minPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(minPrice), minPrice, "Minimum price must be positive.");
        if (maxPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPrice), maxPrice, "Maximum price must be positive.");
        if (minPrice > maxPrice)
            throw new ArgumentOutOfRangeException(nameof(maxPrice), maxPrice, "Maximum price cannot be less than minimum price.");

        Sensitivity = sensitivity == 0 ? 0d : sensitivity;
        MinPrice = minPrice;
        MaxPrice = maxPrice;
    }
}

/// <summary>表示按货物身份组织的一张不可变价格参数表。</summary>
/// <remarks>
/// 数据种类：结构；Goods 是非空有限映射，一个货物只能有一条规则。
/// 键引用同一候选中的非 scene_only 体素；存在性在合并后校验。
/// 价格底价由调用方从同一 ContentSnapshot 的体素 Value 读取，不复制进市场表。
/// 本定义不选择全局市场表，不声明市场刻周期，不计算当前价格。
/// </remarks>
/// <example>含粮食、煤两条规则的 PricesFile；见 MarketSettlementExamples。</example>
public sealed class MarketTableDefinition
{
    /// <summary>获取市场表稳定身份。</summary>
    public MarketTableDefinitionId Id { get; }
    /// <summary>获取按体素定义身份索引的只读价格规则副本。</summary>
    public IReadOnlyDictionary<VoxelDefinitionId, MarketPriceRule> Goods { get; }

    /// <summary>创建市场表，并冻结价格规则映射的副本。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="goods"/> 或其中的规则为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">身份无效、货物映射为空或含无效货物身份。</exception>
    internal MarketTableDefinition(
        MarketTableDefinitionId id, IReadOnlyDictionary<VoxelDefinitionId, MarketPriceRule> goods)
    {
        ArgumentNullException.ThrowIfNull(goods);
        if (string.IsNullOrEmpty(id.Namespace) || string.IsNullOrEmpty(id.LocalId))
            throw new ArgumentException("MarketTableDefinitionId cannot be blank.", nameof(id));
        if (goods.Count == 0)
            throw new ArgumentException("A market table must contain at least one good.", nameof(goods));
        if (goods.Keys.Any(static key => string.IsNullOrEmpty(key.Namespace) || string.IsNullOrEmpty(key.LocalId)))
            throw new ArgumentException("A market table cannot contain an invalid good identity.", nameof(goods));
        if (goods.Values.Any(static rule => rule is null))
            throw new ArgumentNullException(nameof(goods), "A market table cannot contain null price rules.");

        Id = id;
        Goods = goods.ToFrozenDictionary();
    }
}
