using Train.App.Recording;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    private object SummarizeSoak(double seconds, double body)
    {
        const double Hour = 3600;
        static double? Slope(IReadOnlyList<(double X, double Y)> points)
        {
            if (points.Count < 2) return null;
            var meanX = points.Average(point => point.X);
            var meanY = points.Average(point => point.Y);
            var denominator = points.Sum(point => (point.X - meanX) * (point.X - meanX));
            return denominator == 0 ? null : points.Sum(point => (point.X - meanX) * (point.Y - meanY)) / denominator;
        }
        static double? Median(IEnumerable<double> values)
        {
            var array = values.ToArray();
            return array.Length == 0 ? null : FrameMeasurements.Distribution(array).P50;
        }
        var start = _soakMap.Count > 0 ? _soakMap[0].At : 0;
        object MemorySlope(Func<(double At, double Private, double Working, double Video), double> value) =>
            Slope(_soakMap.Select(item => ((item.At - start) / Hour, value(item))).ToArray()) is { } slope ? slope : "unavailable";
        return new
        {
            seconds, segments = _soakSegment, body_seconds = body, round_trips_per_segment = SoakRoundTrips,
            sample_seconds = SoakSampleSeconds,
            gc_method = SoakGcMethod,
            load_classes = new { full = "保存点或之前最近一组配方期望全部成立", all_but_line_battle = "仅 standard-world：armed_line_combats 不成立，其余不成立的只有 armed_combats",
                below_recipe = "其他期望不成立；这些帧不是完整负载证据", unknown = "重负载采样窗尚无本会话且采样开始后取得的计数",
                frame_rule = "每帧按最近一次（每秒最多一次）计数分类" },
            views = _soakTotals.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
            {
                view = pair.Key, frames = pair.Value.Intervals.Count, frame_seconds = FrameMeasurements.Distribution(pair.Value.Intervals),
                over_16_7_ms_share = pair.Value.Intervals.Count(interval => interval > 1d / 60) / (double)pair.Value.Intervals.Count,
                steps_per_second = pair.Value.Steps / pair.Value.Seconds,
                load_class_shares = pair.Value.Classes.ToDictionary(item => item.Key, item => item.Value / (double)pair.Value.Intervals.Count)
            }),
            samples = _soakSamples,
            operations = _soakOperations.GroupBy(operation => operation.Type).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group =>
            {
                var items = group.OrderBy(item => item.At).ToArray();
                var timed = items.Where(item => item.Seconds is not null).ToArray();
                var third = Math.Max(1, timed.Length / 3);
                return new
                {
                    type = group.Key, count = items.Length, measured = timed.Length,
                    latency_seconds = timed.Length == 0 ? null : FrameMeasurements.Distribution(timed.Select(item => item.Seconds!.Value)),
                    ready_seconds = items.Any(item => item.ReadySeconds is not null)
                        ? FrameMeasurements.Distribution(items.Where(item => item.ReadySeconds is not null).Select(item => item.ReadySeconds!.Value)) : null,
                    first_third_median = Median(timed.Take(third).Select(item => item.Seconds!.Value)),
                    last_third_median = Median(timed.TakeLast(third).Select(item => item.Seconds!.Value)),
                    slope_seconds_per_hour = Slope(timed.Select(item => ((item.At - items[0].At) / Hour, item.Seconds!.Value)).ToArray()),
                    peak_over_map = new { private_bytes = items.Max(item => item.PrivatePeakOverMap),
                        working_set_bytes = items.Max(item => item.WorkingPeakOverMap), godot_video_bytes = items.Max(item => item.VideoPeakOverMap) },
                    peak_over_before = new { private_bytes = items.Max(item => item.PrivatePeakOverBefore),
                        working_set_bytes = items.Max(item => item.WorkingPeakOverBefore), godot_video_bytes = items.Max(item => item.VideoPeakOverBefore) },
                    items
                };
            }),
            memory = new
            {
                map_records = _soakMap.Count,
                private_bytes_per_hour = MemorySlope(item => item.Private),
                working_set_bytes_per_hour = MemorySlope(item => item.Working),
                godot_video_bytes_per_hour = MemorySlope(item => item.Video),
                godot_video_label = "Godot 渲染缓冲区、纹理与总分配；集成显卡上是共享的系统内存，不是独立显存",
                private_label = OperatingSystem.IsWindows() ? "Windows 私有字节" : "Linux RssAnon + VmSwap；峰值为 0.2 秒采样最大值",
                reset_heaps = _soakHeaps.Select(item => new { segment = item.Segment, phase = item.Phase, at = item.At,
                    live_bytes = item.LiveBytes, over_first_bytes = item.LiveBytes - _soakHeaps[0].LiveBytes })
            },
            autosave = new { interval_seconds = _app.Saves!.AutoSaveInterval?.TotalSeconds, writes = _soakAutosaves,
                slot_overwrites = _soakAutosaveOverwrites, source = "自动存档槽文件头的保存时间" },
            recording = new { start_interval_steps = RecordingService.StartIntervalSteps,
                retained_starts = _app.Host!.SessionOptions!.RetainedRecordingStarts, trims = _soakTrims,
                incomplete_seconds = _soakIncompleteSeconds, source = "同一会话内前后两次读数都有可用起点时，最早起点位置前移" },
            round_trips = new
            {
                evidence = _soakRejectedLoads.Count > 0 ? "failed" : "passed",
                attempted = _soakRoundTripsAttempted, loaded = _soakRoundTripsAttempted - _soakRejectedLoads.Count,
                rejected = _soakRejectedLoads.Count, rejections = _soakRejectedLoads,
                rule = "手动保存后读同一存档；被拒绝时记录并在原对局继续，不取读档后首显样本"
            },
            load_decay = new { interval_seconds = 10, rule = "每次重置后每 10 秒取一次负载计数", series = _soakDecay },
            body_sizing = new { rule = "首轮实测每真实秒步数（含编辑器暂停）下，1.3 × 起点间隔 × 保留起点数所需时长；不少于 720 秒，不超过总时长的 60%",
                body_seconds = body },
            loads = new { attempted = _soakLoadAttempts, succeeded = _soakLoads }, encounters = _soakEncounters,
            uncovered = new[] { "process_first_open_node", "process_first_open_line", "process_first_follow" }
        };
    }
}
