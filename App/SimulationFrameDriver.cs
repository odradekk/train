using System.Diagnostics;
using Train.Core.Contracts;

namespace Train.App;

internal enum SimulationSpeed { One = 1, Two = 2, Four = 4, Eight = 8 }

internal sealed class SimulationFrameDriver(Func<double>? getRealSeconds = null)
{
    // 沿用旧宿主的暂定参数；性能预算和统计窗口仍待测量。
    private const double FrameBudgetSeconds = 0.005;
    private const double SpeedSampleSeconds = 1;
    private readonly Func<double> _seconds = getRealSeconds ??
        (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    private double _sampleStarted = (getRealSeconds ??
        (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency))();
    private ulong _sampleSteps;
    private double _actualSpeed;

    internal bool PlayerPaused { get; private set; } = true;
    internal bool FocusPaused { get; private set; }
    internal bool OperationLocked { get; private set; }
    internal bool Frozen { get; private set; }
    internal bool IsPaused => PlayerPaused || FocusPaused || OperationLocked || Frozen;
    internal SimulationSpeed Speed { get; private set; } = SimulationSpeed.One;
    internal double Remainder { get; private set; }
    internal double ActualSpeed => IsPaused ? 0 : _actualSpeed;

    internal void SetPlayerPaused(bool value) { PlayerPaused = value; ResetSample(); }
    internal void SetFocusPaused(bool value) { FocusPaused = value; ResetSample(); }
    internal void SetOperationLocked(bool value) { OperationLocked = value; ResetSample(); }
    internal void Freeze() { Frozen = true; ResetSample(); }
    internal void SetSpeed(SimulationSpeed value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        Speed = value;
        ResetSample();
    }

    internal ulong AdvanceFrame(double delta, Func<(uint Completed, bool Stop)> advanceOne)
    {
        if (IsPaused || !double.IsFinite(delta) || delta <= 0) return 0;
        var scaled = delta * (int)Speed + Remainder;
        if (!double.IsFinite(scaled) || scaled * SimulationTime.StepsPerSecond >= (double)ulong.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(delta));
        var allowance = (ulong)Math.Floor(scaled * SimulationTime.StepsPerSecond + 1e-9);
        Remainder = Math.Max(0, scaled - allowance * SimulationTime.StepSeconds);
        var started = _seconds();
        var completed = 0UL;
        for (var step = 0UL; step < allowance; step++)
        {
            var result = advanceOne();
            completed += result.Completed;
            if (result.Completed == 0 || result.Stop || IsPaused || _seconds() - started >= FrameBudgetSeconds) break;
        }
        _sampleSteps = checked(_sampleSteps + completed);
        var now = _seconds();
        var elapsed = now - _sampleStarted;
        _actualSpeed = elapsed > 0 ? _sampleSteps / (double)SimulationTime.StepsPerSecond / elapsed : 0;
        if (elapsed >= SpeedSampleSeconds) { _sampleStarted = now; _sampleSteps = 0; }
        return completed;
    }

    internal ulong Step(ulong steps, Func<(uint Completed, bool Stop)> advanceOne)
    {
        var completed = 0UL;
        for (var step = 0UL; step < steps; step++)
        {
            var result = advanceOne();
            completed += result.Completed;
            if (result.Completed == 0 || result.Stop || Frozen) break;
        }
        return completed;
    }

    private void ResetSample() { _sampleStarted = _seconds(); _sampleSteps = 0; _actualSpeed = 0; }
}
