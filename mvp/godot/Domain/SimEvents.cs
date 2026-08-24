namespace TrainMvp.Domain;

public abstract record SimEvent;

public sealed record TradeEvent(
    string StationId,
    IReadOnlyList<RuleResult> Rules,
    bool IsLoopStart) : SimEvent;

public sealed record TravelEvent(
    string FromStationId,
    string ToStationId,
    IReadOnlyList<string> Path,
    int Ticks,
    IReadOnlyList<string> Transits,
    bool IsReturn) : SimEvent;

public sealed record TransitEvent(string StationId) : SimEvent;

public sealed record RepositionEvent(
    string FromStationId,
    string ToStationId,
    IReadOnlyList<string> Path,
    int Ticks,
    IReadOnlyList<string> Transits) : SimEvent;

public sealed record ReturnArrivalEvent(string StationId) : SimEvent;

public sealed record LoopFinishedEvent : SimEvent;

public sealed class LoopRunResult
{
    public LoopRunResult(SimulationState stateAfter, RunReport report, IReadOnlyList<SimEvent> events)
    {
        StateAfter = stateAfter;
        Report = report;
        Events = events;
    }

    public SimulationState StateAfter { get; }
    public RunReport Report { get; }
    public IReadOnlyList<SimEvent> Events { get; }
}

public sealed record ShortestPathResult(IReadOnlyList<string> Path, int Ticks);

public sealed record LoopSegment(
    string From,
    string To,
    IReadOnlyList<string> Path,
    int Ticks,
    IReadOnlyList<string> Transits,
    bool IsReturn);

public sealed class LoopGeometry
{
    public LoopGeometry(IReadOnlyList<LoopSegment> segments, int totalTicks)
    {
        Segments = segments;
        TotalTicks = totalTicks;
    }

    public IReadOnlyList<LoopSegment> Segments { get; }
    public int TotalTicks { get; }
}
