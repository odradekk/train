using System.Collections.Concurrent;
using System.Text.Json;
using Train.Rendering;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    private sealed record OpeningDisplayInterval(int Display, SceneDisplayInterval Value);
    // Packing callbacks run on workers; never append them to the main-thread host-event list.
    private readonly ConcurrentQueue<OpeningDisplayInterval> _openingDisplayIntervals = new();

    private void WriteOpeningDisplayIntervals(string directory, HashSet<ulong> spikes)
    {
        var frames = _openingResourceWork.Where(work => !work.AfterProbeOperable || spikes.Contains(work.ProcessFrame))
            .Select(work => (work.Display, work.ProcessFrame)).ToHashSet();
        using var writer = new StreamWriter(Path.Combine(directory, "opening-display-intervals.jsonl"));
        foreach (var interval in _openingDisplayIntervals)
            if (interval.Value.Work == SceneDisplayWork.Commit ||
                interval.Value.Stage == "Prepare.BackgroundManagedArrays" ||
                frames.Contains((interval.Display, interval.Value.ProcessFrame)))
                writer.WriteLine(JsonSerializer.Serialize(interval, StreamingJson));
    }
}
