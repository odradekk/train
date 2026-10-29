using System.Text.Json;
using Godot;
using Train.Rendering;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    private SceneDisplay? _resourceDisplay;
    private readonly SceneResourceTimings _resourceTimings = new();
    private readonly List<SceneResourceFrame> _resourceFrames = [];

    private void ObserveSceneResources()
    {
        if (_measuredEditorPanel is not null && _app.Editor!.IsOpen) return;
        ObserveSceneResources(_app.WorldScenes!.Display);
    }

    private void ObserveSceneResources(SceneDisplay? display)
    {
        if (ReferenceEquals(display, _resourceDisplay)) return;
        if (_resourceDisplay is not null) _resourceDisplay.Measure = null;
        _resourceDisplay = display;
        if (display is null) return;
        display.Measure = _resourceTimings.Measure;
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
        if (_resourceDisplay is not null) _resourceDisplay.Measure = null;
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
