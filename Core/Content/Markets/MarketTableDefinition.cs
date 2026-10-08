// 属于内容层的市场表模块。
// 只描述价格参数；选表、市场刻和当前供需属于玩法配置与运行状态。
using System.Collections.Frozen;
using Train.Core.Content.Voxels;

namespace Train.Core.Content.Markets;

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
/// <remarks>数据种类：结构。Sensitivity 为有限非负数；MinPrice、MaxPrice 是正 Int64 货币单位且 MinPrice ≤ MaxPrice。范围由解析器校验。</remarks>
/// <example>粮食为敏感度 0.6、价格区间 60–180；见 MarketSettlementExamples.GrainPrice。</example>
public sealed class MarketPriceRule
{
    /// <summary>获取价格公式的无量纲敏感度。</summary>
    public double Sensitivity { get; }
    /// <summary>获取四舍五入后报价的货币下限。</summary>
    public long MinPrice { get; }
    /// <summary>获取四舍五入后报价的货币上限。</summary>
    public long MaxPrice { get; }

    /// <summary>保存解析器已校验的三个价格参数。</summary>
    internal MarketPriceRule(double sensitivity, long minPrice, long maxPrice)
    {
        Sensitivity = sensitivity;
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

    /// <summary>保存已校验定义，并冻结价格规则映射。</summary>
    internal MarketTableDefinition(
        MarketTableDefinitionId id, IReadOnlyDictionary<VoxelDefinitionId, MarketPriceRule> goods)
    {
        Id = id;
        Goods = goods.ToFrozenDictionary();
    }
}
