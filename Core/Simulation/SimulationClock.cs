
namespace Train.Core.Simulation;

public static class SimulationClock
{
    public const double StepsPerSecond = 60;

    public const double StepDuration = 1.0 / StepsPerSecond;
}

public sealed class SimulationScheduler
{
    private const double StepRoundingTolerance = 1e-9;

    private double _accumulator;

    public double Remainder => _accumulator;

    public double DisplayAlpha =>
        Math.Clamp(_accumulator / SimulationClock.StepDuration, 0, 1);

    public void Reset() => _accumulator = 0;

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
