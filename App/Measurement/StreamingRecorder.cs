using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Train.Rendering;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

internal sealed record StreamingSceneEvent(double At, string Kind, SceneStreamingSceneId? Scene = null,
    SceneStreamingAttemptId? Attempt = null, SceneStreamingStage? Stage = null,
    SceneFrameStamp? Stamp = null, ImmutableArray<SceneStreamingAttemptId>? Parts = null,
    ImmutableArray<SceneObjectId>? Objects = null, SceneStreamingRole? Role = null, WorldLocation? Location = null);
internal sealed record StreamingPartTiming(SceneStreamingAttemptId Attempt, string Outcome,
    double? Needed, double? Queued, double? Started, double? Built, double? Admitted,
    double? ResourcesPrepared, double? Committed)
{
    public bool AlreadyVisibleBeforeDemand => Needed is { } needed && Committed is { } committed && committed < needed;
    public double? NeededToCommittedSeconds => AlreadyVisibleBeforeDemand ? null : Committed - Needed;
    public double? QueueSeconds => Started - Queued;
    public double? BuildSeconds => Built - Started;
    public double? BuiltToAdmittedSeconds => Admitted - Built;
    public double? AdmittedToCommittedSeconds => Committed - Admitted;
}

internal sealed class StreamingRecorder(Func<double>? clock = null) : ISceneStreamingObserver
{
    private readonly Func<double> _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    private readonly ConcurrentQueue<(double At, SceneStreamingEvent Event)> _pending = new();
    private readonly List<StreamingSceneEvent> _events = [];
    private readonly Dictionary<SceneStreamingAttemptId, AttemptState> _attempts = [];
    private readonly Dictionary<SceneStreamingPartId, SceneStreamingAttemptId> _current = [];
    private readonly Dictionary<SceneFrameStamp, SceneStreamingEvent.Frame> _frames = [];
    private readonly Dictionary<SceneStreamingSceneId, SceneFrameStamp> _latest = [];
    private readonly Dictionary<(int Display, long Candidate, SceneFrameStamp Frame), HostCandidate> _preparing = [];
    private readonly HashSet<SceneStreamingAttemptId> _ready = [];
    private sealed class HostCandidate(SceneFrameStamp frame, double opened)
    {
        internal SceneFrameStamp Frame = frame;
        internal double Opened = opened;
        internal double? CompletedPreparation;
        internal HashSet<SceneObjectId>? Required;
        internal readonly Dictionary<SceneObjectId, double> Resources = [];
    }
    private readonly HashSet<SceneStreamingSceneId> _closed = [];
    private SceneFrameStamp? _displayed;
    private sealed class AttemptState
    {
        internal bool Needed;
        internal SceneStreamingStage Stage;
        internal ImmutableArray<SceneObjectId> Objects = [];
        internal readonly Dictionary<SceneStreamingStage, double> Times = [];
        internal double? Prepared, Committed;
    }

    public void OnEvent(SceneStreamingEvent value)
    {
        try { _pending.Enqueue((_clock(), value)); }
        catch { /* Diagnostics must never interrupt the producer. */ }
    }

    internal void Drain()
    {
        while (_pending.TryDequeue(out var item))
        {
            var (at, value) = item;
            switch (value)
            {
                case SceneStreamingEvent.Opened opened:
                    _events.Add(new(at, "Opened", opened.Scene, Role: opened.Role, Location: opened.Location));
                    break;
                case SceneStreamingEvent.Activated active:
                    _events.Add(new(at, "Activated", active.Scene));
                    break;
                case SceneStreamingEvent.Closed closed:
                    _closed.Add(closed.Scene);
                    _events.Add(new(at, "Closed", closed.Scene));
                    break;
                case SceneStreamingEvent.Changed changed:
                    if (!_attempts.TryGetValue(changed.Attempt, out var state))
                        _attempts.Add(changed.Attempt, state = new());
                    if (!_current.TryGetValue(changed.Attempt.Part, out var current) || current.Value < changed.Attempt.Value)
                        _current[changed.Attempt.Part] = changed.Attempt;
                    state.Times.TryAdd(changed.Stage, at);
                    if (changed.Stage == SceneStreamingStage.Needed) state.Needed = true;
                    else if (changed.Stage == SceneStreamingStage.DemandEnded) state.Needed = false;
                    else if (state.Stage is not (SceneStreamingStage.Cancelled or SceneStreamingStage.Failed)) state.Stage = changed.Stage;
                    if (changed.Stage == SceneStreamingStage.Admitted) state.Objects = changed.Objects;
                    _events.Add(new(at, "Changed", Attempt: changed.Attempt, Stage: changed.Stage, Objects: changed.Objects));
                    break;
                case SceneStreamingEvent.Frame frame:
                    _frames[frame.Stamp] = frame;
                    _latest[frame.Scene] = frame.Stamp;
                    _events.Add(new(at, "Frame", frame.Scene, Stamp: frame.Stamp, Parts: frame.Parts));
                    break;
            }
        }
    }

