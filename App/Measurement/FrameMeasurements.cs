using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.App.Measurement;

internal sealed record MeasuredFrame(double ElapsedSeconds, double IntervalSeconds, ulong CompletedSteps,
    double SimulationSeconds, SimulationSpeed RequestedSpeed, bool Paused, FollowFrame? Follow = null);

internal readonly record struct FollowFrame(ulong TrainId, string PositionKind, ulong PositionId, ulong? DisplaySpace);

internal sealed record SpeedWindow(int TargetSecond, double StartedSeconds, double EndedSeconds,
    ulong CompletedSteps, double ActualSpeed);

internal sealed record TimingDistribution(double P50, double P95, double P99, double Maximum);

internal sealed record MeasurementTiming(ulong InitialCompletedSteps, ulong CompletedSteps, double ElapsedSeconds,
    double ActualSpeed, TimingDistribution FrameSeconds, TimingDistribution SimulationSeconds,
    double SimulationBudgetSeconds, int SimulationBudgetReachedFrames, ImmutableArray<MeasuredFrame> Frames,
    ImmutableArray<SpeedWindow> SpeedWindows, double? WindowMedianActualSpeed, double? WindowMinimumActualSpeed);

internal sealed class FrameMeasurements(double startedSeconds, ulong initialCompletedSteps)
{
    private readonly List<MeasuredFrame> _frames = [];
    private readonly double _startedSeconds = startedSeconds;
    private readonly ulong _initialCompletedSteps = initialCompletedSteps;
    private double _previousSeconds = startedSeconds;
    private ulong _previousSteps = initialCompletedSteps;

    internal void AddFrame(double seconds, ulong completedSteps, double simulationSeconds,
        SimulationSpeed requestedSpeed, bool paused, FollowFrame? follow = null)
    {
        if (!double.IsFinite(seconds) || seconds <= _previousSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), "绘制帧的单调时间必须递增。");
        if (completedSteps < _previousSteps)
            throw new ArgumentOutOfRangeException(nameof(completedSteps), "测量期间不能更换模拟起点。");
        if (!double.IsFinite(simulationSeconds) || simulationSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(simulationSeconds));
        _frames.Add(new(seconds - _startedSeconds, seconds - _previousSeconds, completedSteps,
            simulationSeconds, requestedSpeed, paused, follow));
        _previousSeconds = seconds;
        _previousSteps = completedSteps;
    }

    internal MeasurementTiming Summarize(bool includeSpeedWindows = false)
    {
        if (_frames.Count == 0) throw new InvalidOperationException("没有真实绘制帧样本。");
        var elapsed = _previousSeconds - _startedSeconds;
        var completed = _previousSteps - _initialCompletedSteps;
        var windows = ImmutableArray.CreateBuilder<SpeedWindow>();
        if (includeSpeedWindows)
        {
            var boundarySeconds = 0d;
            var boundarySteps = _initialCompletedSteps;
            var targetSecond = 1;
            foreach (var frame in _frames)
            {
                if (frame.ElapsedSeconds < targetSecond) continue;
                if (frame.ElapsedSeconds >= targetSecond + 1)
                    throw new InvalidOperationException("单个绘制帧跨越多个目标秒，无法形成连续的非零采样窗口。");
                var steps = frame.CompletedSteps - boundarySteps;
                windows.Add(new(targetSecond, boundarySeconds, frame.ElapsedSeconds, steps,
                    steps / (double)SimulationTime.StepsPerSecond / (frame.ElapsedSeconds - boundarySeconds)));
                boundarySeconds = frame.ElapsedSeconds;
                boundarySteps = frame.CompletedSteps;
                targetSecond++;
            }
        }
        var speeds = windows.Select(window => window.ActualSpeed).Order().ToArray();
        double? median = speeds.Length == 0 ? null : speeds.Length % 2 == 0
            ? (speeds[speeds.Length / 2 - 1] + speeds[speeds.Length / 2]) / 2
            : speeds[speeds.Length / 2];
        return new(_initialCompletedSteps, completed, elapsed,
            completed / (double)SimulationTime.StepsPerSecond / elapsed,
            Distribution(_frames.Select(frame => frame.IntervalSeconds)),
            Distribution(_frames.Select(frame => frame.SimulationSeconds)),
            SimulationFrameDriver.FrameBudgetSeconds,
            _frames.Count(frame => frame.SimulationSeconds >= SimulationFrameDriver.FrameBudgetSeconds),
            _frames.ToImmutableArray(), windows.ToImmutable(), median, speeds.Length == 0 ? null : speeds[0]);
    }

    internal static TimingDistribution Distribution(IEnumerable<double> samples)
    {
        var sorted = samples.Order().ToArray();
        // Nearest-rank：取排序后第 ceil(p × N) 个样本，不插值。
        double At(double fraction) => sorted[(int)Math.Ceiling(fraction * sorted.Length) - 1];
        return new(At(0.50), At(0.95), At(0.99), sorted[^1]);
    }

    internal static double SumOverlap(IEnumerable<(double Started, double Ended)> samples, double start, double end) =>
        samples.Sum(sample => Math.Max(0, Math.Min(end, sample.Ended) - Math.Max(start, sample.Started)));
}
