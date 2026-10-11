// 属于内容层的 Behavior 定义模块。
// 识别当前可用于数据内容的 Behavior 类型。
namespace Train.Content.Behaviors;

/// <summary>定义当前内容格式允许声明的 Behavior 类型标记。</summary>
/// <example>推进部件在 JSON 中使用 <c>"type":"propulsion"</c>；见 <c>ContentReloaderTests.Combined_behaviors_load_as_typed_data_and_affect_semantic_revision</c>。</example>
public static class BehaviorTypes
{
    /// <summary>推进行为在 JSON <c>type</c> 字段中的固定值；内容解析与语义编码依赖精确值 <c>propulsion</c>。</summary>
    public const string Propulsion = "propulsion";
    /// <summary>列车控制行为在 JSON <c>type</c> 字段中的固定值；内容解析与语义编码依赖精确值 <c>control</c>。</summary>
    public const string Control = "control";
    /// <summary>货舱行为在 JSON <c>type</c> 字段中的固定值；内容解析与语义编码依赖精确值 <c>cargo</c>。</summary>
    public const string Cargo = "cargo";
}

/// <summary>识别已注册的 Behavior 类型标记。</summary>
/// <example><c>propulsion</c>、<c>control</c> 和 <c>cargo</c> 已注册；见 <c>ContentReloaderTests.Combined_behaviors_load_as_typed_data_and_affect_semantic_revision</c>。</example>
public static class BehaviorRegistry
{
    /// <summary>取得已注册行为的固定类型标记；定义构造按它排列行为，规范编码用它写出 type 字段。</summary>
    /// <exception cref="InvalidOperationException">行为不属于已注册的三种类型。</exception>
    internal static string TypeOf(VoxelBehavior behavior) => behavior switch
    {
        PropulsionBehavior => BehaviorTypes.Propulsion,
        ControlBehavior => BehaviorTypes.Control,
        CargoBehavior => BehaviorTypes.Cargo,
        _ => throw new InvalidOperationException($"Unsupported behavior type {behavior.GetType().Name}.")
    };

    /// <summary>检查文本是否为当前已注册的 Behavior 类型标记。</summary>
    /// <param name="type">区分大小写且不修剪的类型标记；运行时传入 <see langword="null"/> 会判为未注册。</param>
    /// <returns>值为 <c>propulsion</c>、<c>control</c> 或 <c>cargo</c> 时返回 <see langword="true"/>。</returns>
    public static bool IsRegistered(string type) =>
        type is BehaviorTypes.Propulsion or BehaviorTypes.Control or BehaviorTypes.Cargo;
}
