// 属于内容层的蓝图定义模块。
// 按蓝图体素顺序汇总已解析的核心字段，输出设计属性及 BOM。

using Train.Core.Content.Behaviors;
using Train.Core.Content.Voxels;

namespace Train.Core.Content.Composites;

/// <summary>从已解析的体素核心定义计算设计属性，不读取布局、表现或碰撞。</summary>
/// <example>最小结构与三种功能部件的输入见 <c>DesignAttributeExamples</c>。</example>
public static class DesignAttributeCalculator
{
    /// <summary>汇总每个体素的质量、价值、结构耐久和功能行为，并按身份累计 BOM。</summary>
    /// <param name="voxels">
    /// 同一候选目录解析出的只读核心定义序列，每个蓝图体素对应一项，允许重复身份和空序列。
    /// 输入及元素不得为 null；定义均满足核心字段契约，次序由蓝图的坐标排序确定。
    /// </param>
    /// <returns>
    /// 不可变的设计属性；牵引力和载量相加，额定速度取最小值，控制能力表示是否存在控制行为。
    /// 仅无功能行为的体素贡献结构耐久。空序列返回 <see cref="DesignAttributes.Empty"/>。
    /// </returns>
    /// <remarks>
    /// 本函数只读取核心字段，不改变输入或目录状态。
    /// </remarks>
    /// <exception cref="ArgumentNullException">输入序列或其中的定义为 null。</exception>
    /// <exception cref="OverflowException">汇总属性超过可表示范围，或浮点汇总不再为有限值。</exception>
    /// <example>
    /// 结构、动力、控制、货舱各一格得到质量 7.12 t、BOM 总 Value 962、
    /// 牵引力 12.5 kN、额定速度 20 m/s、载量 4、结构耐久上限 100，并具有控制能力。
    /// </example>
    public static DesignAttributes Calculate(IReadOnlyList<ICoreVoxelDefinition> voxels)
    {
        ArgumentNullException.ThrowIfNull(voxels);
        var mass = 0d;
        var traction = 0d;
        double? ratedSpeed = null;
        var cargoCapacity = 0L;
        var structuralDurability = 0L;
        var hasControl = false;
        var bom = new Dictionary<VoxelDefinitionId, int>();
        var bomValue = 0L;
        foreach (var voxel in voxels)
        {
            ArgumentNullException.ThrowIfNull(voxel);
            mass += voxel.Mass;
            bom[voxel.Id] = checked(bom.GetValueOrDefault(voxel.Id) + 1);
            bomValue = checked(bomValue + voxel.Value);
            if (voxel.Behaviors.Count == 0)
                structuralDurability = checked(structuralDurability + voxel.MaxDurability);
            foreach (var behavior in voxel.Behaviors)
            {
                switch (behavior)
                {
                    case PropulsionBehavior propulsion:
                        traction += propulsion.Traction;
                        ratedSpeed = ratedSpeed is { } speed ? Math.Min(speed, propulsion.RatedSpeed) : propulsion.RatedSpeed;
                        break;
                    case ControlBehavior:
                        hasControl = true;
                        break;
                    case CargoBehavior cargo:
                        cargoCapacity = checked(cargoCapacity + cargo.Capacity);
                        break;
                }
            }
        }
        if (!double.IsFinite(mass) || !double.IsFinite(traction))
            throw new OverflowException("Design attribute sums must remain finite.");
        return new DesignAttributes(mass, traction, ratedSpeed, cargoCapacity, structuralDurability, hasControl, bom, bomValue);
    }
}
