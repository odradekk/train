// 属于内容层的 Behavior 定义模块。
// 识别当前可用于数据内容的 Behavior 类型并规范化其参数。
namespace Train.Core.Content.Behaviors;

/// <summary>定义当前内容格式允许声明的 Behavior 类型标记。</summary>
/// <example>推进部件在 JSON 中使用 <c>"type":"propulsion"</c>。</example>
public static class BehaviorTypes
{
    /// <summary>推进行为在 JSON <c>type</c> 字段中的固定值；内容解析与语义编码依赖精确值 <c>propulsion</c>。</summary>
    public const string Propulsion = "propulsion";
    /// <summary>列车控制行为在 JSON <c>type</c> 字段中的固定值；内容解析与语义编码依赖精确值 <c>control</c>。</summary>
    public const string Control = "control";
    /// <summary>轨道接触行为在 JSON <c>type</c> 字段中的固定值；内容解析与语义编码依赖精确值 <c>track_contact</c>。</summary>
    public const string TrackContact = "track_contact";
}

/// <summary>识别已注册的 Behavior 类型，并把可省略的运动参数规范化为完整值。</summary>
/// <example>推进参数省略值的语义等价性见 <c>TrainMotionTests.Default_and_explicit_fixture_propulsion_share_a_semantic_revision</c>。</example>
public static class BehaviorRegistry
{
    /// <summary>检查文本是否为当前已注册的 Behavior 类型标记。</summary>
    /// <param name="type">区分大小写且不修剪的类型标记；运行时传入 <see langword="null"/> 会判为未注册。</param>
    /// <returns>值为 <c>propulsion</c>、<c>control</c> 或 <c>track_contact</c> 时返回 <see langword="true"/>。</returns>
    public static bool IsRegistered(string type) =>
        type is BehaviorTypes.Propulsion or BehaviorTypes.Control or BehaviorTypes.TrackContact;

    /// <summary>用默认值补齐为null的推进参数，并验证结果是否为有限正数。</summary>
    /// <param name="targetSpeed">目标速度，单位为体素/秒；<see langword="null"/> 使用默认值 4。</param>
    /// <param name="acceleration">加速度，单位为体素/秒²；<see langword="null"/> 使用默认值 2。</param>
    /// <param name="parameters">成功时为补齐后的推进参数；失败时为完整的默认参数。</param>
    /// <returns>补齐后的两个值均为有限正数时返回 <see langword="true"/>。</returns>
    /// <example>省略参数与显式给出 <c>4</c>、<c>2</c> 的结果见 <c>TrainMotionTests.Default_and_explicit_fixture_propulsion_share_a_semantic_revision</c>。</example>
    public static bool TryNormalizePropulsion(
        double? targetSpeed,
        double? acceleration,
        out PropulsionParameters parameters)
    {
        var target = targetSpeed ?? PropulsionParameters.DefaultTargetSpeed;
        var accel = acceleration ?? PropulsionParameters.DefaultAcceleration;
        if (!PropulsionParameters.TryCreate(target, accel, out var created) || created is null)
        {
            parameters = PropulsionParameters.Default;
            return false;
        }

        parameters = created;
        return true;
    }

    /// <summary>用默认值补齐为null轨道接触参数，并验证结果是否为有限正数。</summary>
    /// <param name="coastDeceleration">无动力滑行减速度，单位为体素/秒²；<see langword="null"/> 使用默认值 1。</param>
    /// <param name="parameters">成功时为补齐后的轨道接触参数；失败时为完整的默认参数。</param>
    /// <returns>补齐后的滑行减速度为有限正数时返回 <see langword="true"/>。</returns>
    /// <example>省略参数与显式给出 <c>1</c> 的结果见 <c>TrainCouplerCoastTests.Default_and_explicit_track_contact_share_a_semantic_revision</c>。</example>
    public static bool TryNormalizeTrackContact(
        double? coastDeceleration,
        out TrackContactParameters parameters)
    {
        var deceleration = coastDeceleration ?? TrackContactParameters.DefaultCoastDeceleration;
        if (!TrackContactParameters.TryCreate(deceleration, out var created) || created is null)
        {
            parameters = TrackContactParameters.Default;
            return false;
        }

        parameters = created;
        return true;
    }
}
