using System.Diagnostics;
using System.Text.Json;

namespace Train.SceneScale;

internal static class ProbeProgress
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    internal static string Mode = "unknown";
    internal static string Input = "unknown";
    private static double nextAt;

    internal static void Write(string stage, object? detail = null)
    {
        if (Clock.Elapsed.TotalSeconds < nextAt) return;
        nextAt = Clock.Elapsed.TotalSeconds + 1;
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            marker = "scene-scale-progress",
            mode = Mode,
            input = Input,
            stage,
            seconds = Clock.Elapsed.TotalSeconds,
            workingSetBytes = process.WorkingSet64,
            peakWorkingSetBytes = process.PeakWorkingSet64,
            privateBytes = process.PrivateMemorySize64,
            managedHeapBytes = GC.GetTotalMemory(false),
            gcHeapBytes = gc.HeapSizeBytes,
            gcCommittedBytes = gc.TotalCommittedBytes,
            totalAllocatedBytes = GC.GetTotalAllocatedBytes(),
            detail
        }));
    }
}
