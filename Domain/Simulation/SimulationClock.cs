namespace Train.Domain.Simulation;

public static class SimulationClock
{
    public const double StepsPerSecond = 60;
    public const double StepDuration = 1.0 / 60.0;
}

public sealed class SimulationScheduler
{
    private double _accumulator;

    public void Reset() => _accumulator = 0;

    public ulong CollectSteps(double displayDelta, bool paused)
    {
        if (paused)
            return 0;
        if (!double.IsFinite(displayDelta) || displayDelta <= 0)
            return 0;

        _accumulator += displayDelta;
        var raw = _accumulator * SimulationClock.StepsPerSecond;
        var steps = raw <= 0 ? 0UL : (ulong)Math.Floor(raw + 1e-9);
        _accumulator -= steps * SimulationClock.StepDuration;
        if (_accumulator < 0)
            _accumulator = 0;
        return steps;
    }
}
