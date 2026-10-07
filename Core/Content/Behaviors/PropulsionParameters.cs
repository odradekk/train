// 属于内容层的 Behavior 定义模块。
// 表示 propulsion Behavior 当前内容格式中的参数。
namespace Train.Core.Content.Behaviors;

/// <summary>表示推进 Behavior 的目标速度和加速度；内容加载通过 <see cref="TryCreate"/> 保证两个值均为有限正数。</summary>
/// <param name="TargetSpeed">目标速度，单位为体素/秒；内容中的有效值必须为有限正数，<c>0</c> 无效。</param>
/// <param name="Acceleration">加速度，单位为体素/秒²；内容中的有效值必须为有限正数，<c>0</c> 无效。</param>
/// <example><see cref="Default"/> 的目标速度为 <c>4</c>、加速度为 <c>2</c>；见 <c>ContentReloaderTests.Omitted_behavior_parameters_and_explicit_defaults_share_a_semantic_revision</c>。</example>
public sealed record PropulsionParameters(double TargetSpeed, double Acceleration)
{
    /// <summary>内容省略目标速度时采用的固定值，4 体素/秒；这是当前内容格式的默认值，不是单位换算常量。</summary>
    public const double DefaultTargetSpeed = 4;
    /// <summary>内容省略加速度时采用的固定值，2 体素/秒²；这是当前内容格式的默认值，不是单位换算常量。</summary>
    public const double DefaultAcceleration = 2;

    /// <summary>获取省略全部 propulsion 参数时采用的完整默认参数。</summary>
    public static PropulsionParameters Default { get; } = new(DefaultTargetSpeed, DefaultAcceleration);

    /// <summary>尝试从有限正数创建推进参数。</summary>
    /// <param name="targetSpeed">目标速度，单位为体素/秒；必须为有限正数，<c>0</c> 无效。</param>
    /// <param name="acceleration">加速度，单位为体素/秒²；必须为有限正数，<c>0</c> 无效。</param>
    /// <param name="parameters">成功时为创建的参数；失败时为 <see langword="null"/>。</param>
    /// <returns>两个输入均为有限正数时返回 <see langword="true"/>。</returns>
    /// <example><c>TryCreate(4, 2, out parameters)</c> 成功并产生对应的默认参数；见 <c>ContentReloaderTests.Omitted_behavior_parameters_and_explicit_defaults_share_a_semantic_revision</c>。</example>
    public static bool TryCreate(double targetSpeed, double acceleration, out PropulsionParameters? parameters)
    {
        parameters = null;
        if (!double.IsFinite(targetSpeed) || targetSpeed <= 0)
            return false;
        if (!double.IsFinite(acceleration) || acceleration <= 0)
            return false;
        parameters = new PropulsionParameters(targetSpeed, acceleration);
        return true;
    }
}
