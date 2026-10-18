using System.Diagnostics;

namespace Train.Scenes;

internal sealed class DisplayRate
{
    private long? windowStart;
    private double simulationSeconds;
    internal double Value { get; private set; } = 1;
    internal Func<long> Timestamp { get; set; } = Stopwatch.GetTimestamp;

    internal void Update(double seconds)
    {
        var now = Timestamp();
        if (windowStart is not { } start)
        { windowStart = now; simulationSeconds = seconds; return; }
        var elapsed = Stopwatch.GetElapsedTime(start, now).TotalSeconds;
        if (elapsed < 1) return;
        Value = Math.Clamp((seconds - simulationSeconds) / elapsed, 1, RailwayCoordinates.MaximumDisplayRate);
        simulationSeconds = seconds;
        windowStart = now;
    }
}
