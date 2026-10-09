// 属于内容层的蓝图定义模块。
// 保存由当前体素定义派生的设计属性和 BOM，不携带体素布局或实例状态。

using System.Collections.Frozen;
using Train.Core.Content.Voxels;

namespace Train.Core.Content.Composites;

/// <summary>表示一个蓝图版本按当前体素定义计算出的不可变设计属性。</summary>
/// <remarks>
/// 所有数值为非负值，浮点数必须有限；没有动力时额定速度为 null，有动力时为正数。
/// BOM 按身份计数，每项数量为正整数；空输入得到零属性和空 BOM。
/// 本数据由内容层计算，不保存损伤、库存或其他实例状态。
/// </remarks>
/// <example>
/// 各一个 <c>DesignAttributeExamples.Structure</c>、<c>Propulsion</c>、<c>Control</c>、<c>Cargo</c>：
/// 质量 7.12 t、BOM 总 Value 962、牵引力 12.5 kN、额定速度 20 m/s、载量 4、结构耐久上限 100，并具有控制能力。
/// </example>
public sealed class DesignAttributes
{
    /// <summary>获取全部体素的质量之和，单位为 t；空输入为 0。</summary>
    public double Mass { get; }

    /// <summary>获取全部动力行为的牵引力之和，单位为 kN；没有动力时为 0。</summary>
    public double Traction { get; }

    /// <summary>获取动力行为额定速度中的最小值，单位为 m/s；没有动力时为 null。</summary>
    public double? RatedSpeed { get; }

    /// <summary>获取全部货舱行为载量之和，单位为货物单位；没有货舱时为 0。</summary>
    public long CargoCapacity { get; }

    /// <summary>获取没有功能行为的体素耐久上限之和；部件耐久不计入此值，空结构为 0。</summary>
    public long StructuralDurabilityMax { get; }

    /// <summary>获取设计是否包含至少一个控制行为；不表示实例的控制部件当前完好。</summary>
    public bool HasControl { get; }

    /// <summary>获取不可变的 BOM：体素定义身份到消耗单位数的映射；不保证枚举顺序。</summary>
    public IReadOnlyDictionary<VoxelDefinitionId, int> Bom { get; }

    /// <summary>获取 BOM 中全部体素单位的基本货币价值之和；空 BOM 为 0。</summary>
    public long BomValue { get; }

    /// <summary>保存内容层已经汇总的属性，并冻结 BOM 为只读映射。</summary>
    /// <param name="mass">全部体素的有限非负质量，单位为 t。</param>
    /// <param name="traction">全部动力行为的有限非负牵引力，单位为 kN。</param>
    /// <param name="ratedSpeed">最小额定速度，单位为 m/s；无动力时为 null。</param>
    /// <param name="cargoCapacity">货舱提供的非负载量，单位为货物单位。</param>
    /// <param name="structuralDurabilityMax">结构体素提供的非负耐久上限。</param>
    /// <param name="hasControl">是否存在控制行为。</param>
    /// <param name="bom">按身份累计的正整数消耗量；空输入时为空。</param>
    /// <param name="bomValue">BOM 按当前定义计算的非负总 Value。</param>
    internal DesignAttributes(
        double mass,
        double traction,
        double? ratedSpeed,
        long cargoCapacity,
        long structuralDurabilityMax,
        bool hasControl,
        IReadOnlyDictionary<VoxelDefinitionId, int> bom,
        long bomValue)
    {
        Mass = mass;
        Traction = traction;
        RatedSpeed = ratedSpeed;
        CargoCapacity = cargoCapacity;
        StructuralDurabilityMax = structuralDurabilityMax;
        HasControl = hasControl;
        Bom = bom.ToFrozenDictionary();
        BomValue = bomValue;
    }
}
