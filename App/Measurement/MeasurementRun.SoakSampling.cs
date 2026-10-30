using System.Text.Json;
using Train.Core.Contracts;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    private const string SoakGcMethod = "Process-local GC.GetTotalPauseDuration and GC.CollectionCount deltas per sample. Read immediately before waiting and after the final draw; seconds retains the original previous-draw to final-draw span. Pause share is an upper bound including collections caused by measurement allocations such as per-frame serialization and world counts; no allocation overhead is subtracted. Collection counts are not pause-event counts. Measurement-forced collections outside samples are recorded in scene-collections.jsonl and excluded.";

    internal sealed class SoakSample(string view, SimulationSpeed speed, double started, ulong steps,
        GcReading gcStart)
    {
        internal FrameMeasurements Frames { get; } = new(started, steps);
        internal Dictionary<string, int> Classes { get; } = new(StringComparer.Ordinal);

        // GC 读数在等待窗口前后取得；帧统计仍从此前一帧起算，窗外主动 GC 不计入。
        internal (MeasurementTiming Timing, object Record) Finish(int segment, GcReading gcEnd)
        {
            var timing = Frames.Summarize();
            var gc = gcEnd.Since(gcStart);
            return (timing, new
            {
                segment, view, speed = (int)speed, started, seconds = timing.ElapsedSeconds,
                frames = timing.Frames.Length, frame_seconds = timing.FrameSeconds,
                over_16_7_ms_share = timing.Frames.Count(frame => frame.IntervalSeconds > 1d / 60) / (double)timing.Frames.Length,
                steps_per_second = timing.CompletedSteps / timing.ElapsedSeconds, target_steps_per_second = (int)speed * SimulationTime.StepsPerSecond,
                paused_frames = timing.Frames.Count(frame => frame.Paused),
                simulation_budget_frames = timing.SimulationBudgetReachedFrames, load_classes = Classes,
                pause_seconds = gc.Pause.TotalSeconds,
                collections_by_generation = gc.CollectionsByGeneration
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
            GcReading.Current);
        _soakSample = sample;
        if (!_soakTargets.Standard)
        {
            // 后台任务可以跨窗口完成；其结果必须晚于新窗口的起点，才能给帧分类。
            var sampling = Seconds();
            _soakWindow.Begin(host.GetStatus()!.Binding, sampling);
            _soakLoadTakenAt = 0;
            _soakLoad = null;
            _soakLoadClass = "unknown";
            _sceneOperationTrace!.WriteLine(JsonSerializer.Serialize(new { kind = "soak-load", at = sampling, applied = sampling,
                segment = _soakSegment, phase = _scenePhase, load_class = "unknown", counts = (object?)null }, SceneJson));
            if (_soakCounting is null) StartSoakCount(host.GetStatus()!);
        }
        await Wait(() => _sceneLastDraw >= started + SoakSampleSeconds || ended?.Invoke() == true && sample.Classes.Count > 0);
        _soakSample = null;
        var (timing, record) = sample.Finish(_soakSegment, GcReading.Current);
        var key = $"{view}/{(int)speed}x";
        if (!_soakTotals.TryGetValue(key, out var totals)) _soakTotals[key] = totals = new();
        totals.Intervals.AddRange(timing.Frames.Select(frame => frame.IntervalSeconds));
        foreach (var (name, count) in sample.Classes) totals.Classes[name] = totals.Classes.GetValueOrDefault(name) + count;
        totals.Seconds += timing.ElapsedSeconds;
        totals.Steps += timing.CompletedSteps;
        _soakSamples.Add(record);
    }

}
