// 属于内容层的 Behavior 定义模块。
// 表示 track_contact Behavior 当前内容格式中的参数。
namespace Train.Core.Content.Behaviors;

/// <summary>表示轨道接触 Behavior 的滑行减速度；内容加载通过 <see cref="TryCreate"/> 保证该值为有限正数。</summary>
/// <param name="CoastDeceleration">无动力滑行减速度，单位为体素/秒²；内容中的有效值必须为有限正数，<c>0</c> 无效。</param>
/// <example>默认滑行减速度为 <c>1</c>；见 <c>TrainCouplerCoastTests.Default_and_explicit_track_contact_share_a_semantic_revision</c>。</example>
public sealed record TrackContactParameters(double CoastDeceleration)
{
    /// <summary>内容省略滑行减速度时采用的固定值，1 体素/秒²；这是当前内容格式的默认值，不是单位换算常量。</summary>
    public const double DefaultCoastDeceleration = 1;

    /// <summary>获取省略 track_contact 参数时采用的完整默认参数。</summary>
    public static TrackContactParameters Default { get; } = new(DefaultCoastDeceleration);

    /// <summary>尝试从有限正数创建轨道接触参数。</summary>
    /// <param name="coastDeceleration">滑行减速度，单位为体素/秒²；必须为有限正数，<c>0</c> 无效。</param>
    /// <param name="parameters">成功时为创建的参数；失败时为 <see langword="null"/>。</param>
    /// <returns>输入为有限正数时返回 <see langword="true"/>。</returns>
    /// <example><c>TryCreate(1, out parameters)</c> 成功并产生默认等价值。</example>
    public static bool TryCreate(double coastDeceleration, out TrackContactParameters? parameters)
    {
        parameters = null;
        if (!double.IsFinite(coastDeceleration) || coastDeceleration <= 0)
            return false;
        parameters = new TrackContactParameters(coastDeceleration);
        return true;
    }
}
