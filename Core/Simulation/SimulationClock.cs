// Train.Core.Simulation 提供核心模拟层共用的定步长时间换算。
// 此文件定义步长，并将宿主帧间隔累计为完整模拟步和显示插值余量。

namespace Train.Core.Simulation;

/// <summary>定义核心模拟层在一次游戏构建中固定的模拟频率和步长。</summary>
/// <example>宿主累计 1/30 秒时，对应两个 1/60 秒的模拟步；见 <c>SimulationSchedulerTests.Display_rates_collect_the_same_step_count</c>。</example>
public static class SimulationClock
{
    /// <summary>每秒的模拟步数，固定为 60 步。</summary>
    /// <remarks>由时间设计确定的构建常量；改变它会改变模拟结果，不作为玩法调参数据。</remarks>
    /// <seealso href="../../docs/design/04-deterministic-simulation.md#时钟与倍速"/>
    public const double StepsPerSecond = 60;

    /// <summary>一个模拟步对应的时间，单位为秒，固定为 1/60 秒。</summary>
    /// <seealso cref="StepsPerSecond"/>
    public const double StepDuration = 1.0 / StepsPerSecond;
}

/// <summary>累计宿主显示帧的时间，计算应推进的完整模拟步数和用于显示的剩余比例。</summary>
/// <remarks>不执行模拟，也不保存世界步号。暂停调用不累计时间；实例由单一宿主线程使用，不提供并发同步。</remarks>
/// <example>先累计 0.4 步，再累计 0.7 步，分别返回 0 和 1 步；见 <c>SimulationSchedulerTests.Accumulator_preserves_fractional_steps_and_display_alpha</c>。</example>
public sealed class SimulationScheduler
{
    /// <summary>取整前允许的步数误差，单位为步，暂沿用 1e-9；用途和依据见模块说明。</summary>
    /// <seealso href="README.md#数值约定"/>
    private const double StepRoundingTolerance = 1e-9;

    private double _accumulator;

    /// <summary>获取尚未换算为完整模拟步的累计时间，单位为秒；初始及重置后为零。</summary>
    public double Remainder => _accumulator;

    /// <summary>获取累计余量占一个模拟步的比例，并将显示结果限制在 0–1。</summary>
    /// <remarks>零表示没有剩余时间；它只供显示插值使用，不推进核心模拟层。</remarks>
    public double DisplayAlpha =>
        Math.Clamp(_accumulator / SimulationClock.StepDuration, 0, 1);

    /// <summary>丢弃尚未换算为完整步的累计时间，使显示插值比例归零。</summary>
    public void Reset() => _accumulator = 0;

    /// <summary>累计未暂停的有效帧间隔，返回当前应推进的完整模拟步数并扣除对应时间。</summary>
    /// <param name="displayDelta">宿主显示帧经过的时间，单位为秒；非正或非有限值被忽略，不修改余量。</param>
    /// <param name="paused">是否暂停宿主推进；为 true 时忽略本次间隔，保留之前的余量。</param>
    /// <returns>应推进的完整模拟步数；暂停、无效间隔或累计时间不足一步时为零。</returns>
    /// <remarks>调用方负责提供适合帧驱动的时间间隔。本方法不限制单次推进量，也不实施倍速或执行模拟。</remarks>
    /// <example>余量为 0.4 步时，暂停期间传入 1 秒仍返回零且保留余量；见 <c>SimulationSchedulerTests.Pause_and_invalid_intervals_preserve_remainder</c>。</example>
    public ulong CollectSteps(double displayDelta, bool paused)
    {
        if (paused)
            return 0;
        if (!double.IsFinite(displayDelta) || displayDelta <= 0)
            return 0;

        _accumulator += displayDelta;
        var raw = _accumulator * SimulationClock.StepsPerSecond;
        // 容忍整步边界附近的浮点误差，再清除因此产生的微小负余量。
        var steps = raw <= 0 ? 0UL : (ulong)Math.Floor(raw + StepRoundingTolerance);
        _accumulator -= steps * SimulationClock.StepDuration;
        if (_accumulator < 0)
            _accumulator = 0;
        return steps;
    }
}
