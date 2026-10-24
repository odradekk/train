using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App.Measurement;

// Temporary #774 instrumentation. Counts only; the trace reader stays out of process.
internal sealed class Issue774Counters : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly HostCoordinator _host;
    private HostBinding? _binding;
    private ulong _previousSteps;
    private long _boundaries, _frames;
    private double _lastAt;
    private string _lastPhase = "";
    internal bool Sample { get; } = Environment.GetEnvironmentVariable("TRAIN_774_MODE") == "sample";

    internal Issue774Counters(string directory, HostCoordinator host)
    {
        _writer = new(Path.Combine(directory, "gc-counters.jsonl"));
        _host = host;
        host.BoundaryCompleted += Boundary;
        _writer.WriteLine(JsonSerializer.Serialize(new { kind = "config", pid = Environment.ProcessId,
            runtime = Environment.Version.ToString(), corelib = typeof(GC).Assembly.Location,
            mode = Sample ? "sample" : "counters", frequency = Stopwatch.Frequency }));
        _writer.Flush();
        if (Sample)
            File.WriteAllText(Path.Combine(directory, "gc-ready.json"), JsonSerializer.Serialize(new { pid = Environment.ProcessId }));
    }

    private void Boundary(HostBinding binding, ICoreReadView view, IContentView content, ImmutableArray<EventEnvelope> events)
    {
        var steps = view.Position.CompletedSteps;
        if (_binding == binding && steps > _previousSteps) _boundaries += checked((long)(steps - _previousSteps));
        _binding = binding;
        _previousSteps = steps;
    }

    internal void Frame(double at, string phase)
    {
        _frames++;
        if (phase == _lastPhase && at - _lastAt < 1) return;
        Mark(phase, at);
    }

    internal void Mark(string phase, double at)
    {
        long allocated = GC.GetTotalAllocatedBytes(false);
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        double pause = GC.GetTotalPauseDuration().TotalSeconds;
        Issue774Markers.Log.Marker(phase, at, allocated, gen0, gen1, gen2, pause, _boundaries, _frames);
        _writer.WriteLine(JsonSerializer.Serialize(new { kind = "counter", phase, at, allocated,
            counts = new[] { gen0, gen1, gen2 }, pause, steps = _boundaries, frames = _frames }));
        _writer.Flush();
        _lastAt = at; _lastPhase = phase;
    }

    internal void MarkNow(string phase) => Mark(phase, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

    public void Dispose()
    {
        MarkNow("run-end");
        _host.BoundaryCompleted -= Boundary;
        _writer.Dispose();
    }
}

[EventSource(Name = "Train-Issue774")]
internal sealed class Issue774Markers : EventSource
{
    internal static readonly Issue774Markers Log = new();
    [Event(1, Level = EventLevel.Informational)]
    public void Marker(string phase, double monotonicSeconds, long allocatedBytes, int gen0, int gen1, int gen2,
        double pauseSeconds, long completedBoundaries, long frames)
    {
        if (IsEnabled()) WriteEvent(1, phase, monotonicSeconds, allocatedBytes, gen0, gen1, gen2, pauseSeconds, completedBoundaries, frames);
    }
}
