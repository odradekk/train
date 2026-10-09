// 属于内容层的体素定义模块。
// 限定核心规则可以读取的体素字段，表现、坐标、朝向和碰撞由其他读取方处理。

using Train.Core.Content.Behaviors;

namespace Train.Core.Content.Voxels;

/// <summary>提供一个不可变体素定义的核心字段，不暴露显示文本或场景数据。</summary>
/// <remarks>
/// 实现提供已经内容校验的只读快照：身份有效，数值在规定范围内，标签已规范化，行为种类不重复。
/// 同一候选目录中的相同身份解析为同一份定义。
/// </remarks>
/// <example>结构、动力、控制和货舱的最小定义见 <c>DesignAttributeExamples</c>。</example>
public interface ICoreVoxelDefinition
{
    /// <summary>获取体素定义的稳定身份。</summary>
    VoxelDefinitionId Id { get; }

    /// <summary>获取一个单位的基本货币价值，范围为 1 至 1,000,000,000。</summary>
    long Value { get; }

    /// <summary>获取一个单位的质量，单位为 t；有效值为 (0, 1,000,000,000] 内的有限数。</summary>
    double Mass { get; }

    /// <summary>获取单格的耐久上限，范围为 1 至 1,000,000,000。</summary>
    int MaxDurability { get; }

    /// <summary>获取按 Ordinal 比较且不可变的规范化标签集合。</summary>
    IReadOnlySet<string> Tags { get; }

    /// <summary>获取不可变的功能行为列表；为空时该体素属于结构，各种行为最多出现一次，顺序没有语义。</summary>
    IReadOnlyList<VoxelBehavior> Behaviors { get; }
}
