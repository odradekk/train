using Train.Core.Contracts;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    internal sealed class SoakSample(string view, SimulationSpeed speed, double started, ulong steps,
        TimeSpan gcPause, int[] gcCollections)
    {
        internal FrameMeasurements Frames { get; } = new(started, steps);
        internal Dictionary<string, int> Classes { get; } = new(StringComparer.Ordinal);

        /// <summary>结束视图窗口，输出同窗帧统计和进程 GC 累计读数的差值；不包含窗外主动 GC。</summary>
        internal (MeasurementTiming Timing, object Record) Finish(int segment, TimeSpan gcEndPause, int[] gcEndCollections)
        {
            var timing = Frames.Summarize();
            return (timing, new
            {
                segment, view, speed = (int)speed, started, seconds = timing.ElapsedSeconds,
                frames = timing.Frames.Length, frame_seconds = timing.FrameSeconds,
                over_16_7_ms_share = timing.Frames.Count(frame => frame.IntervalSeconds > 1d / 60) / (double)timing.Frames.Length,
                steps_per_second = timing.CompletedSteps / timing.ElapsedSeconds, target_steps_per_second = (int)speed * SimulationTime.StepsPerSecond,
                paused_frames = timing.Frames.Count(frame => frame.Paused),
                simulation_budget_frames = timing.SimulationBudgetReachedFrames, load_classes = Classes,
                pause_seconds = (gcEndPause - gcPause).TotalSeconds,
                collections_by_generation = Enumerable.Range(0, 3).Select(generation => gcEndCollections[generation] - gcCollections[generation]).ToArray()
            });
        }
    }

    private async Task SampleSoakView(string view, SimulationSpeed speed, Func<bool>? ended = null, bool setSpeed = true)
    {
        var host = _app.Host!;
        if (setSpeed && (host.SetSpeed(speed) is not null || host.SetPaused(false) is not null))
            throw new InvalidOperationException("不能设置长测倍速。");
        _scenePhase = $"{view}-{(int)speed}x";
        var started = _sceneLastDraw;
        var sample = new SoakSample(view, speed, started, host.GetStatus()!.View.Position.CompletedSteps,
            GC.GetTotalPauseDuration(), Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray());
        _soakSample = sample;
        await Wait(() => _sceneLastDraw >= started + SoakSampleSeconds || ended?.Invoke() == true && sample.Classes.Count > 0);
        _soakSample = null;
        var (timing, record) = sample.Finish(_soakSegment, GC.GetTotalPauseDuration(),
            Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray());
        var key = $"{view}/{(int)speed}x";
        if (!_soakTotals.TryGetValue(key, out var totals)) _soakTotals[key] = totals = new();
        totals.Intervals.AddRange(timing.Frames.Select(frame => frame.IntervalSeconds));
        foreach (var (name, count) in sample.Classes) totals.Classes[name] = totals.Classes.GetValueOrDefault(name) + count;
        totals.Seconds += timing.ElapsedSeconds;
        totals.Steps += timing.CompletedSteps;
        _soakSamples.Add(record);
    }

}
