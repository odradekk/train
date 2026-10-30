using System.Text.Json;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    private bool _heapDiagnosisFinished;

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
        _sceneCollectionTrace!.WriteLine(JsonSerializer.Serialize(new { cycle, phase, at = Seconds(),
            reason = "measurement-forced", pause_seconds = gc.Pause.TotalSeconds, collections_by_generation = gc.CollectionsByGeneration,
            seconds = Seconds() - started, natural, managed_live_bytes = live,
            heap_bytes = heap.HeapSizeBytes, fragmented_bytes = heap.FragmentedBytes,
            committed_bytes = heap.TotalCommittedBytes }, SceneJson));
        _sceneCollectionTrace.Flush();
        if (phase == "reset-map" && cycle is 1 or 2 &&
            Environment.GetEnvironmentVariable("TRAIN_HEAP_DIAGNOSIS") is { Length: > 0 } directory)
        {
            var release = Path.Combine(directory, $"reset-{cycle}.release");
            var ready = Path.Combine(directory, $"reset-{cycle}.ready");
            var waiting = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                File.WriteAllText(ready + ".partial", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.Move(ready + ".partial", ready);
                while (!File.Exists(release))
                {
                    if (waiting.Elapsed > TimeSpan.FromMinutes(5))
                        throw new TimeoutException("Heap diagnosis controller did not release the main thread.");
                    Thread.Sleep(100);
                }
                if (File.ReadAllText(release) != "continue")
                    throw new InvalidOperationException("Heap diagnosis collection failed.");
                _heapDiagnosisFinished = cycle == 2;
            }
            finally
            {
                File.WriteAllText(Path.Combine(directory, $"reset-{cycle}.ended"),
                    waiting.Elapsed.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return live;
    }
}
