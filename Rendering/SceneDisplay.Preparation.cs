using System.Collections.Immutable;
using System.Diagnostics;
using Godot;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed partial class SceneDisplay
{
    internal PreparedFrame? Prepare(ISceneFrame frame, TimeSpan? budget = null)
    {
        using var diagnostic = SceneDisplayDiagnostics.Call(Diagnose, _candidate,
            frame.Update.Frame, SceneDisplayWork.Prepare, Engine.GetProcessFrames());
        _cache.RequireMainThread();
        ResetPreparationBudget();
        var started = Timestamp();
        var pauseStarted = GC.GetTotalPauseDuration();
        try
        {
            if (_candidatePromoted && !HasCandidateFor(frame.Update.Frame))
            {
                _budgetAdvanced = true;
                CancelCandidate();
            }
            if (frame.Readiness == SceneReadiness.Loading ||
                frame.Update.Base is { } baseline && baseline != DisplayedFrame?.Update.Frame)
            {
                _budgetAdvanced |= _preparation is not null;
                CancelPreparation();
                return null;
            }
            if (_preparation is { Frame: var previousFrame } &&
                (previousFrame.Update.Base != frame.Update.Base ||
                 previousFrame.Update.Frame.Session != frame.Update.Frame.Session ||
                 previousFrame.Update.Frame.Request != frame.Update.Frame.Request ||
                 previousFrame.Update.Frame.Space != frame.Update.Frame.Space))
            {
                _budgetAdvanced = true;
                CancelPreparation();
            }
            if (_preparation is null) { _preparation = new(frame, _resources, _packingCosts); _candidate++; }
            var preparation = _preparation;
            frame = preparation.Frame;
            var update = frame.Update;
            SceneDisplayDiagnostics.SetFrame(_candidate, update.Frame);
            var spaceChanged = DisplayedFrame is { } displayed
                ? displayed.Update.Frame.Session != update.Frame.Session || displayed.Update.Frame.Space != update.Frame.Space
                : _completeInitialFrame;
            // 新空间正式首帧仍一次准备并原子提交，后续调用共享每进程帧的额度。
            var slice = new PreparationSlice(this, started, budget, spaceChanged);
            using (SceneDisplayDiagnostics.Begin("Prepare.Initialize"))
                if (!InitializePreparation(preparation, slice, spaceChanged)) return null;
            using (SceneDisplayDiagnostics.Begin("Prepare.CheckPackingFailure"))
                preparation.Packing.Job?.ThrowIfFailed();
            var next = preparation.Objects!;
            for (; preparation.Upsert < update.Upserts.Length; preparation.Upsert++)
            {
                var source = update.Upserts[preparation.Upsert];
                _objects.TryGetValue(source.Id, out var previous);
                if (preparation.Visuals is null)
                {
                    if (!slice.Start()) return null;
                    using var interval = SceneDisplayDiagnostics.Begin("Prepare.CreateObjectRoot", source.Id);
                    if (previous is null) _pendingRoots.Add(source.Id, new Node3D { Name = $"Object{source.Id.Value}" });
                    preparation.Visuals = new PreparedVisual[source.Visuals.Length];
                }
                var root = previous?.Root ?? _pendingRoots[source.Id];
                var visuals = preparation.Visuals;
                var reusedVisuals = preparation.ReusedVisuals[source.Id];
                for (; preparation.Visual < visuals.Length; preparation.Visual++)
                {
                    var i = preparation.Visual;
                    PreparedVisual? candidateVisual;
                    using (SceneDisplayDiagnostics.Begin("Prepare.MatchCandidateVisual", source.Id, i))
                        candidateVisual = reusedVisuals[i] is null
                            ? CandidateVisual(update.Frame, (source.Id, i), source.Visuals[i]) : null;
                    if (!slice.Start(estimate: reusedVisuals[i] is null && candidateVisual is null
                        ? VisualCost(source.Visuals[i]) : default)) return null;
                    using var interval = SceneDisplayDiagnostics.Begin("Prepare.Visual", source.Id, i,
                        source.Visuals[i] is SceneVisual.Geometry geometry ? geometry.Mesh : null);
                    if (reusedVisuals[i] is { } reused) visuals[i] = reused;
                    else
                    {
                        preparation.NeedsResources = true;
                        var visual = candidateVisual;
                        if (visual is not null) _candidateVisuals.Remove((source.Id, i));
                        else if (spaceChanged) visual = _resources.Prepare(source.Visuals[i]);
                        else
                        {
                            var resourceStarted = Timestamp();
                            visual = _resources.TryPrepare(source.Visuals[i], preparation.Packing.Job);
                            if (visual is not null) RecordVisualCost(source.Visuals[i], resourceStarted);
                        }
                        if (visual is null) return null;
                        _pendingVisuals.Add(visual);
                        visuals[i] = visual;
                    }
                }
                next[source.Id] = new(source, root, visuals);
                if (preparation.NeedsResources)
                    Observe?.Invoke(new(_candidate, update.Frame, source.Id, SceneDisplayStage.ResourcesPrepared));
                preparation.Visuals = null;
                preparation.Visual = 0;
                preparation.NeedsResources = false;
            }
            using (SceneDisplayDiagnostics.Begin("Prepare.ValidateObjects"))
                if (!ValidatePreparation(preparation, slice)) return null;
            using (SceneDisplayDiagnostics.Begin("Prepare.EnvironmentChecks"))
            {
                if (!preparation.FogChecked)
                {
                    var regions = update.Environment.FoggedRegions;
                    preparation.FogChanged |= _fog is null || _fog.Regions.Length != regions.Length;
                    if (!preparation.FogChanged)
                        for (; preparation.FogComparison < regions.Length; preparation.FogComparison++)
                        {
                            if (!slice.Start()) return null;
                            if (_fog!.Regions[preparation.FogComparison] == regions[preparation.FogComparison]) continue;
                            preparation.FogChanged = true;
                            break;
                        }
                    preparation.FogChecked = true;
                }
                if (preparation.FogChanged && _pendingFog is null)
                {
                    if (!slice.Start()) return null;
                    _pendingFog = new(update.Environment.FoggedRegions, _fog?.Material ?? _resources.ConstructionFogMaterial(), _fog);
                }
                if (_pendingFog is not null)
                {
                    using var interval = SceneDisplayDiagnostics.Begin("Prepare.Fog");
                    if (!_pendingFog.Prepare(slice)) return null;
                }
                if (!preparation.LinesChecked)
                {
                    var lines = frame.DebugLines;
                    preparation.LinesChanged |= !lines.IsEmpty && (_debugLines is null || _debugLines.Lines.Length != lines.Length);
                    if (!preparation.LinesChanged && !lines.IsEmpty)
                        for (; preparation.LineComparison < lines.Length; preparation.LineComparison++)
                        {
                            if (!slice.Start()) return null;
                            if (_debugLines!.Lines[preparation.LineComparison] == lines[preparation.LineComparison]) continue;
                            preparation.LinesChanged = true;
                            break;
                        }
                    preparation.LinesChecked = true;
                }
                if (preparation.LinesChanged && _pendingDebugLines is null)
                {
                    if (!slice.Start()) return null;
                    _pendingDebugLines = new(frame.DebugLines);
                }
                if (_pendingDebugLines is not null)
                {
                    using var interval = SceneDisplayDiagnostics.Begin("Prepare.DebugLines");
                    if (!_pendingDebugLines.Prepare(slice)) return null;
                }
            }
            using (SceneDisplayDiagnostics.Begin("Prepare.ReleasePacking"))
                if (!preparation.Packing.Release(slice)) return null;
            if (!slice.Start(estimate: _finalizationCost)) return null;
            using var finalizationInterval = SceneDisplayDiagnostics.Begin("Prepare.Finalize");
            var finalizationStarted = Timestamp();
            var prepared = new PreparedFrame(this, _candidate, frame, next.ToImmutable(), _pendingVisuals,
                _pendingRoots, _pendingFog ?? _fog!, _pendingFog is not null,
                frame.DebugLines.IsEmpty ? null : _pendingDebugLines ?? _debugLines, _pendingDebugLines is not null);
            _pendingFog = null;
            _pendingDebugLines = null;
            _pendingVisuals = [];
            _pendingRoots = [];
            _preparation = null;
            Observe?.Invoke(new(_candidate, update.Frame, null, SceneDisplayStage.FramePrepared));
            _finalizationCost = Stopwatch.GetElapsedTime(finalizationStarted, Timestamp());
            return prepared;
        }
        catch
        {
            CancelPreparation();
            throw;
        }
        finally
        {
            var pause = GC.GetTotalPauseDuration() - pauseStarted;
            var ended = Timestamp();
            var elapsed = Stopwatch.GetElapsedTime(started, ended);
            _preparationTime += elapsed;
            Measure?.Invoke(SceneDisplayWork.Prepare, elapsed, pause);
        }
    }

    private sealed class Preparation(ISceneFrame frame, SceneResources resources, ScenePackingPreparation.Costs costs)
    {
        internal ISceneFrame Frame { get; } = frame;
        internal ImmutableDictionary<SceneObjectId, DisplayObject>.Builder? Objects;
        internal IEnumerator<SceneObjectId>? ShiftRoots, ValidationChildren;
        internal int Removed, MatchUpsert, PackingUpsert, PackingVisual, Upsert, Visual;
        internal VisualMatching? Matching;
        internal PreparedVisual[]? Visuals;
        internal bool NeedsResources, Started;
        internal int ValidateUpsert, ValidateRemoved;
        internal bool FogChecked, FogChanged, LinesChecked, LinesChanged;
        internal int FogComparison, LineComparison;
        internal ScenePackingPreparation Packing { get; } = new(resources, costs);
        internal Dictionary<SceneObjectId, PreparedVisual?[]> ReusedVisuals { get; } = [];
    }

    private static bool _budgetAdvanced;

    private static void ResetPreparationBudget()
    {
        var processFrame = Engine.GetProcessFrames();
        if (_budgetFrame == processFrame) return;
        _budgetFrame = processFrame;
        _preparationTime = TimeSpan.Zero;
        _budgetAdvanced = false;
    }

    internal readonly struct PreparationSlice(SceneDisplay owner, long started, TimeSpan? budget, bool unlimited)
    {
        internal long Timestamp() => owner.Timestamp();
        internal bool Unlimited => unlimited;

        internal bool Start(TimeSpan estimate = default)
        {
            if (unlimited) { _budgetAdvanced = true; return true; }
            var elapsed = Stopwatch.GetElapsedTime(started, owner.Timestamp());
            var allowance = budget ?? PreparationBudget;
            var remaining = TimeSpan.FromTicks(Math.Min((allowance - elapsed).Ticks,
                (PreparationBudget - _preparationTime - elapsed).Ticks));
            // 本帧预算尚未耗尽时，第一步总能开始，避免大项成本超过4ms后永久饿死。
            if (_preparationTime >= PreparationBudget ||
                _budgetAdvanced && (remaining <= TimeSpan.Zero || estimate > remaining)) return false;
            _budgetAdvanced = true;
            return true;
        }
    }
    private TimeSpan _geometryCost, _modelCost, _wheelsetCost, _missingCost, _finalizationCost;

    private TimeSpan VisualCost(SceneVisual visual) => visual switch
    {
        SceneVisual.Geometry => _geometryCost,
        SceneVisual.Model => _modelCost,
        SceneVisual.Wheelset => _wheelsetCost,
        SceneVisual.Missing => _missingCost,
        _ => throw new ArgumentOutOfRangeException(nameof(visual))
    };

    private void RecordVisualCost(SceneVisual visual, long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started, Timestamp());
        switch (visual)
        {
            case SceneVisual.Geometry: _geometryCost = elapsed; break;
            case SceneVisual.Model: _modelCost = elapsed; break;
            case SceneVisual.Wheelset: _wheelsetCost = elapsed; break;
            case SceneVisual.Missing: _missingCost = elapsed; break;
        }
    }

}
