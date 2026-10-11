// Core 内容模块的数据示例。
// 为设计属性的函数示例准备结构、功能部件、组合行为和空序列，不依赖表现资产。

using System.Collections.Frozen;
using Train.Content.Behaviors;
using Train.Content.Voxels;

namespace Train.Content.Tests;

/// <summary>提供手工可核算的只读核心定义，供 <c>DesignAttributeTests</c> 的函数示例使用。</summary>
internal static class DesignAttributeExamples
{
    /// <summary>一个 0.01 t、Value 1、耐久上限 100 的结构体素。</summary>
    public static ICoreVoxelDefinition Structure { get; } = Create("structure", 1, 0.01);

    /// <summary>一个 4 t、Value 250、牵引力 12.5 kN、额定速度 20 m/s 的动力部件。</summary>
    public static ICoreVoxelDefinition Propulsion { get; } = Create(
        "propulsion", 250, 4, new PropulsionBehavior(12.5, 20));

    /// <summary>一个 0.74 t、Value 474 的控制部件。</summary>
    public static ICoreVoxelDefinition Control { get; } = Create(
        "control", 474, 0.74, new ControlBehavior());

    /// <summary>一个 2.37 t、Value 237、载量 4 的货舱部件。</summary>
    public static ICoreVoxelDefinition Cargo { get; } = Create(
        "cargo", 237, 2.37, new CargoBehavior(4));

    /// <summary>同一格同时具有动力、控制和载量的功能部件，只贡献一次质量、Value 和 BOM 数量。</summary>
    public static ICoreVoxelDefinition Combined { get; } = Create(
        "combined", 300, 5, new PropulsionBehavior(5, 10), new ControlBehavior(), new CargoBehavior(1));

    /// <summary>空序列覆盖体素列表的空情况。</summary>
    public static IReadOnlyList<ICoreVoxelDefinition> Empty { get; } = Array.Empty<ICoreVoxelDefinition>();

    /// <summary>仅结构的序列覆盖没有动力、控制或货舱的情况。</summary>
    public static IReadOnlyList<ICoreVoxelDefinition> StructureOnly { get; } = Array.AsReadOnly(
        new[] { Structure });

    /// <summary>结构和三种功能部件各一格，作为整份设计属性的最小数据示例。</summary>
    public static IReadOnlyList<ICoreVoxelDefinition> Mixed { get; } = Array.AsReadOnly(
        new[] { Structure, Propulsion, Control, Cargo });

    /// <summary>在冻结集合中保存一个最小核心定义，不携带场景字段。</summary>
    private static ICoreVoxelDefinition Create(string localId, long value, double mass, params VoxelBehavior[] behaviors) =>
        new CoreDefinition(new VoxelDefinitionId("core", "example-" + localId), value, mass, behaviors);

    /// <summary>表示一个已经按核心字段契约取值的不可变测试定义。</summary>
    private sealed class CoreDefinition : ICoreVoxelDefinition
    {
        public VoxelDefinitionId Id { get; }
        public long Value { get; }
        public double Mass { get; }
        public int MaxDurability => 100;
        public IReadOnlySet<string> Tags { get; } = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
        public IReadOnlyList<VoxelBehavior> Behaviors { get; }

        public CoreDefinition(VoxelDefinitionId id, long value, double mass, VoxelBehavior[] behaviors)
        {
            Id = id;
            Value = value;
            Mass = mass;
            Behaviors = Array.AsReadOnly(behaviors.ToArray());
        }
    }
}
