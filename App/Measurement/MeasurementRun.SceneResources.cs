using System.Text.Json;
using Godot;
using Train.Rendering;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    private SceneDisplay? _resourceDisplay;
    private readonly SceneResourceTimings _resourceTimings = new();
    private readonly List<SceneResourceFrame> _resourceFrames = [];
    private bool OpeningDiagnostics => _streamingRecorder is not null && _scenario is ("scene-node" or "scene-edge");
    private readonly List<object> _openingDraws = [], _openingNearParts = [];
    private readonly List<OpeningResourceWork> _openingResourceWork = [];
    private sealed record OpeningResourceWork(string Operation, int Display, ulong ProcessFrame, double CallbackAt,
        SceneDisplayWork Work, double Seconds, double GcPauseSeconds, bool AfterProbeOperable,
        SceneFrameStamp? Returned, SceneFrameStamp? CandidateScope, StreamingHostEvent? LastHostEvent);
    private OpeningNearSnapshot? _openingOperableNear;
    private sealed record OpeningNearSnapshot(SceneFrameStamp Frame, ScenePoint Origin,
        ISceneStreamingCoverage? Coverage, System.Numerics.Vector3 Center, ProbeReferenceView? Reference);

    private void ObserveSceneResources()
    {
        if (_measuredEditorPanel is not null && _app.Editor!.IsOpen) return;
        ObserveSceneResources(_app.WorldScenes!.Display);
    }

    internal void ObserveSceneResources(SceneDisplay? display)
    {
        if (ReferenceEquals(display, _resourceDisplay)) return;
        if (_resourceDisplay is not null) _resourceDisplay.Measure = null;
        _resourceDisplay = display;
        if (display is null) return;
        if (!OpeningDiagnostics) { display.Measure = _resourceTimings.Measure; return; }
        display.Measure = (work, elapsed, pause) =>
        {
            _resourceTimings.Measure(work, elapsed, pause);
            if (_sceneOperation is { } operation)
                _openingResourceWork.Add(new(operation, _streamingDisplayNumber, Engine.GetProcessFrames(), Seconds(), work,
                    elapsed.TotalSeconds, pause.TotalSeconds, _sceneOperable > 0, _scenePresentedStamp,
                    display.CandidateScope, _streamingHost.Count > 0 && _streamingHost[^1].Display == _streamingDisplayNumber
                        ? _streamingHost[^1] : null));
        };
    }

    private void RecordOpeningDraw(double at, ISceneFrame? frame, ISceneFrame? interaction)
    {
        if (!OpeningDiagnostics || _sceneOperation is null) return;
        var started = Seconds();
        _streamingRecorder!.Displayed(frame?.Update.Frame, _streamingHost.Skip(_streamingHostReplayed));
        _streamingHostReplayed = _streamingHost.Count;
        if (_sceneOperable > 0 && _openingOperableNear is null && frame is not null)
        {
            var (center, location, _) = LocateProbeFocus(frame, _subject);
            var absolute = center + ProbeOrigin(frame);
            _openingOperableNear = new(frame.Update.Frame, frame.Origin, _streamingRecorder.Coverage(frame.Update.Frame),
                absolute, FindSceneReference(absolute, location));
        }
        _openingDraws.Add(new
        {
            at, draw = _sceneDrawCount, process_frame = Engine.GetProcessFrames(), operation = _sceneOperation,
            click = _sceneClick, first_operable = _sceneOperable > 0 ? (double?)_sceneOperable : null,
            displayed = frame?.Update.Frame, interaction = interaction?.Update.Frame,
            returned = _scenePresentedStamp, probe = _probeState,
            host_event_count = _streamingHostReplayed, observer_seconds = Seconds() - started
        });
    }

    private void RecordOpeningNearParts(double observationEnded)
    {
        if (!OpeningDiagnostics) return;
        var started = Seconds();
        var snapshot = _openingOperableNear;
        _openingOperableNear = null;
        var coverage = snapshot?.Coverage;
        var reference = snapshot?.Reference;
        var terrain = reference is null ? null : _probeTerrainBands.GetValueOrDefault(reference);
        var parts = new HashSet<SceneStreamingPartId>();
        var columns = 0;
        if (snapshot is not null && coverage is not null && terrain is not null)
            foreach (var (x, z) in terrain)
            {
                var dx = Math.Clamp((double)snapshot.Center.X, x, x + 1) - snapshot.Center.X;
                var dz = Math.Clamp((double)snapshot.Center.Z, z, z + 1) - snapshot.Center.Z;
                if (dx * dx + dz * dz > NearAreaRadiusCells * NearAreaRadiusCells) continue;
                columns++;
                parts.UnionWith(coverage.FindTerrainParts(x + .5 - snapshot.Origin.X, z + .5 - snapshot.Origin.Z));
            }
        _openingNearParts.Add(new
        {
            operation = _sceneOperation, observation_ended = observationEnded, classified_at = started,
            first_operable = _sceneOperable > 0 ? (double?)_sceneOperable : null,
            frame = snapshot?.Frame, center = snapshot?.Center, radius_meters = 32, columns,
            known = coverage is not null && terrain is not null, terrain_parts = parts.ToArray(),
            classification_seconds = Seconds() - started,
            scope = "First probe-operable draw: reference terrain columns intersecting the 32 m disk, classified after observation. Non-terrain dependencies remain in raw frame/host events. Coverage queries can warm diagnostic caches."
        });
    }

    private void WriteOpeningStreamingTrace(string directory)
    {
        if (!OpeningDiagnostics) return;
        _streamingRecorder!.Displayed(_app.WorldScenes!.Display?.DisplayedFrame?.Update.Frame,
            _streamingHost.Skip(_streamingHostReplayed));
        _streamingHostReplayed = _streamingHost.Count;
        var until = Seconds();
        var spikes = _resourceFrames.Where(frame => frame.AfterOperable && !frame.ExcludedSpaceChange &&
            frame.PreparationTotalSeconds - frame.PreparationGcPauseSeconds > .004).Select(frame => frame.ProcessFrame).ToHashSet();
        Write("opening-scenes.jsonl", _streamingRecorder.Events(0, until));
        Write("opening-parts.jsonl", _streamingRecorder.Timings(0, until));
        Write("opening-host.jsonl", _streamingHost);
        Write("opening-draws.jsonl", _openingDraws);
        Write("opening-near-parts.jsonl", _openingNearParts);
        Write("opening-resource-calls.jsonl", _openingResourceWork.Where(work => !work.AfterProbeOperable || spikes.Contains(work.ProcessFrame)));
        WriteOpeningDisplayIntervals(directory, spikes);
        _sceneEvidence["opening_streaming"] = new
        {
            clock = "Absolute Stopwatch seconds, shared with scene-operations and scene-resource-frames; no route origin",
            scope = "Diagnostic only: raw parts, host stages, draws and original operability. Display intervals are nested absolute Stopwatch spans; do not sum parent and child spans. Background packing has a mesh ID and its submitting call/process frame, not an object owner. Resource CallbackAt follows the timed span; LastHostEvent is context, not exclusive attribution.",
            files = new[] { "opening-scenes.jsonl", "opening-parts.jsonl", "opening-host.jsonl", "opening-draws.jsonl",
                "opening-near-parts.jsonl", "opening-resource-calls.jsonl", "opening-display-intervals.jsonl" }
        };
        if (_streamingDisplay is not null) { _streamingDisplay.Observe = null; _streamingDisplay.Diagnose = null; }

        void Write<T>(string name, IEnumerable<T> values)
        {
            using var writer = new StreamWriter(Path.Combine(directory, name));
            foreach (var value in values) writer.WriteLine(JsonSerializer.Serialize(value, StreamingJson));
        }
    }

    private void RecordSceneResourceFrame()
    {
        var scenes = _app.WorldScenes!;
        var frame = _resourceTimings.Draw(Engine.GetProcessFrames(), Seconds(),
            _routeSampling ? (_routeOperableGc is null ? "route-opening" : "route-follow") : _scenePhase,
            _routeSampling ? "route" : _sceneOperation, scenes.View.Interactive, scenes.Display?.DisplayedFrame?.Update.Frame.Space);
        if (frame is not null) _resourceFrames.Add(frame);
    }

    private void WriteSceneResourceTimings()
    {
        ObserveSceneResources(null);
        var path = Path.Combine(Path.GetDirectoryName(_output)!, "scene-resource-frames.jsonl");
        using (var writer = new StreamWriter(path))
            foreach (var frame in _resourceFrames) writer.WriteLine(JsonSerializer.Serialize(frame, SceneJson));
        object? Summary(IEnumerable<SceneResourceFrame> frames)
        {
            var samples = frames.ToArray();
            if (samples.Length == 0) return null;
            return new
            {
                n = samples.Length,
                frame_seconds = FrameMeasurements.Distribution(samples.Select(frame => frame.FrameSeconds)),
                preparation_seconds = FrameMeasurements.Distribution(samples.Select(frame => frame.PreparationSeconds)),
                candidate_preparation_seconds = FrameMeasurements.Distribution(samples.Select(frame => frame.CandidatePreparationSeconds)),
                preparation_total_seconds = FrameMeasurements.Distribution(samples.Select(frame => frame.PreparationTotalSeconds)),
                preparation_gc_pause_seconds = FrameMeasurements.Distribution(samples.Select(frame => frame.PreparationGcPauseSeconds)),
                preparation_calls = samples.Sum(frame => frame.PreparationCalls),
                candidate_preparation_calls = samples.Sum(frame => frame.CandidatePreparationCalls),
                commit_seconds = FrameMeasurements.Distribution(samples.Select(frame => frame.CommitSeconds)),
                longest_frame = samples.MaxBy(frame => frame.FrameSeconds),
                longest_preparation_frame = samples.MaxBy(frame => frame.PreparationTotalSeconds),
                longest_candidate_preparation_frame = samples.MaxBy(frame => frame.CandidatePreparationSeconds)
            };
        }
        _sceneEvidence["resource_work"] = new
        {
            frames = Path.GetFileName(path),
            frame_count = _resourceFrames.Count,
            scope = "Main-thread elapsed spans and GC pause deltas, summed between actual draws; Prepare and PrepareCandidate exclude Commit. Assessment subtracts each frame's GC pauses; raw elapsed and the shared budget clock still include them. Not worker CPU time or streaming stage latency.",
            phases = _resourceFrames.GroupBy(frame => frame.Phase).Select(group => new
            {
                phase = group.Key, all_frames = Summary(group),
                working_frames = Summary(group.Where(frame => frame.PreparationTotalSeconds > 0 || frame.CommitSeconds > 0))
            }).ToArray()
        };
    }
}
