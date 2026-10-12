// 属于内容层的 Behavior 定义模块。
// 用带参数的行为数据表示动力、控制与货舱，供核心字段和设计属性读取。

namespace Train.Content.Behaviors;

/// <summary>表示一个体素提供的一项功能行为；有效内容只包含动力、控制和货舱三种情况。</summary>
/// <remarks>
/// 一个定义可以组合不同种类的行为，每种最多出现一次；空行为列表表示结构体素。
/// 内容加载负责保证参数范围和种类有效，行为本身不保存运行状态。
/// </remarks>
/// <example>
/// 动力、控制、货舱和组合行为的数据示例见 <c>DesignAttributeExamples</c>。
/// </example>
public abstract record VoxelBehavior;

/// <summary>表示单格动力部件提供的牵引力和额定速度。</summary>
/// <param name="Traction">牵引力，单位为 kN；有效内容必须为有限正数，0 无效。</param>
/// <param name="RatedSpeed">额定速度，单位为 m/s；有效内容必须为有限正数，0 无效。</param>
/// <example>
/// <c>new PropulsionBehavior(12.5, 20)</c> 表示提供 12.5 kN、额定速度 20 m/s 的一个动力部件；
/// 见 <c>DesignAttributeExamples.Propulsion</c>。
/// </example>
public sealed record PropulsionBehavior(double Traction, double RatedSpeed) : VoxelBehavior;

/// <summary>表示单格控制部件提供的列车控制能力，没有可调数值参数。</summary>
/// <example><c>new ControlBehavior()</c> 表示一个控制部件；见 <c>DesignAttributeExamples.Control</c>。</example>
public sealed record ControlBehavior : VoxelBehavior;

/// <summary>表示单格货舱部件提供的货物单位数上限。</summary>
/// <param name="Capacity">载量，单位为货物单位；有效内容为 1 至 <see cref="int.MaxValue"/> 的整数，0 无效。</param>
/// <example><c>new CargoBehavior(4)</c> 表示载量为 4 个单位的货舱部件；见 <c>DesignAttributeExamples.Cargo</c>。</example>
public sealed record CargoBehavior(int Capacity) : VoxelBehavior;
