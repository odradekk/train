using System.Text.Json;
using Godot;
using Train.Rendering;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    private SceneDisplay? _resourceDisplay;
    private double _resourcePreparation, _resourceCandidatePreparation, _resourceCommit, _resourceLastDraw;
    private readonly List<SceneResourceFrame> _resourceFrames = [];
    private sealed record SceneResourceFrame(ulong ProcessFrame, double At, string Phase, double FrameSeconds,
        double PreparationSeconds, double CandidatePreparationSeconds, double CommitSeconds)
    {
        public double PreparationTotalSeconds => PreparationSeconds + CandidatePreparationSeconds;
    }

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
        display.Measure = (work, elapsed) =>
        {
            switch (work)
            {
                case SceneDisplayWork.Prepare: _resourcePreparation += elapsed.TotalSeconds; break;
                case SceneDisplayWork.PrepareCandidate: _resourceCandidatePreparation += elapsed.TotalSeconds; break;
                case SceneDisplayWork.Commit: _resourceCommit += elapsed.TotalSeconds; break;
            }
        };
    }

    private void RecordSceneResourceFrame()
    {
        var now = Seconds();
        if (_resourceLastDraw > 0)
            _resourceFrames.Add(new(Engine.GetProcessFrames(), now,
                _routeSampling ? (_routeOperableGc is null ? "route-opening" : "route-follow") : _scenePhase,
                now - _resourceLastDraw,
                _resourcePreparation, _resourceCandidatePreparation, _resourceCommit));
        _resourceLastDraw = now;
        _resourcePreparation = _resourceCandidatePreparation = _resourceCommit = 0;
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
                commit_seconds = FrameMeasurements.Distribution(samples.Select(frame => frame.CommitSeconds)),
                longest_frame = samples.MaxBy(frame => frame.FrameSeconds)
            };
        }
        _sceneEvidence["resource_work"] = new
        {
            frames = Path.GetFileName(path),
            scope = "Main-thread elapsed spans, including GC pauses; preparation reuses the shared budget clock. Calls are summed between actual draws; Prepare and PrepareCandidate exclude Commit. Not worker CPU time or streaming stage latency.",
            phases = _resourceFrames.GroupBy(frame => frame.Phase).Select(group => new
            {
                phase = group.Key, all_frames = Summary(group),
                working_frames = Summary(group.Where(frame => frame.PreparationTotalSeconds > 0 || frame.CommitSeconds > 0))
            }).ToArray()
        };
    }
}