    internal void Host(StreamingHostEvent item)
    {
        Drain();
        var value = item.Value;
        var key = (item.Display, value.Candidate, value.Frame);
        if (value.Stage == SceneDisplayStage.HostCancelled)
        {
            _preparing.Remove(key);
            RefreshReady();
            return;
        }
        if (!_preparing.TryGetValue(key, out var candidate))
            _preparing.Add(key, candidate = new(value.Frame, item.At));
        if (value.Stage == SceneDisplayStage.FramePreparationStarted)
            candidate.Required = value.ResourceObjects?.ToHashSet();
        if (value.Stage == SceneDisplayStage.ResourcesPrepared && value.Object is { } obj)
            candidate.Resources[obj] = item.At;
        if (value.Stage == SceneDisplayStage.FramePrepared) candidate.CompletedPreparation = item.At;
        if (value.Stage == SceneDisplayStage.FrameCommitted)
        {
            if (_frames.TryGetValue(value.Frame, out var frame))
                foreach (var id in frame.Parts)
                    if (_attempts.TryGetValue(id, out var state) && state.Committed is null)
                    {
                        state.Committed = item.At;
                        var prepared = state.Objects.Where(candidate.Resources.ContainsKey)
                            .Select(obj => candidate.Resources[obj]).ToArray();
                        state.Prepared = prepared.Length > 0 ? prepared.Max() : null;
                    }
            _preparing.Remove(key);
        }
        RefreshReady();
    }

    private void RefreshReady()
    {
        _ready.Clear();
        foreach (var candidate in _preparing.Values)
            if (_frames.TryGetValue(candidate.Frame, out var frame))
                foreach (var part in frame.Parts)
                    if (_attempts.TryGetValue(part, out var state) && IsPrepared(candidate, state)) _ready.Add(part);
    }

    private static bool IsPrepared(HostCandidate candidate, AttemptState state, double until = double.PositiveInfinity) =>
        candidate.Opened <= until && (candidate.CompletedPreparation is { } completed && completed <= until ||
            candidate.Required is { } required && state.Objects.Where(required.Contains)
                .All(obj => candidate.Resources.TryGetValue(obj, out var at) && at <= until));

    internal void Displayed(SceneFrameStamp? stamp)
    {
        Drain();
        _displayed = stamp;
        var keep = _latest.Where(pair => !_closed.Contains(pair.Key)).Select(pair => pair.Value)
            .Concat(_preparing.Values.Select(value => value.Frame)).ToHashSet();
        if (stamp is not null) keep.Add(stamp);
        foreach (var old in _frames.Keys.Where(key => !keep.Contains(key)).ToArray()) _frames.Remove(old);
    }

    internal ISceneStreamingCoverage? Coverage(SceneFrameStamp stamp) => _frames.GetValueOrDefault(stamp)?.Coverage;
    internal string Category(SceneStreamingPartId part)
    {
        if (!_current.TryGetValue(part, out var attempt) || !_attempts.TryGetValue(attempt, out var state) ||
            !state.Needed || _closed.Contains(part.Scene)) return "not_needed";
        if (_displayed is not null && _frames.TryGetValue(_displayed, out var frame) && frame.Parts.Contains(attempt)) return "committed";
        return state.Stage switch
        {
            SceneStreamingStage.Queued => "queued", SceneStreamingStage.Started => "started",
            SceneStreamingStage.Built or SceneStreamingStage.CacheHit => "built_waiting_admission",
            SceneStreamingStage.Admitted => !_ready.Contains(attempt) ? "admitted_waiting_resources" : "prepared_waiting_commit",
            SceneStreamingStage.Cancelled => "cancelled", SceneStreamingStage.Failed => "failed", _ => "needed_not_queued"
        };
    }
    internal Dictionary<string, int> MissingCounts() => _current
        .Where(pair => !_closed.Contains(pair.Key.Scene) && _attempts[pair.Value].Needed)
        .Select(pair => Category(pair.Key)).Where(category => category != "committed")
        .GroupBy(category => category).ToDictionary(group => group.Key, group => group.Count());
    internal StreamingSceneEvent[] Events(double origin, double until = double.PositiveInfinity) => _events
        .Where(value => value.At <= until).Select(value => value with { At = value.At - origin }).ToArray();
    internal StreamingPartTiming[] Timings(double origin, double until = double.PositiveInfinity) => _attempts
        .Where(pair => pair.Value.Times.Values.Any(at => at <= until)).Select(pair =>
    {
        var state = pair.Value;
        double? At(SceneStreamingStage stage) => state.Times.TryGetValue(stage, out var at) && at <= until ? at - origin : null;
        var committed = state.Committed is { } done && done <= until ? state.Committed : null;
        var prepared = committed is not null ? state.Prepared : null;
        if (committed is null)
        {
            var candidates = _preparing.Values.Where(candidate => IsPrepared(candidate, state, until) &&
                _frames.TryGetValue(candidate.Frame, out var frame) && frame.Parts.Contains(pair.Key));
            var times = candidates.Select(candidate => state.Objects.Where(candidate.Resources.ContainsKey)
                .Select(obj => candidate.Resources[obj]).Where(at => at <= until).ToArray())
                .Where(times => times.Length > 0).Select(times => times.Max()).ToArray();
            if (times.Length > 0) prepared = times.Min();
        }
        return new StreamingPartTiming(pair.Key, committed is not null ? "committed" :
            At(SceneStreamingStage.Cancelled) is not null ? "cancelled" :
            At(SceneStreamingStage.Failed) is not null ? "failed" : "unfinished",
            At(SceneStreamingStage.Needed), At(SceneStreamingStage.Queued), At(SceneStreamingStage.Started),
            At(SceneStreamingStage.Built) ?? At(SceneStreamingStage.CacheHit), At(SceneStreamingStage.Admitted),
            prepared - origin, committed - origin);
    }).ToArray();
}
