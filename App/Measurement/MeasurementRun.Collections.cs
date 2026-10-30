using System.Text.Json;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    private bool _heapDiagnosisFinished;
    private double _sceneHeapCollectedAt;

    private long CollectSceneHeap(int cycle, string phase)
    {
        ResetSceneProbe();
        _sceneProbeDetail = _sceneFirstNearDetail = null;
        _sceneRecent.Clear();
        var natural = new { private_bytes = _scenePrivate, working_set_bytes = _sceneWorking };
        var started = Seconds();
        var gcStart = GcReading.Current;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var gc = GcReading.Current.Since(gcStart);
        var heap = GC.GetGCMemoryInfo();
        var live = GC.GetTotalMemory(false);
        _sceneCollectionTrace!.WriteLine(JsonSerializer.Serialize(new { cycle, phase, at = _sceneHeapCollectedAt = Seconds(),
            reason = "measurement-forced", pause_seconds = gc.Pause.TotalSeconds, collections_by_generation = gc.CollectionsByGeneration,
            seconds = Seconds() - started, natural, managed_live_bytes = live,
            heap_bytes = heap.HeapSizeBytes, fragmented_bytes = heap.FragmentedBytes,
            committed_bytes = heap.TotalCommittedBytes }, SceneJson));
        _sceneCollectionTrace.Flush();
        return live;
    }

    private async Task CollectIdleSceneHeap(int cycle)
    {
        if (cycle is not (1 or 2) ||
            Environment.GetEnvironmentVariable("TRAIN_HEAP_DIAGNOSIS") is not { Length: > 0 } directory) return;
        var recording = _app.Recording!;
        var saves = _app.Saves!;
        void Observe(string phase, double waitSeconds, bool timedOut)
        {
            var state = recording.DiagnosisState;
            File.AppendAllText(Path.Combine(directory, "heap-observations.jsonl"), JsonSerializer.Serialize(new
            {
                process_id = Environment.ProcessId, cycle, phase, at = Seconds(), collection_at = _sceneHeapCollectedAt,
                utc = DateTimeOffset.UtcNow, timestamp = System.Diagnostics.Stopwatch.GetTimestamp(),
                stopwatch_frequency = System.Diagnostics.Stopwatch.Frequency,
                recording = new { state.PendingIsNull, state.SnapshotBindingEqualsCurrent, state.WorkIsCompleted },
                recording_busy = recording.IsBusy, save_busy = saves.IsBusy, wait_seconds = waitSeconds, timed_out = timedOut
            }) + "\n");
        }
        Observe("reset-map", 0, false);
        var waiting = System.Diagnostics.Stopwatch.StartNew();
        // 原 GC 可能早于本帧的 Recording.Process；至少让主循环完成一帧。
        await _app.ToSignal(_app.GetTree(), Godot.SceneTree.SignalName.ProcessFrame);
        var timedOut = false;
        try { await Wait(() => !recording.IsBusy && !saves.IsBusy, Math.Max(0.001, 60 - waiting.Elapsed.TotalSeconds)); }
        catch (TimeoutException) { timedOut = true; }
        var waitSeconds = waiting.Elapsed.TotalSeconds;
        CollectSceneHeap(cycle, "reset-map-idle");
        Observe("reset-map-idle", waitSeconds, timedOut);
        _heapDiagnosisFinished = cycle == 2;
        var release = Path.Combine(directory, $"reset-{cycle}.release");
        var ready = Path.Combine(directory, $"reset-{cycle}.ready");
        var gate = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            File.WriteAllText(ready + ".partial", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Move(ready + ".partial", ready);
            await Wait(() => File.Exists(release), 360);
            if (File.ReadAllText(release) != "continue")
                throw new InvalidOperationException("Heap diagnosis controller aborted collection.");
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, $"reset-{cycle}.ended"),
                gate.Elapsed.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
