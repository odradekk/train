using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.App.Measurement;

internal sealed record MeasuredFrame(double ElapsedSeconds, double IntervalSeconds, ulong CompletedSteps,
    double SimulationSeconds, SimulationSpeed RequestedSpeed, bool Paused);

internal sealed record TimingDistribution(double P50, double P95, double P99, double Maximum);

internal sealed record MeasurementTiming(ulong InitialCompletedSteps, ulong CompletedSteps, double ElapsedSeconds,
    double ActualSpeed, TimingDistribution FrameSeconds, TimingDistribution SimulationSeconds,
    double SimulationBudgetSeconds, int SimulationBudgetReachedFrames, ImmutableArray<MeasuredFrame> Frames);

internal sealed class FrameMeasurements(double startedSeconds, ulong initialCompletedSteps)
{
    private readonly List<MeasuredFrame> _frames = [];
    private readonly double _startedSeconds = startedSeconds;
    private readonly ulong _initialCompletedSteps = initialCompletedSteps;
    private double _previousSeconds = startedSeconds;
    private ulong _previousSteps = initialCompletedSteps;

    internal void AddFrame(double seconds, ulong completedSteps, double simulationSeconds,
        SimulationSpeed requestedSpeed, bool paused)
    {
        if (!double.IsFinite(seconds) || seconds <= _previousSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), "绘制帧的单调时间必须递增。");
        if (completedSteps < _previousSteps)
            throw new ArgumentOutOfRangeException(nameof(completedSteps), "测量期间不能更换模拟起点。");
        if (!double.IsFinite(simulationSeconds) || simulationSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(simulationSeconds));
        _frames.Add(new(seconds - _startedSeconds, seconds - _previousSeconds, completedSteps,
            simulationSeconds, requestedSpeed, paused));
        _previousSeconds = seconds;
        _previousSteps = completedSteps;
    }

    internal MeasurementTiming Summarize()
    {
        if (_frames.Count == 0) throw new InvalidOperationException("没有真实绘制帧样本。");
        var elapsed = _previousSeconds - _startedSeconds;
        var completed = _previousSteps - _initialCompletedSteps;
        return new(_initialCompletedSteps, completed, elapsed,
            completed / (double)SimulationTime.StepsPerSecond / elapsed,
            Distribution(_frames.Select(frame => frame.IntervalSeconds)),
            Distribution(_frames.Select(frame => frame.SimulationSeconds)),
            SimulationFrameDriver.FrameBudgetSeconds,
            _frames.Count(frame => frame.SimulationSeconds >= SimulationFrameDriver.FrameBudgetSeconds),
            _frames.ToImmutableArray());
    }

    internal static TimingDistribution Distribution(IEnumerable<double> samples)
    {
        var sorted = samples.Order().ToArray();
        // Nearest-rank：取排序后第 ceil(p × N) 个样本，不插值。
        double At(double fraction) => sorted[(int)Math.Ceiling(fraction * sorted.Length) - 1];
        return new(At(0.50), At(0.95), At(0.99), sorted[^1]);
    }
}
