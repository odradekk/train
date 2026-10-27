using System.Text.Json;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    private long CollectSceneHeap(int cycle, string phase)
    {
        ResetSceneProbe();
        _sceneProbeDetail = _sceneFirstNearDetail = null;
        _sceneRecent.Clear();
        var natural = new { private_bytes = _scenePrivate, working_set_bytes = _sceneWorking };
        var started = Seconds();
        var gcPause = GC.GetTotalPauseDuration();
        var gcCollections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var pauseSeconds = (GC.GetTotalPauseDuration() - gcPause).TotalSeconds;
        var collections = Enumerable.Range(0, 3).Select(generation => GC.CollectionCount(generation) - gcCollections[generation]).ToArray();
        var heap = GC.GetGCMemoryInfo();
        var live = GC.GetTotalMemory(false);
        _sceneCollectionTrace!.WriteLine(JsonSerializer.Serialize(new { cycle, phase, at = Seconds(),
            reason = "measurement-forced", pause_seconds = pauseSeconds, collections_by_generation = collections,
            seconds = Seconds() - started, natural, managed_live_bytes = live,
            heap_bytes = heap.HeapSizeBytes, fragmented_bytes = heap.FragmentedBytes,
            committed_bytes = heap.TotalCommittedBytes }, SceneJson));
        _sceneCollectionTrace.Flush();
        return live;
    }
}
